using System.Text.Json;
using VoiceFlow.Core.Audio;
using VoiceFlow.Core.Meetings;
using VoiceFlow.Core.Output;
using VoiceFlow.Core.AI;
using VoiceFlow.Core.Speech;
using Xunit;

namespace VoiceFlow.Core.Tests;

// Ported from the macOS LanguagePipelineTests.swift so both apps are held to the same cases.

public class LanguageEvidenceTests
{
    [Fact]
    public void EnglishReadsAsEnglish() =>
        Assert.True(LanguageEvidence.ReadsAsEnglish("I think we should check the report with the team before the call today"));

    [Fact]
    public void TeluguSpelledBySoundFails() =>
        Assert.False(LanguageEvidence.ReadsAsEnglish("Neme rebu aafisu ki vastaanu meetingu padi gantalaki undi ani cheppandi"));

    [Fact]
    public void TooShortSaysNothing()
    {
        Assert.Null(LanguageEvidence.ReadsAsEnglish("Okay thanks"));
        Assert.Null(LanguageEvidence.ReadsAsHindi("मैं कल"));
    }

    [Fact]
    public void RealHindiPassesAndDisguisedTeluguFails()
    {
        Assert.True(LanguageEvidence.ReadsAsHindi("मैं कल ओफिस आएंगा, मीटिंग 10 बजे है, रिपोर्ट तयार करके भेज दूगा"));
        Assert.False(LanguageEvidence.ReadsAsHindi("नेनु रेपू ओफीस की वस्तानू, मीटिंग पदी गंटलकी उन्दी प्राउजेक्ट रिपोर्ट रेडी चेसी"));
    }
}

public class ResolverListTests
{
    const string Hindi = "मैं कल ओफिस आएंगा, मीटिंग 10 बजे है, रिपोर्ट तयार करके भेज दूगा";
    const string Disguised = "Neme rebu aafisu ki vastaanu meetingu padi gantalaki undi ani cheppandi";
    static DetailedTranscript T(string text, string? lang, double? conf) => new(text, lang, [], conf);
    static LanguageOpinion O(string lang, string text, double conf) => new(lang, T(text, lang, conf));

    [Fact]
    public void RealHindiIsNotReplacedByForcedTelugu() =>
        Assert.Equal("hi", LanguageResolver.Choose(T(Hindi, "hi", -0.074), [O("te", "మెకల ఉఫిస ఆంగా", -0.31)], new LanguagePlan(["en", "te"])).Language);

    [Fact]
    public void SpanishOnTheCallIsLeftAlone() =>
        Assert.Empty(LanguageResolver.OpinionLanguages(T("Hola a todos, vamos a empezar la reunión de hoy", "es", -0.2), new LanguagePlan(["en", "te"], PlanSpeaker.Anyone)));

    [Fact]
    public void SpanishFromTheUserGetsAnOpinion() =>
        Assert.Equal(["te"], LanguageResolver.OpinionLanguages(T("Hola a todos", "es", -0.2), new LanguagePlan(["en", "te"])));

    [Fact]
    public void DisguisedTeluguAsksAndWins()
    {
        var plan = new LanguagePlan(["en", "te"], PlanSpeaker.Anyone);
        var auto = T(Disguised, "en", -0.22);
        Assert.Equal(["te"], LanguageResolver.OpinionLanguages(auto, plan));
        Assert.Equal("te", LanguageResolver.Choose(auto, [O("te", "నేను రేపు office కి వస్తాను", -0.4)], plan).Language);
    }

    [Fact]
    public void AccentedEnglishSurvivesAFalseAlarm() =>
        Assert.Equal("en", LanguageResolver.Choose(T(Disguised, "en", -0.15), [O("te", "గరిగాాందాలిండు", -0.70)], new LanguagePlan(["en", "te"])).Language);

    [Fact]
    public void SeededSameLanguageResultIsPreferred()
    {
        var output = LanguageResolver.Choose(T("मैं कल ओफिस आएंगा, मीटिंग 10 बजे है, रिपोर्ट भेज दूगा", "hi", -0.074),
                                             [O("hi", "मैं कल office आऊँगा, meeting 10 बजे है, report भेज दूँगा", -0.041)], new LanguagePlan(["en", "hi"]));
        Assert.Contains("office", output.Text);
    }

    [Fact]
    public void GarbledForcedHindiLosesToAuto() =>
        Assert.Equal("en", LanguageResolver.Choose(T(Disguised, "en", -0.3), [O("hi", "प्यर कलोई स्थर्मल पीसिह सेहिसंग प्रश्चिमी अफरीकी रंदनेविन", -0.2)], new LanguagePlan(["en", "hi"])).Language);

