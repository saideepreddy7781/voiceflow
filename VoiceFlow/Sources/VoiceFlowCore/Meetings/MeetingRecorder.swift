import Foundation
import os

/// Where a meeting's audio comes from. The real sources are the microphone and the system-audio tap;
/// tests use simulated ones.
@MainActor
public protocol MeetingAudioSource: AnyObject {
    func startCapture() throws
    func stopCapture()
    /// Takes everything captured since the last call, as 16 kHz mono samples.
    func drainSamples() -> [Float]
    /// Tries to bring back a source that stopped delivering audio.
    func restartCapture()
}

public extension MeetingAudioSource {
    func restartCapture() {}
}

/// The microphone as a meeting source.
@MainActor
final class MicSource: MeetingAudioSource {
    private let recorder: AudioRecorder
    private let deviceUID: String?
    init(deviceUID: String?, recorder: AudioRecorder = AudioRecorder()) {
        self.deviceUID = deviceUID
        self.recorder = recorder
    }
    func startCapture() throws { try recorder.start(deviceUID: deviceUID) }
    func stopCapture() { recorder.stop() }
    func drainSamples() -> [Float] { recorder.drain() }
    func restartCapture() { recorder.restartMicrophone() }
}

@available(macOS 14.2, *)
extension SystemAudioCapture: MeetingAudioSource {
    public func startCapture() throws { try start() }
    public func stopCapture() { stop() }
    public func drainSamples() -> [Float] { drain() }
}

/// Records a meeting: your microphone plus the meeting audio, transcribed while it happens.
///
/// Memory stays small for long meetings: audio is drained from both sources every second and only
/// the chunk still being collected (at most 45 s) is kept. Each chunk is mixed into one stream, so a
/// meeting costs one transcription upload per chunk, and Whisper's timed segments are labelled
/// You/Others by comparing the two sources over each segment.
@MainActor
public final class MeetingRecorder {
    public struct Settings: Sendable {
        public var apiKey: String
        public var model: String
        public var language: String?
        public var parallelLanguage: String?
        public var languages: [String]
        public var prompt: String?
        public var micDeviceUID: String?
        public var recordSystemAudio: Bool

        public init(apiKey: String, model: String, language: String?, parallelLanguage: String?, languages: [String],
                    prompt: String?, micDeviceUID: String?, recordSystemAudio: Bool) {
            self.apiKey = apiKey
            self.model = model
            self.language = language
            self.parallelLanguage = parallelLanguage
            self.languages = languages
            self.prompt = prompt
            self.micDeviceUID = micDeviceUID
            self.recordSystemAudio = recordSystemAudio
        }
    }

    public typealias Transcribe = @Sendable (Data, Settings) async throws -> DetailedTranscript

    private let log = Logger(subsystem: "com.saideep.voiceflow", category: "meeting")
    private let makeMic: @MainActor (Settings) -> MeetingAudioSource
    private let makeSystem: @MainActor () -> MeetingAudioSource?
    private let transcribe: Transcribe

    private var mic: MeetingAudioSource?
    private var system: MeetingAudioSource?
    private var timer: Timer?
    private var settings: Settings?

    // Audio not yet sent, aligned sample-for-sample.
    private var pendingMic: [Float] = []
    private var pendingSystem: [Float] = []
    private var queuedMic: [Float] = []
    private var queuedSystem: [Float] = []
    /// Samples already sent, i.e. the start time of the pending chunk.
    private var sentSamples = 0
    /// Appends each chunk's lines in order, exactly once.
    private var appendChain: Task<Void, Never>?
    private var heardSpeech = false
    private var micMonitor = MicHealthMonitor()

    public private(set) var meeting: Meeting?
    public private(set) var isRecording = false
    public private(set) var lastError: String?
    /// Live input levels (0...1) for the Meetings page.
    public private(set) var micLevel: Float = 0
    public private(set) var systemLevel: Float = 0
    /// Seconds in a row the meeting audio has been digital silence (often: system audio permission missing).
    public private(set) var systemSilentSeconds = 0
    /// Whether the microphone is delivering usable audio. A meeting keeps recording through any of these.
    public private(set) var micHealth: MicHealthState = .healthy
    /// Seconds of this meeting with no usable microphone audio.
    public private(set) var micMissingSeconds = 0
    /// Restarts requested because the microphone stalled.
    public private(set) var micRestartsRequested = 0
    /// Called on the main actor whenever new transcript lines arrive.
    public var onUpdate: ((Meeting) -> Void)?

