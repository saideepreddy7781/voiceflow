using System.Text;

namespace VoiceFlow.Core.Speech;

/// <summary>Writing systems, for checking that a transcript is in a script the speaker uses.</summary>
public enum Script
{
    Latin, Telugu, Devanagari, Tamil, Kannada, Malayalam, Bengali, Gujarati, Gurmukhi, Arabic, Cyrillic, Greek,
    Hebrew, Thai, Han, Kana, Hangul, Other,
}

public static class Scripts
{
    internal static Script? Of(int v) => v switch
    {
        >= 0x41 and <= 0x5A or >= 0x61 and <= 0x7A or >= 0xC0 and <= 0x24F => Script.Latin,
        >= 0x0C00 and <= 0x0C7F => Script.Telugu,
        >= 0x0900 and <= 0x097F => Script.Devanagari,
        >= 0x0B80 and <= 0x0BFF => Script.Tamil,
        >= 0x0C80 and <= 0x0CFF => Script.Kannada,
        >= 0x0D00 and <= 0x0D7F => Script.Malayalam,
        >= 0x0980 and <= 0x09FF => Script.Bengali,
        >= 0x0A80 and <= 0x0AFF => Script.Gujarati,
        >= 0x0A00 and <= 0x0A7F => Script.Gurmukhi,
        >= 0x0600 and <= 0x06FF or >= 0x0750 and <= 0x077F => Script.Arabic,
        >= 0x0400 and <= 0x04FF => Script.Cyrillic,
        >= 0x0370 and <= 0x03FF => Script.Greek,
        >= 0x0590 and <= 0x05FF => Script.Hebrew,
        >= 0x0E00 and <= 0x0E7F => Script.Thai,
        >= 0x4E00 and <= 0x9FFF => Script.Han,
        >= 0x3040 and <= 0x30FF => Script.Kana,
        >= 0xAC00 and <= 0xD7AF => Script.Hangul,
        _ => null,   // digits, punctuation, spaces
    };

    /// <summary>The script most letters in <paramref name="text"/> use, or null for text without letters.</summary>
    public static Script? Dominant(string text)
    {
        var counts = new Dictionary<Script, int>();
        foreach (var rune in text.EnumerateRunes())
            if (Of(rune.Value) is { } s) counts[s] = counts.GetValueOrDefault(s) + 1;
        if (counts.Count == 0) return null;
        return counts.MaxBy(p => p.Value).Key;
    }

    /// <summary>Scripts a language is normally written in.</summary>
    public static HashSet<Script> For(string language) => language switch
    {
        "te" => [Script.Telugu],
        "hi" or "mr" or "ne" or "sa" => [Script.Devanagari],
        "ta" => [Script.Tamil],
        "kn" => [Script.Kannada],
        "ml" => [Script.Malayalam],
        "bn" or "as" => [Script.Bengali],
        "gu" => [Script.Gujarati],
        "pa" => [Script.Gurmukhi],
        "ur" or "ar" or "fa" or "ps" => [Script.Arabic],
        "ru" or "uk" or "bg" or "sr" or "kk" => [Script.Cyrillic],
        "el" => [Script.Greek],
        "he" or "yi" => [Script.Hebrew],
        "th" => [Script.Thai],
        "zh" => [Script.Han],
        "ja" => [Script.Kana, Script.Han],
        "ko" => [Script.Hangul],
        _ => [Script.Latin],
    };

    public static bool ContainsTelugu(string text)
    {
        foreach (var r in text.EnumerateRunes()) if (r.Value >= 0x0C00 && r.Value <= 0x0C7F) return true;
        return false;
    }
}

/// <summary>
/// Picks the right transcription when you speak more than one language.
///
/// Whisper's auto-detect often mislabels Indian languages: it heard Telugu as Hindi or Tamil and, with some
/// voices, as English spelled phonetically. Forcing Telugu instead ruins English speech. So both are run
/// in parallel and compared: a result in a script or language you don't speak is rejected; when both are
/// plausible, the one Whisper is more confident in wins, by a margin.
/// </summary>
public static class LanguageResolver
{
    /// <summary>How much more confident (average log-probability) the forced language must be to replace auto-detect.</summary>
    public const double Margin = 0.1;

    static HashSet<Script> Allowed(IEnumerable<string> languages)
    {
        var set = new HashSet<Script>();
        foreach (var l in languages) set.UnionWith(Scripts.For(l));
        return set;
    }

    /// <summary>
    /// Auto-detect is doubtful: wrong script or language, unknown language, or low confidence. English
    /// speech scored −0.06 to −0.22 in testing; −0.45 leaves room for accents while still catching garbled results.
    /// </summary>
    public static bool NeedsSecondOpinion(DetailedTranscript auto, IReadOnlyCollection<string> languages)
    {
        var allowed = Allowed(languages);
        if (Scripts.Dominant(auto.Text) is { } script && !allowed.Contains(script)) return true;
        if (auto.Language is null || !languages.Contains(auto.Language)) return true;
        return (auto.Confidence ?? 0) < -0.45;
    }

    public static DetailedTranscript Choose(DetailedTranscript auto, DetailedTranscript? forced, string forcedLanguage,
                                            IReadOnlyCollection<string> languages)
    {
        if (forced is null) return auto;
        if (auto.Text.Length == 0) return forced;
        if (forced.Text.Length == 0) return auto;
        var allowed = Allowed(languages);
        var autoScriptOk = Scripts.Dominant(auto.Text) is not { } s || allowed.Contains(s);
        var autoLanguageOk = auto.Language is { } l && languages.Contains(l);
        if (!autoScriptOk || !autoLanguageOk) return forced;
        if (auto.Language == forcedLanguage) return auto;
        if (auto.Confidence is not { } a || forced.Confidence is not { } f) return auto;
        return f > a + Margin ? forced : auto;
    }
}
