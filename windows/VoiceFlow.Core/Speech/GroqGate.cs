using System.Globalization;

namespace VoiceFlow.Core.Speech;

/// <summary>Who is asking. A dictation during a meeting must never wait behind meeting chunks.</summary>
public enum GroqPriority
{
    /// <summary>Dictation and cleanup: someone is waiting for this text.</summary>
    Interactive,
    /// <summary>Meeting chunks, transcript rewriting, notes.</summary>
    Background,
}

public sealed record GroqCost(string Model, bool IsTranscription, double AudioSeconds, int Tokens)
{
    public static GroqCost Transcription(string model, double audioSeconds) => new(model, true, audioSeconds, 0);
    public static GroqCost Chat(string model, int estimatedTokens) => new(model, false, 0, estimatedTokens);

    /// <summary>Groq bills every transcription as at least 10 seconds of audio.</summary>
    public double BilledAudioSeconds => IsTranscription ? Math.Max(Groq.MinimumBilledSeconds, AudioSeconds) : 0;
}

/// <summary>Free-tier limits for one model. These are defaults; <see cref="RateBudget"/> adjusts to Groq's response headers.</summary>
public sealed record RateLimits
{
    public int RequestsPerMinute { get; init; }
    public int RequestsPerDay { get; init; }
    public double? AudioSecondsPerHour { get; init; }
    public double? AudioSecondsPerDay { get; init; }
    public int? TokensPerMinute { get; init; }
    public int? TokensPerDay { get; init; }

    /// <summary>From console.groq.com/docs/rate-limits, read 2026-11-10 (base Developer-plan limits).</summary>
    public static readonly RateLimits Whisper = new() { RequestsPerMinute = 20, RequestsPerDay = 2_000, AudioSecondsPerHour = 7_200, AudioSecondsPerDay = 28_800 };
    public static readonly RateLimits Chat = new() { RequestsPerMinute = 30, RequestsPerDay = 1_000, TokensPerMinute = 8_000, TokensPerDay = 200_000 };

    public static RateLimits DefaultsForModel(string model) => model.StartsWith("whisper", StringComparison.Ordinal) ? Whisper : Chat;
}

/// <summary>Go, or try again after <see cref="WaitSeconds"/>.</summary>
public readonly record struct RateDecision(double WaitSeconds)
{
    public static readonly RateDecision Go = new(0);
    public bool IsGo => WaitSeconds <= 0;
}

/// <summary>The rate-limit arithmetic for one model. It has no clock of its own, so it can be tested directly.</summary>
public sealed class RateBudget(RateLimits limits)
{
    /// <summary>What background work leaves free, so dictation always has room even during a long meeting.</summary>
    public const int ReservedRequestsPerMinute = 4;
    public const int ReservedRequestsPerDay = 150;
    public const double ReservedAudioSecondsPerHour = 600;
    public const double ReservedAudioSecondsPerDay = 1_800;
    public const int ReservedTokensPerMinute = 1_500;

    readonly List<DateTime> _requests = [];
    readonly List<(DateTime At, double Seconds)> _audio = [];
    readonly List<(DateTime At, int Tokens)> _tokens = [];
    /// <summary>Requests left today according to the latest response headers, and when that count resets.</summary>
    int? _serverRequestsLeft;
    DateTime? _serverRequestsReset;
    DateTime? _blockedUntil;

    public RateLimits Limits { get; private set; } = limits;