    [Fact]
    public void ManyLanguagesAreCappedAndRankedByConfusion()
    {
        var langs = LanguageResolver.OpinionLanguages(T("नेनु रेपू ओफीस की वस्तानू", "hi", -0.5), new LanguagePlan(["en", "es", "te", "hi", "ta"]));
        Assert.Equal(LanguageResolver.MaxOpinions, langs.Count);
        Assert.DoesNotContain("es", langs);
        Assert.DoesNotContain("hi", langs);
    }

    [Fact]
    public void LoopingOpinionNeverWins()
    {
        var seg = new TranscriptSegment(0, 1, "ఓకే");
        var loop = new DetailedTranscript("ఓకే ఓకే ఓకే", "te", [seg, seg, seg], -0.01);
        Assert.Equal("en", LanguageResolver.Choose(T(Disguised, "en", -0.3), [new LanguageOpinion("te", loop)], new LanguagePlan(["en", "te"])).Language);
    }

    [Fact]
    public void SwitchingLanguageFailsTheStickyCheck()
    {
        Assert.True(LanguageResolver.ForcedResultAcceptable(T("నేను రేపు వస్తాను", "te", -0.08)));
        Assert.False(LanguageResolver.ForcedResultAcceptable(T("మెకల ఉఫిస ఆంగా", "te", -0.31)));
        Assert.False(LanguageResolver.ForcedResultAcceptable(T("", "te", -0.01)));
    }

    [Fact]
    public void TrackerSticksRechecksAndLetsGo()
    {
        var tracker = new StreamLanguageTracker();
        string[] listed = ["en", "te"];
        Assert.Null(tracker.NextHint());
        tracker.Observe("te", true, listed);
        Assert.Null(tracker.Sticky);
        tracker.Observe("te", true, listed);
        Assert.Equal("te", tracker.Sticky);
        Assert.Single(Enumerable.Range(0, 5).Select(_ => tracker.NextHint()), h => h == null);
        tracker.Observe("en", false, listed);
        Assert.Null(tracker.Sticky);
    }
}

public class WhisperPromptTests
{
    [Fact]
    public void AutoDetectRequestsAreNeverSeeded()
    {
        Assert.Equal("Saideep", new WhisperPrompts(["Saideep"]).Text(null));
        Assert.Null(new WhisperPrompts().Text(null));
    }

    [Fact]
    public void ForcedRequestsAreSeededByWritingChoice()
    {
        Assert.Contains("office", new WhisperPrompts([], TeluguScript.Mixed).Text("te"));
        Assert.Contains("ఆఫీస్", new WhisperPrompts([], TeluguScript.Telugu).Text("te"));
        Assert.Contains("ऑफिस", new WhisperPrompts([], Hindi: HindiScript.Devanagari).Text("hi"));
        Assert.Null(new WhisperPrompts().Text("en"));
    }

    [Fact]
    public void StaysInsideGroqsLimitAndKeepsTheSeedLast()
    {
        var text = new WhisperPrompts(Enumerable.Range(0, 400).Select(i => $"Term{i}").ToList()).Text("te")!;
        Assert.True(text.Length <= WhisperPrompt.MaximumCharacters);
        Assert.True(WhisperPrompt.EstimatedTokens(text) <= WhisperPrompt.MaximumEstimatedTokens);
        Assert.EndsWith(WhisperPrompt.TeluguMixedSeed, text, StringComparison.Ordinal);
    }
}

public class LoopFilterTests
{
    static TranscriptLine L(double t, string text, Speaker who = Speaker.Others) => new(t, who, text);

    [Fact]
    public void CompressibleAndSilentSegmentsAreDropped()
    {
        Assert.True(WhisperSegmentFilter.ShouldDrop(new(0, 1, "the same words the same words the same", CompressionRatio: 3.1)));
        Assert.True(WhisperSegmentFilter.ShouldDrop(new(0, 1, "Thanks", AvgLogprob: -1.4, NoSpeechProb: 0.9)));
        Assert.False(WhisperSegmentFilter.ShouldDrop(new(0, 1, "A normal sentence here.", -0.2, 0.01, 1.3)));
    }

    [Fact]
    public void LoopedSegmentsCollapseAndTextIsRebuilt()
    {
        var seg = new TranscriptSegment(0, 1, "Okay.");
        var output = WhisperSegmentFilter.Filtered(new DetailedTranscript("x", "en", [seg, seg, seg, seg, new(4, 5, "Next point.")]));
        Assert.Equal(3, output.Segments.Count);
        Assert.Equal("Okay. Okay. Next point.", output.Text);
    }

