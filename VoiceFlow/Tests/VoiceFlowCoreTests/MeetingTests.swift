import Foundation
import Testing
@testable import VoiceFlowCore

struct MeetingTests {
    @Test func chunkLinesUseTheMeetingClock() {
        let map = TimeMap(spans: [.init(chunkStart: 0, meetingStart: 60, seconds: 2), .init(chunkStart: 2.25, meetingStart: 90, seconds: 2)])
        let transcript = DetailedTranscript(text: "a b", language: "en",
                                            segments: [.init(start: 0, end: 1.9, text: "Hello there"), .init(start: 2.3, end: 3.9, text: "Hi")])
        let lines = ChunkLines.lines(for: transcript, map: map, speaker: .others, chunkFallbackTime: 60)
        #expect(lines.map(\.speaker) == [.others, .others])
        #expect(abs(lines[0].time - 60) < 0.001)
        #expect(abs(lines[1].time - 90.05) < 0.001)
        #expect(lines[0].language == "en")
    }

    @Test func markdownAndSafeFileName() {
        var m = Meeting(title: "Launch / review: Q4?", start: Date(timeIntervalSince1970: 1_800_000_000), app: "Zoom")
        m.end = m.start.addingTimeInterval(30 * 60)
        m.lines = [TranscriptLine(time: 5, speaker: .you, text: "Let's begin.")]
        m.notes = MeetingNotes(title: "Launch review", summary: ["Launch moves"], decisions: ["Ship Nov 20"],
                               actionItems: [ActionItem(task: "Update notes", owner: "Ramesh", due: "Friday")], questions: ["Privacy review?"])
        let md = MeetingMarkdown.render(m)
        #expect(md.contains("## Decisions\n\n- Ship Nov 20"))
        #expect(md.contains("- [ ] Update notes (Ramesh), due Friday"))
        #expect(md.contains("**You** [0:05]: Let's begin."))
        let name = MeetingMarkdown.fileName(m)
        #expect(!name.contains("/") && !name.contains(":") && !name.contains("?"))
        #expect(name.hasSuffix(".md"))
    }

    @Test func notesJSONParsing() {
        let json = #"{"title":"Sync","summary":["a"],"decisions":[],"action_items":[{"task":"Send deck","owner":"","due":""}],"questions":[]}"#
        let notes = MeetingNotesWriter.parse(json)
        #expect(notes?.title == "Sync")
        #expect(notes?.actionItems.first?.task == "Send deck")
        #expect(notes?.actionItems.first?.owner == nil)          // empty owner becomes nil
        #expect(MeetingNotesWriter.parse("not json") == nil)
    }

    @Test func longMeetingsAreSplitForTheTokenLimit() {
        let line = TranscriptLine(time: 0, speaker: .others, text: String(repeating: "word ", count: 500))
        let chunks = MeetingNotesWriter.chunks(Array(repeating: line, count: 13))   // ~6,500 words
        #expect(chunks.count == 3)
        #expect(MeetingNotesWriter.chunks([]).isEmpty)
    }

    @Test func meetingDetection() {
        #expect(MeetingDetector.detect(runningBundleIDs: ["us.zoom.xos"], micInUse: true, browserTitle: nil) == "Zoom")
        #expect(MeetingDetector.detect(runningBundleIDs: ["us.zoom.xos"], micInUse: false, browserTitle: nil) == nil)
        #expect(MeetingDetector.detect(runningBundleIDs: [], micInUse: true, browserTitle: "Meet - abc-defg-hij") == "Google Meet")
        #expect(MeetingDetector.detect(runningBundleIDs: ["com.apple.Notes"], micInUse: true, browserTitle: nil) == nil)
    }

    @Test func notetakerSettingsRoundTrip() {
        var prefs = Preferences()
        prefs.meetingsFolder = "/tmp/notes"
        prefs.meetingsNotesLanguage = .meeting
        prefs.teluguScript = .roman
        let data = try! JSONEncoder().encode(prefs)
        let back = try! JSONDecoder().decode(Preferences.self, from: data)
        #expect(back == prefs)
    }
}

