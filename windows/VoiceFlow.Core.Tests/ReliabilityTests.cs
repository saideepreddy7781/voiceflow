using System.Text.Json;
using VoiceFlow.Core.Audio;
using VoiceFlow.Core.Diagnostics;
using VoiceFlow.Core.Library;
using VoiceFlow.Core.Meetings;
using VoiceFlow.Core.Output;
using VoiceFlow.Core.Speech;
using Xunit;

namespace VoiceFlow.Core.Tests;

public class SharedMicrophoneTests
{
    static readonly float[] Voice = Enumerable.Range(0, 800).Select(i => 0.3f * MathF.Sin(i * 0.2f)).ToArray();
    /// <summary>A quiet room: tiny but never flat.</summary>
    static readonly float[] RoomNoise = Enumerable.Range(0, 800).Select(i => 0.0005f * MathF.Sin(i * 0.7f)).ToArray();
    static readonly float[] Zeros = new float[800];

    TimeSpan _now = TimeSpan.FromSeconds(100);
    readonly SimulatedMicrophoneSource _source = new();
    readonly SharedMicrophone _hub;

    public SharedMicrophoneTests() =>
        _hub = new SharedMicrophone(_source, new SharedMicrophoneOptions { PollInterval = null }, () => _now);

    void Advance(double seconds) => _now += TimeSpan.FromSeconds(seconds);

    /// <summary>Delivers audio for a stretch of time, polling as the real timer would.</summary>
    void Run(float[] frames, double seconds)
    {
        for (double t = 0; t < seconds; t += 0.5)
        {
            _source.Latest!.Emit(frames);
            Advance(0.5);
            _hub.Poll();
        }
    }

    [Fact]
    public void DictationStartingMidMeetingDoesNotTouchTheDevice()
    {
        using var meeting = _hub.Subscribe("meeting");
        _source.Latest!.Emit(Voice);
        var opened = _source.Opened.Count;
        var dictation = _hub.Subscribe("dictation");
        Assert.Equal(opened, _source.Opened.Count);
        Assert.True(_source.Latest!.Running);
        _source.Latest.Emit(Voice);
        Assert.Equal(2 * Voice.Length, meeting.SampleCount);
        Assert.Equal(Voice.Length, dictation.SampleCount);
    }

    [Fact]
    public void DictationStoppingDoesNotAffectTheMeeting()
    {
        using var meeting = _hub.Subscribe("meeting");
        var dictation = _hub.Subscribe("dictation");
        var device = _source.Latest!;
        device.Emit(Voice);
        var said = dictation.Stop();
        Assert.Equal(Voice.Length, said.Length);
        Assert.True(device.Running);
        device.Emit(Voice);
        Assert.Equal(2 * Voice.Length, meeting.SampleCount);
    }

    [Fact]
    public void MeetingStoppingDoesNotAffectDictation()
    {
        var meeting = _hub.Subscribe("meeting");
        using var dictation = _hub.Subscribe("dictation");
        var device = _source.Latest!;
        meeting.Dispose();
        Assert.True(device.Running);
        device.Emit(Voice);
        Assert.Equal(Voice.Length, dictation.SampleCount);
    }

    [Fact]
    public void BuffersAreIndependent()
    {
        using var meeting = _hub.Subscribe("meeting");
        using var dictation = _hub.Subscribe("dictation");
        _source.Latest!.Emit(Voice);
        meeting.Drain();
        Assert.Equal(0, meeting.SampleCount);
        Assert.Equal(Voice.Length, dictation.SampleCount);
    }

    [Fact]
    public void DeviceStopsWithTheLastSubscriberAndRestartsForTheNext()
    {
        var a = _hub.Subscribe("a");
        var b = _hub.Subscribe("b");
        var device = _source.Latest!;
        a.Dispose();
        Assert.True(device.Running);
        b.Dispose();
        Assert.False(device.Running);
        Assert.Equal(MicHealthStatus.Idle, _hub.Health.Status);
        using var c = _hub.Subscribe("c");
        Assert.Equal(2, _source.Opened.Count);
    }

    [Fact]
    public void StartFailureReachesTheFirstSubscriberAndLeavesTheHubIdle()
    {
        _source.StartError = new InvalidOperationException("no mic");
        Assert.Throws<InvalidOperationException>(() => _hub.Subscribe("dictation"));
        Assert.Equal(MicHealthStatus.Idle, _hub.Health.Status);
        Assert.Equal(0, _hub.SubscriberCount);
        _source.StartError = null;
        using var again = _hub.Subscribe("dictation");
        Assert.Equal(MicHealthStatus.Healthy, _hub.Health.Status);
    }

