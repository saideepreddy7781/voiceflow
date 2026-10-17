using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace VoiceFlow.Core.Audio;

/// <summary>Pure audio helpers on 16 kHz mono float samples in -1...1.</summary>
public static class AudioMath
{
    public const double SampleRate = 16_000.0;
    /// <summary>30 ms analysis frames.</summary>
    public const int FrameLength = 480;

    public static float Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return 0;
        float sum = 0;
        foreach (var s in samples) sum += s * s;
        return MathF.Sqrt(sum / samples.Length);
    }

    public static float Dbfs(float rms) => rms <= 0 ? -120 : 20 * MathF.Log10(rms);

    /// <summary>Seconds of audio louder than <paramref name="thresholdDb"/>, measured in 30 ms frames.</summary>
    public static double VoicedSeconds(ReadOnlySpan<float> samples, float thresholdDb = -45)
    {
        int voiced = 0;
        int i = 0;
        while (i < samples.Length)
        {
            int end = Math.Min(i + FrameLength, samples.Length);
            if (Dbfs(Rms(samples[i..end])) > thresholdDb) voiced += end - i;
            i = end;
        }
        return voiced / SampleRate;
    }

    /// <summary>
    /// True when a recording has too little speech to send. Whisper invents text such as "Thank you." for
    /// silence, so silent recordings never reach the API.
    /// </summary>
    public static bool IsSilent(ReadOnlySpan<float> samples, double minimumVoicedSeconds = 0.25) =>
        VoicedSeconds(samples) < minimumVoicedSeconds;

    /// <summary>
    /// Whisper mode: raises quiet speech to a usable level. Peak-normalizes to <paramref name="targetPeak"/>,
    /// with gain capped so background noise in a silent room is not blown up.
    /// </summary>
    public static float[] Normalized(float[] samples, float targetPeak = 0.7f, float maxGain = 12)
    {
        float peak = 0;
        foreach (var s in samples) peak = Math.Max(peak, Math.Abs(s));
        if (peak <= 0) return samples;
        var gain = Math.Min(targetPeak / peak, maxGain);
        if (gain <= 1.05f) return samples;
        var out_ = new float[samples.Length];
        for (int i = 0; i < samples.Length; i++) out_[i] = Math.Clamp(samples[i] * gain, -1f, 1f);
        return out_;
    }

    /// <summary>16-bit PCM WAV, mono, 16 kHz.</summary>
    public static byte[] Wav(ReadOnlySpan<float> samples)
    {
        var data = new byte[44 + samples.Length * 2];
        var span = data.AsSpan();
        uint byteCount = (uint)(samples.Length * 2);
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 36 + byteCount);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)SampleRate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], byteCount);
        int o = 44;
        foreach (var s in samples)
        {
            var clamped = Math.Clamp(s, -1f, 1f);
            BinaryPrimitives.WriteInt16LittleEndian(span[o..], (short)(clamped * short.MaxValue));
            o += 2;
        }
        return data;
    }

    /// <summary>Reads a 16-bit PCM mono WAV (as written by <see cref="Wav"/>) back into samples.</summary>
    public static float[] ReadWav16(byte[] wav)
    {
        int i = 12;
        while (i + 8 <= wav.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(wav, i, 4);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(i + 4));
            if (id == "data")
            {
                var n = Math.Min(size, wav.Length - i - 8) / 2;
                var result = new float[n];
                for (int k = 0; k < n; k++)
                    result[k] = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(i + 8 + k * 2)) / (float)short.MaxValue;
                return result;
            }
            i += 8 + size + (size & 1);
        }
        return [];
    }

    /// <summary>Level for the waveform display, 0...1.</summary>
    public static float Level(ReadOnlySpan<float> samples)
    {
        var db = Dbfs(Rms(samples));
        return Math.Clamp((db + 55) / 45, 0, 1);
    }
}

/// <summary>Known Whisper hallucinations on near-silent audio.</summary>
public static class HallucinationFilter
{
    internal static readonly HashSet<string> Phrases =
    [
        "thank you", "thank you.", "thanks for watching", "thanks for watching!", "thank you for watching",
        "you", "bye", "bye.", "so", ".", "[blank_audio]", "(silence)", "subtitles by the amara.org community",
    ];

    /// <summary>True when the text is a known hallucination and the audio had very little speech.</summary>
    public static bool IsLikelyHallucination(string text, double voicedSeconds)
    {
        var t = text.Trim().ToLowerInvariant();
        return t.Length == 0 || (voicedSeconds < 1.0 && Phrases.Contains(t));
    }
}

