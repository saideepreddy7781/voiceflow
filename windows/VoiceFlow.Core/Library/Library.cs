using System.Text.RegularExpressions;
using VoiceFlow.Core.Text;

namespace VoiceFlow.Core.Library;

// MARK: Dictionary

public enum DictionarySource { Manual, AutoLearned }

public sealed class DictionaryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>The word as it should be typed (the correct spelling).</summary>
    public string Term { get; set; } = "";
    /// <summary>For corrections: what the speech recognizer types instead. Null for plain words.</summary>
    public string? Misspelling { get; set; }
    public DictionarySource Source { get; set; }
    public DateTimeOffset Added { get; set; } = DateTimeOffset.Now;

    public DictionaryEntry() { }

    public DictionaryEntry(string term, string? misspelling = null, DictionarySource source = DictionarySource.Manual)
    {
        Term = term;
        Misspelling = misspelling;
        Source = source;
    }

    public const int MaxLength = 60;

    /// <summary>Validation used by the Dictionary page. Returns an error message, or null when valid.</summary>
    public static string? Validate(string term, string? misspelling, IEnumerable<DictionaryEntry> existing)
    {
        var t = term.Trim();
        var m = misspelling?.Trim();
        if (t.Length == 0 || t.All(c => char.IsPunctuation(c) || char.IsSymbol(c))) return "Word cannot be empty.";
        if (TextUtil.GraphemeCount(t) > MaxLength || (m != null && TextUtil.GraphemeCount(m) > MaxLength))
            return $"Entries can be up to {MaxLength} characters.";
        if (m != null && m.Length == 0) return "Enter the misspelling to correct.";
        if (existing.Any(e => string.Equals(e.Term, t, StringComparison.OrdinalIgnoreCase)
                              && string.Equals(e.Misspelling?.ToLowerInvariant(), m?.ToLowerInvariant(), StringComparison.Ordinal)))
            return $"“{t}” is already in your dictionary.";
        return null;
    }
}

/// <summary>Applies "Correct a misspelling" entries: whole-word, case-insensitive, typed with the saved casing.</summary>
public static class DictionaryCorrector
{
    public static (string Text, int Fixes) Apply(string text, IEnumerable<DictionaryEntry> entries)
    {
        var result = text;
        int fixes = 0;
        foreach (var entry in entries.OrderByDescending(e => (e.Misspelling ?? "").Length))
        {
            var wrong = entry.Misspelling?.Trim(' ', '\t');
            if (string.IsNullOrEmpty(wrong)) continue;
            var words = wrong.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape);
            var pattern = @"(?<![\p{L}\p{N}])" + string.Join(@"\s+", words) + @"(?![\p{L}\p{N}])";
            var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            var count = regex.Count(result);
            if (count == 0) continue;
            fixes += count;
            var term = entry.Term;
            result = regex.Replace(result, _ => term);
        }
        return (result, fixes);
    }
}

// MARK: Snippets

public sealed class Snippet
{
    public const int MaxTrigger = 60;
    public const int MaxExpansion = 4_000;
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>What you say, e.g. "my address".</summary>
    public string Trigger { get; set; } = "";
    /// <summary>What gets typed.</summary>
    public string Expansion { get; set; } = "";

    public Snippet() { }

    public Snippet(string trigger, string expansion)
    {
        Trigger = trigger;
        Expansion = expansion;
    }
}

public static class SnippetMatcher
{
    internal static string Normalize(string s) =>
        string.Join(" ", TextUtil.Split(s.ToLowerInvariant(), g => !TextUtil.IsLetter(g) && !TextUtil.IsNumber(g)));

    /// <summary>The snippet when the whole dictation is just its trigger.</summary>
    public static Snippet? ExactMatch(string transcript, IEnumerable<Snippet> snippets)
    {
        var spoken = Normalize(transcript);
        if (spoken.Length == 0) return null;
        return snippets.FirstOrDefault(s => Normalize(s.Trigger) == spoken);
    }

    /// <summary>Replaces triggers that appear inside longer text (case and punctuation between words ignored).</summary>
    public static string ExpandInline(string text, IEnumerable<Snippet> snippets) => ExpandInlineCounting(text, snippets).Text;

