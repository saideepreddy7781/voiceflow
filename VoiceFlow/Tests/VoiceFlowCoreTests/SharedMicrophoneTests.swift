import Foundation
import Testing
@testable import VoiceFlowCore

/// A stand-in for the audio hardware. `feed` is what the audio thread would deliver.
@MainActor
final class FakeMicEngine: MicrophoneEngine {
    var onConfigurationChange: (@MainActor () -> Void)?
    var starts = 0
    var stops = 0
    var isRunning = false
    var failNextStarts = 0
    var lastDevice: String?
    private var sink: (any SampleSink)?
    var nextSample: Float = 1

    func start(deviceUID: String?, sink: any SampleSink) throws {
        starts += 1
        lastDevice = deviceUID
        if failNextStarts > 0 {
            failNextStarts -= 1
            throw AudioRecorder.RecorderError.noInput
        }
        self.sink = sink
        isRunning = true
    }

    func stop() {
        stops += 1
        sink = nil
        isRunning = false
    }

    /// Delivers `count` numbered samples, so a test can see any sample missing or out of order.
    func feed(_ count: Int = 160) {
        guard let sink else { return }
        let block = (0..<count).map { Float($0) + nextSample }
        nextSample += Float(count)
        sink.append(block)
    }

    func simulateConfigurationChange() { onConfigurationChange?() }
}

@MainActor
struct SharedMicrophoneTests {
    private func makeMic(_ engine: FakeMicEngine, delays: [Double] = [0, 0, 0]) -> SharedMicrophone {
        SharedMicrophone(engine: engine, retryDelays: delays, sleep: { _ in })
    }

    /// True when `samples` run on from `first` without a gap or a repeat.
    private func contiguous(_ samples: [Float], from first: Float) -> Bool {
        samples.enumerated().allSatisfy { $0.element == first + Float($0.offset) }
    }

    @Test func dictationDuringAMeetingLeavesTheMeetingSamplesContiguous() throws {
        let engine = FakeMicEngine()
        let mic = makeMic(engine)
        let meeting = AudioRecorder(microphone: mic)
        try meeting.start(deviceUID: nil)
        engine.feed()

        let dictation = AudioRecorder(microphone: mic)
        try dictation.start(deviceUID: nil)
        engine.feed()
        let dictated = dictation.stop()
        engine.feed()

        #expect(engine.starts == 1 && engine.stops == 0)
        let heard = meeting.drain()
        #expect(heard.count == 480)
        #expect(contiguous(heard, from: 1))
        // The dictation got exactly the audio from while it was subscribed.
        #expect(dictated.count == 160)
        #expect(contiguous(dictated, from: 161))
    }

    @Test func manyDictationsNeverStopTheEngineOrTheMeeting() throws {
        let engine = FakeMicEngine()
        let mic = makeMic(engine)
        let meeting = AudioRecorder(microphone: mic)
        try meeting.start(deviceUID: nil)
        for _ in 0..<50 {
            let dictation = AudioRecorder(microphone: mic)
            try dictation.start(deviceUID: nil)
            engine.feed()
            #expect(dictation.stop().count == 160)
        }
        #expect(engine.starts == 1 && engine.stops == 0)
        #expect(contiguous(meeting.drain(), from: 1))
    }

    @Test func engineStopsOnlyWithTheLastSubscriber() throws {
        let engine = FakeMicEngine()
        let mic = makeMic(engine)
        let one = AudioRecorder(microphone: mic)
        let two = AudioRecorder(microphone: mic)
        try one.start(deviceUID: nil)
        try two.start(deviceUID: nil)
        one.stop()
        #expect(engine.isRunning)
        two.stop()
        #expect(!engine.isRunning)
        #expect(mic.state == .stopped)
    }

    @Test func aSecondSubscriberSharesTheDeviceInUse() throws {
        let engine = FakeMicEngine()
        let mic = makeMic(engine)
        let first = AudioRecorder(microphone: mic)
        try first.start(deviceUID: "airpods")
        let second = AudioRecorder(microphone: mic)
        try second.start(deviceUID: "built-in")
        #expect(engine.starts == 1)
        #expect(engine.lastDevice == "airpods")
    }

    @Test func recoveryKeepsAllSubscribersReceiving() async throws {
        let engine = FakeMicEngine()
        let mic = makeMic(engine)
        let meeting = AudioRecorder(microphone: mic)
        let dictation = AudioRecorder(microphone: mic)
        try meeting.start(deviceUID: nil)
        try dictation.start(deviceUID: nil)
        engine.feed()

        engine.simulateConfigurationChange()
        #expect(mic.state == .recovering)
        await mic.recoveryTask?.value

        #expect(mic.state == .running)
        #expect(mic.restartCount == 1)
        #expect(meeting.restarts == 1)
        engine.feed()
        #expect(contiguous(meeting.drain(), from: 1))
        #expect(contiguous(dictation.stop(), from: 1))
    }

