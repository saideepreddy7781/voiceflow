import Foundation
import Testing
@testable import VoiceFlowCore

struct CallEndDetectorTests {
    /// Feeds one observation every 5 seconds from `from` to `to`, and returns the times it said stop.
    private func run(_ d: inout CallEndDetector, from: Double, to: Double, call: Bool?, silent: (Double) -> Int?) -> [Double] {
        stride(from: from, through: to, by: 5).filter { d.observe(at: $0, callAppRecording: call, meetingAudioSilentSeconds: silent($0)) }
    }

    @Test func stopsAMinuteAfterTheCallAppLetsGoAndTheAudioStops() {
        var d = CallEndDetector()
        #expect(run(&d, from: 0, to: 600, call: true, silent: { _ in 0 }).isEmpty)
        // The call ends at 605: Meet stops using the mic and the tab goes quiet.
        let stops = run(&d, from: 605, to: 900, call: false, silent: { Int($0 - 605) })
        #expect(stops == [665])
    }

    @Test func anInPersonMeetingNeverStopsByItself() {
        var d = CallEndDetector()
        #expect(run(&d, from: 0, to: 3_600, call: false, silent: { Int($0) }).isEmpty)
    }

    @Test func callAudioStillPlayingKeepsRecording() {
        // The call app let go of the mic (muted in some apps) but the others are still talking.
        var d = CallEndDetector()
        _ = run(&d, from: 0, to: 60, call: true, silent: { _ in 0 })
        #expect(run(&d, from: 65, to: 600, call: false, silent: { _ in 2 }).isEmpty)
    }

    @Test func rejoiningResetsTheClock() {
        var d = CallEndDetector()
        _ = run(&d, from: 0, to: 60, call: true, silent: { _ in 0 })
        #expect(run(&d, from: 65, to: 100, call: false, silent: { _ in 100 }).isEmpty)
        _ = run(&d, from: 105, to: 120, call: true, silent: { _ in 0 })
        #expect(run(&d, from: 125, to: 180, call: false, silent: { _ in 100 }).isEmpty)
        #expect(run(&d, from: 185, to: 185, call: false, silent: { _ in 100 }) == [185])
    }

    @Test func withoutMeetingAudioTheAppAloneDecides() {
        var d = CallEndDetector()
        _ = run(&d, from: 0, to: 30, call: true, silent: { _ in nil })
        #expect(run(&d, from: 35, to: 200, call: false, silent: { _ in nil }) == [95])
    }

    @Test func unknownCallStateNeverStops() {
        var d = CallEndDetector()
        #expect(run(&d, from: 0, to: 3_600, call: nil, silent: { _ in 1_000 }).isEmpty)
    }
}

/// A source with a fixed level and an exact-zero mode.
@MainActor
final class LevelSource: MeetingAudioSource {
    var zeros = false
    func startCapture() throws {}
    func stopCapture() {}
    func drainSamples() -> [Float] { [Float](repeating: zeros ? 0 : 0.1, count: 16_000) }
    func currentLevel() -> Float? { zeros ? 0 : 0.8 }
}

@MainActor
struct MeetingAudioPresenceTests {
    @Test func meetingAudioThatNeverPlaysIsCountedFromTheStart() async throws {
        let mic = LevelSource()
        let call = LevelSource()
        call.zeros = true
        let recorder = MeetingRecorder(mic: { _ in mic }, system: { call }, transcribe: { _, _ in
            DetailedTranscript(text: "", language: "en", segments: [])
        })
        try recorder.start(title: "t", app: "", attendees: [], settings: MeetingRecorderTests.settings, autoTick: false)
        #expect(recorder.recordsSystemAudio)
        for _ in 0..<5 { recorder.tick() }
        #expect(recorder.systemHeardSeconds == 0)
        #expect(recorder.systemSilentSeconds == 5)
        call.zeros = false
        recorder.tick()
        #expect(recorder.systemHeardSeconds == 1)
        #expect(recorder.systemSilentSeconds == 0)
        _ = await recorder.stop()
    }

    @Test func levelsRefreshBetweenTicks() async throws {
        let mic = LevelSource()
        let call = LevelSource()
        let recorder = MeetingRecorder(mic: { _ in mic }, system: { call }, transcribe: { _, _ in
            DetailedTranscript(text: "", language: "en", segments: [])
        })
        try recorder.start(title: "t", app: "", attendees: [], settings: MeetingRecorderTests.settings, autoTick: false)
        recorder.refreshLevels()
        #expect(recorder.micLevel == 0.8 && recorder.systemLevel == 0.8)
        _ = await recorder.stop()
        recorder.refreshLevels()
        #expect(recorder.micLevel == 0 && recorder.systemLevel == 0)
    }
}
