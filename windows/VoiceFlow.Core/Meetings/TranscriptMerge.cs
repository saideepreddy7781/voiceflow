using VoiceFlow.Core.Audio;
using VoiceFlow.Core.Speech;
using VoiceFlow.Core.Text;

namespace VoiceFlow.Core.Meetings;

/// <summary>Turns Whisper's result for one chunk into meeting-clock transcript lines.</summary>
public static class ChunkLines
{
    /// <summary>A removed silence this long splits a Whisper segment, so words on either side get their real times.</summary>
    public const double SplitAtGap = 2;

    public static List<TranscriptLine> Lines(DetailedTranscript transcript, TimeMap map, Speaker speaker, double chunkFallbackTime)
    {
        var language = transcript.Language;
        if (transcript.Segments.Count == 0)
            return transcript.Text.Length == 0 ? [] : [new TranscriptLine(chunkFallbackTime, speaker, transcript.Text, Language: language)];
        var gaps = map.SpliceGaps(SplitAtGap);
        var output = new List<TranscriptLine>();
        foreach (var segment in transcript.Segments)
        {
            if (segment.Text.Length == 0) continue;
            var words = segment.Words ?? [];
            var groups = gaps.Count == 0 || words.Count == 0 ? [words] : SplitWords(words, gaps);
            if (groups.Count > 1)
            {
                foreach (var group in groups.Where(g => g.Count > 0))
                    output.Add(new TranscriptLine(map.MeetingTime(group[0].Start), speaker, string.Join(" ", group.Select(w => w.Word)),
                                                  map.MeetingEndTime(group[^1].End), language));
            }
            else
            {
                output.Add(new TranscriptLine(map.MeetingTime(segment.Start), speaker, segment.Text, map.MeetingEndTime(segment.End), language));
            }
        }
        return output;
    }

    static List<IReadOnlyList<TranscriptWord>> SplitWords(IReadOnlyList<TranscriptWord> words, IReadOnlyList<double> gaps)
    {
        var groups = new List<List<TranscriptWord>> { new() };
        var next = 0;
        foreach (var word in words)
        {
            while (next < gaps.Count && word.Start >= gaps[next])
            {
                if (groups[^1].Count > 0) groups.Add([]);
                next++;
            }
            groups[^1].Add(word);
        }
        return groups.Cast<IReadOnlyList<TranscriptWord>>().ToList();
    }
}

/// <summary>Combines lines from the two sides of a call into one time-ordered transcript.</summary>
public static class TranscriptMerge
{
    // A mic line is an echo of the call when most of its words were said on the call around the same time:
    // the call playing through speakers and picked up by the microphone.
    const double EchoWindow = 4;
    const double EchoShare = 0.6;
    const double ShortEchoWindow = 2;
    const double ShortEchoShare = 0.9;
    const int MinimumEchoWords = 3;
    const int LongLineWords = 6;

    public static List<TranscriptLine> Merge(IReadOnlyList<TranscriptLine> existing, IReadOnlyList<TranscriptLine> incoming)
    {
        // OrderBy is stable, and existing lines come first, so equal times keep that order.
        var combined = existing.Concat(incoming).OrderBy(l => l.Time).ToList();
        return TranscriptFilter.CollapseRepeats(RemoveEchoes(combined));
    }

    /// <summary>Removes mic lines that are really the call playing back into the microphone.</summary>
    public static List<TranscriptLine> RemoveEchoes(IReadOnlyList<TranscriptLine> lines)
    {
        var others = lines.Where(l => l.Speaker == Speaker.Others).ToList();
        if (others.Count == 0 || lines.All(l => l.Speaker != Speaker.You)) return lines.ToList();
        return lines.Where(line =>
        {
            if (line.Speaker != Speaker.You) return true;
            var words = Tokens(line.Text);
            if (words.Count < MinimumEchoWords) return true;
            var longLine = words.Count >= LongLineWords;
            var window = longLine ? EchoWindow : ShortEchoWindow;
            var pool = others.Where(o => HeardTogether(o, line, window)).SelectMany(o => Tokens(o.Text)).ToHashSet();
            if (pool.Count == 0) return true;
            var share = (double)words.Count(pool.Contains) / words.Count;
            return share < (longLine ? EchoShare : ShortEchoShare);
        }).ToList();
    }

    /// <summary>
    /// An echo is heard while the call's line plays, so most of the shorter line overlaps the other. A reply starts
    /// after the line it answers has ended, however many of its words it repeats. Lines with no end time (older
    /// meetings) can only compare start times.
    /// </summary>
    static bool HeardTogether(TranscriptLine call, TranscriptLine mic, double window)
    {
        if (call.End is not { } callEnd || mic.End is not { } micEnd) return Math.Abs(call.Time - mic.Time) <= window;
        var overlap = Math.Min(callEnd, micEnd) - Math.Max(call.Time, mic.Time);
        return overlap > 0 && overlap >= Math.Min(callEnd - call.Time, micEnd - mic.Time) * 0.5;
    }

    internal static List<string> Tokens(string text) =>
        TextUtil.Split(text.ToLowerInvariant(), g => !TextUtil.IsLetter(g) && !TextUtil.IsNumber(g));
}
