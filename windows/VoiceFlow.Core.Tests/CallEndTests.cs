using System.Runtime.InteropServices;
using VoiceFlow.Core.Audio;
using VoiceFlow.Core.Meetings;
using VoiceFlow.Core.Speech;
using Xunit;

namespace VoiceFlow.Core.Tests;

/// <summary>The same cases as macOS's CallEndTests.</summary>
public class CallEndDetectorTests
{
    /// <summary>Feeds one observation every 5 seconds from <paramref name="from"/> to <paramref name="to"/>, and returns the times it said stop.</summary>
    static List<double> Run(CallEndDetector d, double from, double to, bool? call, Func<double, int?> silent)
    {
        var stops = new List<double>();
        for (var t = from; t <= to; t += 5)
            if (d.Observe(t, call, silent(t))) stops.Add(t);
        return stops;
    }

    [Fact]
    public void StopsAMinuteAfterTheCallAppLetsGoAndTheAudioStops()
    {
        var d = new CallEndDetector();
        Assert.Empty(Run(d, 0, 600, true, _ => 0));
        // The call ends at 605: Meet stops using the mic and the tab goes quiet.
        Assert.Equal([665], Run(d, 605, 900, false, t => (int)(t - 605)));
    }

    [Fact]
    public void AnInPersonMeetingNeverStopsByItself() =>
        Assert.Empty(Run(new CallEndDetector(), 0, 3_600, false, t => (int)t));

    [Fact]
    public void CallAudioStillPlayingKeepsRecording()
    {
        // The call app let go of the mic (muted in some apps) but the others are still talking.
        var d = new CallEndDetector();
        Run(d, 0, 60, true, _ => 0);
        Assert.Empty(Run(d, 65, 600, false, _ => 2));
    }

    [Fact]
    public void RejoiningResetsTheClock()
    {
        var d = new CallEndDetector();
        Run(d, 0, 60, true, _ => 0);
        Assert.Empty(Run(d, 65, 100, false, _ => 100));
        Run(d, 105, 120, true, _ => 0);
        Assert.Empty(Run(d, 125, 180, false, _ => 100));
        Assert.Equal([185], Run(d, 185, 185, false, _ => 100));
    }

    [Fact]
    public void WithoutMeetingAudioTheAppAloneDecides()
    {
        var d = new CallEndDetector();
        Run(d, 0, 30, true, _ => null);
        Assert.Equal([95], Run(d, 35, 200, false, _ => null));
    }

    [Fact]
    public void UnknownCallStateNeverStops() =>
        Assert.Empty(Run(new CallEndDetector(), 0, 3_600, null, _ => 1_000));

    [Fact]
    public void OurOwnSessionAndTheSystemSessionDoNotCountAsACall()
    {
        const uint self = 4242;
        Assert.False(CaptureSessions.AnotherAppIsRecording([new(self, true), new(0, true), new(77, false)], self));
        Assert.True(CaptureSessions.AnotherAppIsRecording([new(self, true), new(77, true)], self));
        Assert.False(CaptureSessions.AnotherAppIsRecording([], self));
        Assert.Null(CaptureSessions.AnotherAppIsRecording(null, self));
    }

    [Fact]
    public void ACallSeenThroughSessionsEndsTheRecording()
    {
        // Zoom (pid 900) records while we do; then only our session is left and the call audio stops.
        const uint self = 10;
        var d = new CallEndDetector();
        var during = new CaptureSession[] { new(self, true), new(900, true) };
        var after = new CaptureSession[] { new(self, true), new(900, false) };
        Assert.False(d.Observe(0, CaptureSessions.AnotherAppIsRecording(during, self), 0));
        Assert.False(d.Observe(5, CaptureSessions.AnotherAppIsRecording(after, self), 0));
        Assert.True(d.Observe(65, CaptureSessions.AnotherAppIsRecording(after, self), 60));
    }

    [Fact]
    public void AutoStopDefaultsOnAndDecodesFromOlderFiles()
    {
        Assert.True(Preferences.FromJson("""{"hotkey":"rightCtrl","meetingsDetect":true}""").MeetingsAutoStop);
        var off = new Preferences { MeetingsAutoStop = false };
        Assert.False(Preferences.FromJson(off.ToJson()).MeetingsAutoStop);
    }
}

public class CaptureConverterTests
{
    static byte[] Floats(float[] interleaved) => MemoryMarshal.AsBytes(interleaved.AsSpan()).ToArray();