    [Fact]
    public void RunsCollapseAcrossChunksAndSpeakers()
    {
        var output = TranscriptFilter.CollapseRepeats([L(0, "Okay."), L(3, "Okay."), L(4, "Sure", Speaker.You), L(6, "Okay."), L(9, "okay"), L(40, "Okay.")]);
        Assert.Equal([0, 4, 40], output.Select(l => l.Time));
    }

    [Fact]
    public void TwoRepeatsAndSlowRepeatsStay()
    {
        Assert.Equal(2, TranscriptFilter.CollapseRepeats([L(0, "Yes"), L(3, "Yes")]).Count);
        Assert.Equal(3, TranscriptFilter.CollapseRepeats([L(0, "Yes"), L(30, "Yes"), L(60, "Yes")]).Count);
    }

    [Fact]
    public void LostMarkersAreNeverCollapsed()
    {
        var m = TranscriptFilter.LostMarker;
        Assert.Equal(3, TranscriptFilter.CollapseRepeats([L(0, m), L(5, m), L(10, m)]).Count);
    }
}

public class StreamChunkerTests
{
    static float[] Speech(double s) => MeetingRecorderTests.Speech(s);
    static float[] Silence(double s) => MeetingRecorderTests.Silence(s);
    static float[] Cat(params float[][] p) => MeetingRecorderTests.Cat(p);

    [Fact]
    public void SilenceIsNeverUploaded()
    {
        var c = new StreamChunker();
        Assert.Empty(c.Feed(Silence(200)));
        Assert.Null(c.Finish());
    }

    [Fact]
    public void OnlyVoicedAudioIsUploadedAndMapsBack()
    {
        var c = new StreamChunker();
        var chunks = c.Feed(Cat(Silence(10), Speech(12), Silence(30), Speech(12), Silence(2)));
        if (c.Finish() is { } last) chunks.Add(last);
        var chunk = Assert.Single(chunks);
        Assert.True(chunk.Samples.Length / 16_000.0 < 26);
        Assert.True(Math.Abs(chunk.Map.MeetingTime(0.3) - 10) < 0.4);
        Assert.True(Math.Abs(chunk.Map.MeetingTime(chunk.Map.Spans[^1].ChunkStart + 0.3) - 52) < 0.4);
        Assert.Single(chunk.Map.SpliceGaps(2));
    }

    [Fact]
    public void ContinuousSpeechIsCutAt45Seconds()
    {
        var c = new StreamChunker();
        var chunks = c.Feed(Speech(100));
        if (c.Finish() is { } last) chunks.Add(last);
        var seconds = chunks.Select(x => x.Samples.Length / 16_000.0).ToList();
        Assert.All(seconds, s => Assert.True(s <= 45.05));
        Assert.True(Math.Abs(seconds.Sum() - 100) < 0.1);
        Assert.True(Math.Abs(chunks[1].Map.MeetingTime(0) - 45) < 0.1);
    }

    [Fact]
    public void AQuietSideStillShowsUpAfterMaxWait() =>
        Assert.Single(new StreamChunker().Feed(Cat(Speech(3), Silence(125))));

    [Fact]
    public void NoisyRoomStaysSilentButVoiceGetsThrough()
    {
        var c = new StreamChunker();
        var random = new Random(4);
        // Steady hiss at about -50 dB is under the absolute floor; speech on top is voiced.
        Assert.Empty(c.Feed(Enumerable.Range(0, 16_000 * 30).Select(_ => (float)(random.NextDouble() * 0.009 - 0.0045)).ToArray()));
        Assert.Single(c.Feed(Cat(Speech(25), Silence(1))));
    }

    [Fact]
    public void SplittingAtARemovedPauseUsesWordTimes()
    {
        var map = new TimeMap([new(0, 100, 2), new(2.25, 160, 2)]);
        TranscriptWord[] words = [new("before", 0.2, 0.8), new("pause", 1, 1.6), new("after", 2.4, 3)];
        var t = new DetailedTranscript("before pause after", "en", [new(0.2, 3, "before pause after", Words: words)]);
        var lines = ChunkLines.Lines(t, map, Speaker.You, 100);
        Assert.Equal(["before pause", "after"], lines.Select(l => l.Text));
        Assert.Equal(160.15, lines[1].Time, 2);
    }

