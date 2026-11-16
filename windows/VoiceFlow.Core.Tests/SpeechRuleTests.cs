using VoiceFlow.Core.Audio;
using Xunit;

namespace VoiceFlow.Core.Tests;

/// <summary>Speech-like test audio: syllables of a voiced harmonic sound with gaps and word pauses, over a noise floor. Same as macOS.</summary>
internal static class TestVoice
{
    /// <summary>Deterministic white noise at <paramref name="db"/> dBFS RMS.</summary>
    public static float[] Noise(double seconds, float db, ulong seed = 7)
    {
        var state = seed;
        var amplitude = (float)(Math.Pow(10, db / 20.0) * Math.Sqrt(3));   // uniform -a...a has RMS a/√3
        var result = new float[(int)(seconds * 16_000)];
        for (int i = 0; i < result.Length; i++)
        {
            state = unchecked(state * 6364136223846793005UL + 1442695040888963407UL);
            result[i] = ((float)(state >> 40) / (1 << 24) * 2 - 1) * amplitude;
        }
        return result;
    }

    /// <summary>Speech at about <paramref name="db"/> dBFS RMS while voiced, over a room at <paramref name="roomDb"/>.</summary>
    public static float[] Speech(double seconds, float db = -26, float roomDb = -62)
    {
        const int rate = 16_000;
        var voice = new float[(int)(seconds * rate)];
        int syllable = (int)(0.18 * rate), gap = (int)(0.07 * rate), wordPause = (int)(0.25 * rate);
        var i = (int)(0.3 * rate);
        var n = 0;
        while (i + syllable < voice.Length)
        {
            var f0 = (float)(120 + n % 5 * 12);
            for (int k = 0; k < syllable; k++)
            {
                var t = (float)k / rate;
                var envelope = MathF.Sin(MathF.PI * k / syllable);
                float v = 0;
                for (int h = 1; h <= 6; h++) v += MathF.Sin(2 * MathF.PI * f0 * h * t) / h;
                voice[i + k] = v * envelope;
            }
            n++;
            i += syllable + (n % 3 == 0 ? wordPause : gap);
        }
        var voicedRms = AudioMath.Rms(voice.Where(v => v != 0).ToArray());
        var scale = (float)Math.Pow(10, db / 20.0) / voicedRms;
        var room = Noise(seconds, roomDb);
        return voice.Zip(room, (v, r) => v * scale + r).ToArray();
    }

    public static float[] Attenuated(float[] samples, float db)
    {
        var g = (float)Math.Pow(10, -db / 20.0);
        return samples.Select(s => s * g).ToArray();
    }
}

public class SpeechRuleTests
{
    /// <summary>The old fixed rule, for the before-and-after comparison.</summary>
    static double OldVoicedSeconds(float[] samples) => AudioMath.VoicedSeconds(samples, thresholdDb: -45);

    [Fact]
    public void NormalSpeechIsSpeechAndIsNotRaised()
    {
        var s = TestVoice.Speech(3);
        var a = SpeechRule.Analyze(s);
        Assert.True(a.VoicedSeconds > 1.2);
        Assert.Equal(CaptureVerdict.Speech, AudioMath.Verdict(s, 3));
        Assert.True(SpeechRule.UploadGainDb(a) < 3);
    }

    /// <summary>
    /// A microphone under a call's voice processing: everything about 10 to 15 dB down, or more when the call also
    /// ducks the speakers in a test. The old -45 dBFS floor lost it; the floor-relative rule keeps it.
    /// </summary>
    [Fact]
    public void SpeechTenToTwentyFiveDecibelsDownIsStillSpeechAndIsRaised()
    {
        var normal = TestVoice.Speech(3);
        foreach (var drop in new float[] { 10, 15, 20, 25 })
        {
            var quiet = TestVoice.Attenuated(normal, drop);
            var a = SpeechRule.Analyze(quiet);
            Assert.True(a.VoicedSeconds > 1.2, $"drop {drop} dB");
            Assert.False(AudioMath.IsSilent(quiet), $"drop {drop} dB");
            Assert.Equal(CaptureVerdict.Speech, AudioMath.Verdict(quiet, 3));
            Assert.False(HallucinationFilter.IsLikelyHallucination("Thank you.", a.VoicedSeconds));
            // Raised back to the upload level, or by the most gain allowed.
            var raised = SpeechRule.Analyze(SpeechRule.PrepareForUpload(quiet));
            var expected = Math.Min(SpeechRule.UploadSpeechDb, a.SpeechDb + SpeechRule.MaximumGainDb);
            Assert.True(Math.Abs(raised.SpeechDb - expected) < 1.5, $"drop {drop} dB: {raised.SpeechDb}");
        }
        // Before: 25 dB down the fixed floor called it silence.
        Assert.True(OldVoicedSeconds(TestVoice.Attenuated(normal, 25)) < 0.25);
    }

