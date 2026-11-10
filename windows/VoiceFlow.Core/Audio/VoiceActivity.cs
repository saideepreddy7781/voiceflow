namespace VoiceFlow.Core.Audio;

/// <summary>Decides whether a 30 ms frame holds a voice, by loudness against a slowly tracked noise floor.</summary>
public sealed class VoiceGate
{
    /// <summary>Below this is always silence, so digital silence and very quiet rooms can't be called speech.</summary>
    public const float AbsoluteFloorDb = -45;
    /// <summary>A frame is voiced when it is this far above the noise floor.</summary>
    public const float MarginDb = 12;
    /// <summary>A frame above this is voiced whatever the noise floor, so a loud room can't mute a voice.</summary>
    public const float CeilingDb = -35;
    const int HistoryFrames = 500;          // 15 s
    const int RefreshEveryFrames = 33;      // about 1 s

    readonly List<float> _history = new(HistoryFrames);
    int _cursor;
    int _sinceRefresh;
    float _threshold = AbsoluteFloorDb;

    public bool IsVoiced(float frameDb)
    {
        if (_history.Count < HistoryFrames) _history.Add(frameDb);
        else
        {
            _history[_cursor] = frameDb;
            _cursor = (_cursor + 1) % HistoryFrames;
        }
        _sinceRefresh++;
        if (_sinceRefresh >= RefreshEveryFrames)
        {
            _sinceRefresh = 0;
            var sorted = _history.ToArray();
            Array.Sort(sorted);
            var floor = sorted[sorted.Length / 10];
            _threshold = Math.Max(AbsoluteFloorDb, Math.Min(CeilingDb, floor + MarginDb));
        }
        return frameDb > _threshold;
    }
}

public sealed record TimeMapSpan(double ChunkStart, double MeetingStart, double Seconds);

/// <summary>
/// Maps a position in the uploaded audio back to the meeting clock. The upload has only the voiced parts, with
/// short silences spliced between them.
/// </summary>
public sealed record TimeMap(IReadOnlyList<TimeMapSpan> Spans)
{
    /// <summary>Meeting time for a position in the upload. A position inside a splice maps to the start of the next span.</summary>
    public double MeetingTime(double chunkSeconds)
    {
        if (Spans.Count == 0) return chunkSeconds;
        var first = Spans[0];
        if (chunkSeconds <= first.ChunkStart) return first.MeetingStart;
        for (int i = Spans.Count - 1; i >= 0; i--)
        {
            var span = Spans[i];
            if (chunkSeconds < span.ChunkStart) continue;
            if (chunkSeconds > span.ChunkStart + span.Seconds && i + 1 < Spans.Count) return Spans[i + 1].MeetingStart;
            return span.MeetingStart + Math.Min(chunkSeconds - span.ChunkStart, span.Seconds);
        }
        return first.MeetingStart;
    }

    /// <summary>Meeting time for the end of a line. A position inside a splice is the end of the speech before it.</summary>
    public double MeetingEndTime(double chunkSeconds)
    {
        if (Spans.Count == 0) return chunkSeconds;
        var first = Spans[0];
        if (chunkSeconds <= first.ChunkStart) return first.MeetingStart;
        foreach (var span in Spans.Reverse())
            if (chunkSeconds >= span.ChunkStart) return span.MeetingStart + Math.Min(chunkSeconds - span.ChunkStart, span.Seconds);
        return first.MeetingStart;
    }

    /// <summary>Upload positions where the original meeting had at least <paramref name="minimumGap"/> seconds of silence removed.</summary>
    public IReadOnlyList<double> SpliceGaps(double minimumGap)
    {
        var gaps = new List<double>();
        for (int i = 0; i + 1 < Spans.Count; i++)
        {
            var (a, b) = (Spans[i], Spans[i + 1]);
            if (b.MeetingStart - (a.MeetingStart + a.Seconds) >= minimumGap) gaps.Add(b.ChunkStart);
        }
        return gaps;
    }
}

/// <summary>One piece of a meeting ready to upload.</summary>
public sealed record AudioChunk(float[] Samples, TimeMap Map, double VoicedSeconds);

/// <summary>
/// Cuts one side of a meeting into uploads made of voiced audio only.
///
/// Silence is left out (a splice of <see cref="GapSeconds"/> marks each removed pause) so a side's hour of audio costs
/// only its speech. Chunks are cut at a pause once they hold <see cref="MinimumVoiced"/> seconds of speech, forced at
/// <see cref="MaximumVoiced"/>, and flushed at a pause after <see cref="MaxWait"/> seconds so a quiet side still shows up.
/// </summary>
public sealed class StreamChunker
{
    public double MinimumVoiced { get; init; } = 20;
    public double MaximumVoiced { get; init; } = 45;
    public double PauseToCut { get; init; } = 0.6;
    public double MaxWait { get; init; } = 120;
    public double GapSeconds { get; init; } = 0.25;
    /// <summary>Chunks with less speech than this are not sent. Whisper invents text for noise.</summary>
    public const double MinimumSendableVoiced = 0.4;
    const int PrerollFrames = 7;            // 210 ms kept before speech starts
    const int HangoverFrames = 10;          // 300 ms kept after speech stops
    const double Rate = AudioMath.SampleRate;