    [Fact]
    public void LostDeviceRestartsAndSubscribersKeepTheirAudio()
    {
        using var meeting = _hub.Subscribe("meeting");
        var first = _source.Latest!;
        first.Emit(Voice);
        first.SimulateLoss(new TimeoutException());
        Advance(0.5);
        _hub.Poll();
        Assert.Equal(2, _source.Opened.Count);
        Advance(1.5);
        _source.Latest!.Emit(Voice);
        Advance(0.5);
        _hub.Poll();
        Assert.Equal(2 * Voice.Length, meeting.SampleCount);
        Assert.Equal(MicHealthStatus.Healthy, _hub.Health.Status);
        Assert.Equal(1, _hub.Health.RestartCount);
        Assert.Equal(2.0, meeting.DrainGapSeconds(), 3);
        Assert.Equal(0, meeting.DrainGapSeconds());
    }

    [Fact]
    public void CallbacksFromAReplacedDeviceAreIgnored()
    {
        using var meeting = _hub.Subscribe("meeting");
        var first = _source.Latest!;
        first.SimulateLoss();
        _hub.Poll();
        var second = _source.Latest!;
        Assert.NotSame(first, second);
        first.Emit(Voice);
        Assert.Equal(0, meeting.SampleCount);
        first.SimulateLoss();
        _hub.Poll();
        Assert.Same(second, _source.Latest);     // a stale Lost must not restart the working device
    }

    [Fact]
    public void DefaultDeviceChangeSwitchesInOnePlaceForEveryone()
    {
        using var meeting = _hub.Subscribe("meeting");
        using var dictation = _hub.Subscribe("dictation");
        _source.CurrentId = "mic-2";
        _source.CurrentName = "Headset";
        _source.RaiseDevicesChanged();
        _hub.Poll();
        Assert.Equal(2, _source.Opened.Count);
        Assert.Equal("Headset", _hub.Health.DeviceName);
        Assert.Equal(1, _hub.Diagnostics.DeviceSwitches);
        _source.Latest!.Emit(Voice);
        Assert.Equal(Voice.Length, meeting.SampleCount);
        Assert.Equal(Voice.Length, dictation.SampleCount);
    }

    [Fact]
    public void AChangeThatKeepsTheSameDeviceDoesNothing()
    {
        using var meeting = _hub.Subscribe("meeting");
        _source.RaiseDevicesChanged();
        _hub.Poll();
        Assert.Single(_source.Opened);
        Assert.Equal(0, _hub.Diagnostics.DeviceSwitches);
    }

    [Fact]
    public void AQuietRoomIsHealthyAndNeverRestarts()
    {
        using var meeting = _hub.Subscribe("meeting");
        var changes = new List<MicHealthStatus>();
        _hub.HealthChanged += h => changes.Add(h.Status);
        Run(RoomNoise, 30);
        Assert.Single(_source.Opened);
        Assert.Equal(MicHealthStatus.Healthy, _hub.Health.Status);
        Assert.True(_hub.Health.QuietRoom);
        Assert.DoesNotContain(MicHealthStatus.DeadSignal, changes);
        Assert.DoesNotContain(MicHealthStatus.Stalled, changes);
    }

    [Fact]
    public void DigitalSilenceIsADeadMicAndRestartsOnce()
    {
        using var meeting = _hub.Subscribe("meeting");
        var changes = new List<MicHealthStatus>();
        _hub.HealthChanged += h => changes.Add(h.Status);
        Run(Zeros, 30);
        Assert.Contains(MicHealthStatus.DeadSignal, changes);
        Assert.Equal(2, _source.Opened.Count);     // one restart per episode, however long the silence lasts
        Assert.Equal(1, _hub.Diagnostics.DeadSignals);
        Run(Voice, 1);
        Assert.Equal(MicHealthStatus.Healthy, _hub.Health.Status);
    }

    [Fact]
    public void AMicThatWentFlatAfterWorkingIsReportedButNotRestarted()
    {
        using var meeting = _hub.Subscribe("meeting");
        Run(Voice, 2);
        Run(Zeros, 12);
        Assert.Equal(MicHealthStatus.DeadSignal, _hub.Health.Status);
        Assert.Equal(1, _hub.Diagnostics.DeadSignals);
        Assert.Single(_source.Opened);             // restarting can't fix a mute or a noise gate
    }

