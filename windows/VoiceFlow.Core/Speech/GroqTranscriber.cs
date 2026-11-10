using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VoiceFlow.Core.Audio;

namespace VoiceFlow.Core.Speech;

/// <summary>
/// A transcription with the detected language and timed segments (Whisper's verbose_json).
/// Confidence is the duration-weighted average log-probability of the segments (closer to 0 = more confident).
/// </summary>
public sealed record DetailedTranscript(string Text, string? Language, IReadOnlyList<TranscriptSegment> Segments, double? Confidence = null);

public sealed record TranscriptWord(string Word, double Start, double End);

public sealed record TranscriptSegment(double Start, double End, string Text, double? AvgLogprob = null, double? NoSpeechProb = null,
                                       double? CompressionRatio = null, IReadOnlyList<TranscriptWord>? Words = null);

public interface ITranscriber
{
    Task<string> TranscribeAsync(byte[] wav, string? language, string? prompt, CancellationToken cancel = default);
}

/// <summary>How to get a transcript when several languages are in play.</summary>
/// <param name="Sequential">False: auto-detect and forced opinions at once (dictation, fastest). True: auto-detect, then forced opinions only if it looks wrong (meetings, cheapest).</param>
/// <param name="Hint">With <paramref name="Sequential"/>, starts with this forced language when the stream has been in one language for a while.</param>
public readonly record struct ResolveMode(bool Sequential, string? Hint = null)
{
    public static readonly ResolveMode Parallel = new(false);
    public static ResolveMode SequentialWith(string? hint) => new(true, hint);
}

