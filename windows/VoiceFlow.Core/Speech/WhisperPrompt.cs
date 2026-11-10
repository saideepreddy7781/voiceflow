using VoiceFlow.Core.Text;

namespace VoiceFlow.Core.Speech;

/// <summary>
/// The <c>prompt</c> sent with each Whisper request.
///
/// A prompt is the transcript so far: Whisper imitates its script and spelling. Seeding a forced Telugu or Hindi
/// request with code-mixed sentences made Whisper write English words in English letters (or the speaker's script,
/// per their writing choice) and reduced errors (see docs/LANGUAGE-PIPELINE.md). Seeding an auto-detect request is
/// not safe: English speech came back as Telugu gibberish, so auto-detect requests get only the dictionary terms.
/// </summary>
public sealed record WhisperPrompts(IReadOnlyList<string> Terms, TeluguScript Telugu = TeluguScript.Mixed, HindiScript Hindi = HindiScript.Mixed)
{
    public WhisperPrompts() : this([]) { }

    /// <summary>The prompt for a request forcing <paramref name="language"/>, or null when there is nothing to send.</summary>
    public string? Text(string? language) => WhisperPrompt.Build(language == null ? null : Seed(language), Terms);

    internal string? Seed(string language) => language switch
    {
        "te" => Telugu == TeluguScript.Telugu ? WhisperPrompt.TeluguScriptSeed : WhisperPrompt.TeluguMixedSeed,
        "hi" => Hindi == HindiScript.Devanagari ? WhisperPrompt.HindiScriptSeed : WhisperPrompt.HindiMixedSeed,
        _ => null,
    };
}

public static class WhisperPrompt
{
    /// <summary>Groq rejects prompts over 896 characters (HTTP 400, measured 2026-11-10).</summary>
    public const int MaximumCharacters = 896;
    /// <summary>
    /// Whisper reads only 224 tokens and keeps the end. Telugu and Devanagari run about 1.5 tokens per character,
    /// so a character limit alone isn't enough for those scripts.
    /// </summary>
    internal const double MaximumEstimatedTokens = 200;

    internal const string TeluguMixedSeed = "నేను రేపు office కి వస్తాను, meeting 10 గంటలకి ఉంది. project report ready చేసి పంపిస్తాను. ఈ రోజు client తో call ఉంది, deadline గురించి discuss చేద్దాం.";
    internal const string TeluguScriptSeed = "నేను రేపు ఆఫీస్ కి వస్తాను, మీటింగ్ పది గంటలకి ఉంది. ప్రాజెక్ట్ రిపోర్ట్ రెడీ చేసి పంపిస్తాను. ఈ రోజు క్లయింట్ తో కాల్ ఉంది.";
    internal const string HindiMixedSeed = "मैं कल office आऊँगा, meeting दस बजे है। report ready करके भेज दूँगा। आज client के साथ call है, deadline के बारे में बात करेंगे।";
    internal const string HindiScriptSeed = "मैं कल ऑफिस आऊँगा, मीटिंग दस बजे है। रिपोर्ट तैयार करके भेज दूँगा। आज क्लाइंट के साथ कॉल है।";

    /// <summary>Dictionary terms first, seed last, because Whisper keeps the end of a long prompt. Terms are dropped from the end of the list when there isn't room.</summary>
    internal static string? Build(string? seed, IEnumerable<string> terms)
    {
        var seen = new HashSet<string>();
        var kept = new List<string>();
        var tokens = EstimatedTokens(seed ?? "");
        var characters = (seed == null ? 0 : TextUtil.GraphemeCount(seed)) + 4;
        foreach (var term in terms)
        {
            if (term.Length == 0 || !seen.Add(term.ToLowerInvariant())) continue;
            var cost = EstimatedTokens(term) + 1;
            var length = TextUtil.GraphemeCount(term);
            if (tokens + cost > MaximumEstimatedTokens || characters + length + 2 > MaximumCharacters) break;
            kept.Add(term);
            tokens += cost;
            characters += length + 2;
        }
        var glossary = string.Join(", ", kept);
        if (glossary.Length == 0) return seed;
        return seed == null ? glossary : glossary + ". " + seed;
    }

    internal static double EstimatedTokens(string text)
    {
        double ascii = 0, other = 0;
        foreach (var r in text.EnumerateRunes())
        {
            if (r.IsAscii) ascii++;
            else other++;
        }
        return ascii / 3.5 + other * 1.5;
    }
}