/// <summary>Converts captured PCM bytes (any common WASAPI format) to interleaved floats.</summary>
public static class PcmDecoder
{
    public static float[] ToFloat(ReadOnlySpan<byte> bytes, int bitsPerSample, bool isFloat)
    {
        switch (bitsPerSample)
        {
            case 32 when isFloat:
                return MemoryMarshal.Cast<byte, float>(bytes[..(bytes.Length / 4 * 4)]).ToArray();
            case 64 when isFloat:
            {
                var d = MemoryMarshal.Cast<byte, double>(bytes[..(bytes.Length / 8 * 8)]);
                var r = new float[d.Length];
                for (int i = 0; i < d.Length; i++) r[i] = (float)d[i];
                return r;
            }
            case 16:
            {
                var n = bytes.Length / 2;
                var r = new float[n];
                for (int i = 0; i < n; i++) r[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes[(i * 2)..]) / 32768f;
                return r;
            }
            case 24:
            {
                var n = bytes.Length / 3;
                var r = new float[n];
                for (int i = 0; i < n; i++)
                {
                    int v = bytes[i * 3] | (bytes[i * 3 + 1] << 8) | (bytes[i * 3 + 2] << 16);
                    if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                    r[i] = v / 8388608f;
                }
                return r;
            }
            case 32:
            {
                var n = bytes.Length / 4;
                var r = new float[n];
                for (int i = 0; i < n; i++) r[i] = BinaryPrimitives.ReadInt32LittleEndian(bytes[(i * 4)..]) / 2147483648f;
                return r;
            }
            case 8:
            {
                var r = new float[bytes.Length];
                for (int i = 0; i < bytes.Length; i++) r[i] = (bytes[i] - 128) / 128f;
                return r;
            }
            default:
                throw new NotSupportedException($"{bitsPerSample}-bit audio is not supported.");
        }
    }
}

/// <summary>
/// Streaming conversion of interleaved audio at any rate and channel count to 16 kHz mono, with a
/// windowed-sinc low-pass so 44.1/48 kHz speech does not alias when decimated. State carries across calls,
/// so chunk boundaries from the capture thread leave no clicks.
/// </summary>
public sealed class StreamingResampler
{
    const int HalfTaps = 16;
    const int Resolution = 256;
    readonly int _channels;
    readonly double _step;
    readonly bool _passthrough;
    readonly float[] _table;
    readonly List<float> _buffer = new();
    double _pos;

    public int InputRate { get; }

    public StreamingResampler(int inputRate, int channels)
    {
        if (inputRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        InputRate = inputRate;
        _channels = channels;
        _step = inputRate / AudioMath.SampleRate;
        _passthrough = inputRate == (int)AudioMath.SampleRate;
        // Cutoff a little under the output Nyquist (8 kHz), relative to the input rate.
        double cutoff = Math.Min(1.0, AudioMath.SampleRate / inputRate) * 0.92;
        _table = new float[HalfTaps * Resolution + 1];
        for (int i = 0; i < _table.Length; i++)
        {
            double t = (double)i / Resolution;
            double x = Math.PI * cutoff * t;
            double sinc = t == 0 ? 1 : Math.Sin(x) / x;
            double w = 0.42 + 0.5 * Math.Cos(Math.PI * t / HalfTaps) + 0.08 * Math.Cos(2 * Math.PI * t / HalfTaps);
            _table[i] = (float)(cutoff * sinc * w);
        }
        _pos = 0;
    }

    float Kernel(double t)
    {
        t = Math.Abs(t);
        if (t >= HalfTaps) return 0;
        return _table[(int)(t * Resolution + 0.5)];
    }

    public float[] Process(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / _channels;
        if (frames == 0) return [];
        if (_passthrough && _channels == 1) return interleaved[..frames].ToArray();
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < _channels; c++) sum += interleaved[f * _channels + c];
            _buffer.Add(sum / _channels);
        }
        if (_passthrough) { var all = _buffer.ToArray(); _buffer.Clear(); return all; }

        var output = new List<float>((int)(frames / _step) + 2);
        while (_pos + HalfTaps < _buffer.Count)
        {
            int center = (int)Math.Floor(_pos);
            float acc = 0;
            for (int k = center - HalfTaps + 1; k <= center + HalfTaps; k++)
            {
                if (k < 0) continue;
                acc += _buffer[k] * Kernel(_pos - k);
            }
            output.Add(acc);
            _pos += _step;
        }
        int drop = (int)Math.Floor(_pos) - HalfTaps;
        if (drop > 0)
        {
            drop = Math.Min(drop, _buffer.Count);
            _buffer.RemoveRange(0, drop);
            _pos -= drop;
        }
        return output.ToArray();
    }
}

/// <summary>Thread-safe sample store written from the capture thread and read from the UI thread.</summary>
public sealed class SampleBuffer
{
    readonly object _lock = new();
    readonly List<float> _samples = new();

    public void Append(float[] samples)
    {
        lock (_lock) _samples.AddRange(samples);
    }

    public float[] Snapshot()
    {
        lock (_lock) return _samples.ToArray();
    }

    public int Count
    {
        get { lock (_lock) return _samples.Count; }
    }

    /// <summary>The samples from <paramref name="start"/> to the end.</summary>
    public float[] CopyFrom(int start)
    {
        lock (_lock)
        {
            var from = Math.Clamp(start, 0, _samples.Count);
            return _samples.GetRange(from, _samples.Count - from).ToArray();
        }
    }

    public float[] Tail(int n)
    {
        lock (_lock)
        {
            var take = Math.Min(n, _samples.Count);
            return _samples.GetRange(_samples.Count - take, take).ToArray();
        }
    }

    public void Reset()
    {
        lock (_lock) _samples.Clear();
    }

    /// <summary>Takes and removes everything buffered (long recordings stay small in memory).</summary>
    public float[] Drain()
    {
        lock (_lock)
        {
            var all = _samples.ToArray();
            _samples.Clear();
            return all;
        }
    }
}