/// A synthetic two-person meeting through the real pipeline. Runs only with GROQ_API_KEY.
struct LiveMeetingTests {
    /// Speaks `text` with a macOS voice and returns 16 kHz mono samples.
    static func speak(_ text: String, voice: String) throws -> [Float] {
        let dir = FileManager.default.temporaryDirectory.appending(path: "vf-meeting-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let aiff = dir.appending(path: "a.aiff"), wav = dir.appending(path: "a.wav")
        for (tool, args) in [("/usr/bin/say", ["-v", voice, "-o", aiff.path, text]),
                             ("/usr/bin/afconvert", ["-f", "WAVE", "-d", "LEI16@16000", "-c", "1", aiff.path, wav.path])] {
            let p = Process()
            p.executableURL = URL(fileURLWithPath: tool)
            p.arguments = args
            try p.run()
            p.waitUntilExit()
        }
        let data = try Data(contentsOf: wav)
        // Find the "data" chunk and read 16-bit samples.
        guard let range = data.range(of: Data("data".utf8)) else { return [] }
        let start = range.upperBound + 4
        return stride(from: start, to: data.count - 1, by: 2).map { i in
            Float(Int16(bitPattern: UInt16(data[i]) | UInt16(data[i + 1]) << 8)) / Float(Int16.max)
        }
    }

    @Test(.enabled(if: LivePipelineTests.key != nil))
    func twoSpeakerMeetingBecomesNotes() async throws {
        let script: [(Speaker, String)] = [
            (.you, "Thanks for joining. Let's review the launch plan for the mobile app."),
            (.others, "The payment integration is still pending, so I suggest we move the launch to November twentieth."),
            (.you, "Agreed, we will move the launch to November twentieth. Ramesh, can you update the release notes by Friday?"),
            (.others, "Yes, I will update the release notes by Friday. One open question is whether we need a new privacy review."),
        ]
        var mic: [Float] = [], system: [Float] = []
        let gap = [Float](repeating: 0, count: 12_800)   // 0.8 s
        for (who, text) in script {
            let voice = try Self.speak(text, voice: who == .you ? "Samantha" : "Daniel")
            let quiet = [Float](repeating: 0, count: voice.count)
            mic += (who == .you ? voice : quiet.map { _ in 0.002 }) + gap      // a little room noise on your mic
            system += (who == .others ? voice : quiet) + gap
        }
        let key = LivePipelineTests.key!
        let micTranscript = try await GroqTranscriber(apiKey: key).transcribeDetailed(wav: AudioMath.wav(mic), language: "en", prompt: "Ramesh")
        let transcript = try await GroqTranscriber(apiKey: key).transcribeDetailed(wav: AudioMath.wav(system), language: "en", prompt: "Ramesh")
        let lines = zip([micTranscript, transcript], [Speaker.you, .others]).flatMap { t, who in
            ChunkLines.lines(for: t, map: TimeMap(spans: [.init(chunkStart: 0, meetingStart: 0, seconds: 120)]), speaker: who, chunkFallbackTime: 0)
        }.sorted { $0.time < $1.time }
        for l in lines { print("[\(Meeting.clock(l.time))] \(l.speaker.label): \(l.text)") }
        #expect(lines.contains { $0.speaker == .you && $0.text.contains("launch plan") })
        #expect(lines.contains { $0.speaker == .others && $0.text.lowercased().contains("payment") })

        var meeting = Meeting(title: "Meeting", app: "Zoom")
        meeting.lines = lines
        let notes = try await MeetingNotesWriter(chat: GroqChat(apiKey: key), model: "openai/gpt-oss-120b")
            .write(meeting: meeting, language: .english)
        print("NOTES:", notes)
        let all = (notes.summary + notes.decisions + notes.actionItems.map(\.task) + notes.questions).joined(separator: " ").lowercased()
        #expect(all.contains("november"))
        #expect(notes.actionItems.contains { $0.task.lowercased().contains("release notes") })
        #expect(notes.questions.contains { $0.lowercased().contains("privacy") })
    }
}

/// Simulated audio source: hands out the samples pushed into it.
@MainActor
final class FakeSource: MeetingAudioSource {
    var queue: [Float] = []
    var failStart = false
    func startCapture() throws { if failStart { throw NSError(domain: "fake", code: 1) } }
    func stopCapture() {}
    func drainSamples() -> [Float] { defer { queue = [] }; return queue }
}

@MainActor
struct MeetingRecorderTests {
    static func speech(_ seconds: Double) -> [Float] { (0..<Int(seconds * 16_000)).map { 0.3 * sin(Float($0) * 0.17) } }
    static func silence(_ seconds: Double) -> [Float] { [Float](repeating: 0, count: Int(seconds * 16_000)) }
    static let settings = MeetingRecorder.Settings(apiKey: "test", model: "m", languages: ["en"], prompts: WhisperPrompts(),
                                                    micDeviceUID: nil, recordSystemAudio: true)

