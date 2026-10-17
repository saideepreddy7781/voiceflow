using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace VoiceFlow.Core.Speech;

/// <summary>
/// A transcription with the detected language and timed segments (Whisper's verbose_json).
/// Confidence is the duration-weighted average log-probability of the segments (closer to 0 = more confident).
/// </summary>
public sealed record DetailedTranscript(string Text, string? Language, IReadOnlyList<TranscriptSegment> Segments, double? Confidence = null);

public sealed record TranscriptSegment(double Start, double End, string Text);

public interface ITranscriber
{
    Task<string> TranscribeAsync(byte[] wav, string? language, string? prompt, CancellationToken cancel = default);
}

/// <summary>Groq Whisper (/audio/transcriptions, OpenAI-compatible).</summary>
public sealed class GroqTranscriber : ITranscriber
{
    readonly string _apiKey;
    readonly string _model;
    readonly string? _fallbackModel;
    readonly HttpClient _client;
    readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public GroqTranscriber(string apiKey, string model = "whisper-large-v3-turbo", string? fallbackModel = "whisper-large-v3",
                           HttpClient? client = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _apiKey = apiKey.Trim();
        _model = model;
        _fallbackModel = fallbackModel == model ? null : fallbackModel;
        _client = client ?? GroqHttp.Shared;
        _delay = delay ?? Task.Delay;
    }

    public async Task<string> TranscribeAsync(byte[] wav, string? language, string? prompt, CancellationToken cancel = default)
    {
        try
        {
            return ParseSimple(await PostAsync(wav, _model, language, prompt, "json", cancel).ConfigureAwait(false));
        }
        catch (ProviderException e)
        {
            switch (e.Kind)
            {
                case ProviderErrorKind.RateLimited:
                    // One wait-and-retry if Groq says it is short; otherwise surface it.
                    if (e.RetryAfter is not { } retry || retry > 3) throw;
                    await _delay(TimeSpan.FromSeconds(retry + 0.2), cancel).ConfigureAwait(false);
                    return ParseSimple(await PostAsync(wav, _model, language, prompt, "json", cancel).ConfigureAwait(false));
                case ProviderErrorKind.MissingKey:
                    throw;
                default:
                    if (_fallbackModel is null || e.IsBadKey) throw;
                    return ParseSimple(await PostAsync(wav, _fallbackModel, language, prompt, "json", cancel).ConfigureAwait(false));
            }
        }
    }

    /// <summary>
    /// For speakers of several languages: auto-detect and, in parallel, the given language; then the better
    /// result (see <see cref="LanguageResolver"/>). With <paramref name="parallelLanguage"/> null this is one request.
    /// <paramref name="sequential"/> (meetings): ask for the second language only when auto-detect looks wrong,
    /// which halves the audio counted against Groq's hourly limit for a long meeting.
    /// </summary>
    public async Task<DetailedTranscript> TranscribeResolvingAsync(byte[] wav, string? language, string? parallelLanguage,
        IReadOnlyCollection<string> languages, string? prompt, bool sequential = false, CancellationToken cancel = default)
    {
        if (language is not null || parallelLanguage is null)
            return await TranscribeDetailedAsync(wav, language, prompt, cancel).ConfigureAwait(false);

        if (sequential)
        {
            var first = await TranscribeDetailedAsync(wav, null, prompt, cancel).ConfigureAwait(false);
            if (!LanguageResolver.NeedsSecondOpinion(first, languages)) return first;
            DetailedTranscript? second = null;
            try { second = await TranscribeDetailedAsync(wav, parallelLanguage, prompt, cancel).ConfigureAwait(false); }
            catch (ProviderException) { }
            return LanguageResolver.Choose(first, second, parallelLanguage, languages);
        }

        var autoTask = TranscribeDetailedAsync(wav, null, prompt, cancel);
        var forcedTask = TranscribeDetailedAsync(wav, parallelLanguage, prompt, cancel);
        DetailedTranscript? forced = null;
        try { forced = await forcedTask.ConfigureAwait(false); }
        catch (ProviderException) { }
        DetailedTranscript auto;
        try
        {
            auto = await autoTask.ConfigureAwait(false);
        }
        catch (ProviderException)
        {
            // Auto-detect failed but the forced language worked: use it rather than losing the speech.
            if (forced is not null) return forced;
            throw;
        }
        return LanguageResolver.Choose(auto, forced, parallelLanguage, languages);
    }

    /// <summary>Text, detected language and timed segments.</summary>
    public async Task<DetailedTranscript> TranscribeDetailedAsync(byte[] wav, string? language, string? prompt, CancellationToken cancel = default)
    {
        try
        {
            return ParseVerbose(await PostAsync(wav, _model, language, prompt, "verbose_json", cancel).ConfigureAwait(false));
        }
        catch (ProviderException e)
        {
            if (e.Kind == ProviderErrorKind.RateLimited && e.RetryAfter is { } retry && retry <= 3)
            {
                await _delay(TimeSpan.FromSeconds(retry + 0.2), cancel).ConfigureAwait(false);
                return ParseVerbose(await PostAsync(wav, _model, language, prompt, "verbose_json", cancel).ConfigureAwait(false));
            }
            if (_fallbackModel is null || e.Kind == ProviderErrorKind.MissingKey || e.IsBadKey) throw;
            return ParseVerbose(await PostAsync(wav, _fallbackModel, language, prompt, "verbose_json", cancel).ConfigureAwait(false));
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
            var segments = new List<TranscriptSegment>();
            double weighted = 0, total = 0;
            if (root.TryGetProperty("segments", out var segs) && segs.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in segs.EnumerateArray())
                {
                    if (s.ValueKind != JsonValueKind.Object) continue;
                    double? start = Num(s, "start"), end = Num(s, "end");
                    if (start is { } a && end is { } b && s.TryGetProperty("text", out var st) && st.ValueKind == JsonValueKind.String)
                        segments.Add(new TranscriptSegment(a, b, st.GetString()!.Trim()));
                    if (Num(s, "avg_logprob") is { } lp && start is { } a2 && end is { } b2)
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
    public static MultipartFormDataContent BuildContent(byte[] wav, string model, string? language, string? prompt, string format)
    {
        var content = new MultipartFormDataContent("vf-" + Guid.NewGuid().ToString("N"));
        void Field(string name, string value) => content.Add(new StringContent(value, Encoding.UTF8), name);
        Field("model", model);
        Field("response_format", format);
        Field("temperature", "0");
        if (!string.IsNullOrEmpty(language)) Field("language", language);
        if (!string.IsNullOrEmpty(prompt)) Field("prompt", prompt);
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(file, "file", "audio.wav");
        return content;
    }

    Task<byte[]> PostAsync(byte[] wav, string model, string? language, string? prompt, string format, CancellationToken cancel)
    {
        if (_apiKey.Length == 0) throw ProviderException.MissingKey();
        return GroqHttp.SendAsync(_client, () => new HttpRequestMessage(HttpMethod.Post, new Uri(Groq.BaseUrl, "audio/transcriptions"))
        {
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", _apiKey) },
            Content = BuildContent(wav, model, language, prompt, format),
        }, RequestTimeout, cancel);
    }
}