/// <summary>Groq Whisper (/audio/transcriptions, OpenAI-compatible).</summary>
public sealed class GroqTranscriber : ITranscriber
{
    readonly string _apiKey;
    readonly string _model;
    readonly string? _fallbackModel;
    readonly HttpClient _client;
    readonly Func<TimeSpan, CancellationToken, Task> _delay;
    readonly GroqGate _gate;

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);

    public GroqTranscriber(string apiKey, string model = "whisper-large-v3-turbo", string? fallbackModel = "whisper-large-v3",
                           HttpClient? client = null, Func<TimeSpan, CancellationToken, Task>? delay = null, GroqGate? gate = null)
    {
        _apiKey = apiKey.Trim();
        _model = model;
        _fallbackModel = fallbackModel == model ? null : fallbackModel;
        _client = client ?? GroqHttp.Shared;
        _delay = delay ?? Task.Delay;
        _gate = gate ?? GroqGate.Shared;
    }

    public async Task<string> TranscribeAsync(byte[] wav, string? language, string? prompt, CancellationToken cancel = default)
    {
        Task<byte[]> Send(string model) => PostAsync(wav, model, language, prompt, "json", GroqPriority.Interactive, false, cancel);
        try
        {
            return ParseSimple(await Send(_model).ConfigureAwait(false));
        }
        catch (ProviderException e)
        {
            switch (e.Kind)
            {
                case ProviderErrorKind.RateLimited:
                    // One wait-and-retry if Groq says it is short; otherwise surface it.
                    if (e.RetryAfter is not { } retry || retry > 3) throw;
                    await _delay(TimeSpan.FromSeconds(retry + 0.2), cancel).ConfigureAwait(false);
                    return ParseSimple(await Send(_model).ConfigureAwait(false));
                case ProviderErrorKind.MissingKey or ProviderErrorKind.DailyLimit:
                    throw;
                default:
                    if (_fallbackModel is null || e.IsBadKey) throw;
                    return ParseSimple(await Send(_fallbackModel).ConfigureAwait(false));
            }
        }
    }

    /// <summary>The transcript for one piece of audio, for speakers of one or more languages (see <see cref="LanguageResolver"/>).</summary>
    public async Task<DetailedTranscript> TranscribeResolvingAsync(byte[] wav, LanguagePlan plan, WhisperPrompts prompts, ResolveMode mode = default,
                                                                   GroqPriority priority = GroqPriority.Interactive, bool wordTimestamps = false,
                                                                   CancellationToken cancel = default)
    {
        Task<DetailedTranscript> Run(string? language) =>
            TranscribeDetailedAsync(wav, language, prompts.Text(language), priority, wordTimestamps, cancel);

        if (plan.ForcedFirst is { } forced) return WhisperSegmentFilter.Filtered(await Run(forced).ConfigureAwait(false));
        if (plan.Languages.Count == 0) return WhisperSegmentFilter.Filtered(await Run(null).ConfigureAwait(false));

        if (mode.Sequential)
        {
            if (mode.Hint is { } hint)
            {
                var hinted = await Run(hint).ConfigureAwait(false);
                if (LanguageResolver.ForcedResultAcceptable(hinted)) return WhisperSegmentFilter.Filtered(hinted);
                // The sticky language no longer fits; fall through to a full look.
            }
            var first = await Run(null).ConfigureAwait(false);
            var wanted = LanguageResolver.OpinionLanguages(first, plan);
            if (wanted.Count == 0) return WhisperSegmentFilter.Filtered(first);
            var heard = new List<LanguageOpinion>();
            foreach (var language in wanted)
            {
                try { heard.Add(new LanguageOpinion(language, await Run(language).ConfigureAwait(false))); }
                catch (ProviderException) { }
            }
            return WhisperSegmentFilter.Filtered(LanguageResolver.Choose(first, heard, plan));
        }

        var auto = Run(null);
        var tasks = plan.OpinionCandidates.Take(LanguageResolver.MaxOpinions).Select(async language =>
        {
            try { return new LanguageOpinion(language, await Run(language).ConfigureAwait(false)); }
            catch (ProviderException) { return null; }
        }).ToList();
        var opinions = (await Task.WhenAll(tasks).ConfigureAwait(false)).OfType<LanguageOpinion>().ToList();
        DetailedTranscript autoResult;
        try
        {
            autoResult = await auto.ConfigureAwait(false);
        }
        catch (ProviderException)
        {
            // Auto-detect failed but a forced language worked: use it rather than losing the speech.
            if (opinions.MaxBy(o => o.Transcript.Confidence ?? -10) is { } best) return WhisperSegmentFilter.Filtered(best.Transcript);
            throw;
        }
        return WhisperSegmentFilter.Filtered(LanguageResolver.Choose(autoResult, opinions, plan));
    }

    /// <summary>Text, detected language and timed segments.</summary>
    public async Task<DetailedTranscript> TranscribeDetailedAsync(byte[] wav, string? language, string? prompt, GroqPriority priority = GroqPriority.Interactive,
                                                                  bool wordTimestamps = false, CancellationToken cancel = default)
    {
        Task<byte[]> Send(string model) => PostAsync(wav, model, language, prompt, "verbose_json", priority, wordTimestamps, cancel);
        try
        {
            return ParseVerbose(await Send(_model).ConfigureAwait(false));
        }
        catch (ProviderException e)
        {
            if (e.Kind == ProviderErrorKind.RateLimited && e.RetryAfter is { } retry && retry <= 3)
            {
                await _delay(TimeSpan.FromSeconds(retry + 0.2), cancel).ConfigureAwait(false);
                return ParseVerbose(await Send(_model).ConfigureAwait(false));
            }
            if (e.Kind == ProviderErrorKind.DailyLimit || _fallbackModel is null || e.Kind == ProviderErrorKind.MissingKey || e.IsBadKey) throw;
            return ParseVerbose(await Send(_fallbackModel).ConfigureAwait(false));
        }
    }

    internal static string ParseSimple(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("text", out var t)
                && t.ValueKind == JsonValueKind.String)
                return t.GetString()!.Trim();
        }
        catch (JsonException) { }
        throw ProviderException.BadResponse();
    }

    internal static DetailedTranscript ParseVerbose(byte[] data)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(data); }
        catch (JsonException) { throw ProviderException.BadResponse(); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("text", out var textEl) || textEl.ValueKind != JsonValueKind.String)
                throw ProviderException.BadResponse();
            string? language = null;
            if (root.TryGetProperty("language", out var l) && l.ValueKind == JsonValueKind.String)
            {
                var name = l.GetString()!;
                language = Languages.CodeForWhisperName(name) ?? (name.Length <= 3 ? name : null);
            }
            var words = new List<TranscriptWord>();
            if (root.TryGetProperty("words", out var ws) && ws.ValueKind == JsonValueKind.Array)
            {
                foreach (var w in ws.EnumerateArray())
                {
                    if (w.ValueKind == JsonValueKind.Object && Num(w, "start") is { } a && Num(w, "end") is { } b
                        && w.TryGetProperty("word", out var t) && t.ValueKind == JsonValueKind.String)
                        words.Add(new TranscriptWord(t.GetString()!.Trim(), a, b));
                }
            }
            var wordIndex = 0;
            var segments = new List<TranscriptSegment>();
            double weighted = 0, total = 0;
            if (root.TryGetProperty("segments", out var segs) && segs.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in segs.EnumerateArray())
                {
                    if (s.ValueKind != JsonValueKind.Object) continue;
                    double? start = Num(s, "start"), end = Num(s, "end"), logprob = Num(s, "avg_logprob");
                    if (start is { } a && end is { } b && s.TryGetProperty("text", out var st) && st.ValueKind == JsonValueKind.String)
                    {
                        // Words are reported in order; hand each to the segment whose span holds its midpoint.
                        var own = new List<TranscriptWord>();
                        while (wordIndex < words.Count && (words[wordIndex].Start + words[wordIndex].End) / 2 <= b + 0.01)
                            own.Add(words[wordIndex++]);
                        segments.Add(new TranscriptSegment(a, b, st.GetString()!.Trim(), logprob, Num(s, "no_speech_prob"), Num(s, "compression_ratio"), own));
                    }
                    if (logprob is { } lp && start is { } a2 && end is { } b2)
                    {
                        var d = Math.Max(0.01, b2 - a2);
                        weighted += lp * d;
                        total += d;
                    }
                }
            }
            return new DetailedTranscript(textEl.GetString()!.Trim(), language, segments, total > 0 ? weighted / total : null);
        }

        static double? Num(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    }

    /// <summary>The multipart body for one transcription request. Public so the self-test can build it offline.</summary>
    public static MultipartFormDataContent BuildContent(byte[] wav, string model, string? language, string? prompt, string format, bool wordTimestamps = false)
    {
        var content = new MultipartFormDataContent("vf-" + Guid.NewGuid().ToString("N"));
        void Field(string name, string value) => content.Add(new StringContent(value, Encoding.UTF8), name);
        Field("model", model);
        Field("response_format", format);
        Field("temperature", "0");
        if (!string.IsNullOrEmpty(language)) Field("language", language);
        if (!string.IsNullOrEmpty(prompt)) Field("prompt", prompt);
        if (wordTimestamps)
        {
            Field("timestamp_granularities[]", "segment");
            Field("timestamp_granularities[]", "word");
        }
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(file, "file", "audio.wav");
        return content;
    }

    Task<byte[]> PostAsync(byte[] wav, string model, string? language, string? prompt, string format, GroqPriority priority, bool wordTimestamps, CancellationToken cancel)
    {
        if (_apiKey.Length == 0) throw ProviderException.MissingKey();
        var seconds = Math.Max(0.01, Math.Max(0, wav.Length - 44) / 2.0 / AudioMath.SampleRate);
        return _gate.PerformAsync(GroqCost.Transcription(model, seconds), priority, () =>
            GroqHttp.SendDetailedAsync(_client, () => new HttpRequestMessage(HttpMethod.Post, new Uri(Groq.BaseUrl, "audio/transcriptions"))
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", _apiKey) },
                Content = BuildContent(wav, model, language, prompt, format, wordTimestamps),
            }, RequestTimeout, cancel), cancel);
    }
}
