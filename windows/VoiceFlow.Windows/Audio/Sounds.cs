using System.Runtime.InteropServices;
using VoiceFlow.Core.Audio;

namespace VoiceFlow.Windows.Audio;

/// <summary>
/// Short, quiet cues generated in code (no sound files): a soft rising blip when recording starts and a low
/// double tone for errors. There is no stop or done sound: the text appearing is the confirmation.
/// </summary>
internal static class Sounds
{
    // PlaySound with SND_MEMORY | SND_ASYNC reads the buffer while it plays, so the buffers are pinned for the
    // life of the process (two small arrays).
    static readonly Lazy<byte[]> StartWav = new(() => Pinned(Tone([(880, 0.045), (1320, 0.055)], 0.12f)));
    static readonly Lazy<byte[]> ErrorWav = new(() => Pinned(Tone([(330, 0.09), (0, 0.04), (262, 0.12)], 0.14f)));

    const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_MEMORY = 0x0004;

    [DllImport("winmm.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PlaySound(IntPtr pszSound, IntPtr hmod, uint fdwSound);

    public static void Start(bool enabled) => Play(StartWav.Value, enabled);
    public static void Error(bool enabled) => Play(ErrorWav.Value, enabled);

    static byte[] Pinned(byte[] data)
    {
        var pinned = GC.AllocateArray<byte>(data.Length, pinned: true);
        data.CopyTo(pinned, 0);
        return pinned;
    }

    static void Play(byte[] pinnedWav, bool enabled)
    {
        if (!enabled) return;
        if (!PlaySound(Marshal.UnsafeAddrOfPinnedArrayElement(pinnedWav, 0), IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT))
            Logger.Debug("PlaySound failed");
    }

    /// <summary>A 16 kHz WAV of sine tones with 5 ms fades (no clicks). Frequency 0 is a pause.</summary>
    internal static byte[] Tone((double Hz, double Seconds)[] parts, float amplitude)
    {
        var samples = new List<float>();
        foreach (var (hz, seconds) in parts)
        {
            var n = (int)(seconds * AudioMath.SampleRate);
            var fade = (int)(0.005 * AudioMath.SampleRate);
            for (int i = 0; i < n; i++)
            {
                var envelope = Math.Min(1.0, Math.Min(i, n - 1 - i) / (double)fade);
                samples.Add(hz == 0 ? 0 : (float)(amplitude * envelope * Math.Sin(2 * Math.PI * hz * i / AudioMath.SampleRate)));
            }
        }
        return AudioMath.Wav(samples.ToArray());
    }
}