    static float[] Tone(int frames, int rate, int channels, Func<int, int, float>? channel = null)
    {
        var r = new float[frames * channels];
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < channels; c++)
                r[f * channels + c] = (channel?.Invoke(f, c) ?? 1) * 0.3f * MathF.Sin(2 * MathF.PI * 300 * f / rate);
        return r;
    }

    static readonly CaptureFormat Stereo48 = new(48_000, 2, 32, true);

    [Fact]
    public void AFormatChangeMidCaptureKeepsAudioFlowing()
    {
        var converter = new CaptureConverter(oneChannel: true);
        var output = new List<float>();
        for (int i = 0; i < 5; i++) output.AddRange(converter.Convert(Floats(Tone(4_800, 48_000, 2)), Stereo48));
        // The device moves to 44.1 kHz with four channels, as 16-bit PCM.
        var four = new CaptureFormat(44_100, 4, 16, false);
        var pcm = Tone(4_410, 44_100, 4).SelectMany(v => BitConverter.GetBytes((short)(v * 32767))).ToArray();
        var after = new List<float>();
        for (int i = 0; i < 5; i++) after.AddRange(converter.Convert(pcm, four));
        Assert.Equal(1, converter.FormatChanges);
        Assert.InRange(output.Count, 7_000, 8_000);
        Assert.InRange(after.Count, 7_000, 8_000);
        Assert.True(AudioMath.Dbfs(AudioMath.Rms(after.Skip(2_000).ToArray())) > -15);
    }

    [Fact]
    public void AnInvertedSecondChannelDoesNotCancelTheVoice()
    {
        // Laptop microphone arrays often deliver the second channel inverted. Averaging would leave nothing.
        var converter = new CaptureConverter(oneChannel: true);
        var output = converter.Convert(Floats(Tone(48_000, 48_000, 2, (_, c) => c == 0 ? 1 : -1)), Stereo48);
        Assert.True(AudioMath.Dbfs(AudioMath.Rms(output.AsSpan(2_000))) > -15);
        var averaged = new CaptureConverter(oneChannel: false).Convert(Floats(Tone(48_000, 48_000, 2, (_, c) => c == 0 ? 1 : -1)), Stereo48);
        Assert.True(AudioMath.Dbfs(AudioMath.Rms(averaged.AsSpan(2_000))) < -60);
    }

    [Fact]
    public void AMicrophoneOnTheSecondInputIsFound()
    {
        var converter = new CaptureConverter(oneChannel: true);
        var output = new List<float>();
        for (int i = 0; i < 20; i++) output.AddRange(converter.Convert(Floats(Tone(4_800, 48_000, 2, (_, c) => c)), Stereo48));
        Assert.Equal(1, converter.Channel);
        Assert.True(AudioMath.Dbfs(AudioMath.Rms(output.Skip(output.Count / 2).ToArray())) > -15);
    }

    [Fact]
    public void APartialFrameWaitsForTheNextBuffer()
    {
        var converter = new CaptureConverter(oneChannel: true);
        var bytes = Floats(Tone(1_600, 16_000, 2));
        var a = converter.Convert(bytes.AsSpan(0, 1_001), new CaptureFormat(16_000, 2, 32, true));
        var b = converter.Convert(bytes.AsSpan(1_001), new CaptureFormat(16_000, 2, 32, true));
        Assert.Equal(1_600, a.Length + b.Length);
    }

    [Fact]
    public void AnUnusableFormatIsIgnored()
    {
        var converter = new CaptureConverter(oneChannel: true);
        Assert.Empty(converter.Convert(new byte[64], new CaptureFormat(0, 2, 32, true)));
        Assert.Empty(converter.Convert(new byte[64], new CaptureFormat(48_000, 2, 12, false)));
    }
}

public class MeetingAudioPresenceTests
{
    static readonly MeetingRecorder.Settings Settings = new("gsk_test", "whisper-large-v3-turbo", [], new WhisperPrompts(), null, true);

    sealed class LevelSource : IMeetingAudioSource
    {
        public bool Zeros;
        public void StartCapture() { }
        public void StopCapture() { }
        public float[] DrainSamples() => Enumerable.Repeat(Zeros ? 0f : 0.1f, 16_000).ToArray();
        public float? CurrentLevel() => Zeros ? 0 : 0.8f;
        public void Dispose() { }
    }

    static MeetingRecorder Recorder(LevelSource mic, LevelSource call) =>
        new(_ => mic, () => call, (_, _, _) => Task.FromResult(new DetailedTranscript("", "en", [])), _ => Task.CompletedTask);

    [Fact]
    public async Task MeetingAudioThatNeverPlaysIsCountedFromTheStart()
    {
        var mic = new LevelSource();
        var call = new LevelSource { Zeros = true };
        var recorder = Recorder(mic, call);
        recorder.Start("t", "", [], Settings);
        Assert.True(recorder.RecordsSystemAudio);
        for (int i = 0; i < 5; i++) recorder.Tick();
        Assert.Equal(0, recorder.SystemHeardSeconds);
        Assert.Equal(5, recorder.SystemSilentSeconds);
        call.Zeros = false;
        recorder.Tick();
        Assert.Equal(1, recorder.SystemHeardSeconds);
        Assert.Equal(0, recorder.SystemSilentSeconds);
        await recorder.StopAsync();
    }

    [Fact]
    public async Task LevelsRefreshBetweenTicks()
    {
        var recorder = Recorder(new LevelSource(), new LevelSource());
        recorder.Start("t", "", [], Settings);
        recorder.RefreshLevels();
        Assert.Equal(0.8f, recorder.MicLevel);
        Assert.Equal(0.8f, recorder.SystemLevel);
        await recorder.StopAsync();
        recorder.RefreshLevels();
        Assert.Equal(0, recorder.MicLevel);
        Assert.Equal(0, recorder.SystemLevel);
    }
}