    public init() {
        makeMic = { settings in MicSource(deviceUID: settings.micDeviceUID) }
        makeSystem = {
            if #available(macOS 14.2, *) { return SystemAudioCapture() }
            return nil
        }
        transcribe = { wav, settings in
            try await GroqTranscriber(apiKey: settings.apiKey, model: settings.model)
                .transcribeResolving(wav: wav, language: settings.language, parallelLanguage: settings.parallelLanguage,
                                     languages: settings.languages, prompt: settings.prompt, sequential: true)
        }
    }

    /// For tests: simulated sources and transcription.
    init(mic: @escaping @MainActor (Settings) -> MeetingAudioSource, system: @escaping @MainActor () -> MeetingAudioSource?,
         transcribe: @escaping Transcribe) {
        makeMic = mic
        makeSystem = system
        self.transcribe = transcribe
    }

    /// Meeting chunks: 20–45 s, cut at a pause, no overlap (pauses separate sentences).
    static func makeCutter() -> SegmentCutter {
        var c = SegmentCutter()
        c.minimumSeconds = 20
        c.maximumSeconds = 45
        c.pauseSeconds = 0.6
        c.overlapSeconds = 0
        return c
    }

    public func start(title: String, app: String, attendees: [String], settings: Settings, autoTick: Bool = true) throws {
        guard !isRecording else { return }
        let micSource = makeMic(settings)
        try micSource.startCapture()
        mic = micSource
        var m = Meeting(title: title, app: app)
        m.attendees = attendees
        lastError = nil
        if settings.recordSystemAudio, let capture = makeSystem() {
            do {
                try capture.startCapture()
                system = capture
            } catch {
                lastError = "\(error)"
                m.micOnly = true
                log.error("System audio unavailable: \(String(describing: error), privacy: .public)")
            }
        } else {
            m.micOnly = true
        }
        meeting = m
        self.settings = settings
        isRecording = true
        heardSpeech = false
        systemSilentSeconds = 0
        micMonitor = MicHealthMonitor()
        micHealth = .healthy
        micMissingSeconds = 0
        micRestartsRequested = 0
        pendingMic = []; pendingSystem = []; queuedMic = []; queuedSystem = []
        sentSamples = 0
        appendChain = nil
        if autoTick {
            timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated { self?.tick() }
            }
        }
        log.info("Meeting recording started (system audio: \(self.system != nil, privacy: .public))")
    }

    /// Stops recording and returns the meeting once every chunk is transcribed.
    public func stop() async -> Meeting? {
        guard isRecording else { return meeting }
        timer?.invalidate()
        timer = nil
        tick(final: true)
        if micMissingSeconds > 0 { meeting?.micMissingSeconds = micMissingSeconds }
        mic?.stopCapture()
        system?.stopCapture()
        mic = nil
        system = nil
        isRecording = false
        micLevel = 0
        systemLevel = 0
        meeting?.end = Date()
        await appendChain?.value
        if meeting?.lines.isEmpty == true {
            meeting?.error = heardSpeech
                ? "Speech was recorded but could not be transcribed. Check your Groq key and internet connection, then try again."
                : "No speech was picked up. Check that your microphone works (Settings → General → Test microphone) and that the call's audio plays on this Mac."
        }
        return meeting
    }

    func tick(final: Bool = false) {
        let newMic = mic?.drainSamples() ?? []
        queuedMic.append(contentsOf: newMic)
        micLevel = AudioMath.level(newMic.suffix(1_600)[...])
        if !final { updateMicHealth(newMic) }
        if let system {
            let newSystem = system.drainSamples()
            systemLevel = AudioMath.level(newSystem.suffix(1_600)[...])
            systemSilentSeconds = newSystem.allSatisfy { $0 == 0 } ? systemSilentSeconds + 1 : 0
            queuedSystem.append(contentsOf: newSystem)
            // Keep the two sources aligned. Whichever stalls (mic restarting, output device changing) is padded.
            let twoSeconds = Int(AudioMath.sampleRate) * 2
            let lag = queuedMic.count - queuedSystem.count
            if lag > twoSeconds { queuedSystem.append(contentsOf: [Float](repeating: 0, count: lag)) }
            if -lag > twoSeconds { queuedMic.append(contentsOf: [Float](repeating: 0, count: -lag)) }
            let n = final ? max(queuedMic.count, queuedSystem.count) : min(queuedMic.count, queuedSystem.count)
            pendingMic.append(contentsOf: take(&queuedMic, n))
            pendingSystem.append(contentsOf: take(&queuedSystem, n))
        } else {
            let n = queuedMic.count
            pendingMic.append(contentsOf: take(&queuedMic, n))
            pendingSystem.append(contentsOf: [Float](repeating: 0, count: n))
        }

        // Send every complete chunk (more than one if audio piled up), never more than 45 s per upload.
        while true {
            let mixed = SpeakerAttribution.mix(pendingMic, pendingSystem)
            var cutter = Self.makeCutter()
            var range = cutter.nextCut(in: mixed)
            if range == nil, final, !mixed.isEmpty { range = 0..<mixed.count }   // what's left at Stop (< 45 s)
            guard let range else { break }
            send(mixed: Array(mixed[range]), mic: Array(pendingMic[range]), system: Array(pendingSystem[range]))
            pendingMic.removeFirst(range.upperBound)
            pendingSystem.removeFirst(range.upperBound)
            sentSamples += range.upperBound
            if pendingMic.isEmpty { break }
        }
    }

    /// Notices a microphone that stopped working: the zero padding below keeps the meeting aligned, so
    /// without this a dead microphone looks exactly like a person who is not talking.
    private func updateMicHealth(_ samples: [Float]) {
        let silent = !samples.isEmpty && MicHealthMonitor.isDigitallySilent(samples)
        if samples.isEmpty || silent { micMissingSeconds += 1 }
        let decision = micMonitor.observe(sampleCount: samples.count, digitallySilent: silent)
        micHealth = decision.state
        guard decision.restart else { return }
        micRestartsRequested += 1
        log.notice("Microphone stalled (\(decision.state.rawValue, privacy: .public)); restarting")
        mic?.restartCapture()
    }

    private func take(_ array: inout [Float], _ n: Int) -> [Float] {
        let count = min(n, array.count)
        let head = Array(array.prefix(count))
        array.removeFirst(count)
        if n > count { return head + [Float](repeating: 0, count: n - count) }
        return head
    }

    private func send(mixed: [Float], mic: [Float], system: [Float]) {
        guard let settings else { return }
        let chunkStart = Double(sentSamples) / AudioMath.sampleRate
        if AudioMath.isSilent(mixed, minimumVoicedSeconds: 0.5) { return }
        heardSpeech = true
        let wav = AudioMath.wav(AudioMath.normalized(mixed))
        let transcribe = self.transcribe
        let log = self.log
        let chunk = Task.detached(priority: .utility) { () -> [TranscriptLine] in
            // Up to 4 tries: rate limits are waited out, other errors retried after a short pause.
            for attempt in 1...4 {
                do {
                    let result = try await transcribe(wav, settings)
                    if HallucinationFilter.isLikelyHallucination(result.text, voicedSeconds: AudioMath.voicedSeconds(mixed)) { return [] }
                    return SpeakerAttribution.lines(for: result, chunkStart: chunkStart, mic: mic, system: system)
                } catch {
                    log.error("Meeting chunk failed (try \(attempt)): \(String(describing: error), privacy: .private)")
                    if case ProviderError.missingKey = error { break }
                    if attempt < 4 {
                        let wait: Double
                        if case ProviderError.rateLimited(let retryAfter) = error { wait = min(60, (retryAfter ?? 10) + 1) } else { wait = Double(attempt) * 2 }
                        try? await Task.sleep(for: .seconds(wait))
                    }
                }
            }
            return [TranscriptLine(time: chunkStart, speaker: .others, text: "[Part of the meeting could not be transcribed]")]
        }
        let previous = appendChain
        appendChain = Task { [weak self] in
            await previous?.value
            let lines = await chunk.value
            guard let self, !lines.isEmpty else { return }
            self.meeting?.lines.append(contentsOf: lines)
            if let m = self.meeting { self.onUpdate?(m) }
        }
    }
}