    /// <summary>Whether a request of this cost may start at <paramref name="now"/>.</summary>
    public RateDecision Decision(GroqCost cost, GroqPriority priority, DateTime now)
    {
        var background = priority == GroqPriority.Background;
        double wait = 0;
        if (_blockedUntil is { } blocked && blocked > now)
        {
            var left = (blocked - now).TotalSeconds;
            if (background) return new RateDecision(left);
            wait = Math.Max(wait, Math.Min(left, 2));
        }
        var requests = _requests.Select(t => (t, 1.0)).ToList();
        wait = Math.Max(wait, WindowWait(requests, 60, Limits.RequestsPerMinute - (background ? ReservedRequestsPerMinute : 0), 1, now));
        var perDayReserve = background ? ReservedRequestsPerDay : 0;
        wait = Math.Max(wait, WindowWait(requests, 86_400, Limits.RequestsPerDay - perDayReserve, 1, now));
        if (_serverRequestsLeft is { } serverLeft && _serverRequestsReset is { } reset && reset > now && serverLeft <= perDayReserve)
            wait = Math.Max(wait, (reset - now).TotalSeconds);
        var billed = cost.BilledAudioSeconds;
        if (billed > 0)
        {
            var audio = _audio.Select(a => (a.At, a.Seconds)).ToList();
            if (Limits.AudioSecondsPerHour is { } hourly)
                wait = Math.Max(wait, WindowWait(audio, 3_600, hourly - (background ? ReservedAudioSecondsPerHour : 0), billed, now));
            if (Limits.AudioSecondsPerDay is { } daily)
                wait = Math.Max(wait, WindowWait(audio, 86_400, daily - (background ? ReservedAudioSecondsPerDay : 0), billed, now));
        }
        if (cost.Tokens > 0)
        {
            var tokens = _tokens.Select(t => (t.At, (double)t.Tokens)).ToList();
            if (Limits.TokensPerMinute is { } perMinute)
                wait = Math.Max(wait, WindowWait(tokens, 60, perMinute - (background ? ReservedTokensPerMinute : 0), cost.Tokens, now));
            if (Limits.TokensPerDay is { } perDay)
                wait = Math.Max(wait, WindowWait(tokens, 86_400, perDay, cost.Tokens, now));
        }
        return wait > 0 ? new RateDecision(wait) : RateDecision.Go;
    }

    /// <summary>Records that a request started.</summary>
    public void Record(GroqCost cost, DateTime now)
    {
        _requests.Add(now);
        if (cost.BilledAudioSeconds > 0) _audio.Add((now, cost.BilledAudioSeconds));
        if (cost.Tokens > 0) _tokens.Add((now, cost.Tokens));
        Prune(now);
    }

    /// <summary>Reads Groq's rate-limit response headers, which beat any local estimate.</summary>
    public void Apply(IReadOnlyDictionary<string, string> headers, DateTime now)
    {
        string? Header(string name) => headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        static int? Int(string? text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        if (Int(Header("x-ratelimit-limit-requests")) is { } requests && requests > 0) Limits = Limits with { RequestsPerDay = requests };
        if (Int(Header("x-ratelimit-limit-tokens")) is { } tokens && tokens > 0 && Limits.TokensPerMinute != null) Limits = Limits with { TokensPerMinute = tokens };
        if (Int(Header("x-ratelimit-remaining-requests")) is { } left)
        {
            _serverRequestsLeft = left;
            _serverRequestsReset = Header("x-ratelimit-reset-requests") is { } text && ParseDuration(text) is { } reset ? now.AddSeconds(reset) : null;
        }
    }

    /// <summary>Records a 429: background work waits this long.</summary>
    public void ApplyRetryAfter(double? seconds, DateTime now) => _blockedUntil = now.AddSeconds(Math.Max(seconds ?? 5, 1));

    /// <summary>"43.2s", "2m59.56s", "1h2m3s".</summary>
    internal static double? ParseDuration(string text)
    {
        double total = 0;
        var number = new System.Text.StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsAsciiDigit(ch) || ch == '.')
            {
                number.Append(ch);
                continue;
            }
            if (!double.TryParse(number.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return null;
            number.Clear();
            switch (ch)
            {
                case 'h': total += value * 3_600; break;
                case 'm': total += value * 60; break;
                case 's': total += value; break;
                default: return null;
            }
        }
        return number.Length == 0 ? total : null;
    }

    /// <summary>Seconds until the events inside <paramref name="window"/> leave room for <paramref name="cost"/> under <paramref name="limit"/>.</summary>
    static double WindowWait(List<(DateTime At, double Amount)> events, double window, double limit, double cost, DateTime now)
    {
        if (limit <= 0) return window;
        var live = events.Where(e => (now - e.At).TotalSeconds < window).OrderBy(e => e.At).ToList();
        var used = live.Sum(e => e.Amount);
        if (used + cost <= limit) return 0;
        foreach (var (at, amount) in live)
        {
            used -= amount;
            if (used + cost <= limit) return (at.AddSeconds(window) - now).TotalSeconds;
        }
        return window;
    }

    void Prune(DateTime now)
    {
        var cut = now.AddSeconds(-86_400);
        if (_requests.Count > 0 && _requests[0] < cut) _requests.RemoveAll(t => t < cut);
        if (_audio.Count > 0 && _audio[0].At < cut) _audio.RemoveAll(a => a.At < cut);
        if (_tokens.Count > 0 && _tokens[0].At < cut) _tokens.RemoveAll(t => t.At < cut);
    }

    internal double RemainingAudioThisHour(DateTime now)
    {
        if (Limits.AudioSecondsPerHour is not { } hourly) return double.PositiveInfinity;
        return hourly - _audio.Where(a => (now - a.At).TotalSeconds < 3_600).Sum(a => a.Seconds);
    }
}

/// <summary>
/// The single queue every Groq request goes through. Background requests run in order and leave room for
/// interactive ones; interactive requests jump the line and only ever wait a moment.
/// </summary>
public sealed class GroqGate(Func<DateTime>? now = null, Func<double, CancellationToken, Task>? sleep = null)
{
    public static GroqGate Shared { get; } = new();