    [Fact]
    public void WordTimestampsAreParsedAndGivenToTheirSegments()
    {
        var json = """{"text":"a b","language":"english","segments":[{"start":0,"end":1,"text":"a","compression_ratio":1.2,"no_speech_prob":0.1},{"start":1,"end":2,"text":"b"}],"words":[{"word":" a","start":0.1,"end":0.9},{"word":"b","start":1.1,"end":1.9}]}""";
        var d = GroqTranscriber.ParseVerbose(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Equal("a", Assert.Single(d.Segments[0].Words!).Word);
        Assert.Equal("b", Assert.Single(d.Segments[1].Words!).Word);
        Assert.Equal(1.2, d.Segments[0].CompressionRatio);
    }

    [Fact]
    public async Task WordTimestampsAreRequested()
    {
        using var content = GroqTranscriber.BuildContent(AudioMath.Wav(new float[160]), "m", null, null, "verbose_json", wordTimestamps: true);
        Assert.Contains("timestamp_granularities[]", await content.ReadAsStringAsync());
    }
}

public class EchoTests
{
    const string Sentence = "the payment service is ready for the next release";
    static TranscriptLine L(double t, string text, Speaker who) => new(t, who, text);

    [Fact]
    public void CallHeardByTheMicIsRemoved() =>
        Assert.Equal([Speaker.Others], TranscriptMerge.Merge([L(10, Sentence, Speaker.Others)], [L(10.8, "payment service is ready for the next release", Speaker.You)]).Select(l => l.Speaker));

    [Fact]
    public void RealAnswersStay() =>
        Assert.Equal(2, TranscriptMerge.Merge([L(10, Sentence, Speaker.Others)], [L(13, "great, let's ship it on Friday then", Speaker.You)]).Count);

    [Fact]
    public void ShortRepliesAreNeverEchoes() =>
        Assert.Equal(2, TranscriptMerge.Merge([L(10, "Okay.", Speaker.Others)], [L(10.2, "Okay.", Speaker.You)]).Count);

    [Fact]
    public void FarApartLinesAreNotEchoes() =>
        Assert.Equal(2, TranscriptMerge.Merge([L(10, Sentence, Speaker.Others)], [L(60, Sentence, Speaker.You)]).Count);

    [Fact]
    public void MergeIsOrderedByTime() =>
        Assert.Equal([5, 10, 20], TranscriptMerge.Merge([L(5, "one two", Speaker.Others), L(20, "five six", Speaker.Others)], [L(10, "three four", Speaker.You)]).Select(l => l.Time));
}

public class RateBudgetTests
{
    static readonly DateTime T0 = new(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
    static readonly GroqCost Whisper = GroqCost.Transcription("whisper-large-v3-turbo", 30);

    [Fact]
    public void BackgroundLeavesRoomForDictation()
    {
        var b = new RateBudget(RateLimits.Whisper);
        for (int i = 0; i < 16; i++) b.Record(Whisper, T0);
        Assert.InRange(b.Decision(Whisper, GroqPriority.Background, T0.AddSeconds(1)).WaitSeconds, 50.01, 60);
        Assert.True(b.Decision(Whisper, GroqPriority.Interactive, T0.AddSeconds(1)).IsGo);
    }

    [Fact]
    public void ShortUploadsAreBilledTenSeconds()
    {
        Assert.Equal(10, GroqCost.Transcription("w", 2).BilledAudioSeconds);
        Assert.Equal(30, Whisper.BilledAudioSeconds);
    }

    [Fact]
    public void HourlyAudioBudgetHolds()
    {
        var b = new RateBudget(RateLimits.Whisper);
        for (int i = 0; i < 220; i++) b.Record(Whisper, T0.AddSeconds(i * 10));   // 6,600 s billed
        var now = T0.AddSeconds(2_210);
        Assert.False(b.Decision(Whisper, GroqPriority.Background, now).IsGo);
        Assert.True(b.Decision(Whisper, GroqPriority.Interactive, now).IsGo);
    }

    [Fact]
    public void RetryAfterBlocksBackgroundOnly()
    {
        var b = new RateBudget(RateLimits.Whisper);
        b.ApplyRetryAfter(30, T0);
        Assert.Equal(30, b.Decision(Whisper, GroqPriority.Background, T0).WaitSeconds, 2);
        Assert.InRange(b.Decision(Whisper, GroqPriority.Interactive, T0).WaitSeconds, 0.01, 2);
    }

    [Fact]
    public void HeadersOverrideDefaults()
    {
        var b = new RateBudget(RateLimits.Chat);
        b.Apply(new Dictionary<string, string> { ["x-ratelimit-limit-requests"] = "500", ["X-RateLimit-Limit-Tokens"] = "6000" }, T0);
        Assert.Equal(500, b.Limits.RequestsPerDay);
        Assert.Equal(6_000, b.Limits.TokensPerMinute);
    }

    [Fact]
    public void DurationsParse()
    {
        Assert.Equal(43.2, RateBudget.ParseDuration("43.2s"));
        Assert.Equal(179.5, RateBudget.ParseDuration("2m59.5s"));
        Assert.Equal(3_723, RateBudget.ParseDuration("1h2m3s"));
    }

    [Fact]
    public void DailyLimitTextIsRecognised()
    {
        Assert.Equal("tokens", GroqLimitMessage.DailyResource("Rate limit reached on tokens per day (TPD): Limit 200000"));
        Assert.Equal("transcription", GroqLimitMessage.DailyResource("on seconds of audio per day (ASPD): Limit 28800"));
        Assert.Null(GroqLimitMessage.DailyResource("on seconds of audio per hour (ASPH): Limit 7200"));
    }

    [Fact]
    public void DailyLimitMessageSaysWhenItResets() =>
        Assert.Contains("resets in about 2 hours", ProviderException.DailyLimit("transcription", 7_200).Message);
}

public class GroqGateTests
{
    static readonly GroqCost Cost = GroqCost.Transcription("whisper-large-v3-turbo", 30);
    static Task<GroqResponse> Ok() => Task.FromResult(new GroqResponse([], new Dictionary<string, string>()));

