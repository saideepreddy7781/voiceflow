import AppKit
import CoreAudio
@preconcurrency import AVFoundation
import Foundation
import VoiceFlowCore

/// `VoiceFlow --audio-selftest <out.json>` runs the meeting audio paths on this Mac's real microphone and speakers,
/// then writes what happened as numbers and quits.
///
/// It plays speech with `say` so the system-audio tap and the microphone both have something to hear. It never reads
/// or writes preferences, history, meetings or the Keychain, shows no UI, and uploads nothing: meetings use a local
/// stand-in for the transcriber. It runs before the single-instance check, so it works while VoiceFlow is open.
@MainActor
enum AudioSelfTest {
    static func requestedOutput(_ arguments: [String]) -> URL? {
        guard let flag = arguments.firstIndex(of: "--audio-selftest"), arguments.count > flag + 1 else { return nil }
        return URL(fileURLWithPath: (arguments[flag + 1] as NSString).expandingTildeInPath)
    }

    static func run(to url: URL) -> Never {
        let app = NSApplication.shared
        app.setActivationPolicy(.prohibited)
        app.finishLaunching()
        Task { @MainActor in
            let only = CommandLine.arguments.firstIndex(of: "--only").flatMap {
                CommandLine.arguments.count > $0 + 1 ? CommandLine.arguments[$0 + 1] : nil
            }
            let report = await AudioSelfTestRunner().runAll(only: only)
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            do {
                try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
                try encoder.encode(report).write(to: url)
            } catch {
                FileHandle.standardError.write(Data("Could not write the self-test report: \(error)\n".utf8))
                exit(1)
            }
            exit(report.passed ? 0 : 2)
        }
        app.run()
        exit(0)
    }
}

// MARK: Report

struct SelfTestReport: Codable {
    var version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev"
    var macOS = ProcessInfo.processInfo.operatingSystemVersionString
    var date = Date()
    /// `AVCaptureDevice.authorizationStatus(for: .audio)`: authorized, denied, notDetermined, restricted.
    var microphoneAuthorization = ""
    /// The system-audio recording permission as macOS reports it before anything starts: granted, denied, unknown.
    var systemAudioPermission = ""
    var devicesAtStart = AudioDevices.Snapshot()
    var devicesAtEnd = AudioDevices.Snapshot()
    var scenarios: [Scenario] = []
    var passed = false
}

struct Scenario: Codable {
    var name: String
    var passed = false
    var failures: [String] = []
    var seconds: [Second] = []
    var events: [Event] = []
    var dictations: [Dictation] = []
    var meeting: MeetingResult?
    var micEngineAtEnd = MicEngineDiagnostics()
    var systemAudioAtEnd: SystemAudioDiagnostics?
    var callApp: CallSimResult?
    var probes: [ProbeResult] = []
    var devices: [DeviceInfo] = []
    var classifications: [Classification] = []
    var errors: [String] = []
}

struct Second: Codable {
    var t: Int
    var micSamples = 0
    var micDB: Float = -120
    var micZeros = false
    var systemSamples: Int?
    var systemDB: Float?
    var systemZeros: Bool?
    var micEngineRunning = false
    var micInputDeviceID: UInt32 = 0
    var micInputSampleRate = 0.0
    var micConfigurationChanges = 0
    var micRestarts = 0
    var micState = ""
    var defaultInputID: UInt32 = 0
    var defaultOutputID: UInt32 = 0
    var speaking = false
    /// Meeting scenarios: the 0...1 levels the live view shows.
    var meetingMicLevel: Float?
    var meetingSystemLevel: Float?
    var meetingMicHealth: String?
}

struct Event: Codable {
    var t: Double
    var what: String
}

struct Dictation: Codable {
    var startedAt: Double
    var heldSeconds: Double
    var samples: Int
    var db: Float
    var verdict: String
    var classification: Classification?
}

/// How one recording is judged by the old fixed -45 dBFS rule and by `SpeechRule`. Numbers only.
struct Classification: Codable {
    var label = ""
    var seconds = 0.0
    var rmsDB: Float = -120
    var oldVoicedSeconds = 0.0
    var oldVerdict = ""
    var noiseFloorDB: Float?
    var thresholdDB: Float = 0
    var voicedSeconds = 0.0
    var speechDB: Float = -120
    var verdict = ""
    var uploadGainDB: Float = 0
    /// The dictation path would upload it: verdict is speech and the prepared segment is not silent.
    var uploads = false
    var uploadedSpeechDB: Float?