    [Fact]
    public void ADeadEpisodeEndingAllowsAnotherOne()
    {
        using var meeting = _hub.Subscribe("meeting");
        Run(Zeros, 12);
        Run(Voice, 1);
        Run(Zeros, 12);
        Assert.Equal(2, _hub.Diagnostics.DeadSignals);
    }

    [Fact]
    public void NoFramesIsAStallAndRestarts()
    {
        using var meeting = _hub.Subscribe("meeting");
        var changes = new List<MicHealthStatus>();
        _hub.HealthChanged += h => changes.Add(h.Status);
        _source.Latest!.Emit(Voice);
        Advance(3);
        _hub.Poll();
        Assert.Contains(MicHealthStatus.Stalled, changes);
        Assert.Equal(1, _hub.Diagnostics.Stalls);
        Assert.Equal(2, _source.Opened.Count);
        Run(Voice, 1);
        Assert.Equal(MicHealthStatus.Healthy, _hub.Health.Status);
    }

    [Fact]
    public void RepeatedStallsAreReportedAsFailedAndBackOff()
    {
        using var meeting = _hub.Subscribe("meeting");
        var failed = 0;
        meeting.Failed += () => failed++;
        _source.Latest!.Emit(Voice);
        Advance(3);
        _hub.Poll();                               // restart 1: starts cleanly, delivers nothing
        Advance(3);
        _hub.Poll();                               // restart 2
        Assert.Equal(MicHealthStatus.Failed, _hub.Health.Status);
        Assert.Equal(1, failed);
        var opened = _source.Opened.Count;
        Advance(0.5);
        _hub.Poll();                               // inside the backoff: no third restart yet
        Assert.Equal(opened, _source.Opened.Count);
    }

    [Fact]
    public void FailedRestartsRetryAndTheSubscriberHearsOnce()
    {
        using var meeting = _hub.Subscribe("meeting");
        var failed = 0;
        meeting.Failed += () => failed++;
        _source.StartError = new TimeoutException();
        _source.Latest!.SimulateLoss();
        _hub.Poll();                               // attempt 1 fails
        Advance(1);
        _hub.Poll();                               // attempt 2 fails: now Failed
        Assert.Equal(MicHealthStatus.Failed, _hub.Health.Status);
        Assert.Equal(1, failed);
        Advance(2);
        _hub.Poll();                               // attempt 3 fails: still one notification
        Assert.Equal(1, failed);
        _source.StartError = null;
        Advance(4);
        _hub.Poll();
        _source.Latest!.Emit(Voice);
        Advance(0.5);
        _hub.Poll();
        Assert.Equal(MicHealthStatus.Healthy, _hub.Health.Status);
        Assert.Equal(1, _hub.Diagnostics.Restarts);
        Assert.Equal(TimeoutExceptionName, _hub.Diagnostics.LastFailure);
    }

    const string TimeoutExceptionName = nameof(TimeoutException);

    [Fact]
    public void APluggedInDeviceDoesNotWaitOutTheRetryDelay()
    {
        using var meeting = _hub.Subscribe("meeting");
        _source.StartError = new TimeoutException();
        _source.Latest!.SimulateLoss();
        _hub.Poll();
        Advance(1);
        _hub.Poll();                               // retry delay is 2 s now
        _source.StartError = null;
        var opened = _source.Opened.Count;
        Advance(0.5);
        _source.RaiseDevicesChanged();
        _hub.Poll();
        Assert.Equal(opened + 1, _source.Opened.Count);
    }

    [Fact]
    public void SubscribingDuringAFailureRetriesImmediately()
    {
        using var meeting = _hub.Subscribe("meeting");
        _source.StartError = new TimeoutException();
        _source.Latest!.SimulateLoss();
        _hub.Poll();
        _source.StartError = null;
        using var dictation = _hub.Subscribe("dictation");
        Assert.True(_source.Latest!.Running);
        _source.Latest.Emit(Voice);
        Assert.Equal(Voice.Length, meeting.SampleCount);
    }

    [Fact]
    public void SubscribingDuringAFailureThatPersistsLeavesOthersAlone()
    {
        using var meeting = _hub.Subscribe("meeting");
        _source.StartError = new TimeoutException();
        _source.Latest!.SimulateLoss();
        _hub.Poll();
        Assert.Throws<TimeoutException>(() => _hub.Subscribe("dictation"));
        Assert.Equal(1, _hub.SubscriberCount);
    }

