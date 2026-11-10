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

    /// <summary>Share of the letters in <paramref name="text"/> written in <paramref name="script"/> (0 for text without letters).</summary>
    public static double Share(Script script, string text)
    {
        int total = 0, hits = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Of(rune.Value) is not { } s) continue;
            total++;
            if (s == script) hits++;
        }
        return total == 0 ? 0 : (double)hits / total;
    }

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

    public static bool ContainsDevanagari(string text)
    {
        foreach (var r in text.EnumerateRunes()) if (r.Value >= 0x0900 && r.Value <= 0x097F) return true;
        return false;
    }
}

/// <summary>What the resolver may assume about who is speaking.</summary>
public enum PlanSpeaker
{
    /// <summary>The person using the app: dictation, and your microphone in a meeting. They speak the listed languages.</summary>
    User,
    /// <summary>The call: other people, who may speak anything.</summary>
    Anyone,
}

public sealed record LanguagePlan(IReadOnlyList<string> Languages, PlanSpeaker Speaker = PlanSpeaker.User)
{
    public bool OpenWorld => Speaker == PlanSpeaker.Anyone;

    /// <summary>
    /// The language to force on the first request, or null to auto-detect. Forcing happens only when one language
    /// is listed and the speaker is the user; with several languages, or other people, Whisper detects it.
    /// </summary>
    public string? ForcedFirst => Languages.Count == 1 && !OpenWorld ? Languages[0] : null;

    /// <summary>Listed languages other than English, worth a forced second opinion.</summary>
    public IReadOnlyList<string> OpinionCandidates => Languages.Where(l => l != "en").ToList();
}

/// <summary>One forced-language transcription, for comparing with auto-detect.</summary>
public sealed record LanguageOpinion(string Language, DetailedTranscript Transcript);

/// <summary>
/// Picks the right transcription when several languages are in play.
///
/// Whisper's auto-detect mislabels Indian languages: in testing it heard Telugu as Hindi or Tamil (writing
/// Devanagari or Tamil script, at high confidence) and spelled it by sound in English letters, labelled
/// "English". Forcing the language instead ruins English speech. So the auto result is checked with the rules
/// below, and forced opinions are requested only when it looks wrong.
/// </summary>
public static class LanguageResolver
{
    /// <summary>How much more confident (average log-probability) a forced result must be to replace an accepted auto result.</summary>
    public const double Margin = 0.1;
    /// <summary>Below this, auto-detect is doubtful. English speech scored -0.06 to -0.22 in testing; -0.45 leaves room for accents.</summary>
    public const double LowConfidence = -0.45;
    /// <summary>A forced result below this is assumed to be the wrong language or unusable audio.</summary>
    public const double ForcedFloor = -0.8;
    /// <summary>Most forced opinions per piece of audio. Each one costs the same audio budget as the first request.</summary>
    public const int MaxOpinions = 2;
    /// <summary>
    /// A sticky-language result below this gets the full check. Real Hindi forced to Telugu scored -0.27 to -0.31 in
    /// testing and correct Telugu -0.05 to -0.12, so someone switching language can't pass as the sticky one.
    /// </summary>
    public const double StickyFloor = -0.2;

    /// <summary>Languages Whisper confuses with each other. Telugu heard as Hindi is common; Telugu heard as Spanish isn't.</summary>
    static readonly HashSet<string> Indic = ["te", "hi", "ta", "kn", "ml", "mr", "bn", "gu", "pa", "ur", "ne", "as", "or", "si", "sa"];

    static bool Confusable(string a, string b) => Indic.Contains(a) && Indic.Contains(b);

    internal enum DoubtKind { Disguised, Unlisted, LowConfidence }

    /// <summary>Why an auto-detect result can't be taken at face value. <c>Real</c> applies to <c>Unlisted</c>.</summary>
    internal readonly record struct Doubt(DoubtKind Kind, bool? Real = null);

    internal static Doubt? DoubtAbout(DetailedTranscript auto, LanguagePlan plan)
    {
        if (auto.Text.Length == 0) return null;
        var allowed = new HashSet<Script>();
        foreach (var l in plan.Languages) allowed.UnionWith(Scripts.For(l));
        var scriptOk = Scripts.Dominant(auto.Text) is not { } s || allowed.Contains(s);
        var listed = auto.Language is { } lang && plan.Languages.Contains(lang);
        var confidence = auto.Confidence ?? 0;
        if (!scriptOk || !listed)
        {
            // Short text with no language label and nothing to compare: other people can't be judged at all.
            if (auto.Language is not { } language) return plan.OpenWorld ? null : new Doubt(DoubtKind.Unlisted);
            var real = LanguageEvidence.Reads(language, auto.Text);
            if (plan.OpenWorld)
            {
                // Someone else on the call may really speak this. Only a lookalike of a listed language is suspect.
                if (real == true) return null;
                if (!plan.Languages.Any(l => Confusable(l, language)) && confidence >= LowConfidence) return null;
            }
            return new Doubt(DoubtKind.Unlisted, real);
        }
        // English full of technical terms also fails the reading check, but it isn't spelled by sound.
        if (auto.Language is { } label && LanguageEvidence.Reads(label, auto.Text) == false && plan.OpinionCandidates.Any(l => l != label)
            && (label != "en" || LanguageEvidence.LooksSoundSpelled(auto.Text)))
            return new Doubt(DoubtKind.Disguised);
        return confidence < LowConfidence ? new Doubt(DoubtKind.LowConfidence) : null;
    }