    init(label: String, samples: [Float], heldSeconds: Double) {
        self.label = label
        seconds = (Double(samples.count) / AudioMath.sampleRate * 10).rounded() / 10
        rmsDB = AudioMath.dbfs(AudioMath.rms(samples[...])).rounded()
        oldVoicedSeconds = (AudioMath.voicedSeconds(samples, thresholdDB: -45) * 100).rounded() / 100
        let length = Double(samples.count) / AudioMath.sampleRate
        if length < 0.5 { oldVerdict = heldSeconds >= 1 ? "noAudio" : "tooShort" }
        else if length >= 1, MicHealthMonitor.isDigitallySilent(samples) { oldVerdict = "digitalSilence" }
        else { oldVerdict = oldVoicedSeconds < 0.25 ? "noSpeech" : "speech" }
        let a = SpeechRule.analyze(samples)
        noiseFloorDB = a.noiseFloorDB.map { $0.rounded() }
        thresholdDB = a.thresholdDB.rounded()
        voicedSeconds = (a.voicedSeconds * 100).rounded() / 100
        speechDB = a.speechDB.rounded()
        verdict = "\(AudioMath.captureVerdict(samples, heldSeconds: heldSeconds))"
        uploadGainDB = SpeechRule.uploadGainDB(a).rounded()
        let prepared = SpeechRule.prepareForUpload(samples)
        uploads = verdict == "speech" && !AudioMath.isSilent(prepared)
        if uploads { uploadedSpeechDB = SpeechRule.analyze(prepared).speechDB.rounded() }
    }
}

struct MeetingResult: Codable {
    var micOnly = false
    var lastError: String?
    var micHealthStates: [String] = []
    var micMissingSeconds = 0
    var micRestartsRequested = 0
    var systemSilentSecondsMax = 0
    var micLevelMax: Float = 0
    var systemLevelMax: Float = 0
    var chunksYou = 0
    var chunksOthers = 0
    var linesYou = 0
    var linesOthers = 0
    var uploadedSeconds = 0.0
    var status: String?
    var error: Bool = false
    var savedMicMissingSeconds: Int?
}

// MARK: Runner

@MainActor
final class AudioSelfTestRunner {
    private let rate = Int(AudioMath.sampleRate)
    private var clock = Date()
    fileprivate var scenario = Scenario(name: "")
    private let voice = SpeechPlayer()

    func runAll(only: String?) async -> SelfTestReport {
        var report = SelfTestReport()
        report.microphoneAuthorization = Self.name(AudioRecorder.microphoneStatus)
        report.systemAudioPermission = SystemAudioPermission.current().rawValue
        report.devicesAtStart = AudioDevices.snapshot()
        let all: [(String, () async -> Void)] = [
            ("a-mic-alone", { await self.micAlone() }),
            ("b-mic-then-system-audio", { await self.micThenSystem(dictations: false) }),
            ("c-dictation-during-system-audio", { await self.micThenSystem(dictations: true) }),
            ("d-meeting-recorder", { await self.meetingRecorder() }),
            ("e-back-to-back-meetings-then-dictation", { await self.backToBack() }),
            ("f-call-app-first-then-meeting", { await self.withCallApp(callFirst: true) }),
            ("g-meeting-first-then-call-app", { await self.withCallApp(callFirst: false) }),
            ("h-capture-methods", { await self.captureMethods() }),
            ("i-devices-under-call", { await self.devicesUnderCall() }),
            ("j-quiet-speech-classification", { await self.quietSpeech() }),
        ]
        for (name, body) in all where only.map({ name.hasPrefix($0) }) ?? true {
            scenario = Scenario(name: name)
            clock = Date()
            await body()
            voice.stop()
            scenario.micEngineAtEnd = SharedMicrophone.shared.diagnostics
            scenario.passed = scenario.failures.isEmpty
            report.scenarios.append(scenario)
            try? await Task.sleep(for: .seconds(2))   // let the hardware settle between scenarios
        }
        report.devicesAtEnd = AudioDevices.snapshot()
        report.passed = !report.scenarios.isEmpty && report.scenarios.allSatisfy(\.passed)
        return report
    }

    private var now: Double { Date().timeIntervalSince(clock) }
    fileprivate func event(_ what: String) { scenario.events.append(Event(t: (now * 10).rounded() / 10, what: what)) }
    fileprivate func fail(_ why: String) { scenario.failures.append(why) }

    private static func name(_ status: AVAuthorizationStatus) -> String {
        switch status {
        case .authorized: "authorized"
        case .denied: "denied"
        case .notDetermined: "notDetermined"
        case .restricted: "restricted"
        @unknown default: "unknown"
        }
    }