    @Test func recoveryRetriesAFailedRebuild() async throws {
        // AirPods report no input format for a moment while they switch to call mode.
        let engine = FakeMicEngine()
        let mic = makeMic(engine)
        let meeting = AudioRecorder(microphone: mic)
        try meeting.start(deviceUID: nil)
        engine.failNextStarts = 2
        engine.simulateConfigurationChange()
        await mic.recoveryTask?.value
        #expect(mic.state == .running)
        #expect(engine.starts == 4)
        engine.feed()
        #expect(meeting.drain().count == 160)
    }

    @Test func recoveryGivesUpAfterEveryRetryFails() async throws {
        let engine = FakeMicEngine()
        let mic = makeMic(engine)
        let meeting = AudioRecorder(microphone: mic)
        try meeting.start(deviceUID: nil)
        engine.failNextStarts = 99
        engine.simulateConfigurationChange()
        await mic.recoveryTask?.value
        #expect(mic.state == .failed)
        // A later dictation is allowed to try again.
        engine.failNextStarts = 0
        let dictation = AudioRecorder(microphone: mic)
        try dictation.start(deviceUID: nil)
        #expect(mic.state == .running)
        engine.feed()
        #expect(meeting.drain().count == 160)
    }

    @Test func stoppingEveryoneCancelsARecoveryInProgress() async throws {
        let engine = FakeMicEngine()
        let mic = makeMic(engine)
        let meeting = AudioRecorder(microphone: mic)
        try meeting.start(deviceUID: nil)
        engine.simulateConfigurationChange()
        let recovery = mic.recoveryTask
        meeting.stop()
        await recovery?.value
        #expect(!engine.isRunning)
        #expect(mic.state == .stopped)
    }

    @Test func aHealthRestartRebuildsTheEngineForEveryone() async throws {
        let engine = FakeMicEngine()
        let mic = makeMic(engine)
        let meeting = AudioRecorder(microphone: mic)
        try meeting.start(deviceUID: nil)
        meeting.restartMicrophone()
        await mic.recoveryTask?.value
        #expect(engine.starts == 2 && mic.state == .running)
        // A restart while one is already running is ignored.
        engine.simulateConfigurationChange()
        meeting.restartMicrophone()
        await mic.recoveryTask?.value
        #expect(engine.starts == 3)
        engine.feed()
        #expect(meeting.drain().count == 160)
    }

    @Test func restartWithNobodyListeningDoesNothing() {
        let engine = FakeMicEngine()
        let mic = makeMic(engine)
        mic.restart()
        #expect(engine.starts == 0)
        #expect(mic.state == .stopped)
    }

    @Test func aFailedStartDoesNotLeaveASubscriberBehind() throws {
        let engine = FakeMicEngine()
        let mic = makeMic(engine)
        let recorder = AudioRecorder(microphone: mic)
        engine.failNextStarts = 1
        #expect(throws: (any Error).self) { try recorder.start(deviceUID: nil) }
        #expect(!recorder.isRecording)
        #expect(mic.state == .failed)
    }

    @Test func aMicSourceSharesTheEngineWithDictation() throws {
        let engine = FakeMicEngine()
        let mic = makeMic(engine)
        let source = MicSource(deviceUID: nil, recorder: AudioRecorder(microphone: mic))
        try source.startCapture()
        let dictation = AudioRecorder(microphone: mic)
        try dictation.start(deviceUID: nil)
        engine.feed()
        dictation.stop()
        engine.feed()
        #expect(source.drainSamples().count == 320)
        #expect(engine.isRunning)
        source.stopCapture()
        #expect(!engine.isRunning)
    }
}

struct MicHealthMonitorTests {
    private let speech = Array(repeating: Float(0.2), count: 16_000)

    /// Feeds `seconds` of the same condition and returns the last decision.
    private func run(_ monitor: inout MicHealthMonitor, seconds: Int, count: Int, silent: Bool) -> [MicHealthMonitor.Decision] {
        (0..<seconds).map { _ in monitor.observe(sampleCount: count, digitallySilent: silent) }
    }

    @Test func aHealthyMicNeverRestarts() {
        var monitor = MicHealthMonitor()
        let decisions = run(&monitor, seconds: 600, count: 16_000, silent: false)
        #expect(decisions.allSatisfy { $0.state == .healthy && !$0.restart })
    }

    @Test func aQuietRoomIsNotDigitalSilence() {
        // A real microphone in a silent room still has a noise floor around -80 dBFS.
        let room = (0..<16_000).map { Float($0 % 2 == 0 ? 0.0001 : -0.0001) }
        #expect(!MicHealthMonitor.isDigitallySilent(room))
        #expect(MicHealthMonitor.isDigitallySilent([Float](repeating: 0, count: 16_000)))
    }

    @Test func aStallRestartsWithinSeconds() {
        var monitor = MicHealthMonitor()
        let decisions = run(&monitor, seconds: 4, count: 0, silent: false)
        #expect(decisions.last == .init(state: .restarting, restart: true))
        #expect(decisions.dropLast().allSatisfy { !$0.restart })
    }