    /// <summary>Longest a background request waits for room before giving up. The caller holds the chunk for later.</summary>
    public const double MaxBackgroundWait = 90;
    /// <summary>Longest an interactive request waits. Past that it goes out anyway and Groq has the last word.</summary>
    public const double MaxInteractiveWait = 2;

    readonly object _lock = new();
    readonly Dictionary<string, RateBudget> _budgets = [];
    readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);
    readonly Func<double, CancellationToken, Task> _sleep = sleep ?? ((seconds, cancel) => Task.Delay(TimeSpan.FromSeconds(seconds), cancel));
    Task _backgroundTail = Task.CompletedTask;

    RateBudget Budget(string model)
    {
        if (!_budgets.TryGetValue(model, out var budget)) _budgets[model] = budget = new RateBudget(RateLimits.DefaultsForModel(model));
        return budget;
    }

    /// <summary>Runs one Groq request when the budget allows it.</summary>
    public async Task<byte[]> PerformAsync(GroqCost cost, GroqPriority priority, Func<Task<GroqResponse>> body, CancellationToken cancel = default)
    {
        await AdmitAsync(cost, priority, cancel).ConfigureAwait(false);
        try
        {
            var response = await body().ConfigureAwait(false);
            lock (_lock) Budget(cost.Model).Apply(response.Headers, _now());
            return response.Data;
        }
        catch (ProviderException e) when (e.Kind is ProviderErrorKind.RateLimited or ProviderErrorKind.DailyLimit)
        {
            var retryAfter = e.Kind == ProviderErrorKind.DailyLimit ? e.RetryAfter ?? 3_600 : e.RetryAfter;
            lock (_lock) Budget(cost.Model).ApplyRetryAfter(retryAfter, _now());
            throw;
        }
    }

    async Task AdmitAsync(GroqCost cost, GroqPriority priority, CancellationToken cancel)
    {
        if (priority == GroqPriority.Interactive)
        {
            RateDecision decision;
            lock (_lock) decision = Budget(cost.Model).Decision(cost, priority, _now());
            if (!decision.IsGo) await _sleep(Math.Min(decision.WaitSeconds, MaxInteractiveWait), cancel).ConfigureAwait(false);
            lock (_lock) Budget(cost.Model).Record(cost, _now());
            return;
        }
        // One background request is admitted at a time, so requests go out in the order they were queued.
        var turn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (_lock)
        {
            previous = _backgroundTail;
            _backgroundTail = turn.Task;
        }
        try
        {
            await previous.WaitAsync(cancel).ConfigureAwait(false);
            await WaitForRoomAsync(cost, cancel).ConfigureAwait(false);
        }
        finally
        {
            turn.TrySetResult();
        }
    }

    async Task WaitForRoomAsync(GroqCost cost, CancellationToken cancel)
    {
        double waited = 0;
        while (true)
        {
            RateDecision decision;
            lock (_lock)
            {
                decision = Budget(cost.Model).Decision(cost, GroqPriority.Background, _now());
                if (decision.IsGo)
                {
                    Budget(cost.Model).Record(cost, _now());
                    return;
                }
            }
            if (waited + decision.WaitSeconds > MaxBackgroundWait) throw ProviderException.RateLimited(decision.WaitSeconds);
            var step = Math.Min(decision.WaitSeconds, 2);
            await _sleep(step, cancel).ConfigureAwait(false);
            waited += step;
        }
    }

    /// <summary>How much audio budget is left this hour, by the app's own count. Meetings use it to drop second opinions.</summary>
    public double TranscriptionHeadroom(string model)
    {
        lock (_lock) return Budget(model).RemainingAudioThisHour(_now());
    }
}