    private func second(_ t: Int, mic: [Float], system: [Float]?) -> Second {
        var s = Second(t: t)
        s.micSamples = mic.count
        s.micDB = AudioMath.dbfs(AudioMath.rms(mic[...])).rounded()
        s.micZeros = !mic.isEmpty && MicHealthMonitor.isDigitallySilent(mic)
        if let system {
            s.systemSamples = system.count
            s.systemDB = AudioMath.dbfs(AudioMath.rms(system[...])).rounded()
            s.systemZeros = !system.isEmpty && MicHealthMonitor.isDigitallySilent(system)
        }
        let engine = SharedMicrophone.shared.diagnostics
        s.micEngineRunning = engine.engineRunning
        s.micInputDeviceID = engine.inputDeviceID
        s.micInputSampleRate = engine.inputSampleRate
        s.micConfigurationChanges = engine.configurationChanges
        s.micRestarts = engine.restartCount
        s.micState = engine.state
        let devices = AudioDevices.snapshot()
        s.defaultInputID = devices.defaultInputID
        s.defaultOutputID = devices.defaultOutputID
        s.speaking = voice.isSpeaking
        return s
    }

    /// Checks one stream's seconds: audio arrives every second, and it is real audio (not exact zeros) at least sometimes.
    /// The system tap delivers exact zeros whenever nothing plays, so `allowZeros` is for that stream.
    private func check(_ label: String, counts: [Int], dbs: [Float], zeros: [Bool], wantSpeech: Bool, allowZeros: Bool = false) {
        guard !counts.isEmpty else { return }
        let total = counts.reduce(0, +)
        let expected = counts.count * rate
        if Double(total) < 0.85 * Double(expected) { fail("\(label): \(total) of \(expected) samples arrived") }
        let stalled = counts.filter { $0 == 0 }.count
        if stalled > 1 { fail("\(label): \(stalled) seconds with no samples") }
        let zeroSeconds = zeros.filter { $0 }.count
        if !allowZeros, zeroSeconds > 1 { fail("\(label): \(zeroSeconds) seconds of digital silence") }
        if wantSpeech, (dbs.max() ?? -120) < -50 { fail("\(label): never louder than \(dbs.max() ?? -120) dBFS while speech played") }
    }

    // MARK: (a)

    private func micAlone() async {
        let mic = AudioRecorder()
        do { try mic.start(deviceUID: nil) } catch {
            scenario.errors.append("\(error)"); fail("mic did not start"); return
        }
        event("mic started")
        voice.start()
        for t in 1...8 {
            try? await Task.sleep(for: .seconds(1))
            scenario.seconds.append(second(t, mic: mic.drain(), system: nil))
        }
        voice.stop()
        mic.stop()
        event("mic stopped")
        let s = scenario.seconds
        check("mic", counts: s.map(\.micSamples), dbs: s.map(\.micDB), zeros: s.map(\.micZeros), wantSpeech: true)
    }

    // MARK: (b) and (c)

    private func micThenSystem(dictations: Bool) async {
        let mic = AudioRecorder()
        do { try mic.start(deviceUID: nil) } catch {
            scenario.errors.append("\(error)"); fail("mic did not start"); return
        }
        event("mic started")
        try? await Task.sleep(for: .seconds(1))
        _ = mic.drain()
        guard #available(macOS 14.2, *) else { fail("system audio needs macOS 14.2"); mic.stop(); return }
        let system = SystemAudioCapture()
        do { try system.start(); event("system audio started") } catch {
            scenario.errors.append("\(error)"); fail("system audio did not start")
        }
        voice.start()
        var dictation: AudioRecorder?
        var dictationStart = Date()
        for t in 1...15 {
            try? await Task.sleep(for: .seconds(1))
            scenario.seconds.append(second(t, mic: mic.drain(), system: system.isRunning ? system.drain() : nil))
            guard dictations else { continue }
            if t == 3 || t == 9 {
                let d = AudioRecorder()
                do {
                    try d.start(deviceUID: nil)
                    dictation = d
                    dictationStart = Date()
                    event("dictation started")
                } catch { scenario.errors.append("\(error)"); fail("dictation did not start") }
            }
            if t == 6 || t == 12, let d = dictation {
                let held = Date().timeIntervalSince(dictationStart)
                let samples = d.stop()
                event("dictation stopped")
                record(samples: samples, held: held, startedAt: dictationStart)
                dictation = nil
            }
        }
        voice.stop()
        scenario.systemAudioAtEnd = system.diagnostics
        system.stop()
        mic.stop()
        event("stopped")
        // The first second after the tap starts may be partial.
        let s = scenario.seconds
        check("mic", counts: s.map(\.micSamples), dbs: s.map(\.micDB), zeros: s.map(\.micZeros), wantSpeech: true)
        let sys = s.dropFirst()
        check("system", counts: sys.compactMap(\.systemSamples), dbs: sys.compactMap(\.systemDB),
              zeros: sys.compactMap(\.systemZeros), wantSpeech: true, allowZeros: true)
        let heard = sys.filter { ($0.systemDB ?? -120) > -50 }.count
        if heard < 5 { fail("system: speech heard in only \(heard) seconds") }
        if dictations, scenario.dictations.count != 2 { fail("\(scenario.dictations.count) of 2 dictations finished") }
    }

