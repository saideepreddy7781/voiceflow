using System.Text.RegularExpressions;

namespace VoiceFlow.Core;

/// <summary>Finds names and terms on screen so they can be spelled right. Local rules only, so it adds no latency.</summary>
public static class NameExtractor
{
    internal static readonly HashSet<string> Common =
    [
        "The", "This", "That", "These", "Those", "There", "Then", "They", "Their", "We", "You", "Your", "He", "She",
        "It", "Its", "A", "An", "And", "But", "Or", "So", "If", "In", "On", "At", "To", "For", "From", "With", "Of",
        "As", "By", "Is", "Are", "Was", "Were", "Be", "Hi", "Hello", "Hey", "Thanks", "Thank", "Dear", "Regards",
        "Best", "Please", "Yes", "No", "Ok", "Okay", "What", "When", "Where", "Why", "How", "Who", "Which", "Can",
        "Could", "Would", "Should", "Will", "Do", "Does", "Did", "Have", "Has", "Had", "Not", "Just", "Also",
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday", "January", "February",
        "March", "April", "May", "June", "July", "August", "September", "October", "November", "December",
        "Re", "Fwd", "Sent", "Subject", "Cc", "Bcc", "Today", "Tomorrow", "Yesterday", "Let", "Here", "My", "Our",
    ];

    static readonly Regex Pattern = new(@"@[A-Za-z0-9_.-]{2,}|\b[A-Z][a-z]*[A-Z][A-Za-z0-9]*\b|\b[A-Z][a-z]{2,}\b|\b[A-Z]{2,6}\b",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Up to <paramref name="limit"/> distinct names and terms, most frequent first.</summary>
    public static List<string> Extract(IEnumerable<string> texts, int limit = 30)
    {
        var counts = new Dictionary<string, int>();
        var order = new List<string>();
        foreach (var text in texts)
        {
            foreach (Match m in Pattern.Matches(text))
            {
                var word = m.Value;
                if (Common.Contains(word)) continue;
                if (!counts.ContainsKey(word)) order.Add(word);
                counts[word] = counts.GetValueOrDefault(word) + 1;
            }
        }
        return order.Select((w, i) => (w, i)).OrderByDescending(p => counts[p.w]).ThenBy(p => p.i)
            .Take(limit).Select(p => p.w).ToList();
    }

    /// <summary>Whisper's prompt is limited to 224 tokens; this keeps it to about 180 by character count.</summary>
    public static string? WhisperPrompt(IEnumerable<string> dictionary, IEnumerable<string> names, int maxCharacters = 650)
    {
        var seen = new HashSet<string>();
        var parts = new List<string>();
        int length = 0;
        foreach (var term in dictionary.Concat(names))
        {
            if (term.Length == 0 || !seen.Add(term.ToLowerInvariant())) continue;
            if (length + term.Length + 2 > maxCharacters) break;
            parts.Add(term);
            length += term.Length + 2;
        }
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
}