    [Fact]
    public async Task DictationGoesAheadOfABlockedBackgroundQueue()
    {
        var gate = new GroqGate(sleep: (_, _) => Task.Delay(5));
        for (int i = 0; i < 16; i++) await gate.PerformAsync(Cost, GroqPriority.Background, Ok);
        var started = DateTime.UtcNow;
        await gate.PerformAsync(Cost, GroqPriority.Interactive, Ok);
        Assert.True((DateTime.UtcNow - started).TotalSeconds < 1);
    }

    [Fact]
    public async Task BackgroundGivesUpInsteadOfWaitingForever()
    {
        var gate = new GroqGate(sleep: (_, _) => Task.CompletedTask);
        var e = await Assert.ThrowsAsync<ProviderException>(async () =>
        {
            for (int i = 0; i < 40; i++) await gate.PerformAsync(Cost, GroqPriority.Background, Ok);
        });
        Assert.Equal(ProviderErrorKind.RateLimited, e.Kind);
    }

    [Fact]
    public async Task ADailyLimitBlocksBackgroundWork()
    {
        var gate = new GroqGate(sleep: (_, _) => Task.CompletedTask);
        await Assert.ThrowsAsync<ProviderException>(() => gate.PerformAsync(Cost, GroqPriority.Background, () => throw ProviderException.DailyLimit("transcription", 7_200)));
        await Assert.ThrowsAsync<ProviderException>(() => gate.PerformAsync(Cost, GroqPriority.Background, Ok));
    }
}

public class ChunkHoldTests
{
    static HeldChunk Chunk(double s) => new(Enumerable.Repeat(0.1f, (int)(s * 16_000)).ToArray(), new TimeMap([]), Speaker.Others, s);

    [Fact]
    public void OldestIsEvictedWhenFull()
    {
        var hold = new ChunkHold(100);
        Assert.Empty(hold.Add(Chunk(60)));
        Assert.Single(hold.Add(Chunk(60)));
        Assert.Equal(60, hold.TotalSeconds);
        Assert.Single(hold.TakeAll());
        Assert.Empty(hold.Chunks);
    }

    [Fact]
    public void SamplesSurviveTheRoundTrip()
    {
        var held = new HeldChunk([0.5f, -0.5f], new TimeMap([]), Speaker.You, 1);
        Assert.Equal(0.5f, held.FloatSamples[0], 3);
        Assert.Equal(-0.5f, held.FloatSamples[1], 3);
    }
}

public class SeparateStreamsTests
{
    static readonly MeetingRecorder.Settings Settings = MeetingRecorderTests.Settings;
    static Task NoWait(TimeSpan _) => Task.CompletedTask;
    static float[] Speech(double s) => MeetingRecorderTests.Speech(s);

    [Fact]
    public async Task EachSideIsUploadedSeparatelyAndOverlapStaysApart()
    {
        var mic = new FakeSource();
        var system = new FakeSource();
        var plans = new System.Collections.Concurrent.ConcurrentBag<PlanSpeaker>();
        var recorder = new MeetingRecorder(_ => mic, () => system, (_, r, _) =>
        {
            plans.Add(r.Plan.Speaker);
            var who = r.Plan.Speaker == PlanSpeaker.User ? "mine words here now" : "their different sentence over";
            return Task.FromResult(new DetailedTranscript(who, "en", [new(0, 5, who)]));
        }, NoWait);
        recorder.Start("T", "", [], Settings);
        mic.Push(Speech(5));
        system.Push(Speech(5));
        var meeting = await recorder.StopAsync();
        Assert.Equal([PlanSpeaker.Anyone, PlanSpeaker.User], plans.Distinct().OrderByDescending(p => p));
        Assert.Equal(2, meeting!.Lines.Select(l => l.Speaker).Distinct().Count());
    }

