namespace VoiceFlow.Core.Audio;

/// <summary>
/// The one rule for "is this speech", shared by dictation and meetings: a 30 ms frame is voiced when it is
/// <see cref="MarginDb"/> above the noise floor of the capture it came from. Same rule and constants as macOS.
///
/// A fixed level does not work for every microphone. While a call app uses voice processing, a microphone can be
/// about 10 dB quieter, and a fixed -45 dBFS floor called real speech silence. The floor is the 10th percentile of
/// the frames heard, leaving out exact zeros, so digital silence and a microphone that gates to zero between words
/// never drag it down.
/// </summary>
public static class SpeechRule
{
    /// <summary>Never speech below this, whatever the floor: digital silence and the quietest rooms stay silent.</summary>
    public const float AbsoluteMinimumDb = -70;
    /// <summary>A frame is voiced when it is this far above the noise floor.</summary>
    public const float MarginDb = 10;
    /// <summary>A frame above this is voiced whatever the floor, so a loud room can't mute a voice.</summary>
    public const float CeilingDb = -35;
    /// <summary>Exact zeros are not a noise floor.</summary>
    internal const float ZeroDb = -119;

    /// <summary>The voiced threshold for a capture with this noise floor (null: nothing but zeros heard yet).</summary>
    public static float Threshold(float? noiseFloorDb) =>
        noiseFloorDb is { } floor ? Math.Max(AbsoluteMinimumDb, Math.Min(CeilingDb, floor + MarginDb)) : AbsoluteMinimumDb;

    /// <summary>The 10th percentile of the frame levels, leaving out exact zeros.</summary>
    public static float? NoiseFloor(IEnumerable<float> frameDbs)
    {
        var heard = frameDbs.Where(db => db > ZeroDb).ToArray();
        if (heard.Length == 0) return null;
        Array.Sort(heard);
        return heard[heard.Length / 10];
    }

    /// <summary>Frame levels in dBFS, 30 ms each.</summary>
    public static float[] FrameDbs(ReadOnlySpan<float> samples)
    {
        var frame = AudioMath.FrameLength;
        var dbs = new float[(samples.Length + frame - 1) / frame];
        for (int k = 0, i = 0; i < samples.Length; k++, i += frame)
            dbs[k] = AudioMath.Dbfs(AudioMath.Rms(samples[i..Math.Min(i + frame, samples.Length)]));
        return dbs;
    }

    /// <summary>A whole recording measured against its own floor.</summary>
    /// <param name="SpeechDb">RMS level of the voiced frames (-120 when there are none).</param>
    public sealed record Analysis(float? NoiseFloorDb, float ThresholdDb, double VoicedSeconds, float SpeechDb);

    public static Analysis Analyze(ReadOnlySpan<float> samples)
    {
        var dbs = FrameDbs(samples);
        var floor = NoiseFloor(dbs);
        var threshold = Threshold(floor);
        var voiced = 0;
        double energy = 0;
        for (int k = 0; k < dbs.Length; k++)
        {
            if (dbs[k] <= threshold) continue;
            var start = k * AudioMath.FrameLength;
            var end = Math.Min(start + AudioMath.FrameLength, samples.Length);
            voiced += end - start;
            for (int i = start; i < end; i++) energy += samples[i] * samples[i];
        }
        var speechDb = voiced > 0 ? AudioMath.Dbfs((float)Math.Sqrt(energy / voiced)) : -120;
        return new Analysis(floor, threshold, voiced / AudioMath.SampleRate, speechDb);
    }

    /// <summary>Level the voiced frames are raised to before upload.</summary>
    public const float UploadSpeechDb = -24;
    /// <summary>
    /// Most gain added before upload. 24 dB covers a voice-processing call (about 10 dB down) with room to spare,
    /// and the voiced check has already kept room noise from getting this far.
    /// </summary>
    public const float MaximumGainDb = 24;
    /// <summary>The cap in Whisper mode, for speech that is quiet on purpose.</summary>
    public const float WhisperModeMaximumGainDb = 30;

    /// <summary>Gain in dB to bring quiet speech up to <see cref="UploadSpeechDb"/>. Speech already at that level or louder is untouched.</summary>
    public static float UploadGainDb(Analysis analysis, float maximum = MaximumGainDb) =>
        analysis.VoicedSeconds > 0 ? Math.Max(0, Math.Min(maximum, UploadSpeechDb - analysis.SpeechDb)) : 0;

    /// <summary>The audio to upload: quiet speech raised, then clipped to -1...1 (a few clipped clicks do not hurt Whisper).</summary>
    public static float[] PrepareForUpload(float[] samples, float maximumGainDb = MaximumGainDb)
    {
        var gainDb = UploadGainDb(Analyze(samples), maximumGainDb);
        if (gainDb <= 0.5f) return samples;
        var gain = (float)Math.Pow(10, gainDb / 20.0);
        var raised = new float[samples.Length];
        for (int i = 0; i < samples.Length; i++) raised[i] = Math.Clamp(samples[i] * gain, -1f, 1f);
        return raised;
    }
}

/// <summary>
/// Decides whether a 30 ms frame holds a voice while a stream is still arriving: <see cref="SpeechRule"/> against a
/// noise floor tracked over the last 15 seconds.
/// </summary>
public sealed class VoiceGate
{
    const int HistoryFrames = 500;          // 15 s
    const int RefreshEveryFrames = 33;      // about 1 s

    readonly List<float> _history = new(HistoryFrames);
    int _cursor;
    int _sinceRefresh;
    float _threshold = SpeechRule.AbsoluteMinimumDb;

    public bool IsVoiced(float frameDb)
    {
        if (frameDb > SpeechRule.ZeroDb)
        {
            if (_history.Count < HistoryFrames) _history.Add(frameDb);
            else
            {
                _history[_cursor] = frameDb;
                _cursor = (_cursor + 1) % HistoryFrames;
            }
        }
        _sinceRefresh++;
        // Every frame until a second has been heard, so the start of a stream is judged against its own floor.
        if (_sinceRefresh >= RefreshEveryFrames || _history.Count < RefreshEveryFrames)
        {
            _sinceRefresh = 0;
            _threshold = SpeechRule.Threshold(SpeechRule.NoiseFloor(_history));
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