    @Test func chunksAreTranscribedInOrderWithSpeakers() async throws {
        let mic = FakeSource(), system = FakeSource()
        let counter = Counter()
        let recorder = MeetingRecorder(mic: { _ in mic }, system: { system }) { wav, _ in
            let n = await counter.next()
            return DetailedTranscript(text: "chunk \(n)", language: "en", segments: [.init(start: 0, end: 2, text: "chunk \(n)")])
        }
        try recorder.start(title: "T", app: "Zoom", attendees: [], settings: Self.settings, autoTick: false)
        // 25 s: you talk for 22 s, then a pause; others silent. Then others talk 12 s.
        mic.queue = Self.speech(22) + Self.silence(3);           system.queue = Self.silence(25)
        recorder.tick()                                            // first chunk cut at the pause
        mic.queue = Self.silence(12);                             system.queue = Self.speech(12)
        recorder.tick()                                            // not long enough to cut
        let meeting = await recorder.stop()                        // final piece is sent on stop
        #expect(meeting?.lines.map(\.text) == ["chunk 1", "chunk 2"])
        #expect(meeting?.lines.map(\.speaker) == [.you, .others])
        #expect((meeting?.lines.last?.time ?? 0) > 20)            // second chunk starts after the first
        #expect(meeting?.micOnly == false)
        #expect(meeting?.error == nil)
    }

    @Test func shortMeetingIsStillTranscribedOnStop() async throws {
        let mic = FakeSource()
        let recorder = MeetingRecorder(mic: { _ in mic }, system: { nil }) { _, _ in
            DetailedTranscript(text: "hello", language: "en", segments: [])
        }
        try recorder.start(title: "T", app: "", attendees: [], settings: Self.settings, autoTick: false)
        mic.queue = Self.speech(4)
        recorder.tick()
        let meeting = await recorder.stop()
        #expect(meeting?.lines.map(\.text) == ["hello"])
        #expect(meeting?.micOnly == true)
    }

    @Test func silentMeetingExplainsWhy() async throws {
        let mic = FakeSource()
        let recorder = MeetingRecorder(mic: { _ in mic }, system: { nil }) { _, _ in
            Issue.record("silence must not be uploaded")
            return DetailedTranscript(text: "", language: nil, segments: [])
        }
        try recorder.start(title: "T", app: "", attendees: [], settings: Self.settings, autoTick: false)
        mic.queue = Self.silence(30)
        recorder.tick()
        let meeting = await recorder.stop()
        #expect(meeting?.lines.isEmpty == true)
        #expect(meeting?.error?.contains("No speech was picked up") == true)
    }

    @Test func systemAudioFailureFallsBackToMicOnly() async throws {
        let mic = FakeSource(), system = FakeSource()
        system.failStart = true
        let recorder = MeetingRecorder(mic: { _ in mic }, system: { system }) { _, _ in
            DetailedTranscript(text: "hi", language: "en", segments: [])
        }
        try recorder.start(title: "T", app: "", attendees: [], settings: Self.settings, autoTick: false)
        #expect(recorder.lastError != nil)
        mic.queue = Self.speech(3)
        let meeting = await recorder.stop()
        #expect(meeting?.micOnly == true)
        #expect(meeting?.lines.count == 1)
    }

    @Test func failedChunkIsMarkedNotLost() async throws {
        let mic = FakeSource()
        let recorder = MeetingRecorder(mic: { _ in mic }, system: { nil }) { _, _ in throw ProviderError.network("offline") }
        try recorder.start(title: "T", app: "", attendees: [], settings: Self.settings, autoTick: false)
        mic.queue = Self.speech(5)
        let meeting = await recorder.stop()
        #expect(meeting?.lines.first?.text.contains("could not be transcribed") == true)
    }
}

actor Counter {
    private var n = 0
    func next() -> Int { n += 1; return n }
}

@MainActor
struct MeetingRecorderRobustnessTests {
    typealias T = MeetingRecorderTests