    /// `underCall`: a call app ducks the speech this test plays, so silence-free audio is the bar, not words.
    fileprivate var underCall = false

    private func record(samples: [Float], held: Double, startedAt: Date) {
        let verdict = AudioMath.captureVerdict(samples, heldSeconds: held)
        scenario.dictations.append(Dictation(startedAt: (startedAt.timeIntervalSince(clock) * 10).rounded() / 10,
                                             heldSeconds: (held * 10).rounded() / 10, samples: samples.count,
                                             db: AudioMath.dbfs(AudioMath.rms(samples[...])).rounded(), verdict: "\(verdict)",
                                             classification: Classification(label: underCall ? "call" : "normal", samples: samples, heldSeconds: held)))
        let acceptable: Set<String> = underCall ? ["\(CaptureVerdict.speech)", "\(CaptureVerdict.noSpeech)"] : ["\(CaptureVerdict.speech)"]
        if !acceptable.contains("\(verdict)") { fail("dictation verdict \(verdict) with \(samples.count) samples over \(held) s") }
    }

    // MARK: (d)

    private func meetingRecorder() async {
        await runMeeting(seconds: 24, dictationsAt: [6, 14])
    }

    /// (e) What the owner did: a meeting, stop, another meeting at once, then dictation after it.
    private func backToBack() async {
        await runMeeting(seconds: 12, dictationsAt: [])
        let first = scenario.meeting
        await runMeeting(seconds: 12, dictationsAt: [4])
        if let first, first.linesYou == 0 { fail("first meeting had no lines from the microphone") }
        try? await Task.sleep(for: .seconds(1))
        let d = AudioRecorder()
        do { try d.start(deviceUID: nil) } catch { scenario.errors.append("\(error)"); fail("dictation after meetings did not start"); return }
        event("dictation after meetings started")
        voice.start()
        try? await Task.sleep(for: .seconds(1.5))
        let began = Date()
        try? await Task.sleep(for: .seconds(3))
        voice.stop()
        record(samples: d.stop(), held: Date().timeIntervalSince(began), startedAt: began)
        event("dictation after meetings")
    }

    fileprivate func runMeeting(seconds: Int, dictationsAt: [Int]) async {
        let counter = ChunkCounter()
        let recorder = MeetingRecorder { wav, request in
            let seconds = Double(max(0, wav.count - 44)) / 2 / AudioMath.sampleRate
            await counter.add(seconds: seconds)
            // Distinct words per side, so echo removal keeps both.
            let text = request.plan.speaker == .user ? "alpha bravo charlie" : "delta echo foxtrot"
            return DetailedTranscript(text: text, language: "en",
                                      segments: [.init(start: 0, end: min(seconds, 2), text: text)])
        }
        let settings = MeetingRecorder.Settings(apiKey: "selftest", model: "whisper-large-v3-turbo", languages: ["en"],
                                                prompts: WhisperPrompts(), micDeviceUID: nil, recordSystemAudio: true)
        var result = MeetingResult()
        do {
            try recorder.start(title: "Self-test", app: "", attendees: [], settings: settings)
            event("meeting started")
        } catch {
            scenario.errors.append("\(error)"); fail("meeting did not start"); return
        }
        result.micOnly = recorder.meeting?.micOnly ?? true
        result.lastError = recorder.lastError
        if result.micOnly { fail("meeting fell back to microphone only") }
        voice.start()
        var dictation: AudioRecorder?
        var dictationStart = Date()
        for t in 1...seconds {
            try? await Task.sleep(for: .seconds(1))
            var s = second(t, mic: [], system: nil)
            s.meetingMicLevel = (recorder.micLevel * 100).rounded() / 100
            s.meetingSystemLevel = (recorder.systemLevel * 100).rounded() / 100
            s.meetingMicHealth = recorder.micHealth.rawValue
            scenario.seconds.append(s)
            result.micHealthStates.append(recorder.micHealth.rawValue)
            result.systemSilentSecondsMax = max(result.systemSilentSecondsMax, recorder.systemSilentSeconds)
            result.micLevelMax = max(result.micLevelMax, recorder.micLevel)
            result.systemLevelMax = max(result.systemLevelMax, recorder.systemLevel)
            if dictationsAt.contains(t) {
                let d = AudioRecorder()
                do { try d.start(deviceUID: nil); dictation = d; dictationStart = Date(); event("dictation started") } catch {
                    scenario.errors.append("\(error)"); fail("dictation during meeting did not start")
                }
            }
            if let d = dictation, Date().timeIntervalSince(dictationStart) >= 3 {
                let held = Date().timeIntervalSince(dictationStart)
                record(samples: d.stop(), held: held, startedAt: dictationStart)
                event("dictation stopped")
                dictation = nil
            }
        }
        voice.stop()
        scenario.systemAudioAtEnd = recorder.systemDiagnostics
        result.micMissingSeconds = recorder.micMissingSeconds
        result.micRestartsRequested = recorder.micRestartsRequested
        let meeting = await recorder.stop()
        event("meeting stopped")
        result.chunksYou = recorder.chunksSent[.you] ?? 0
        result.chunksOthers = recorder.chunksSent[.others] ?? 0
        result.linesYou = meeting?.lines.filter { $0.speaker == .you }.count ?? 0
        result.linesOthers = meeting?.lines.filter { $0.speaker == .others }.count ?? 0
        result.uploadedSeconds = (await counter.seconds * 10).rounded() / 10
        result.error = meeting?.error != nil
        result.savedMicMissingSeconds = meeting?.micMissingSeconds
        scenario.meeting = result

        if result.micMissingSeconds > 1 { fail("meeting mic missing for \(result.micMissingSeconds) s") }
        if result.micHealthStates.contains(where: { $0 != MicHealthState.healthy.rawValue }) { fail("meeting mic health left healthy") }
        if result.linesYou == 0, !underCall { fail("no lines from the microphone") }
        if result.linesOthers == 0 { fail("no lines from the meeting audio") }
        // The test speech starts a second or two in and pauses between sentences, so silence alone is no failure.
        if result.systemLevelMax < 0.3 { fail("meeting audio never louder than level \(result.systemLevelMax)") }
    }
}

