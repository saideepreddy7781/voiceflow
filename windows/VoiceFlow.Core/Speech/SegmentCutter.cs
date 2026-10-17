using VoiceFlow.Core.Audio;

namespace VoiceFlow.Core.Speech;

/// <summary>
/// Decides where to cut a growing recording into upload segments.
///
/// Groq bills each request as at least 10 s, so a segment is cut only once it is at least
/// <see cref="MinimumSeconds"/> long and a pause of <see cref="PauseSeconds"/> appears, or when it reaches
/// <see cref="MaximumSeconds"/>. Segments overlap by <see cref="OverlapSeconds"/> so no word is split.
/// </summary>
public sealed class SegmentCutter
{
    public double MinimumSeconds { get; set; } = 10.0;
    public double MaximumSeconds { get; set; } = 30.0;
    public double PauseSeconds { get; set; } = 0.4;
    public double OverlapSeconds { get; set; } = 0.2;
    public float SilenceDb { get; set; } = -45;

    /// <summary>Start of the segment that is still open, in samples.</summary>
    public int OpenStart { get; private set; }

    static int Rate => (int)AudioMath.SampleRate;

    /// <summary>Looks at the samples recorded so far. Returns the range of a finished segment, if one should be cut now.</summary>
    public Range? NextCut(ReadOnlySpan<float> samples) => NextCut(samples, 0);

    /// <summary>
    /// The same, given only the recording from <paramref name="tailStart"/> on (at or before <see cref="OpenStart"/>),
    /// so long recordings are not copied in full every tick. Ranges are still in whole-recording samples.
    /// </summary>
    public Range? NextCut(ReadOnlySpan<float> tail, int tailStart)
    {
        if (tailStart > OpenStart) throw new ArgumentOutOfRangeException(nameof(tailStart));
        var samples = tail.Slice(OpenStart - tailStart);
        int offset = OpenStart;
        int length = samples.Length;
        int minimum = (int)(MinimumSeconds * Rate);
        if (length < minimum) return null;

        int maximum = (int)(MaximumSeconds * Rate);
        int pauseFrames = (int)(PauseSeconds * Rate) / AudioMath.FrameLength;
        int frame = AudioMath.FrameLength;

        int quiet = 0;
        int i = minimum;   // relative to OpenStart from here on
        while (i + frame <= samples.Length)
        {
            if (AudioMath.Dbfs(AudioMath.Rms(samples.Slice(i, frame))) <= SilenceDb)
            {
                quiet += 1;
                if (quiet >= pauseFrames)
                {
                    int cut = i + frame - (quiet * frame) / 2;   // middle of the pause
                    return MakeCut(offset + cut);
                }
            }
            else quiet = 0;
            i += frame;
        }
        if (length >= maximum) return MakeCut(offset + maximum);
        return null;
    }

    /// <summary>The final segment when recording stops.</summary>
    public Range? Finish(int totalSamples)
    {
        if (totalSamples <= OpenStart) return null;
        var range = OpenStart..totalSamples;
        OpenStart = totalSamples;
        return range;
    }

    Range MakeCut(int cut)
    {
        int overlap = (int)(OverlapSeconds * Rate);
        var range = OpenStart..cut;
        OpenStart = Math.Max(0, cut - overlap);
        return range;
    }

    /// <summary>Joins segment transcripts, dropping a word repeated across an overlap.</summary>
    public static string Join(IEnumerable<string> parts)
    {
        var result = "";
        foreach (var raw in parts)
        {
            var part = raw.Trim();
            if (part.Length == 0) continue;
            if (result.Length == 0) { result = part; continue; }
            var lastWord = Normalize(result.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "");
            var words = part.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (words.Count > 0 && Normalize(words[0]) == lastWord) words.RemoveAt(0);
            result += " " + string.Join(" ", words);
        }
        return result;
    }

    static string Normalize(string word) => word.ToLowerInvariant().Trim(PunctuationChars(word));

    static char[] PunctuationChars(string word) => word.Where(char.IsPunctuation).Distinct().ToArray();
}