    public static bool NeedsSecondOpinion(DetailedTranscript auto, LanguagePlan plan) => OpinionLanguages(auto, plan).Count > 0;

    /// <summary>Which languages to force for a second opinion on this auto result, most likely first. Empty means accept it.</summary>
    public static IReadOnlyList<string> OpinionLanguages(DetailedTranscript auto, LanguagePlan plan)
    {
        if (DoubtAbout(auto, plan) == null) return [];
        var others = plan.OpinionCandidates.Where(l => l != auto.Language || Scripts.Dominant(auto.Text) == Script.Latin);
        var detected = auto.Language;
        return others.OrderByDescending(l => detected != null && Confusable(detected, l)).Take(MaxOpinions).ToList();
    }

    /// <summary>Whether a transcript looks like Whisper looping rather than speech.</summary>
    public static bool IsLooping(DetailedTranscript t) => WhisperSegmentFilter.IsLooping(t.Segments);

    /// <summary>Picks between auto-detect and the forced opinions.</summary>
    public static DetailedTranscript Choose(DetailedTranscript auto, IReadOnlyList<LanguageOpinion> opinions, LanguagePlan plan)
    {
        // A forced opinion that doesn't read as its own language is Whisper guessing, however confident it sounds.
        var viable = opinions.Where(o => o.Transcript.Text.Length > 0 && !IsLooping(o.Transcript)
                                         && LanguageEvidence.Reads(o.Language, o.Transcript.Text) != false).ToList();
        var best = viable.MaxBy(o => o.Transcript.Confidence ?? -10);
        if (auto.Text.Length == 0) return best?.Transcript ?? auto;
        if (best == null) return auto;
        if (IsLooping(auto)) return best.Transcript;
        var a = auto.Confidence ?? 0;
        var f = best.Transcript.Confidence ?? ForcedFloor;
        if (f < ForcedFloor) return auto;
        var doubt = DoubtAbout(auto, plan);
        if (doubt == null)
        {
            // Same language, and the forced request had the seeded prompt: its spelling is the better one.
            if (auto.Language == best.Language) return f >= a ? best.Transcript : auto;
            return f > a + Margin ? best.Transcript : auto;
        }
        switch (doubt.Value.Kind)
        {
            case DoubtKind.Disguised:
                // The words already say auto-detect is wrong; only a much worse forced result keeps it.
                return f >= a - 0.5 ? best.Transcript : auto;
            case DoubtKind.Unlisted:
                // Real Hindi, say, must be beaten outright.
                var tolerance = doubt.Value.Real == true ? 0 : plan.OpenWorld ? 0.12 : 0.25;
                return f >= a - tolerance ? best.Transcript : auto;
            default:
                return f > a + Margin ? best.Transcript : auto;
        }
    }

    /// <summary>Accepts a forced result without a second opinion (used for sticky-language chunks).</summary>
    public static bool ForcedResultAcceptable(DetailedTranscript t) =>
        t.Text.Length > 0 && !IsLooping(t) && (t.Confidence ?? 0) >= StickyFloor;
}

/// <summary>
/// Remembers a call's or mic's language so long stretches in Telugu don't pay for two uploads per chunk.
/// After <see cref="StickAfter"/> chunks that needed a forced opinion to come out right, the next chunks go straight
/// to the forced language. Every <see cref="RecheckEvery"/>th chunk still runs auto-detect, so a meeting that moves
/// to English follows.
/// </summary>
public sealed class StreamLanguageTracker
{
    public const int StickAfter = 2;
    public const int RecheckEvery = 5;

    string? _streakLanguage;
    int _streak;
    int _sinceRecheck;

    public string? Sticky { get; private set; }

    /// <summary>The language to force on the next chunk, or null to run auto-detect.</summary>
    public string? NextHint()
    {
        if (Sticky == null) return null;
        _sinceRecheck++;
        if (_sinceRecheck >= RecheckEvery)
        {
            _sinceRecheck = 0;
            return null;
        }
        return Sticky;
    }

    /// <summary>Records how a chunk turned out. <paramref name="viaOpinion"/>: a forced language won (or was used directly).</summary>
    public void Observe(string? language, bool viaOpinion, IReadOnlyCollection<string> listed)
    {
        if (!viaOpinion || language == null || language == "en" || !listed.Contains(language))
        {
            if (!viaOpinion && language is "en" or null)
            {
                Sticky = null;
                _streak = 0;
                _streakLanguage = null;
            }
            return;
        }
        if (_streakLanguage == language) _streak++;
        else
        {
            _streakLanguage = language;
            _streak = 1;
        }
        if (_streak >= StickAfter) Sticky = language;
    }
}