    readonly VoiceGate _gate = new();
    float[] _leftover = [];
    /// <summary>Whole frames seen so far, in samples.</summary>
    int _consumed;
    bool _inRegion;
    int _hangover;
    int _silentFrames;
    readonly Queue<float[]> _preroll = new();
    readonly List<float> _chunk = [];
    readonly List<TimeMapSpan> _spans = [];
    int? _chunkFirstVoice;
    int _voicedSamples;

    /// <summary>Feeds newly captured samples. Returns any chunks ready to upload, oldest first.</summary>
    public List<AudioChunk> Feed(float[] samples)
    {
        var input = _leftover.Length == 0 ? samples : [.. _leftover, .. samples];
        _leftover = [];
        var output = new List<AudioChunk>();
        var i = 0;
        while (i + AudioMath.FrameLength <= input.Length)
        {
            if (ProcessFrame(input[i..(i + AudioMath.FrameLength)]) is { } chunk) output.Add(chunk);
            i += AudioMath.FrameLength;
        }
        _leftover = input[i..];
        return output;
    }

    /// <summary>The final chunk at Stop.</summary>
    public AudioChunk? Finish()
    {
        if (_leftover.Length > 0 && _inRegion && _chunk.Count > 0)
        {
            AppendToRegion(_leftover);
            _leftover = [];
        }
        return Cut();
    }

    AudioChunk? ProcessFrame(float[] frame)
    {
        var frameStart = _consumed;
        _consumed += frame.Length;
        if (_gate.IsVoiced(AudioMath.Dbfs(AudioMath.Rms(frame))))
        {
            if (!_inRegion) BeginRegion(frameStart);
            EnsureSpan(frameStart);
            _inRegion = true;
            _hangover = HangoverFrames;
            _silentFrames = 0;
            AppendToRegion(frame);
            _voicedSamples += frame.Length;
            _chunkFirstVoice ??= frameStart;
        }
        else if (_inRegion && _hangover > 0)
        {
            _hangover--;
            _silentFrames++;
            EnsureSpan(frameStart);
            AppendToRegion(frame);
        }
        else
        {
            _inRegion = false;
            _silentFrames++;
            PushPreroll(frame);
        }
        var voicedSeconds = _voicedSamples / Rate;
        if (voicedSeconds >= MaximumVoiced) return Cut();
        var quiet = _silentFrames * (double)AudioMath.FrameLength / Rate;
        if (!_inRegion && quiet >= PauseToCut && _chunk.Count > 0)
        {
            if (voicedSeconds >= MinimumVoiced) return Cut();
            if (_chunkFirstVoice is { } first && (_consumed - first) / Rate >= MaxWait) return Cut();
        }
        return null;
    }

    void BeginRegion(int frameStart)
    {
        var meetingSamples = Math.Max(0, frameStart - _preroll.Sum(p => p.Length));
        if (_chunk.Count > 0) _chunk.AddRange(new float[(int)(GapSeconds * Rate)]);
        _spans.Add(new TimeMapSpan(_chunk.Count / Rate, meetingSamples / Rate, 0));
        foreach (var p in _preroll) AppendToRegion(p);
        _preroll.Clear();
    }

    /// <summary>A forced cut can land in the middle of speech; the next chunk starts its map here.</summary>
    void EnsureSpan(int frameStart)
    {
        if (_spans.Count == 0) _spans.Add(new TimeMapSpan(_chunk.Count / Rate, frameStart / Rate, 0));
    }

    void AppendToRegion(float[] frame)
    {
        _chunk.AddRange(frame);
        if (_spans.Count > 0) _spans[^1] = _spans[^1] with { Seconds = _chunk.Count / Rate - _spans[^1].ChunkStart };
    }

    void PushPreroll(float[] frame)
    {
        _preroll.Enqueue(frame);
        if (_preroll.Count > PrerollFrames) _preroll.Dequeue();
    }

    AudioChunk? Cut()
    {
        var seconds = _voicedSamples / Rate;
        AudioChunk? result = seconds >= MinimumSendableVoiced && _chunk.Count > 0
            ? new AudioChunk(_chunk.ToArray(), new TimeMap(_spans.ToArray()), seconds)
            : null;
        _chunk.Clear();
        _spans.Clear();
        _voicedSamples = 0;
        _chunkFirstVoice = null;
        _preroll.Clear();
        _silentFrames = 0;
        return result;
    }
}