    [Fact]
    public void ASubscriberWhoJoinedLateOnlyMissesWhatFollowedIt()
    {
        using var meeting = _hub.Subscribe("meeting");
        _source.StartError = new TimeoutException();
        _source.Latest!.SimulateLoss();
        _hub.Poll();
        Advance(5);
        _source.StartError = null;
        using var dictation = _hub.Subscribe("dictation");
        Advance(0.2);
        _source.Latest!.Emit(Voice);
        Assert.Equal(5.2, meeting.DrainGapSeconds(), 3);
        Assert.Equal(0.2, dictation.DrainGapSeconds(), 3);
    }

    [Fact]
    public async Task ManySubscribersFromManyThreadsLoseNothing()
    {
        using var meeting = _hub.Subscribe("meeting");
        var device = _source.Latest!;
        var writer = Task.Run(() =>
        {
            for (int i = 0; i < 2_000; i++) device.Emit(Voice);
        });
        Parallel.For(0, 50, _ =>
        {
            using var dictation = _hub.Subscribe("dictation");
            dictation.Drain();
        });
        await writer;
        Assert.Equal(2_000 * Voice.Length, meeting.SampleCount);
        Assert.True(device.Running);
    }

    [Fact]
    public void FlatSignalsAreDigitalSilenceAndNoiseIsNot()
    {
        Assert.True(AudioMath.IsDigitalSilence(new float[100]));
        Assert.True(AudioMath.IsDigitalSilence(Enumerable.Repeat(0.25f, 100).ToArray()));
        Assert.False(AudioMath.IsDigitalSilence(RoomNoise));
        Assert.False(AudioMath.IsDigitalSilence([]));
    }
}

public class InsertionPolicyTests
{
    static InsertionFacts Same() => new() { ContextProcessId = 42, FrontProcessId = 42 };

    [Fact]
    public void SameAppWithAnUnknownFieldStillPastes() =>
        Assert.Equal(InsertionPlan.Paste, InsertionPolicy.Decide(Same()).Plan);

    [Fact]
    public void ProtectionsStayInForce()
    {
        Assert.Equal(new InsertionDecision(InsertionPlan.Block, DeliveryReason.Blocked, DeliveryDetail.ExcludedApp),
                     InsertionPolicy.Decide(Same() with { Excluded = true }));
        Assert.Equal(new InsertionDecision(InsertionPlan.Block, DeliveryReason.Blocked, DeliveryDetail.PasswordField),
                     InsertionPolicy.Decide(Same() with { PasswordField = true }));
        Assert.Equal(new InsertionDecision(InsertionPlan.CopyNoField, DeliveryReason.CopiedNoField, DeliveryDetail.Elevated),
                     InsertionPolicy.Decide(Same() with { Elevated = true }));
        Assert.Equal(new InsertionDecision(InsertionPlan.CopyNoField, DeliveryReason.CopiedNoField, DeliveryDetail.AppNotResponding),
                     InsertionPolicy.Decide(Same() with { Hung = true }));
    }

    [Fact]
    public void ProtectedFocusWinsOverAnAppSwitch()
    {
        Assert.Equal(InsertionPlan.Block, InsertionPolicy.Decide(Same() with { FrontProcessId = 7, Excluded = true }).Plan);
        Assert.Equal(InsertionPlan.Block, InsertionPolicy.Decide(Same() with { FrontProcessId = 7, PasswordField = true }).Plan);
    }

    [Fact]
    public void SwitchingAppsOrWindowsCopies()
    {
        Assert.Equal(new InsertionDecision(InsertionPlan.CopyFocusChanged, DeliveryReason.CopiedFocusChanged, null),
                     InsertionPolicy.Decide(Same() with { FrontProcessId = 7 }));
        Assert.Equal(InsertionPlan.CopyFocusChanged, InsertionPolicy.Decide(Same() with { WindowChanged = true }).Plan);
    }

    [Fact]
    public void NothingInFrontAfterAKnownStartIsAFocusChange() =>
        Assert.Equal(InsertionPlan.CopyFocusChanged, InsertionPolicy.Decide(Same() with { FrontProcessId = null }).Plan);

    [Fact]
    public void NothingInFrontWithNoKnownStartHasNoField() =>
        Assert.Equal(DeliveryReason.CopiedNoField, InsertionPolicy.Decide(new InsertionFacts()).Reason);

    [Fact]
    public void AnUnknownStartPastesIntoWhatIsInFront() =>
        Assert.Equal(InsertionPlan.Paste, InsertionPolicy.Decide(new InsertionFacts { FrontProcessId = 7 }).Plan);