extension AudioSelfTestRunner {
    /// A call app such as Chrome running Google Meet: another process holding the microphone with Apple's voice
    /// processing (echo cancellation) on, the way call apps open it.
    fileprivate func withCallApp(callFirst: Bool) async {
        let out = FileManager.default.temporaryDirectory.appending(path: "voiceflow-callsim-\(UUID().uuidString).json")
        let call = Process()
        call.executableURL = Bundle.main.executableURL
        call.arguments = ["--audio-callsim", "40", out.path]
        func launchCall() {
            do { try call.run(); event("call app started") } catch {
                scenario.errors.append("\(error)"); fail("call simulator did not start")
            }
        }
        underCall = true
        defer { underCall = false }
        if callFirst {
            launchCall()
            try? await Task.sleep(for: .seconds(3))
            await runMeeting(seconds: 20, dictationsAt: [8])
        } else {
            Task { @MainActor in
                try? await Task.sleep(for: .seconds(5))
                launchCall()
            }
            await runMeeting(seconds: 22, dictationsAt: [12])
        }
        call.terminate()
        call.waitUntilExit()
        if let data = try? Data(contentsOf: out), let sim = try? JSONDecoder().decode(CallSimResult.self, from: data) {
            scenario.callApp = sim
        }
        try? FileManager.default.removeItem(at: out)
    }
}

extension AudioSelfTestRunner {
    /// Each way of reading the microphone, alone and while a call app holds it with voice processing.
    fileprivate func captureMethods() async {
        for withCall in [false, true] {
            var call: Process?
            let out = FileManager.default.temporaryDirectory.appending(path: "voiceflow-callsim-\(UUID().uuidString).json")
            if withCall {
                let p = Process()
                p.executableURL = Bundle.main.executableURL
                p.arguments = ["--audio-callsim", "60", out.path]
                try? p.run()
                call = p
                event("call app started")
                try? await Task.sleep(for: .seconds(3))
            }
            voice.start()
            try? await Task.sleep(for: .seconds(1))
            for method in ["engine", "engine-voice-processing", "hal-ioproc", "capture-session", "voice-processing-unit"] {
                var result: ProbeResult
                do {
                    result = try await probe(method)
                } catch {
                    result = ProbeResult(buffers: 0, frames: 0, zeroBuffers: 0, db: -120, peakDB: -120)
                    result.error = "\(error)"
                }
                result.method = (withCall ? "call+" : "") + method
                scenario.probes.append(result)
                try? await Task.sleep(for: .seconds(0.5))
            }
            voice.stop()
            if let call {
                call.terminate()
                call.waitUntilExit()
                if let data = try? Data(contentsOf: out), let sim = try? JSONDecoder().decode(CallSimResult.self, from: data) { scenario.callApp = sim }
                try? FileManager.default.removeItem(at: out)
            }
        }
    }

