using VoiceFlow.Core.Audio;

namespace VoiceFlow.Core.Meetings;

/// <summary>A chunk that failed, kept in memory to retry when the meeting ends. Never written to disk.</summary>
public sealed class HeldChunk(float[] samples, TimeMap map, Speaker speaker, double voicedSeconds)
{
    public short[] Samples { get; } = samples.Select(s => (short)(Math.Max(-1f, Math.Min(1f, s)) * short.MaxValue)).ToArray();
    public TimeMap Map { get; } = map;
    public Speaker Speaker { get; } = speaker;
    public double VoicedSeconds { get; } = voicedSeconds;

    public double Seconds => Samples.Length / AudioMath.SampleRate;
    public float[] FloatSamples => Samples.Select(s => s / (float)short.MaxValue).ToArray();
}

/// <summary>Failed chunks waiting for a retry, bounded so a long outage can't use unlimited memory.</summary>
public sealed class ChunkHold(double capacity = ChunkHold.CapacitySeconds)
{
    /// <summary>About 38 MB as 16-bit samples.</summary>
    public const double CapacitySeconds = 1_200;

    readonly List<HeldChunk> _chunks = [];

    public IReadOnlyList<HeldChunk> Chunks => _chunks;
    public double TotalSeconds => _chunks.Sum(c => c.Seconds);

    /// <summary>Keeps a chunk. Returns the oldest chunks pushed out to make room: those are lost.</summary>
    public List<HeldChunk> Add(HeldChunk chunk)
    {
        _chunks.Add(chunk);
        var evicted = new List<HeldChunk>();
        while (TotalSeconds > capacity && _chunks.Count > 1)
        {
            evicted.Add(_chunks[0]);
            _chunks.RemoveAt(0);
        }
        if (TotalSeconds > capacity)
        {
            evicted.AddRange(_chunks);
            _chunks.Clear();
        }
        return evicted;
    }

    /// <summary>Hands over everything, oldest first.</summary>
    public List<HeldChunk> TakeAll()
    {
        var all = _chunks.ToList();
        _chunks.Clear();
        return all;
    }
}