    [Fact]
    public void EmptyTextIsNeverPasted() =>
        Assert.Equal(InsertionPlan.CopyNoField, InsertionPolicy.Decide(Same() with { TextIsEmpty = true }).Plan);

    [Fact]
    public void TheTextShowingUpVerifiesThePaste() =>
        Assert.Equal(PasteCheck.Verified, InsertionPolicy.Verify("Hello ", "Hello world", "world"));

    [Fact]
    public void SameTextDictatedTwiceStillCounts() =>
        Assert.Equal(PasteCheck.Verified, InsertionPolicy.Verify("ok ok", "ok ok ok", "ok"));

    [Fact]
    public void WhitespaceDoesntFailVerification() =>
        Assert.Equal(PasteCheck.Verified, InsertionPolicy.Verify("", "line one\r\nline  two", "line one\nline two"));

    [Fact]
    public void LongTextIsMatchedByItsStart()
    {
        var text = new string('a', 80) + "tail";
        Assert.Equal(PasteCheck.Verified, InsertionPolicy.Verify("x", "x" + new string('a', 80), text));
    }

    [Fact]
    public void AFieldThatCannotBeReadSaysNothing()
    {
        Assert.Equal(PasteCheck.Unknown, InsertionPolicy.Verify(null, null, "hello"));
        Assert.Equal(PasteCheck.Unknown, InsertionPolicy.Verify("before", null, "hello"));
    }

    [Fact]
    public void AnEmptyValueIsNotEvidence()
    {
        // Monaco reports "" whatever it holds, so before and after matching here means nothing.
        Assert.Equal(PasteCheck.Unknown, InsertionPolicy.Verify("", "", "hello"));
    }

    [Fact]
    public void AnUnchangedFieldMeansItDidNotLand() =>
        Assert.Equal(PasteCheck.NotLanded, InsertionPolicy.Verify("draft", "draft", "hello"));

    [Fact]
    public void AnUnrelatedChangeSaysNothing() =>
        Assert.Equal(PasteCheck.Unknown, InsertionPolicy.Verify("a long field of text", "a long field of text!", "something else"));

    [Fact]
    public void ReasonsMapExactly()
    {
        Assert.Equal((DeliveryReason.PastedVerified, (string?)null), InsertionPolicy.ReasonFor(PasteCheck.Verified));
        Assert.Equal((DeliveryReason.PastedUnverified, DeliveryDetail.FieldUnreadable), InsertionPolicy.ReasonFor(PasteCheck.Unknown));
        Assert.Equal((DeliveryReason.PasteNotLanded, DeliveryDetail.ValueUnchanged), InsertionPolicy.ReasonFor(PasteCheck.NotLanded));
    }
}

public class CaptureVerdictTests
{
    static float[] Tone(double s) => Enumerable.Range(0, (int)(s * 16_000)).Select(i => 0.3f * MathF.Sin(i * 0.2f)).ToArray();
    static float[] Noise(double s) => Enumerable.Range(0, (int)(s * 16_000)).Select(i => 0.0003f * MathF.Sin(i * 0.7f)).ToArray();

    [Fact] public void AQuickTapIsTooShort() => Assert.Equal(CaptureVerdict.TooShort, AudioMath.Verdict(Tone(0.2), 0.3));
    [Fact] public void AHeldKeyWithNoAudioIsAMicProblem() => Assert.Equal(CaptureVerdict.NoAudio, AudioMath.Verdict(Tone(0.2), 2));
    [Fact] public void ExactZerosAreDigitalSilence() => Assert.Equal(CaptureVerdict.DigitalSilence, AudioMath.Verdict(new float[32_000], 2));
    [Fact] public void AQuietRoomIsNoSpeech() => Assert.Equal(CaptureVerdict.NoSpeech, AudioMath.Verdict(Noise(2), 2));
    [Fact] public void SpeechIsSpeech() => Assert.Equal(CaptureVerdict.Speech, AudioMath.Verdict(Tone(2), 2));
}

public class DeliveryDiagnosticsTests
{
    const string Transcript = "my bank account number is 123456";
    const string WindowTitle = "Salary negotiation - Confidential.docx";
    const string Key = "gsk_abcdefghijklmnop1234567890";
    const string MeetingLine = "we are laying off the Hyderabad team";