    [Fact]
    public async Task FailedChunkIsRetriedAtTheEndNotMarked()
    {
        var mic = new FakeSource();
        int tries = 0;
        var recorder = new MeetingRecorder(_ => mic, () => null, (_, _, _) =>
        {
            if (Interlocked.Increment(ref tries) <= MeetingRecorder.LiveAttempts) throw ProviderException.Network("offline");
            return Task.FromResult(new DetailedTranscript("recovered", "en", []));
        }, NoWait);
        recorder.Start("T", "", [], Settings);
        mic.Push(Speech(5));
        var meeting = await recorder.StopAsync();
        Assert.Equal(["recovered"], meeting!.Lines.Select(l => l.Text));
        Assert.Null(meeting.Note);
    }

    [Fact]
    public async Task DailyLimitIsToldPlainlyAndMarked()
    {
        var mic = new FakeSource();
        var recorder = new MeetingRecorder(_ => mic, () => null, (_, _, _) => throw ProviderException.DailyLimit("transcription", 7_200), NoWait);
        string? told = null;
        recorder.DailyLimitReached += m => told = m;
        recorder.Start("T", "", [], Settings);
        mic.Push(Speech(5));
        var meeting = await recorder.StopAsync();
        Assert.Equal(TranscriptFilter.LostMarker, meeting!.Lines[0].Text);
        Assert.Contains("daily limit", meeting.Note);
        Assert.Contains("daily limit", told);
    }

    [Fact]
    public async Task QuittingMarksFailedChunksWithoutRetrying()
    {
        var mic = new FakeSource();
        int tries = 0;
        var recorder = new MeetingRecorder(_ => mic, () => null, (_, _, _) =>
        {
            Interlocked.Increment(ref tries);
            throw ProviderException.Network("offline");
        }, NoWait);
        recorder.Start("T", "", [], Settings);
        mic.Push(Speech(5));
        var meeting = await recorder.StopAsync(retryFailed: false);
        Assert.Equal(TranscriptFilter.LostMarker, meeting!.Lines[0].Text);
        Assert.Equal(MeetingRecorder.LiveAttempts, tries);
    }

    [Fact]
    public async Task LanguageOfEachSideIsReported()
    {
        var mic = new FakeSource();
        var recorder = new MeetingRecorder(_ => mic, () => null, (_, _, _) => Task.FromResult(new DetailedTranscript("నేను వస్తాను", "te", [])), NoWait);
        recorder.Start("T", "", [], Settings);
        mic.Push(Speech(5));
        var meeting = await recorder.StopAsync();
        Assert.Equal("te", recorder.MicLanguage);
        Assert.Equal("te", meeting!.Lines[0].Language);
    }
}

public class ScriptWriterTests
{
    [Fact]
    public void OnlyLinesThatNeedItAreSent()
    {
        Assert.True(TranscriptScriptWriter.NeedsRewrite("నేను రేపు office కి వస్తాను", TeluguScript.Mixed, null));
        Assert.False(TranscriptScriptWriter.NeedsRewrite("నేను రేపు వస్తాను", TeluguScript.Telugu, null));
        Assert.True(TranscriptScriptWriter.NeedsRewrite("నేను office", TeluguScript.Telugu, null));
        Assert.False(TranscriptScriptWriter.NeedsRewrite("Plain English line", TeluguScript.Mixed, HindiScript.Mixed));
        Assert.False(TranscriptScriptWriter.NeedsRewrite("मैं कल आऊँगा", TeluguScript.Mixed, null));
    }

    [Fact]
    public void BadRepliesAreRejected()
    {
        Assert.Null(TranscriptScriptWriter.Parse("""{"lines":["a"]}""", ["x", "y"]));
        Assert.Null(TranscriptScriptWriter.Parse("""{"lines":[""]}""", ["x"]));
        Assert.Null(TranscriptScriptWriter.Parse("""{"lines":["one two three four five six seven eight nine"]}""", ["one two"]));
        Assert.Null(TranscriptScriptWriter.Parse("nope", ["x"]));
        Assert.Equal(["ok then"], TranscriptScriptWriter.Parse("""{"lines":["ok then"]}""", ["okay then"]));
    }

