using VoiceFlow.Core.Speech;
using VoiceFlow.Core.Text;

namespace VoiceFlow.Core.AI;

public sealed record CleanupRequest(
    string Transcript,
    WritingStyle Style,
    string AppName = "",
    IReadOnlyList<string>? Names = null,
    string BeforeCursor = "",
    IReadOnlyList<string>? Files = null,
    bool TagFiles = false,
    CleanupLevel Level = CleanupLevel.Light,
    /// <summary>Set when the dictation may be Telugu: how to write it.</summary>
    TeluguScript? Telugu = null);

/// <summary>The cleaned text. <see cref="Note"/> says why the AI result was not used, if it wasn't.</summary>
public sealed record CleanupOutcome(string Text, bool UsedAI, string? Note);

/// <summary>AI cleanup with fallbacks: a dictation never comes back empty because of the network or the model.</summary>
public sealed class TextCleaner(GroqChat chat, string model)
{
    public async Task<CleanupOutcome> CleanAsync(CleanupRequest request, CancellationToken cancel = default)
    {
        var raw = request.Transcript.Trim();
        if (raw.Length == 0) return new CleanupOutcome("", false, null);
        if (request.Level == CleanupLevel.None) return new CleanupOutcome(raw, false, null);
        // Telugu always goes through the model: script conversion and recognition fixes need it.
        var hasTelugu = request.Telugu != null && Scripts.ContainsTelugu(raw);
        if (!hasTelugu && CanSkipAI(raw))
            return new CleanupOutcome(Tidy(raw, request.Style, request.BeforeCursor), false, null);
        var user = CleanupPrompt.UserMessage(
            raw, request.Style.PromptName(), request.AppName, request.Names ?? [], request.BeforeCursor, request.Files ?? [],
            request.TagFiles, EnumText.Raw(request.Level), hasTelugu && request.Telugu is { } t ? EnumText.PromptName(t) : null);
        try
        {
            var output = await chat.CompleteAsync(model, CleanupPrompt.System, user, maxTokens: 1_200, cancel: cancel).ConfigureAwait(false);
            if (Problem(output, raw) is { } problem)
                return new CleanupOutcome(Tidy(raw, request.Style, request.BeforeCursor), false, problem);
            return new CleanupOutcome(output, true, null);
        }
        catch (ProviderException e)
        {
            return new CleanupOutcome(Tidy(raw, request.Style, request.BeforeCursor), false, e.Message);
        }
        catch (HttpRequestException e)
        {
            return new CleanupOutcome(Tidy(raw, request.Style, request.BeforeCursor), false, e.Message);
        }
    }

    internal static readonly HashSet<string> Fillers = ["um", "uh", "er", "ah", "hmm", "like", "actually", "scratch", "mean", "no", "wait"];
    internal static readonly string[] SpokenCommands = ["new line", "new paragraph", "comma", "period", "full stop", "question mark"];

    /// <summary>Very short dictations with nothing to fix skip the model (saves latency and rate limit).</summary>
    internal static bool CanSkipAI(string raw)
    {
        var lower = raw.ToLowerInvariant();
        var words = TextUtil.Split(lower, g => !TextUtil.IsLetter(g) && !TextUtil.IsNumber(g) && g != "'");
        if (words.Count > 3) return false;
        if (words.Any(Fillers.Contains)) return false;
        return !SpokenCommands.Any(lower.Contains);
    }

    /// <summary>Local cleanup used when the model is skipped or fails.</summary>
    public static string Tidy(string raw, WritingStyle style, string beforeCursor)
    {
        var text = raw.Trim();
        if (text.Length == 0) return text;
        var midSentence = IsMidSentence(beforeCursor);
        switch (style)
        {
            case WritingStyle.VeryCasual:
            case WritingStyle.Code:
                break;
            default:
                if (!midSentence)
                {
                    var first = TextUtil.Prefix(text, 1);
                    text = first.ToUpperInvariant() + text[first.Length..];
                }
                break;
        }
        return text;
    }

    /// <summary>True when the text before the cursor ends inside a sentence (a letter, digit or comma).</summary>
    public static bool IsMidSentence(string beforeCursor)
    {
        for (int i = beforeCursor.Length - 1; i >= 0; i--)
        {
            var c = beforeCursor[i];
            if (char.IsWhiteSpace(c)) continue;
            return !".!?:;\n".Contains(c);
        }
        return false;
    }

    /// <summary>
    /// Detects outputs that ignored the rules: empty, wrapped in tags, or far longer than the input (the model
    /// answered or added content instead of cleaning).
    /// </summary>
    internal static string? Problem(string output, string raw)
    {
        if (output.Length == 0) return "empty model output";
        if (output.Contains("<transcript>") || output.Contains("</transcript>")) return "model echoed the prompt";
        var inWords = TextUtil.WhitespaceWords(raw).Length;
        var outWords = TextUtil.WhitespaceWords(output).Length;
        if (outWords > inWords * 2 + 12) return "model output much longer than speech";
        return null;
    }
}