    public static (string Text, int Count) ExpandInlineCounting(string text, IEnumerable<Snippet> snippets)
    {
        var result = text;
        int count = 0;
        foreach (var snippet in snippets.OrderByDescending(s => TextUtil.GraphemeCount(s.Trigger)))
        {
            var words = Normalize(snippet.Trigger).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape).ToArray();
            if (words.Length == 0) continue;
            var pattern = @"\b" + string.Join(@"[\s,.;:!?-]+", words) + @"\b";
            var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            var expansion = snippet.Expansion;
            count += regex.Count(result);
            // An evaluator, not a replacement template: "$1" in an expansion is typed as is.
            result = regex.Replace(result, _ => expansion);
        }
        return (result, count);
    }

    /// <summary>Validation used by the Snippets editor. Returns an error message, or null when valid.</summary>
    public static string? Validate(string trigger, string expansion, IEnumerable<Snippet> existing, Guid? editing = null)
    {
        var t = trigger.Trim();
        if (t.Length == 0 || expansion.Trim().Length == 0) return "Please enter a snippet and its expansion.";
        if (Normalize(t).Length == 0) return "A snippet needs at least one letter or number.";
        if (TextUtil.GraphemeCount(t) > Snippet.MaxTrigger) return $"Snippets can be up to {Snippet.MaxTrigger} characters.";
        if (TextUtil.GraphemeCount(expansion) > Snippet.MaxExpansion) return "This expansion is too long. Please shorten it and try again.";
        if (existing.Any(s => s.Id != editing && Normalize(s.Trigger) == Normalize(t)))
            return $"“{t}” already exists in your snippets.";
        return null;
    }
}

// MARK: History

public enum HistoryKind { Dictation, Command }

public sealed class HistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset Date { get; set; } = DateTimeOffset.Now;
    public HistoryKind Kind { get; set; }
    public string AppName { get; set; } = "";
    public string Raw { get; set; } = "";
    public string Final { get; set; } = "";
    public double AudioSeconds { get; set; }
    public int LatencyMs { get; set; }
    /// <summary>"inserted", "pasted", "copied", "search", "answer", "failed".</summary>
    public string Outcome { get; set; } = "";
    public UsageCategory? Usage { get; set; }
    public int? WordsCorrected { get; set; }
    public int? DictionaryFixes { get; set; }
    public bool? Flagged { get; set; }

    public HistoryEntry() { }

    public HistoryEntry(HistoryKind kind, string appName, string raw, string final, double audioSeconds, int latencyMs, string outcome)
    {
        Kind = kind;
        AppName = appName;
        Raw = raw;
        Final = final;
        AudioSeconds = audioSeconds;
        LatencyMs = latencyMs;
        Outcome = outcome;
    }

    public int WordCount => TextUtil.WhitespaceWords(Final).Length;

    static List<string> Words(string s) =>
        TextUtil.Split(s.ToLowerInvariant(), g => !TextUtil.IsLetter(g) && !TextUtil.IsNumber(g) && g != "'");

    /// <summary>Words in what was said that did not survive into the typed text (fillers, corrections, fixes).</summary>
    public static int WordsChanged(string raw, string final)
    {
        var remaining = new Dictionary<string, int>();
        foreach (var w in Words(final)) remaining[w] = remaining.GetValueOrDefault(w) + 1;
        int changed = 0;
        foreach (var w in Words(raw))
        {
            if (remaining.TryGetValue(w, out var n) && n > 0) remaining[w] = n - 1;
            else changed++;
        }
        return changed;
    }
}

public sealed class UsageStats
{
    public int TotalWords { get; set; }
    public int WordsThisWeek { get; set; }
    public int Dictations { get; set; }
    /// <summary>Words per minute of speaking time.</summary>
    public int WordsPerMinute { get; set; }
    /// <summary>Consecutive days with at least one dictation, ending today.</summary>
    public int DayStreak { get; set; }
    public List<(string App, int Words)> TopApps { get; set; } = [];
    public int LongestStreak { get; set; }
    public int WordsCorrected { get; set; }
    public int DictionaryFixes { get; set; }
    public List<(UsageCategory Category, int Words)> ByCategory { get; set; } = [];
    /// <summary>Words per calendar day (local date).</summary>
    public Dictionary<DateOnly, int> DailyWords { get; set; } = [];

    public static UsageStats Compute(IEnumerable<HistoryEntry> entries, DateTimeOffset? now = null, TimeZoneInfo? zone = null)
    {
        var tz = zone ?? TimeZoneInfo.Local;
        var current = now ?? DateTimeOffset.Now;
        DateOnly Day(DateTimeOffset d) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(d, tz).DateTime);