    @Test func backlogIsSentInPiecesOfAtMost45Seconds() async throws {
        let mic = FakeSource()
        let sizes = SizeLog()
        let recorder = MeetingRecorder(mic: { _ in mic }, system: { nil }) { wav, _ in
            await sizes.add(wav.count)
            return DetailedTranscript(text: "x", language: "en", segments: [])
        }
        try recorder.start(title: "T", app: "", attendees: [], settings: T.settings, autoTick: false)
        mic.queue = T.speech(100)                       // 100 s piled up in one tick (no pauses)
        recorder.tick()
        mic.queue = T.speech(70)                        // more continuous speech before Stop
        let meeting = await recorder.stop()
        let seconds = await sizes.all.map { Double($0 - 44) / 2 / 16_000 }
        #expect(seconds.allSatisfy { $0 <= 45.01 })
        #expect(abs(seconds.reduce(0, +) - 170) < 0.1)  // nothing lost
        #expect(meeting?.lines.count == seconds.count)
    }

    @Test func stalledMicIsPaddedSoMeetingAudioKeepsFlowing() async throws {
        let mic = FakeSource(), system = FakeSource()
        let count = Counter()
        let recorder = MeetingRecorder(mic: { _ in mic }, system: { system }) { _, _ in
            _ = await count.next()
            return DetailedTranscript(text: "others", language: "en", segments: [])
        }
        try recorder.start(title: "T", app: "", attendees: [], settings: T.settings, autoTick: false)
        system.queue = T.speech(22) + T.silence(3) + T.speech(5)   // mic delivers nothing (restarting after AirPods switch)
        recorder.tick()                                // the mic side is padded, so a chunk goes out now
        try await Task.sleep(for: .milliseconds(300))  // uploads run in the background
        #expect(await count.next() == 2)               // one upload happened before Stop (counter was at 1)
        _ = await recorder.stop()
    }

    @Test func rateLimitedChunkIsRetried() async throws {
        let mic = FakeSource()
        let attempts = Counter()
        let recorder = MeetingRecorder(mic: { _ in mic }, system: { nil }) { _, _ in
            if await attempts.next() == 1 { throw ProviderError.rateLimited(retryAfter: 0) }
            return DetailedTranscript(text: "recovered", language: "en", segments: [])
        }
        try recorder.start(title: "T", app: "", attendees: [], settings: T.settings, autoTick: false)
        mic.queue = T.speech(5)
        let meeting = await recorder.stop()
        #expect(meeting?.lines.map(\.text) == ["recovered"])
    }

    @Test func secondOpinionRule() {
        let langs = LanguagePlan(languages: ["en", "te"])
        func t(_ text: String, _ lang: String?, _ conf: Double?) -> DetailedTranscript { .init(text: text, language: lang, segments: [], confidence: conf) }
        #expect(!LanguageResolver.needsSecondOpinion(t("Can you check the invoice?", "en", -0.14), plan: langs))
        #expect(LanguageResolver.needsSecondOpinion(t("नेनु रेपू", "hi", -0.08), plan: langs))
        #expect(LanguageResolver.needsSecondOpinion(t("Neme Rebu meeting", "en", -0.7), plan: langs))
        #expect(LanguageResolver.needsSecondOpinion(t("hello", nil, -0.1), plan: langs))
    }

    @Test func teluguCountsAsMoreTokens() {
        #expect(MeetingNotesWriter.estimatedTokens("hello world, this is fine") < 10)
        #expect(MeetingNotesWriter.estimatedTokens("నేను రేపు ఆఫీస్ కి వస్తాను") > 15)
        let line = TranscriptLine(time: 0, speaker: .others, text: String(repeating: "నేను రేపు ఆఫీస్ ", count: 120))
        let chunks = MeetingNotesWriter.chunks(Array(repeating: line, count: 4))           // ~1,560 tokens per line
        #expect(chunks.count == 2)
        #expect(chunks.allSatisfy { MeetingNotesWriter.estimatedTokens($0) <= MeetingNotesWriter.chunkTokens })
    }

    @Test func handsFreeFromTheBarFinishesOrCancels() {
        var r = GestureRecognizer()
        r.enterHandsFree()
        #expect(r.handle(.hotkeyDown(at: 0, controlHeld: false)) == [.finish(.dictation)])
        var e = GestureRecognizer()
        e.enterHandsFree()
        #expect(e.handle(.escape(at: 0)) == [.cancel])
    }
}

actor SizeLog {
    private(set) var all: [Int] = []
    func add(_ n: Int) { all.append(n) }
}