    @Test func aStallThatDoesNotRecoverIsReported() {
        var monitor = MicHealthMonitor()
        let decisions = run(&monitor, seconds: 19, count: 0, silent: false)
        #expect(decisions.filter(\.restart).count == 3)
        #expect(decisions.last?.state == .noAudio)
        // It keeps trying quietly, so a reconnected mic comes back without the user doing anything.
        let later = run(&monitor, seconds: 61, count: 0, silent: false)
        #expect(later.contains { $0.restart })
        #expect(later.last?.state == .noAudio)
    }

    @Test func digitalSilenceGetsALongerGracePeriodAndFewerAttempts() {
        var monitor = MicHealthMonitor()
        let first = run(&monitor, seconds: 10, count: 16_000, silent: true)
        #expect(first.filter(\.restart).count == 1)
        #expect(first.last?.state == .restarting)
        let second = run(&monitor, seconds: 20, count: 16_000, silent: true)
        #expect(second.last == .init(state: .silent, restart: true))
    }

    @Test func recoveryClearsTheProblem() {
        var monitor = MicHealthMonitor()
        _ = run(&monitor, seconds: 19, count: 0, silent: false)
        #expect(monitor.state == .noAudio)
        #expect(monitor.observe(sampleCount: 16_000, digitallySilent: false) == .init(state: .healthy, restart: false))
        // The next episode starts from scratch.
        let again = run(&monitor, seconds: 4, count: 0, silent: false)
        #expect(again.last == .init(state: .restarting, restart: true))
    }

    @Test func dictationVerdictTellsADeadMicFromSilence() {
        #expect(AudioMath.captureVerdict([Float](repeating: 0.2, count: 3_000), heldSeconds: 0.2) == .tooShort)
        #expect(AudioMath.captureVerdict([], heldSeconds: 4) == .noAudio)
        #expect(AudioMath.captureVerdict([Float](repeating: 0, count: 32_000), heldSeconds: 2) == .digitalSilence)
        let room = (0..<32_000).map { Float($0 % 2 == 0 ? 0.0002 : -0.0002) }
        #expect(AudioMath.captureVerdict(room, heldSeconds: 2) == .noSpeech)
        #expect(AudioMath.captureVerdict(speech + speech, heldSeconds: 2) == .speech)
    }
}

/// A meeting microphone that goes dead until it is restarted.
@MainActor
final class StallingSource: MeetingAudioSource {
    var stalled = false
    var restarts = 0
    var recoversOnRestart = true
    var sample: Float = 0.2
    func startCapture() throws {}
    func stopCapture() {}
    func restartCapture() {
        restarts += 1
        if recoversOnRestart { stalled = false }
    }
    func drainSamples() -> [Float] { stalled ? [] : Array(repeating: sample, count: 16_000) }
}

@MainActor
struct MeetingMicHealthTests {
    private static let settings = MeetingRecorderTests.settings

    private func makeRecorder(_ mic: StallingSource) -> MeetingRecorder {
        MeetingRecorder(mic: { _ in mic }, system: { nil }, transcribe: { _, _ in DetailedTranscript(text: "", language: "en", segments: []) })
    }

    @Test func aStalledMicIsRestartedAndRecovers() async throws {
        let mic = StallingSource()
        let recorder = makeRecorder(mic)
        try recorder.start(title: "t", app: "", attendees: [], settings: Self.settings, autoTick: false)
        for _ in 0..<3 { recorder.tick() }
        mic.stalled = true
        for _ in 0..<4 { recorder.tick() }
        #expect(mic.restarts == 1)
        #expect(recorder.micHealth == .healthy || recorder.micHealth == .restarting)
        recorder.tick()
        #expect(recorder.micHealth == .healthy)
        _ = await recorder.stop()
        #expect(recorder.micMissingSeconds == 4)
    }

    @Test func aDeadMicIsReportedAndTheMeetingCarriesOn() async throws {
        let mic = StallingSource()
        mic.recoversOnRestart = false
        let recorder = makeRecorder(mic)
        try recorder.start(title: "t", app: "", attendees: [], settings: Self.settings, autoTick: false)
        mic.stalled = true
        for _ in 0..<19 { recorder.tick() }
        #expect(recorder.micHealth == .noAudio)
        #expect(mic.restarts == 3)
        #expect(recorder.isRecording)
        let meeting = await recorder.stop()
        #expect(meeting?.micMissingSeconds == 19)
    }

    @Test func anIntactMeetingRecordsNoMissingSeconds() async throws {
        let mic = StallingSource()
        let recorder = makeRecorder(mic)
        try recorder.start(title: "t", app: "", attendees: [], settings: Self.settings, autoTick: false)
        for _ in 0..<3 { recorder.tick() }
        let meeting = await recorder.stop()
        #expect(meeting?.micMissingSeconds == nil)
        #expect(mic.restarts == 0)
    }
}
