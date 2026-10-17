namespace VoiceFlow.Core;

/// <summary>
/// Groq API facts (same as the macOS app's Providers.swift, verified 2026-10-17).
/// Endpoints are OpenAI-compatible: /audio/transcriptions (multipart) and /chat/completions.
/// Every transcription request is billed as at least 10 seconds of audio; the Whisper prompt is
/// limited to 224 tokens.
/// </summary>
public static class Groq
{
    public static readonly Uri BaseUrl = new("https://api.groq.com/openai/v1/");

    public static readonly string[] TranscriptionModels = ["whisper-large-v3-turbo", "whisper-large-v3"];

    /// <summary>Cleanup and Command Mode candidates, fastest first.</summary>
    public static readonly string[] TextModels = ["qwen/qwen3.8-27b", "openai/gpt-oss-20b", "openai/gpt-oss-120b"];

    public const int WhisperPromptTokenLimit = 224;
    public const double MinimumBilledSeconds = 10.0;

    /// <summary>gpt-oss models take "low"; Qwen takes "none" to turn reasoning off.</summary>
    public static string ReasoningEffort(string model) => model.StartsWith("openai/gpt-oss", StringComparison.Ordinal) ? "low" : "none";

    public const string KeysUrl = "https://console.groq.com/keys";
}

/// <summary>Gemini is optional: the key can be stored and checked, nothing else uses it yet.</summary>
public static class Gemini
{
    public static readonly Uri BaseUrl = new("https://generativelanguage.googleapis.com/v1beta/");
}

public enum ProviderErrorKind { MissingKey, RateLimited, Http, Network, BadResponse }

/// <summary>A provider failure with a message that can be shown to the user as is.</summary>
public sealed class ProviderException : Exception
{
    public ProviderErrorKind Kind { get; }
    public double? RetryAfter { get; }
    public int StatusCode { get; }
    public string Detail { get; }

    ProviderException(ProviderErrorKind kind, string message, double? retryAfter = null, int status = 0, string detail = "")
        : base(message)
    {
        Kind = kind;
        RetryAfter = retryAfter;
        StatusCode = status;
        Detail = detail;
    }

    public static ProviderException MissingKey() => new(ProviderErrorKind.MissingKey, "No Groq key saved. Add it in Settings.");

    public static ProviderException RateLimited(double? retryAfter) =>
        new(ProviderErrorKind.RateLimited, "Groq rate limit reached. Try again in a moment.", retryAfter);

    public static ProviderException Http(int status, string body)
    {
        var message = status switch
        {
            401 or 403 => "Groq rejected the API key. Check it in Settings.",
            _ => $"Groq returned HTTP {status}. {(body.Length > 160 ? body[..160] : body)}".TrimEnd(),
        };
        return new(ProviderErrorKind.Http, message, status: status, detail: body);
    }

    public static ProviderException Network(string message) =>
        new(ProviderErrorKind.Network, $"Can't reach Groq. Check your internet connection. ({message})", detail: message);

    public static ProviderException BadResponse() => new(ProviderErrorKind.BadResponse, "Groq sent a response VoiceFlow could not read.");

    public bool IsBadKey => Kind == ProviderErrorKind.Http && StatusCode is 401 or 403;
}
