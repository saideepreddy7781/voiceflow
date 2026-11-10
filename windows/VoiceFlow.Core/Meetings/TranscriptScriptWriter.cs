using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceFlow.Core.AI;
using VoiceFlow.Core.Speech;
using VoiceFlow.Core.Text;

namespace VoiceFlow.Core.Meetings;

/// <summary>
/// Rewrites meeting transcript lines in the chosen Telugu or Hindi writing, through Groq's free chat model. Only the
/// writing changes: a batch whose reply doesn't line up with its input keeps the words as heard.
/// </summary>
public sealed class TranscriptScriptWriter(GroqChat chat, string model, Func<TimeSpan, Task>? delay = null)
{
    public sealed record Result(List<TranscriptLine> Lines, int RewrittenLines, string? Note);

    /// <summary>Input per request, in estimated tokens. The reply is about as long as the input.</summary>
    internal const int BatchTokens = 1_500;
    /// <summary>A rewritten line this much longer than the original has gained words.</summary>
    internal const double MaximumGrowth = 1.6;

    readonly Func<TimeSpan, Task> _delay = delay ?? (t => Task.Delay(t));

    /// <summary>Whether a line needs the model at all under these choices.</summary>
    public static bool NeedsRewrite(string text, TeluguScript? telugu, HindiScript? hindi)
    {
        if (telugu is { } t && Scripts.Share(Script.Telugu, text) > 0)
            return t != TeluguScript.Telugu || Scripts.Share(Script.Latin, text) > 0;   // pure Telugu script is already what "telugu" asks for
        if (hindi is { } h && Scripts.Share(Script.Devanagari, text) > 0)
            return h != HindiScript.Devanagari || Scripts.Share(Script.Latin, text) > 0;
        return false;
    }

    /// <summary>Indices of lines to rewrite, split into batches that fit one request.</summary>
    internal static List<List<int>> Batches(IReadOnlyList<TranscriptLine> lines, TeluguScript? telugu, HindiScript? hindi)
    {
        var output = new List<List<int>>();
        var current = new List<int>();
        var tokens = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            if (!NeedsRewrite(lines[i].Text, telugu, hindi)) continue;
            var n = TokenEstimate.Count(lines[i].Text) + 4;
            if (tokens + n > BatchTokens && current.Count > 0)
            {
                output.Add(current);
                current = [];
                tokens = 0;
            }
            current.Add(i);
            tokens += n;
        }
        if (current.Count > 0) output.Add(current);
        return output;
    }

    /// <summary>Builds the user message: one JSON object with sorted keys, so transcript text can't be mistaken for instructions.</summary>
    internal static string UserMessage(IReadOnlyList<string> texts, TeluguScript? telugu, HindiScript? hindi)
    {
        var o = new JsonObject
        {
            ["hindi"] = hindi is { } h ? EnumText.PromptName(h) : null,
            ["lines"] = new JsonArray(texts.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
            ["telugu"] = telugu is { } t ? EnumText.PromptName(t) : null,
        };
        return o.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// <summary>The model's lines, or null if the reply can't be trusted: wrong count, empty lines, or lines that grew.</summary>
    internal static List<string>? Parse(string json, IReadOnlyList<string> expected)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("lines", out var array)
                || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() != expected.Count) return null;
            var lines = new List<string>();
            foreach (var e in array.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.String) return null;
                lines.Add(e.GetString()!);
            }
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Trim().Length == 0) return null;
                if (TextUtil.WhitespaceWords(lines[i]).Length > TextUtil.WhitespaceWords(expected[i]).Length * MaximumGrowth + 2) return null;
            }
            return lines;
        }
        catch (JsonException) { return null; }
    }

    public async Task<Result> RewriteAsync(IReadOnlyList<TranscriptLine> lines, TeluguScript? telugu, HindiScript? hindi, CancellationToken cancel = default)
    {
        var output = lines.ToList();
        var rewritten = 0;
        string? note = null;
        foreach (var batch in Batches(lines, telugu, hindi))
        {
            var texts = batch.Select(i => lines[i].Text).ToList();
            try
            {
                var reply = await RequestRewriteAsync(texts, telugu, hindi, cancel).ConfigureAwait(false);
                if (Parse(reply, texts) is not { } converted)
                {
                    note = "Some lines stayed as heard because the rewrite didn't match them.";
                    continue;
                }
                for (int k = 0; k < batch.Count; k++)
                {
                    if (converted[k] == lines[batch[k]].Text) continue;
                    output[batch[k]] = output[batch[k]] with { Text = converted[k] };
                    rewritten++;
                }
            }
            catch (ProviderException e) when (e.Kind == ProviderErrorKind.DailyLimit)
            {
                note = $"{e.Message} The rest of the transcript stays as heard.";
                break;
            }
            catch (ProviderException)
            {
                note = "Some lines stayed as heard because Groq didn't respond.";
            }
        }
        return new Result(output, rewritten, note);
    }

    async Task<string> RequestRewriteAsync(IReadOnlyList<string> texts, TeluguScript? telugu, HindiScript? hindi, CancellationToken cancel)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                return await chat.CompleteAsync(model, CleanupPrompt.MeetingScript, UserMessage(texts, telugu, hindi), maxTokens: 3_000, json: true,
                                                timeoutSeconds: 60, priority: GroqPriority.Background, cancel: cancel).ConfigureAwait(false);
            }
            catch (ProviderException e) when (e.Kind == ProviderErrorKind.RateLimited && attempt < 4 && (e.RetryAfter ?? 0) <= MeetingNotesWriter.MaxRateLimitWait)
            {
                attempt++;
                await _delay(TimeSpan.FromSeconds((e.RetryAfter ?? 8) + 1)).ConfigureAwait(false);
            }
        }
    }
}