    private func probe(_ method: String) async throws -> ProbeResult {
        let seconds = Duration.seconds(4)
        switch method {
        case "engine":
            let mic = AudioRecorder()
            try mic.start(deviceUID: nil)
            try? await Task.sleep(for: seconds)
            let samples = mic.stop()
            let counts = ProbeCounts()
            samples.withUnsafeBufferPointer { if let b = $0.baseAddress { counts.add(b, count: $0.count) } }
            return counts.result
        case "engine-voice-processing":
            let p = VoiceProcessingEngineProbe()
            try p.start()
            try? await Task.sleep(for: seconds)
            p.stop()
            return p.counts.result
        case "hal-ioproc":
            let p = HALInputProbe()
            try p.start()
            try? await Task.sleep(for: seconds)
            p.stop()
            for (i, var channel) in p.channelResults.enumerated() {
                channel.method = "channel-\(i)"
                scenario.probes.append(channel)
            }
            return p.counts.result
        case "capture-session":
            let p = CaptureSessionProbe()
            try p.start()
            try? await Task.sleep(for: seconds)
            p.stop()
            return p.counts.result
        default:
            let p = VoiceProcessingUnit()
            try p.start()
            try? await Task.sleep(for: seconds)
            p.stop()
            return p.counts.result
        }
    }
}

extension AudioSelfTestRunner {
    fileprivate func devicesUnderCall() async {
        scenario.devices = DeviceInfo.all(label: "before")
        let before = AudioDevices.anotherAppIsRecording()
        let out = FileManager.default.temporaryDirectory.appending(path: "voiceflow-callsim-\(UUID().uuidString).json")
        let p = Process()
        p.executableURL = Bundle.main.executableURL
        p.arguments = ["--audio-callsim", "20", out.path]
        try? p.run()
        try? await Task.sleep(for: .seconds(3))
        scenario.devices += DeviceInfo.all(label: "call")
        let during = AudioDevices.anotherAppIsRecording()
        p.terminate()
        p.waitUntilExit()
        try? FileManager.default.removeItem(at: out)
        try? await Task.sleep(for: .seconds(2))
        scenario.devices += DeviceInfo.all(label: "after")
        // Core Audio can report a finished process for a moment after it exits.
        var after = AudioDevices.anotherAppIsRecording()
        for _ in 0..<12 where after == true {
            try? await Task.sleep(for: .milliseconds(500))
            after = AudioDevices.anotherAppIsRecording()
        }
        event("another app recording: before \(before.map(String.init) ?? "unknown"), during \(during.map(String.init) ?? "unknown"), after \(after.map(String.init) ?? "unknown")")
        // What call-end detection and meeting detection rely on.
        if before != false || during != true || after != false { fail("another-app-recording check was wrong") }
    }
}

/// One Core Audio device as numbers.
struct DeviceInfo: Codable {
    var label = ""
    var id: UInt32 = 0
    var transport = ""
    var isDefaultInput = false
    var isDefaultOutput = false
    var alive: UInt32 = 0
    var runningSomewhere: UInt32 = 0
    var hidden: UInt32 = 0
    var nominalRate = 0.0
    var inputStreams = 0
    var inputChannels = 0
    var virtualRate: Double?
    var virtualChannels: UInt32?
    var virtualFormatID = ""
    var virtualFlags: UInt32?
    var streamFormatStatus: Int32 = 0
    var virtualStatus: Int32 = 0