    [Fact]
    public void BatchesFitOneRequest()
    {
        var line = new TranscriptLine(0, Speaker.Others, string.Concat(Enumerable.Repeat("నేను రేపు ", 40)));
        var batches = TranscriptScriptWriter.Batches(Enumerable.Repeat(line, 10).ToList(), TeluguScript.Mixed, null);
        Assert.True(batches.Count > 1);
        Assert.Equal(10, batches.Sum(b => b.Count));
    }

    [Fact]
    public void UserMessageHasSortedKeysAndReadableText() =>
        Assert.Equal("""{"hindi":null,"lines":["నేను/ok"],"telugu":"mixed"}""", TranscriptScriptWriter.UserMessage(["నేను/ok"], TeluguScript.Mixed, null));
}

public class MeetingFeatureTests
{
    static Meeting Sample() => new("Sync")
    {
        Lines = [new(0, Speaker.Others, "The payment gateway is down"), new(5, Speaker.You, "నేను చూస్తాను")],
        Notes = new MeetingNotes { Summary = ["Budget moved"], ActionItems = [new ActionItem("Call Ramesh")] },
    };

    [Fact]
    public void SearchCoversTitleNotesAndTranscript()
    {
        var m = Sample();
        Assert.True(MeetingSearch.Matches(m, "sync"));
        Assert.True(MeetingSearch.Matches(m, "RAMESH"));
        Assert.True(MeetingSearch.Matches(m, "చూస్తాను"));
        Assert.False(MeetingSearch.Matches(m, "invoice"));
        Assert.True(MeetingSearch.Matches(m, "  "));
        Assert.Contains("gateway", MeetingSearch.Snippet(m, "gateway"));
        Assert.Null(MeetingSearch.Snippet(m, "sync"));
    }

    [Fact]
    public void SnippetsAreCutToARadius()
    {
        var m = new Meeting("T") { Lines = [new(0, Speaker.You, new string('a', 100) + " needle " + new string('b', 100))] };
        var snippet = MeetingSearch.Snippet(m, "needle", radius: 5)!;
        Assert.Equal("…aaaa needle bbbb…", snippet);
    }

    [Fact]
    public void RenamingOthersReachesTheMarkdownAndYouStays()
    {
        var m = Sample();
        m.OthersLabel = "Priya";
        var md = MeetingMarkdown.Render(m);
        Assert.Contains("**Priya** [0:00]", md);
        Assert.Contains("**You** [0:05]", md);
        Assert.Contains("Priya:", m.TranscriptText);
        m.OthersLabel = "   ";
        Assert.Equal("Others", m.LabelFor(Speaker.Others));
    }

    [Fact]
    public void TicksSurviveRewritingNotes()
    {
        var old = new MeetingNotes { ActionItems = [new ActionItem("Send deck") { Done = true }, new ActionItem("Book room")] };
        var fresh = new MeetingNotes { ActionItems = [new ActionItem("send deck"), new ActionItem("New task")] }.CarryingOverDone(old);
        Assert.Equal([true, false], fresh.ActionItems.Select(a => a.Done));
    }

    [Fact]
    public void OldMeetingsStillDecode()
    {
        const string json = """[{"id":"7E57D004-2B97-4E7A-B45F-5387367791CD","title":"Old","start":"2026-01-01T00:00:00+00:00","app":"","attendees":[],"lines":[{"time":1,"speaker":"you","text":"hi"}],"status":"done","micOnly":false,"speakerNames":{"others":"Rahul"}}]""";
        var m = JsonSerializer.Deserialize<List<Meeting>>(json, JsonFile<Meeting>.Options)![0];
        Assert.Null(m.Lines[0].End);
        Assert.Null(m.Lines[0].Language);
        Assert.Null(m.Note);
        Assert.Null(m.ScriptRewritten);
        Assert.Equal("Rahul", m.OthersLabel);
    }

    [Fact]
    public void NewFieldsSurviveTheJsonRoundTrip()
    {
        var m = new Meeting("T") { Note = "gap", ScriptRewritten = true, Lines = [new(1, Speaker.You, "hi", 2, "te")] };
        var copy = m.Copy();
        Assert.Equal("gap", copy.Note);
        Assert.True(copy.ScriptRewritten);
        Assert.Equal(m.Lines[0], copy.Lines[0]);
    }

    [Fact]
    public void VagueTitlesAreReplaced()
    {
        Assert.True(NotesTitle.IsVague("Unclear Discussion Session"));
        Assert.True(NotesTitle.IsVague("General Meeting"));
        Assert.False(NotesTitle.IsVague("Payment gateway outage"));
        var notes = new MeetingNotes { Summary = ["Payment gateway outage hit checkout on Tuesday morning again"] };
        Assert.Equal("Payment gateway outage hit checkout on", NotesTitle.Resolved("Unclear Discussion Session", notes, "Zoom meeting"));
        Assert.Equal("Zoom meeting", NotesTitle.Resolved("Unclear", new MeetingNotes(), "Zoom meeting"));
    }