    [Fact]
    public void WireNamesAreTheAgreedList()
    {
        string[] expected =
        [
            "inserted", "pasted-verified", "pasted-unverified", "paste-not-landed", "copied-no-field", "copied-focus-changed",
            "blocked", "no-speech", "too-short", "cancelled", "transcription-failed", "mic-failed", "mic-recovered",
        ];
        Assert.Equal(expected, Enum.GetValues<DeliveryReason>().Where(r => r != DeliveryReason.Unknown).Select(r => r.WireName()));
    }

    [Fact]
    public void HistoryEntriesFromOlderFilesDecodeWithAUnknownReason()
    {
        const string old = """[{"id":"11111111-1111-1111-1111-111111111111","kind":"dictation","appName":"Notepad","raw":"r","final":"f","audioSeconds":1,"latencyMs":2,"outcome":"pasted"}]""";
        var entries = JsonSerializer.Deserialize<List<HistoryEntry>>(old, JsonFile<List<HistoryEntry>>.Options)!;
        Assert.Equal(DeliveryReason.Unknown, entries[0].Reason);
        Assert.Equal("pasted", entries[0].Outcome);
    }

    [Fact]
    public void HistoryEntriesKeepTheirReasonUnderTheWireName()
    {
        var entry = new HistoryEntry(HistoryKind.Dictation, "App", "r", "f", 1, 2, "pasted") { Reason = DeliveryReason.PastedVerified };
        var json = JsonSerializer.Serialize(new List<HistoryEntry> { entry }, JsonFile<List<HistoryEntry>>.Options);
        Assert.Contains("\"pasted-verified\"", json);
        var back = JsonSerializer.Deserialize<List<HistoryEntry>>(json, JsonFile<List<HistoryEntry>>.Options)!;
        Assert.Equal(DeliveryReason.PastedVerified, back[0].Reason);
    }

    [Fact]
    public void AReasonFromANewerVersionReadsAsUnknown() =>
        Assert.Equal(DeliveryReason.Unknown, DeliveryReasonNames.Parse("something-new"));

    [Fact]
    public void TheLogIsARing()
    {
        var log = new DeliveryLog(null, capacity: 3);
        for (int i = 0; i < 5; i++) log.Record(new DeliveryLogEntry(DeliverySession.Dictation, DeliveryReason.NoSpeech, audioMs: i));
        var recent = log.Recent();
        Assert.Equal(3, recent.Count);
        Assert.Equal(4, recent[0].AudioMs);
        Assert.Equal(2, recent[^1].AudioMs);
    }

