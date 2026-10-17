using System.Net.Http.Headers;
using System.Text.Json;

namespace VoiceFlow.Core;

/// <summary>The API keys VoiceFlow can hold.</summary>
public enum SecretKey { Groq, Gemini }

public enum KeyCheckStatus { Ok, InvalidKey, Failed }

public sealed record KeyCheckResult(KeyCheckStatus Status, string Detail)
{
    public string Summary => Status switch
    {
        KeyCheckStatus.Ok => $"Works. {Detail}",
        KeyCheckStatus.InvalidKey => "The provider rejected this key.",
        _ => $"Could not check: {Detail}",
    };
}

/// <summary>Checks an API key with a cheap, read-only request (each provider's model list).</summary>
public sealed class KeyTester(HttpClient? client = null)
{
    readonly HttpClient _client = client ?? GroqHttp.Shared;

    public async Task<KeyCheckResult> CheckAsync(SecretKey key, string value, CancellationToken cancel = default)
    {
        value = value.Trim();
        if (value.Length == 0) return new(KeyCheckStatus.Failed, "no key entered");

        HttpRequestMessage Build()
        {
            var request = key == SecretKey.Groq
                ? new HttpRequestMessage(HttpMethod.Get, new Uri(Groq.BaseUrl, "models"))
                : new HttpRequestMessage(HttpMethod.Get, new Uri(Gemini.BaseUrl, "models"));
            if (key == SecretKey.Groq) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", value);
            else request.Headers.Add("x-goog-api-key", value);
            return request;
        }

        try
        {
            var data = await GroqHttp.SendAsync(_client, Build, TimeSpan.FromSeconds(10), cancel).ConfigureAwait(false);
            return new(KeyCheckStatus.Ok, DescribeModels(data, key));
        }
        catch (ProviderException e)
        {
            return e.Kind switch
            {
                // Gemini answers 400 API_KEY_INVALID for a bad key; Groq answers 401.
                ProviderErrorKind.Http when e.StatusCode is 400 or 401 or 403 => new(KeyCheckStatus.InvalidKey, ""),
                ProviderErrorKind.RateLimited => new(KeyCheckStatus.Failed, "rate limited, try again in a minute"),
                ProviderErrorKind.Http => new(KeyCheckStatus.Failed, $"HTTP {e.StatusCode}"),
                ProviderErrorKind.Network => new(KeyCheckStatus.Failed, e.Detail),
                _ => new(KeyCheckStatus.Failed, e.Message),
            };
        }
    }

    internal static string DescribeModels(byte[] data, SecretKey key)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (key == SecretKey.Groq)
            {
                var ids = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array
                    ? d.EnumerateArray().Select(e => e.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null)
                        .Where(s => s != null).ToList()
                    : [];
                var missing = new[] { Groq.TranscriptionModels[0], Groq.TextModels[0] }.Where(m => !ids.Contains(m)).ToList();
                return missing.Count == 0
                    ? $"{ids.Count} models available, including the defaults."
                    : $"{ids.Count} models available. Missing: {string.Join(", ", missing)}.";
            }
            var count = root.TryGetProperty("models", out var m) && m.ValueKind == JsonValueKind.Array ? m.GetArrayLength() : 0;
            return $"{count} models available.";
        }
        catch (JsonException)
        {
            return "Model list could not be read.";
        }
    }
}
