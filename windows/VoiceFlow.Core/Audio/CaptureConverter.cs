namespace VoiceFlow.Core.Audio;

/// <summary>The format of one capture buffer, as WASAPI describes it.</summary>
public readonly record struct CaptureFormat(int SampleRate, int Channels, int BitsPerSample, bool IsFloat)
{
    public int BytesPerFrame => Channels * BitsPerSample / 8;

    public bool IsUsable => SampleRate > 0 && Channels > 0 && BitsPerSample is 8 or 16 or 24 or 32 or 64 && (!IsFloat || BitsPerSample is 32 or 64);
}

/// <summary>
/// Turns capture buffers into 16 kHz mono. The format comes with every buffer, and the resampler is rebuilt when it
/// changes, so a capture whose sample rate or channel count moves keeps delivering audio instead of noise or nothing.
/// A partial frame at the end of a buffer waits for the next one.
///
/// A microphone reads one channel. Laptop microphone arrays often deliver two channels with one inverted, and
/// averaging those cancels the voice (on macOS, averaging a 3-channel array cost 9 dB). It starts on the first
/// channel and moves to another only when that one is clearly louder, which covers an audio interface with the
/// microphone on its second input. Loopback (what the PC plays) averages all channels.
/// </summary>
public sealed class CaptureConverter(bool oneChannel)
{
    /// <summary>Another channel has to be this much louder (power ratio, 6 dB) before the microphone moves to it.</summary>
    const double SwitchRatio = 4;
    /// <summary>Seconds of audio the channel loudness is averaged over.</summary>
    const double LoudnessSeconds = 1;

    CaptureFormat? _format;
    StreamingResampler? _resampler;
    byte[] _partial = [];
    double[] _power = [];
    int _channel;

    /// <summary>Times the format changed after the first buffer.</summary>
    public int FormatChanges { get; private set; }
    /// <summary>The channel a microphone reads now (0 for loopback).</summary>
    public int Channel => _channel;
    public CaptureFormat? Format => _format;

    public float[] Convert(ReadOnlySpan<byte> data, CaptureFormat format)
    {
        if (!format.IsUsable) return [];
        if (_format != format)
        {
            if (_format != null) FormatChanges++;
            _format = format;
            _resampler = new StreamingResampler(format.SampleRate, oneChannel ? 1 : format.Channels);
            _partial = [];
            _power = new double[format.Channels];
            _channel = Math.Min(_channel, format.Channels - 1);
        }
        var frameBytes = format.BytesPerFrame;
        ReadOnlySpan<byte> bytes = data;
        byte[]? joined = null;
        if (_partial.Length > 0)
        {
            joined = new byte[_partial.Length + data.Length];
            _partial.CopyTo(joined, 0);
            data.CopyTo(joined.AsSpan(_partial.Length));
            bytes = joined;
        }
        var whole = bytes.Length / frameBytes * frameBytes;
        _partial = bytes[whole..].ToArray();
        if (whole == 0) return [];
        var samples = PcmDecoder.ToFloat(bytes[..whole], format.BitsPerSample, format.IsFloat);
        if (!oneChannel || format.Channels == 1) return _resampler!.Process(samples);
        return _resampler!.Process(PickChannel(samples, format));
    }

    float[] PickChannel(float[] interleaved, CaptureFormat format)
    {
        var channels = format.Channels;
        var frames = interleaved.Length / channels;
        // Exponential average of each channel's power, over about a second of audio.
        var keep = Math.Exp(-frames / (LoudnessSeconds * format.SampleRate));
        for (int c = 0; c < channels; c++)
        {
            double sum = 0;
            for (int f = 0; f < frames; f++)
            {
                var v = interleaved[f * channels + c];
                sum += v * v;
            }
            _power[c] = _power[c] * keep + sum / frames * (1 - keep);
        }
        var loudest = 0;
        for (int c = 1; c < channels; c++)
            if (_power[c] > _power[loudest]) loudest = c;
        if (loudest != _channel && _power[loudest] > _power[_channel] * SwitchRatio) _channel = loudest;
        var mono = new float[frames];
        for (int f = 0; f < frames; f++) mono[f] = interleaved[f * channels + _channel];
        return mono;
    }
}
