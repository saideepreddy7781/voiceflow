using System.Globalization;
using System.Text;

namespace VoiceFlow.Core.Text;

/// <summary>
/// Character helpers that behave like Swift's <c>Character</c>: one user-perceived character (grapheme),
/// classified by its first scalar. Telugu vowel signs are combining marks, so splitting by UTF-16 char
/// would cut words apart; splitting by grapheme keeps "కి" whole, as the macOS app does.
/// </summary>
public static class TextUtil
{
    public static List<string> Graphemes(string s)
    {
        var list = new List<string>(s.Length);
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) list.Add((string)e.Current);
        return list;
    }

    public static int GraphemeCount(string s) => new StringInfo(s).LengthInTextElements;

    static Rune First(string g) => Rune.TryGetRuneAt(g, 0, out var r) ? r : new Rune(' ');

    public static bool IsLetter(string g)
    {
        var c = Rune.GetUnicodeCategory(First(g));
        return c is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter;
    }

    public static bool IsNumber(string g)
    {
        var c = Rune.GetUnicodeCategory(First(g));
        return c is UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;
    }

    public static bool IsUpper(string g) => Rune.IsUpper(First(g));

    public static bool IsWhitespace(string g) => Rune.IsWhiteSpace(First(g));

    /// <summary>Splits on graphemes for which <paramref name="isSeparator"/> is true, dropping empty pieces.</summary>
    public static List<string> Split(string s, Func<string, bool> isSeparator)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        foreach (var g in Graphemes(s))
        {
            if (isSeparator(g))
            {
                if (current.Length > 0) { parts.Add(current.ToString()); current.Clear(); }
            }
            else current.Append(g);
        }
        if (current.Length > 0) parts.Add(current.ToString());
        return parts;
    }

    /// <summary>Words separated by whitespace (Swift <c>split(whereSeparator: \.isWhitespace)</c>).</summary>
    public static string[] WhitespaceWords(string s) =>
        s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The first <paramref name="n"/> graphemes.</summary>
    public static string Prefix(string s, int n)
    {
        if (s.Length <= n) return s;
        var e = StringInfo.GetTextElementEnumerator(s);
        int count = 0;
        while (e.MoveNext())
        {
            if (count == n) return s[..e.ElementIndex];
            count++;
        }
        return s;
    }

    /// <summary>The last <paramref name="n"/> graphemes.</summary>
    public static string Suffix(string s, int n)
    {
        var g = Graphemes(s);
        return g.Count <= n ? s : string.Concat(g.Skip(g.Count - n));
    }

    /// <summary>Swift <c>trimmingCharacters(in: .whitespacesAndNewlines)</c>.</summary>
    public static string TrimWs(string s) => s.Trim();
}