    [Fact]
    public void TheLogSurvivesARestart()
    {
        var folder = Path.Combine(Path.GetTempPath(), "voiceflow-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new DeliveryLog(folder);
            log.Record(new DeliveryLogEntry(DeliverySession.Command, DeliveryReason.MicFailed, DeliveryDetail.StartFailed, 1500, 20));
            var back = new DeliveryLog(folder).Recent().Single();
            Assert.Equal(DeliveryReason.MicFailed, back.Reason);
            Assert.Equal(DeliveryDetail.StartFailed, back.Detail);
            Assert.Equal(1500, back.AudioMs);
            Assert.Contains("\"mic-failed\"", File.ReadAllText(Path.Combine(folder, "delivery-log.json")));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void NeverStoreMyDataKeepsTheLogOffDisk()
    {
        var folder = Path.Combine(Path.GetTempPath(), "voiceflow-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "delivery-log.json");
        try
        {
            var log = new DeliveryLog(folder);
            log.Record(new DeliveryLogEntry(DeliverySession.Dictation, DeliveryReason.NoSpeech));
            Assert.True(File.Exists(path));
            log.Persists = false;
            Assert.False(File.Exists(path));
            log.Record(new DeliveryLogEntry(DeliverySession.Dictation, DeliveryReason.NoSpeech));
            Assert.False(File.Exists(path));
            Assert.Equal(2, log.Recent().Count);        // still there for this run's report
            Assert.Empty(new DeliveryLog(folder, persists: false).Recent());
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public void OwnerNamesComeOffDeviceNames()
    {
        var report = DebugReport.Build(Input([], "Priya's AirPods Pro"));
        Assert.Contains("AirPods Pro", report);
        Assert.DoesNotContain("Priya", report);
    }

    [Fact]
    public void AnUnreadableLogFileStartsEmpty()
    {
        var folder = Path.Combine(Path.GetTempPath(), "voiceflow-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "delivery-log.json"), "{not json");
            Assert.Empty(new DeliveryLog(folder).Recent());
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void FreeTextCannotBecomeADetail()
    {
        var log = new DeliveryLog(null);
        log.Record(new DeliveryLogEntry(DeliverySession.Dictation, DeliveryReason.Blocked, Transcript));
        Assert.Null(log.Recent()[0].Detail);
    }

    [Fact]
    public void DetailsReadFromDiskAreCheckedToo()
    {
        var folder = Path.Combine(Path.GetTempPath(), "voiceflow-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "delivery-log.json"), JsonSerializer.Serialize(
                new[] { new { session = "dictation", reason = "blocked", detail = Transcript, audioMs = 0, latencyMs = 0 } }));
            var report = DebugReport.Build(Input(new DeliveryLog(folder).Recent()));
            Assert.DoesNotContain(Transcript, report);
        }
        finally { Directory.Delete(folder, true); }
    }

    static DebugReportInput Input(IReadOnlyList<DeliveryLogEntry> entries, string mic = "USB Microphone") => new(
        "0.2.0", "Windows 11 10.0.26100", "X64", ".NET 8.0.11", [mic, "Realtek Array"], ["Speakers"],
        new MicDiagnostics(3, 1, 2, 1, 4, mic, "TimeoutException"), entries, DateTimeOffset.UnixEpoch);

    [Fact]
    public void TheReportHasWhatABugReportNeeds()
    {
        var report = DebugReport.Build(Input(
        [
            new DeliveryLogEntry(DeliverySession.Dictation, DeliveryReason.PastedVerified, null, 3000, 900),
            new DeliveryLogEntry(DeliverySession.Meeting, DeliveryReason.MicFailed, DeliveryDetail.StartFailed),
            new DeliveryLogEntry(DeliverySession.Dictation, DeliveryReason.PastedUnverified, DeliveryDetail.FieldUnreadable, 1000, 600),
        ]));
        foreach (var expected in new[]
                 {
                     "0.2.0", "Windows 11", ".NET 8", "USB Microphone", "Realtek Array", "Speakers",
                     "Restarts: 3", "device switches: 1", "stalls: 2", "dead signals: 1", "failed starts: 4", "TimeoutException",
                     "pasted-verified", "mic-failed", "pasted-unverified", "field-unreadable", "start-failed", "900",
                 })
            Assert.Contains(expected, report);
    }

    [Fact]
    public void TheReportNeverContainsPrivateText()
    {
        // Sentinels pushed through every string the report accepts, as if the code or a device had leaked them.
        var entry = new DeliveryLogEntry(DeliverySession.Dictation, DeliveryReason.Blocked, Transcript) { Detail = WindowTitle };
        var input = new DebugReportInput(
            "0.2.0", "Windows 11", "X64", ".NET 8",
            [$"Mic {Key}"], [$"Headphones ({Key})"],
            new MicDiagnostics(0, 0, 0, 0, 0, $"Mic {Key}", null),
            [entry],
            DateTimeOffset.UnixEpoch);
        var report = DebugReport.Build(input);
        Assert.DoesNotContain(Transcript, report);
        Assert.DoesNotContain(WindowTitle, report);
        Assert.DoesNotContain(MeetingLine, report);
        Assert.DoesNotContain(Key, report);
        Assert.DoesNotContain("gsk_", report);
    }

    [Fact]
    public void DeliveryEntriesCannotHoldText()
    {
        // The rule by construction: the only string on an entry is the allowlisted detail.
        var strings = typeof(DeliveryLogEntry).GetProperties().Where(p => p.PropertyType == typeof(string)).Select(p => p.Name).ToList();
        Assert.Equal(["Detail"], strings);
    }

    [Fact]
    public void DeviceNamesCannotInjectLines()
    {
        var report = DebugReport.Build(Input([], "Mic\nFake line: pwned"));
        Assert.DoesNotContain("\nFake line", report);
    }
}

public class MeetingMicGapTests
{
    static readonly MeetingRecorder.Settings Settings = new("gsk_test", "whisper-large-v3-turbo", null, null, [], null, null, true);
    static Task NoWait(TimeSpan _) => Task.CompletedTask;
    static float[] Speech(double s) => Enumerable.Range(0, (int)(s * 16_000)).Select(i => 0.3f * MathF.Sin(i * 0.2f)).ToArray();

    sealed class Source : IMeetingAudioSource
    {
        public readonly Queue<float[]> Pending = new();
        public double Gap;
        public void StartCapture() { }
        public void StopCapture() { }
        public float[] DrainSamples() => Pending.Count > 0 ? Pending.Dequeue() : [];
        public double DrainGapSeconds()
        {
            var g = Gap;
            Gap = 0;
            return g;
        }
        public void Dispose() { }
    }

    static MeetingRecorder Recorder(Source mic, Source? system = null, Func<byte[], string>? text = null) =>
        new(_ => mic, () => system, (wav, _, _) => Task.FromResult(new DetailedTranscript(text?.Invoke(wav) ?? "hello", "en", [])), NoWait);

    [Fact]
    public async Task AGapInAMicOnlyMeetingIsPaddedAndMarked()
    {
        var mic = new Source();
        var recorder = Recorder(mic);
        recorder.Start("T", "", [], Settings with { RecordSystemAudio = false });
        mic.Pending.Enqueue(Speech(10));
        recorder.Tick();
        mic.Gap = 30;
        mic.Pending.Enqueue(Speech(5));
        recorder.Tick();
        var meeting = await recorder.StopAsync();
        var gap = Assert.Single(meeting!.MicGaps);
        Assert.Equal(10, gap.Time, 2);
        Assert.Equal(30, gap.Seconds, 2);
        Assert.Contains(meeting.Lines, l => l.Text.Contains("not recording for 30 seconds", StringComparison.Ordinal));
        Assert.Contains(meeting.Lines, l => l.Text == "hello");
    }

    [Fact]
    public async Task AStallDuringAMeetingWithSystemAudioIsOneGap()
    {
        var mic = new Source();
        var system = new Source();
        var recorder = Recorder(mic, system);
        recorder.Start("T", "", [], Settings);
        mic.Pending.Enqueue(Speech(5));
        system.Pending.Enqueue(Speech(5));
        recorder.Tick();
        for (int i = 0; i < 40; i++)
        {
            system.Pending.Enqueue(Speech(1));
            recorder.Tick();
        }
        var meeting = await recorder.StopAsync();
        var gap = Assert.Single(meeting!.MicGaps);
        Assert.InRange(gap.Time, 5, 7);
        Assert.InRange(gap.Seconds, 35, 42);
    }

    [Fact]
    public async Task DigitalSilenceFromALiveMicIsNotAGap()
    {
        // Noise-gating software sends real zeros while you are not talking; only padded audio is a gap.
        var mic = new Source();
        var recorder = Recorder(mic);
        recorder.Start("T", "", [], Settings with { RecordSystemAudio = false });
        mic.Pending.Enqueue(Speech(3));
        recorder.Tick();
        for (int i = 0; i < 20; i++)
        {
            mic.Pending.Enqueue(new float[16_000]);
            recorder.Tick();
        }
        mic.Pending.Enqueue(Speech(3));
        recorder.Tick();
        var meeting = await recorder.StopAsync();
        Assert.Empty(meeting!.MicGaps);
    }

    [Fact]
    public async Task ABareGapDoesNotInventATranscript()
    {
        var mic = new Source();
        var recorder = Recorder(mic);
        recorder.Start("T", "", [], Settings with { RecordSystemAudio = false });
        mic.Gap = 60;
        recorder.Tick();
        var meeting = await recorder.StopAsync();
        Assert.Single(meeting!.MicGaps);
        Assert.Empty(meeting.Lines);
    }

    [Fact]
    public async Task ACleanMeetingHasNoGapsOrMarkers()
    {
        var mic = new Source();
        var recorder = Recorder(mic);
        recorder.Start("T", "", [], Settings with { RecordSystemAudio = false });
        mic.Pending.Enqueue(Speech(5));
        var meeting = await recorder.StopAsync();
        Assert.Empty(meeting!.MicGaps);
        Assert.DoesNotContain(meeting.Lines, l => l.Text.StartsWith("[Your microphone", StringComparison.Ordinal));
    }

    [Fact]
    public void OldMeetingFilesDecodeWithNoGaps()
    {
        const string old = """{"id":"11111111-1111-1111-1111-111111111111","title":"T","start":"2026-01-01T00:00:00+00:00","app":"","attendees":[],"lines":[],"status":"done","micOnly":false}""";
        var meeting = JsonSerializer.Deserialize<Meeting>(old, JsonFile<Meeting>.Options)!;
        Assert.Empty(meeting.MicGaps);
    }

    [Fact]
    public void GapsSurviveTheJsonRoundTrip()
    {
        var meeting = new Meeting("T") { MicGaps = [new MicGap(12.5, 30)] };
        var copy = meeting.Copy();
        Assert.Equal(new MicGap(12.5, 30), copy.MicGaps.Single());
    }
}
