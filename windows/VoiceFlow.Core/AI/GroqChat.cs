using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceFlow.Core.Speech;

namespace VoiceFlow.Core.AI;

/// <summary>Groq chat completions (OpenAI-compatible).</summary>
public class GroqChat
{
    readonly string _apiKey;
    readonly HttpClient _client;
    readonly GroqGate _gate;

    public GroqChat(string apiKey, HttpClient? client = null, GroqGate? gate = null)
    {
        _apiKey = apiKey.Trim();
        _client = client ?? GroqHttp.Shared;
        _gate = gate ?? GroqGate.Shared;
    }

    public virtual async Task<string> CompleteAsync(string model, string system, string user, int maxTokens = 800,
                                                    bool json = false, double timeoutSeconds = 6, GroqPriority priority = GroqPriority.Interactive,
                                                    CancellationToken cancel = default)
    {
        if (_apiKey.Length == 0) throw ProviderException.MissingKey();
        var body = new JsonObject
        {
            ["model"] = model,
            ["temperature"] = 0,
            ["max_completion_tokens"] = maxTokens,
            ["reasoning_effort"] = Groq.ReasoningEffort(model),
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
        };
        if (json) body["response_format"] = new JsonObject { ["type"] = "json_object" };
        var payload = body.ToJsonString();

        var estimate = TokenEstimate.Count(system) + TokenEstimate.Count(user) + maxTokens / 2;
        var data = await _gate.PerformAsync(GroqCost.Chat(model, estimate), priority, () =>
            GroqHttp.SendDetailedAsync(_client, () => new HttpRequestMessage(HttpMethod.Post, new Uri(Groq.BaseUrl, "chat/completions"))
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", _apiKey) },
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            }, TimeSpan.FromSeconds(timeoutSeconds), cancel), cancel).ConfigureAwait(false);

        return ParseCompletion(data);
    }

    internal static string ParseCompletion(byte[] data)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(data); }
        catch (JsonException) { throw ProviderException.BadResponse(); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                throw ProviderException.BadResponse();
            var choice = choices[0];
            if (choice.ValueKind != JsonValueKind.Object || !choice.TryGetProperty("message", out var message)
                || message.ValueKind != JsonValueKind.Object)
                throw ProviderException.BadResponse();
            var finish = choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : "stop";
            if (finish != "stop") throw ProviderException.Http(200, $"finish_reason {finish}");
            var content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "";
            return content.Trim();
        }
    }
}

/// <summary>A rough token count for chat requests: about 4 characters per token for English, about 1 token per character for Telugu and other non-Latin scripts.</summary>
public static class TokenEstimate
{
    public static int Count(string text)
    {
        int ascii = 0, other = 0;
        foreach (var r in text.EnumerateRunes())
        {
            if (r.IsAscii) ascii++;
            else other++;
        }
        return ascii / 4 + other;
    }
}