        var s = new UsageStats();
        var weekAgo = current.AddDays(-7);
        double seconds = 0;
        var apps = new Dictionary<string, int>();
        var categories = new Dictionary<UsageCategory, int>();
        foreach (var e in entries.Where(e => e.Outcome != "failed"))
        {
            var words = e.WordCount;
            s.TotalWords += words;
            s.Dictations += 1;
            if (e.Date >= weekAgo) s.WordsThisWeek += words;
            seconds += e.AudioSeconds;
            apps[e.AppName] = apps.GetValueOrDefault(e.AppName) + words;
            s.WordsCorrected += e.WordsCorrected ?? 0;
            s.DictionaryFixes += e.DictionaryFixes ?? 0;
            var cat = e.Usage ?? UsageCategory.Other;
            categories[cat] = categories.GetValueOrDefault(cat) + words;
            var day = Day(e.Date);
            s.DailyWords[day] = s.DailyWords.GetValueOrDefault(day) + words;
        }
        s.ByCategory = categories.Where(p => p.Value > 0).OrderByDescending(p => p.Value).Select(p => (p.Key, p.Value)).ToList();
        int run = 0;
        DateOnly? previous = null;
        foreach (var day in s.DailyWords.Keys.OrderBy(d => d))
        {
            run = previous is { } p && p.AddDays(1) == day ? run + 1 : 1;
            s.LongestStreak = Math.Max(s.LongestStreak, run);
            previous = day;
        }
        s.WordsPerMinute = seconds > 0 ? (int)(s.TotalWords / (seconds / 60)) : 0;
        s.TopApps = apps.OrderByDescending(p => p.Value).Take(5).Select(p => (p.Key, p.Value)).ToList();
        var d0 = Day(current);
        while (s.DailyWords.ContainsKey(d0))
        {
            s.DayStreak += 1;
            d0 = d0.AddDays(-1);
        }
        return s;
    }
}

public static class HistoryRetention
{
    /// <summary>Drops entries older than the retention setting (-1 keeps everything, 0 keeps nothing).</summary>
    public static List<HistoryEntry> Apply(IEnumerable<HistoryEntry> entries, int days, DateTimeOffset? now = null)
    {
        if (days < 0) return entries.ToList();
        if (days == 0) return [];
        var cutoff = (now ?? DateTimeOffset.Now).AddDays(-days);
        return entries.Where(e => e.Date >= cutoff).ToList();
    }
}

// MARK: Auto-learn

/// <summary>
/// Learns spellings from your edits: a word you replaced with a similar-looking one that looks like a name
/// or term becomes a dictionary entry. (The Windows app does not read fields back yet, so this is used
/// only where the edited text is known.)
/// </summary>
public static class AutoLearner
{
    public static List<string> LearnedTerms(string inserted, string fieldBefore, string fieldAfter, ISet<string> known)
    {
        var insertedWords = Tokens(inserted);
        var afterWords = Tokens(fieldAfter);
        if (insertedWords.Count == 0 || afterWords.Count == 0) return [];
        var beforeWords = new HashSet<string>(Tokens(fieldBefore));
        var afterSet = new HashSet<string>(afterWords);

        var learned = new List<string>();
        foreach (var word in insertedWords.Where(w => !afterSet.Contains(w)))
        {
            var candidates = afterWords.Where(c => !insertedWords.Contains(c) && !beforeWords.Contains(c)).ToList();
            if (candidates.Count == 0) continue;
            // Like Swift's max(by:): the first of equal maxima wins.
            string best = candidates[0];
            double bestScore = Similarity(word, best);
            foreach (var c in candidates.Skip(1))
            {
                var sc = Similarity(word, c);
                if (sc > bestScore) { best = c; bestScore = sc; }
            }
            if (bestScore < 0.5 || !LooksLikeTerm(best) || known.Contains(best.ToLowerInvariant()) || learned.Contains(best)) continue;
            learned.Add(best);
        }
        return learned;
    }

    internal static List<string> Tokens(string s) =>
        TextUtil.Split(s, g => !TextUtil.IsLetter(g) && !TextUtil.IsNumber(g) && g != "'" && g != "-" && g != "_");

    /// <summary>Names, acronyms, mixed case or words with digits are worth remembering. Plain lowercase words are not.</summary>
    internal static bool LooksLikeTerm(string w)
    {
        var g = TextUtil.Graphemes(w);
        if (g.Count < 2 || g.Count > 32) return false;
        // Account numbers, phone numbers and codes are not vocabulary.
        if (g.Count(TextUtil.IsNumber) >= 4) return false;
        return g.Any(TextUtil.IsUpper) || g.Any(TextUtil.IsNumber);
    }

    /// <summary>1 − normalized Levenshtein distance, case-insensitive.</summary>
    internal static double Similarity(string a, string b)
    {
        var x = TextUtil.Graphemes(a.ToLowerInvariant());
        var y = TextUtil.Graphemes(b.ToLowerInvariant());
        if (x.Count == 0 || y.Count == 0) return 0;
        var row = Enumerable.Range(0, y.Count + 1).ToArray();
        for (int i = 1; i <= x.Count; i++)
        {
            int prev = row[0];
            row[0] = i;
            for (int j = 1; j <= y.Count; j++)
            {
                int cur = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), prev + (x[i - 1] == y[j - 1] ? 0 : 1));
                prev = cur;
            }
        }
        return 1 - (double)row[y.Count] / Math.Max(x.Count, y.Count);
    }
}
