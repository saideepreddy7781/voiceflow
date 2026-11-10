using VoiceFlow.Core.Meetings;
using VoiceFlow.Core.Text;

namespace VoiceFlow.Core.Speech;

/// <summary>Removes what Whisper invents on hard audio, using the numbers it reports for each segment.</summary>
public static class WhisperSegmentFilter
{
    /// <summary>A segment this compressible (its text repeats itself) is a loop. OpenAI's Whisper uses 2.4 as well.</summary>
    public const double CompressionRatioLimit = 2.4;
    public const double NoSpeechLimit = 0.6;
    public const double NoSpeechLogprob = -1.0;
    /// <summary>Three identical segments in a row is a loop; people repeating themselves don't do it with Whisper's timing.</summary>
    public const int LoopRun = 3;

    /// <summary>Whether a single segment is noise or a loop.</summary>
    public static bool ShouldDrop(TranscriptSegment s)
    {
        if (s.CompressionRatio is { } ratio && ratio > CompressionRatioLimit && TextUtil.GraphemeCount(s.Text) >= 20) return true;
        return s.NoSpeechProb is { } noSpeech && s.AvgLogprob is { } logprob && noSpeech > NoSpeechLimit && logprob < NoSpeechLogprob;
    }

    /// <summary>Looks like a loop across segments: identical text three or more times running.</summary>
    public static bool IsLooping(IReadOnlyList<TranscriptSegment> segments)
    {
        var run = 1;
        for (int i = 1; i < segments.Count; i++)
        {
            var text = Normalize(segments[i].Text);
            run = text == Normalize(segments[i - 1].Text) && text.Length > 0 ? run + 1 : 1;
            if (run >= LoopRun) return true;
        }
        return segments.Any(s => (s.CompressionRatio ?? 0) > CompressionRatioLimit && TextUtil.GraphemeCount(s.Text) >= 20);
    }

    /// <summary>
    /// Returns the transcript without dropped segments or repeats of a looped segment. Text is rebuilt only if
    /// something was removed, so normal transcripts keep Whisper's exact spacing.
    /// </summary>
    public static DetailedTranscript Filtered(DetailedTranscript t)
    {
        var kept = new List<TranscriptSegment>();
        var runText = "";
        var run = 0;
        foreach (var segment in t.Segments.Where(s => !ShouldDrop(s)))
        {
            var n = Normalize(segment.Text);
            run = n == runText && n.Length > 0 ? run + 1 : 1;
            runText = n;
            if (run >= LoopRun) continue;
            kept.Add(segment);
        }
        if (kept.Count == t.Segments.Count) return t;
        return t with { Segments = kept, Text = string.Join(" ", kept.Select(s => s.Text).Where(s => s.Length > 0)) };
    }

    internal static string Normalize(string text) =>
        string.Concat(TextUtil.Graphemes(text.ToLowerInvariant()).Where(g => TextUtil.IsLetter(g) || TextUtil.IsNumber(g)));
}

/// <summary>Transcript-level cleanup that also works on meetings already saved.</summary>
public static class TranscriptFilter
{
    public const string LostMarker = "[Part of the meeting could not be transcribed]";
    /// <summary>A run this long from one speaker, gaps within <see cref="RunGap"/>, keeps only its first line.</summary>
    public const int RunLength = 3;
    public const double RunGap = 20;

    public static List<TranscriptLine> CollapseRepeats(IReadOnlyList<TranscriptLine> lines)
    {
        var keep = Enumerable.Repeat(true, lines.Count).ToArray();
        // Runs are per speaker: the other person can talk between looped lines.
        foreach (var idx in Enumerable.Range(0, lines.Count).GroupBy(i => lines[i].Speaker).Select(g => g.ToList()))
        {
            var i = 0;
            while (i < idx.Count)
            {
                var n = WhisperSegmentFilter.Normalize(lines[idx[i]].Text);
                var j = i;
                if (n.Length > 0 && lines[idx[i]].Text != LostMarker)
                {
                    while (j + 1 < idx.Count && WhisperSegmentFilter.Normalize(lines[idx[j + 1]].Text) == n
                           && lines[idx[j + 1]].Time - lines[idx[j]].Time <= RunGap) j++;
                }
                if (j - i + 1 >= RunLength)
                    for (int k = i + 1; k <= j; k++) keep[idx[k]] = false;
                i = j + 1;
            }
        }
        return Enumerable.Range(0, lines.Count).Where(i => keep[i]).Select(i => lines[i]).ToList();
    }
}