    /// <summary>Free-tier budget: a room with nobody talking is never uploaded, at any noise level.</summary>
    [Fact]
    public void RoomNoiseIsNeverSpeech()
    {
        foreach (var db in new float[] { -80, -72, -60, -50, -45, -40, -36 })
        {
            var room = TestVoice.Noise(5, db, (ulong)(100 - (int)db));
            Assert.True(AudioMath.IsSilent(room), $"room at {db} dB");
            Assert.Equal(CaptureVerdict.NoSpeech, AudioMath.Verdict(room, 5));
        }
        // Before: a noisy room above the fixed floor counted as speech and was uploaded.
        Assert.True(OldVoicedSeconds(TestVoice.Noise(5, -40)) > 0.25);
    }

    [Fact]
    public void DigitalSilenceAndClicksStaySilent()
    {
        Assert.True(AudioMath.IsSilent(new float[48_000]));
        var clicks = new float[48_000];
        for (int k = 1_000; k < 48_000; k += 8_000) clicks[k] = 0.8f;
        Assert.True(AudioMath.IsSilent(clicks));
        Assert.Null(SpeechRule.Analyze(new float[16_000]).NoiseFloorDb);
    }

    [Fact]
    public void AMicrophoneThatGatesToZeroBetweenWordsStillCountsTheWords()
    {
        var gated = TestVoice.Speech(3, db: -30, roomDb: -200).Select(v => Math.Abs(v) < 0.000_01f ? 0 : v).ToArray();
        Assert.False(AudioMath.IsSilent(gated));
    }

    [Fact]
    public void ThresholdFollowsTheFloorWithinLimits()
    {
        Assert.Equal(SpeechRule.AbsoluteMinimumDb, SpeechRule.Threshold(null));
        Assert.Equal(SpeechRule.AbsoluteMinimumDb, SpeechRule.Threshold(-100));
        Assert.Equal(-50, SpeechRule.Threshold(-60));
        Assert.Equal(SpeechRule.CeilingDb, SpeechRule.Threshold(-20));
    }

    [Fact]
    public void GainIsCappedAndSilenceIsNotRaised()
    {
        Assert.Equal(new float[16_000], SpeechRule.PrepareForUpload(new float[16_000]));
        var faint = TestVoice.Attenuated(TestVoice.Speech(2), 40);
        Assert.Equal(SpeechRule.MaximumGainDb, SpeechRule.UploadGainDb(SpeechRule.Analyze(faint)));
    }

    [Fact]
    public void TheConstantsMatchMacOS()
    {
        Assert.Equal(-70, SpeechRule.AbsoluteMinimumDb);
        Assert.Equal(10, SpeechRule.MarginDb);
        Assert.Equal(-35, SpeechRule.CeilingDb);
        Assert.Equal(-24, SpeechRule.UploadSpeechDb);
        Assert.Equal(24, SpeechRule.MaximumGainDb);
        Assert.Equal(30, SpeechRule.WhisperModeMaximumGainDb);
    }
}

public class QuietMeetingChunkTests
{
    [Fact]
    public void QuietCallSpeechIsChunkedAndRoomNoiseIsNot()
    {
        var chunker = new StreamChunker();
        var chunks = new List<AudioChunk>();
        // 60 s of speech 20 dB down, as heard through a voice-processing call.
        for (int k = 0; k < 20; k++) chunks.AddRange(chunker.Feed(TestVoice.Attenuated(TestVoice.Speech(3), 20)));
        if (chunker.Finish() is { } last) chunks.Add(last);
        Assert.True(chunks.Sum(c => c.VoicedSeconds) > 25);

        var quietRoom = new StreamChunker();
        var noiseChunks = new List<AudioChunk>();
        for (int k = 0; k < 60; k++) noiseChunks.AddRange(quietRoom.Feed(TestVoice.Noise(5, -50, (ulong)(k + 1))));
        if (quietRoom.Finish() is { } tail) noiseChunks.Add(tail);
        Assert.Empty(noiseChunks);
    }

    [Fact]
    public void SpeechAfterALongDigitalSilenceIsHeardAtOnce()
    {
        var gate = new VoiceGate();
        var anyVoiced = false;
        for (int k = 0; k < 600; k++) if (gate.IsVoiced(-120)) anyVoiced = true;
        Assert.False(anyVoiced);
        Assert.True(gate.IsVoiced(-30));
    }
}