    static func all(label: String) -> [DeviceInfo] {
        let system = AudioObjectID(kAudioObjectSystemObject)
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDevices, mScope: kAudioObjectPropertyScopeGlobal,
                                                 mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        AudioObjectGetPropertyDataSize(system, &address, 0, nil, &size)
        var ids = [AudioDeviceID](repeating: 0, count: Int(size) / 4)
        AudioObjectGetPropertyData(system, &address, 0, nil, &size, &ids)
        let snap = AudioDevices.snapshot()
        return ids.map { id in
            var d = DeviceInfo(label: label, id: id)
            d.isDefaultInput = id == snap.defaultInputID
            d.isDefaultOutput = id == snap.defaultOutputID
            d.alive = u32(id, kAudioDevicePropertyDeviceIsAlive)
            d.runningSomewhere = u32(id, kAudioDevicePropertyDeviceIsRunningSomewhere)
            d.hidden = u32(id, kAudioDevicePropertyIsHidden)
            let t = u32(id, kAudioDevicePropertyTransportType)
            d.transport = String(decoding: [24, 16, 8, 0].map { UInt8((t >> $0) & 0xff) }, as: UTF8.self)
            var rate = Float64(0)
            var a = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyNominalSampleRate, mScope: kAudioObjectPropertyScopeGlobal,
                                               mElement: kAudioObjectPropertyElementMain)
            var sz = UInt32(8)
            AudioObjectGetPropertyData(id, &a, 0, nil, &sz, &rate)
            d.nominalRate = rate
            a = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyStreams, mScope: kAudioDevicePropertyScopeInput,
                                           mElement: kAudioObjectPropertyElementMain)
            sz = 0
            AudioObjectGetPropertyDataSize(id, &a, 0, nil, &sz)
            var streams = [AudioStreamID](repeating: 0, count: Int(sz) / 4)
            if sz > 0 { AudioObjectGetPropertyData(id, &a, 0, nil, &sz, &streams) }
            d.inputStreams = streams.count
            a = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyStreamConfiguration, mScope: kAudioDevicePropertyScopeInput,
                                           mElement: kAudioObjectPropertyElementMain)
            sz = 0
            if AudioObjectGetPropertyDataSize(id, &a, 0, nil, &sz) == noErr, sz > 0 {
                let raw = UnsafeMutableRawPointer.allocate(byteCount: Int(sz), alignment: 16)
                if AudioObjectGetPropertyData(id, &a, 0, nil, &sz, raw) == noErr {
                    d.inputChannels = UnsafeMutableAudioBufferListPointer(raw.assumingMemoryBound(to: AudioBufferList.self))
                        .reduce(0) { $0 + Int($1.mNumberChannels) }
                }
                raw.deallocate()
            }
            if let first = streams.first {
                var asbd = AudioStreamBasicDescription()
                sz = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
                a = AudioObjectPropertyAddress(mSelector: kAudioStreamPropertyVirtualFormat, mScope: kAudioObjectPropertyScopeGlobal,
                                               mElement: kAudioObjectPropertyElementMain)
                d.virtualStatus = AudioObjectGetPropertyData(first, &a, 0, nil, &sz, &asbd)
                d.virtualRate = asbd.mSampleRate
                d.virtualChannels = asbd.mChannelsPerFrame
                d.virtualFlags = asbd.mFormatFlags
                let f = asbd.mFormatID
                d.virtualFormatID = String(decoding: [24, 16, 8, 0].map { UInt8((f >> $0) & 0xff) }, as: UTF8.self)
            }
            var asbd = AudioStreamBasicDescription()
            sz = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
            a = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyStreamFormat, mScope: kAudioDevicePropertyScopeInput,
                                           mElement: kAudioObjectPropertyElementMain)
            d.streamFormatStatus = AudioObjectGetPropertyData(id, &a, 0, nil, &sz, &asbd)
            return d
        }
    }

    private static func u32(_ id: AudioObjectID, _ selector: AudioObjectPropertySelector) -> UInt32 {
        var a = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var v = UInt32(0)
        var sz = UInt32(4)
        AudioObjectGetPropertyData(id, &a, 0, nil, &sz, &v)
        return v
    }
}

extension AudioSelfTestRunner {
    /// Real recordings through this Mac's microphone, judged by the old and new speech rules: the room, speech from
    /// the speakers, the same speech made 10 to 25 dB quieter (a voice-processing call is about 10 dB down), and both
    /// again while the call stand-in holds the microphone.
    fileprivate func quietSpeech() async {
        func capture(seconds: Double, speaking: Bool) async -> [Float] {
            if speaking {
                voice.start()
                try? await Task.sleep(for: .seconds(1.5))
            }
            let mic = AudioRecorder()
            do { try mic.start(deviceUID: nil) } catch { scenario.errors.append("\(error)"); return [] }
            try? await Task.sleep(for: .seconds(seconds))
            let samples = mic.stop()
            voice.stop()
            try? await Task.sleep(for: .seconds(1))
            return samples
        }
        func judge(_ label: String, _ samples: [Float]) -> Classification {
            let c = Classification(label: label, samples: samples, heldSeconds: Double(samples.count) / AudioMath.sampleRate)
            scenario.classifications.append(c)
            return c
        }
        let room = await capture(seconds: 5, speaking: false)
        let speech = await capture(seconds: 6, speaking: true)
        event("recorded room and speech")
        if judge("room", room).verdict != "noSpeech" { fail("room noise was judged speech") }
        if judge("room-10dB", Self.attenuate(room, by: 10)).verdict != "noSpeech" { fail("quiet room noise was judged speech") }
        if !judge("speech", speech).uploads { fail("speech was not uploaded") }
        for drop: Float in [10, 15, 20, 25] {
            let c = judge("speech-\(Int(drop))dB", Self.attenuate(speech, by: drop))
            if drop <= 15, !c.uploads { fail("speech \(Int(drop)) dB down was not uploaded") }
        }

        let out = FileManager.default.temporaryDirectory.appending(path: "voiceflow-callsim-\(UUID().uuidString).json")
        let call = Process()
        call.executableURL = Bundle.main.executableURL
        call.arguments = ["--audio-callsim", "40", out.path]
        do { try call.run(); event("call app started") } catch { scenario.errors.append("\(error)"); return }
        try? await Task.sleep(for: .seconds(3))
        let callRoom = await capture(seconds: 5, speaking: false)
        let callSpeech = await capture(seconds: 6, speaking: true)
        call.terminate()
        call.waitUntilExit()
        try? FileManager.default.removeItem(at: out)
        if judge("call-room", callRoom).verdict != "noSpeech" { fail("room noise under the call was judged speech") }
        _ = judge("call-speech", callSpeech)
    }