    [Fact]
    public void NotesFollowTheWritingChoice()
    {
        var m = new Meeting("T") { Lines = [new(0, Speaker.Others, "నేను రేపు వస్తాను", Language: "te")] };
        Assert.Contains("English letters", MeetingNotesWriter.LanguageRule(NotesLanguage.Meeting, m, TeluguScript.Roman, HindiScript.Mixed));
        Assert.Contains("in English", MeetingNotesWriter.LanguageRule(NotesLanguage.English, m, TeluguScript.Roman, HindiScript.Mixed));
    }

    [Fact]
    public async Task NotesFallBackToTheOtherModelOnADailyLimit()
    {
        var chat = new DailyLimitedChat("big");
        var notes = await new MeetingNotesWriter(chat, "big", fallbackModel: "small")
            .WriteAsync(new Meeting("Meeting") { Lines = [new(0, Speaker.You, "We moved the launch")] }, NotesLanguage.English);
        Assert.Equal("Launch moved to Friday", notes.Title);
        Assert.Equal(["big", "small"], chat.Models);
    }

    [Fact]
    public void HindiChoiceRoundTripsAndOldPreferencesMigrate()
    {
        Assert.Equal(HindiScript.Hinglish, Preferences.FromJson(new Preferences { HindiScript = HindiScript.Hinglish }.ToJson()).HindiScript);
        var old = Preferences.FromJson("""{"languages":["en","te"],"teluguScript":"roman"}""");
        Assert.Equal(["en", "te"], old.Languages);
        Assert.Equal(HindiScript.Mixed, old.HindiScript);
        Assert.Contains("<hindi>hinglish</hindi>", CleanupPrompt.UserMessage("x", "casual", null, [], hindi: "hinglish"));
    }

    [Fact]
    public async Task HindiNeverSkipsTheModel()
    {
        var chat = new FakeChat("मैं आऊँगा।");
        await new TextCleaner(chat, "m").CleanAsync(new CleanupRequest("मैं आऊँगा", WritingStyle.Casual, Hindi: HindiScript.Hinglish));
        Assert.Contains("<hindi>hinglish</hindi>", chat.LastUser);
    }

    [Fact]
    public void CaretCheckConfirmsAndNeverRejects()
    {
        Assert.Equal(PasteCheck.Verified, InsertionPolicy.VerifyCaret("Dear team,\r\nHello there", "Hello there"));
        Assert.Equal(PasteCheck.Verified, InsertionPolicy.VerifyCaret("start " + new string('a', 60), new string('a', 500)));
        Assert.Equal(PasteCheck.Unknown, InsertionPolicy.VerifyCaret("something else", "Hello there"));
        Assert.Equal(PasteCheck.Unknown, InsertionPolicy.VerifyCaret("", "Hello there"));
    }

    [Fact]
    public void LoopbackIgnoresSilenceButFollowsTheOutputDevice()
    {
        var now = TimeSpan.Zero;
        var source = new SimulatedMicrophoneSource { CurrentId = "speakers" };
        using var loopback = new SharedMicrophone(source, SharedMicrophoneOptions.Loopback with { PollInterval = null }, () => now);
        using var sub = loopback.Subscribe("meeting audio");

        // Nothing playing: no frames for a long time is normal.
        now += TimeSpan.FromMinutes(5);
        loopback.Poll();
        Assert.Single(source.Opened);

        // Headset connected: the default output changes.
        source.CurrentId = "headset";
        source.RaiseDevicesChanged();
        loopback.Poll();
        Assert.Equal(2, source.Opened.Count);
        Assert.Equal("headset", source.Latest!.Id);

        // Windows drops the stream.
        source.Latest.SimulateLoss();
        loopback.Poll();
        Assert.Equal(3, source.Opened.Count);
    }

    sealed class DailyLimitedChat(string limited) : GroqChat("test")
    {
        public List<string> Models { get; } = [];

        public override Task<string> CompleteAsync(string model, string system, string user, int maxTokens = 800, bool json = false,
                                                   double timeoutSeconds = 6, GroqPriority priority = GroqPriority.Interactive, CancellationToken cancel = default)
        {
            Models.Add(model);
            if (model == limited) throw ProviderException.DailyLimit("tokens", 3_600);
            return Task.FromResult("""{"title":"Launch moved to Friday","summary":["a"]}""");
        }
    }
}