    static func attenuate(_ samples: [Float], by db: Float) -> [Float] {
        let g = Float(pow(10, Double(-db) / 20))
        return samples.map { $0 * g }
    }
}

struct CallSimResult: Codable {
    var voiceProcessing = false
    var sampleRate = 0.0
    var channels = 0
    var buffers = 0
    var zeroBuffers = 0
    var db = -120.0
    var error: String?
}

/// `--audio-callsim <seconds> <out.json>`: holds the microphone with voice processing, like a call app, then exits.
@MainActor
enum CallSimulator {
    static func requested(_ arguments: [String]) -> (Double, URL)? {
        guard let flag = arguments.firstIndex(of: "--audio-callsim"), arguments.count > flag + 2,
              let seconds = Double(arguments[flag + 1]) else { return nil }
        return (seconds, URL(fileURLWithPath: arguments[flag + 2]))
    }

    final class Counts: @unchecked Sendable {
        let lock = NSLock()
        var buffers = 0
        var zeros = 0
    }

    static func run(seconds: Double, to url: URL) -> Never {
        var result = CallSimResult()
        let unit = VoiceProcessingUnit()
        func write() {
            let r = unit.counts.result
            result.buffers = r.buffers
            result.zeroBuffers = r.zeroBuffers
            result.db = r.db
            try? JSONEncoder().encode(result).write(to: url)
        }
        signal(SIGTERM, SIG_IGN)
        let term = DispatchSource.makeSignalSource(signal: SIGTERM, queue: .main)
        term.setEventHandler { unit.stop(); write(); exit(0) }
        term.resume()
        do {
            try unit.start()
            result.voiceProcessing = true
            result.sampleRate = 48_000
            result.channels = 1
        } catch {
            result.error = "\(error)"
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + seconds) { unit.stop(); write(); exit(0) }
        dispatchMain()
    }

    nonisolated static func makeBlock(_ counts: Counts) -> AVAudioNodeTapBlock {
        { buffer, _ in
            let n = Int(buffer.frameLength)
            var zero = true
            if let data = buffer.floatChannelData?[0] {
                for i in 0..<n where abs(data[i]) >= 0.000_001 { zero = false; break }
            }
            counts.lock.lock(); counts.buffers += 1; if zero { counts.zeros += 1 }; counts.lock.unlock()
        }
    }
}

private actor ChunkCounter {
    var seconds = 0.0
    func add(seconds s: Double) { seconds += s }
}

/// Speaks test sentences through the default output, one after another, until stopped.
@MainActor
private final class SpeechPlayer {
    private var process: Process?
    private var active = false
    private var index = 0
    var isSpeaking: Bool { process?.isRunning == true }

    private static let lines = [
        "This is the VoiceFlow audio self test. The microphone and the meeting audio should both hear this sentence.",
        "Seven bright orange boats crossed the quiet harbour before the morning rain arrived.",
        "Please read the numbers four, eight, fifteen, sixteen, twenty three and forty two out loud.",
    ]

    func start() {
        guard !active else { return }
        active = true
        speakNext()
    }

    func stop() {
        active = false
        process?.terminate()
        process = nil
    }

    private func speakNext() {
        guard active else { return }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/say")
        // One long paragraph per process: starting a new `say` can take seconds on a busy Mac, which left
        // dictation windows with nothing playing.
        let paragraph = (0..<3).map { Self.lines[($0 + index) % Self.lines.count] }.joined(separator: " ")
        p.arguments = ["-v", "Samantha", paragraph]
        index += 1
        p.terminationHandler = { [weak self] _ in
            Task { @MainActor in self?.speakNext() }
        }
        do { try p.run(); process = p } catch { active = false }
    }
}
