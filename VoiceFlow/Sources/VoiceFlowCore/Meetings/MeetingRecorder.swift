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

/// Records a meeting: your microphone and the call, transcribed separately while it happens.
///
/// Each side is cut into uploads of voiced audio only (`StreamChunker`), so "You" and "Others" are real labels,
/// overlapping talk stays apart, and a meeting costs about its own length in audio seconds. The two sides are
/// merged by time (`TranscriptMerge`). Audio stays in memory: at most the chunk being collected on each side,
/// plus failed chunks up to `ChunkHold.capacitySeconds` to retry when the meeting ends.
@MainActor
public final class MeetingRecorder {
    public struct Settings: Sendable {
        public var apiKey: String
        public var model: String
        public var languages: [String]
        public var prompts: WhisperPrompts
        public var micDeviceUID: String?
        public var recordSystemAudio: Bool

        public init(apiKey: String, model: String, languages: [String], prompts: WhisperPrompts,
                    micDeviceUID: String?, recordSystemAudio: Bool) {
            self.apiKey = apiKey
            self.model = model
            self.languages = languages
            self.prompts = prompts
            self.micDeviceUID = micDeviceUID
            self.recordSystemAudio = recordSystemAudio
        }
    }

    /// One upload: which side it is, and the language to try first.
    public struct ChunkRequest: Sendable {
        public var settings: Settings
        public var plan: LanguagePlan
        public var hint: String?
    }

    public typealias Transcribe = @Sendable (Data, ChunkRequest) async throws -> DetailedTranscript

    /// Tries per chunk during the meeting, and again at the end.
    static let liveAttempts = 2
    static let finalAttempts = 3
    /// Longest Stop spends retrying failed chunks.
    static let finalRetrySeconds = 180.0
    /// Longest Stop spends retrying failed chunks in this recorder (shortened in tests).
    var finalRetryLimit = MeetingRecorder.finalRetrySeconds
    /// With less audio budget than this left in the hour, meetings skip second opinions.
    static let secondOpinionHeadroom = 900.0

    private struct Side {
        var chunker = StreamChunker()
        var tracker = StreamLanguageTracker()
    }

    private let log = Logger(subsystem: "com.saideep.voiceflow", category: "meeting")
    private let makeMic: @MainActor (Settings) -> MeetingAudioSource
    private let makeSystem: @MainActor () -> MeetingAudioSource?
    private let transcribe: Transcribe
    private let retryDelay: @Sendable (Int) -> Double

    private var mic: MeetingAudioSource?
    private var system: MeetingAudioSource?
    private var timer: Timer?
    private var settings: Settings?

    // Audio not yet handed to a chunker, so the two sides stay aligned.
    private var queuedMic: [Float] = []
    private var queuedSystem: [Float] = []
    private var micSide = Side()
    private var callSide = Side()
    private var uploads: [Task<Void, Never>] = []
    private var hold = ChunkHold()
    private var lostSeconds = 0.0
    private var dailyLimit: ProviderError?
    private var heardSpeech = false
    private var micMonitor = MicHealthMonitor()

    public private(set) var meeting: Meeting?
    public private(set) var isRecording = false
    public private(set) var lastError: String?
    /// Live input levels (0...1) for the Meetings page.
    public private(set) var micLevel: Float = 0
    public private(set) var systemLevel: Float = 0
    /// The language last heard on each side, for the live view.
    public private(set) var micLanguage: String?
    public private(set) var callLanguage: String?
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
    /// Called once when Groq's daily limit runs out during a meeting, with a sentence for the user.
    public var onDailyLimit: ((String) -> Void)?

    public init() {
        makeMic = { settings in MicSource(deviceUID: settings.micDeviceUID) }
        makeSystem = {
            if #available(macOS 14.2, *) { return SystemAudioCapture() }
            return nil
        }
        retryDelay = { Double($0) * 2 }
        transcribe = { wav, request in
            let settings = request.settings
            var plan = request.plan
            // Short on audio budget: one upload per chunk, and auto-detect has the last word.
            if await GroqGate.shared.transcriptionHeadroom(model: settings.model) < Self.secondOpinionHeadroom { plan.languages = [] }
            return try await GroqTranscriber(apiKey: settings.apiKey, model: settings.model)
                .transcribeResolving(wav: wav, plan: plan, prompts: settings.prompts, mode: .sequential(hint: request.hint),
                                     priority: .background, wordTimestamps: true)
        }
    }

    /// For tests: simulated sources and transcription.
    init(mic: @escaping @MainActor (Settings) -> MeetingAudioSource, system: @escaping @MainActor () -> MeetingAudioSource?,
         retryDelay: @escaping @Sendable (Int) -> Double = { _ in 0 }, transcribe: @escaping Transcribe) {
        makeMic = mic
        makeSystem = system
        self.retryDelay = retryDelay
        self.transcribe = transcribe
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
        queuedMic = []; queuedSystem = []
        micSide = Side(); callSide = Side()
        uploads = []
        hold = ChunkHold()
        lostSeconds = 0
        dailyLimit = nil
        micLanguage = nil; callLanguage = nil
        if autoTick {
            timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated { self?.tick() }
            }
        }
        log.info("Meeting recording started (system audio: \(self.system != nil, privacy: .public))")
    }

    /// Stops recording and returns the meeting once every chunk is transcribed.
    /// `retryFailed` is false when the app is quitting, so failed chunks are marked at once.
    public func stop(retryFailed: Bool = true) async -> Meeting? {
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
        let running = uploads
        uploads = []
        for upload in running { await upload.value }
        await retryHeld(enabled: retryFailed)
        writeGapNote()
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
        var micSamples: [Float] = []
        var callSamples: [Float] = []
        if let system {
            let newSystem = system.drainSamples()
            systemLevel = AudioMath.level(newSystem.suffix(1_600)[...])
            systemSilentSeconds = newSystem.allSatisfy { $0 == 0 } ? systemSilentSeconds + 1 : 0
            queuedSystem.append(contentsOf: newSystem)
            // Keep the two sides on one clock. Whichever stalls (mic restarting, output device changing) is padded.
            let twoSeconds = Int(AudioMath.sampleRate) * 2
            let lag = queuedMic.count - queuedSystem.count
            if lag > twoSeconds { queuedSystem.append(contentsOf: [Float](repeating: 0, count: lag)) }
            if -lag > twoSeconds { queuedMic.append(contentsOf: [Float](repeating: 0, count: -lag)) }
            let n = final ? max(queuedMic.count, queuedSystem.count) : min(queuedMic.count, queuedSystem.count)
            micSamples = take(&queuedMic, n)
            callSamples = take(&queuedSystem, n)
        } else {
            micSamples = take(&queuedMic, queuedMic.count)
        }

        for chunk in micSide.chunker.feed(micSamples) { send(chunk, speaker: .you) }
        for chunk in callSide.chunker.feed(callSamples) { send(chunk, speaker: .others) }
        if final {
            if let chunk = micSide.chunker.finish() { send(chunk, speaker: .you) }
            if let chunk = callSide.chunker.finish() { send(chunk, speaker: .others) }
        }
    }

    /// Notices a microphone that stopped working: padding keeps the meeting aligned, so without this
    /// a dead microphone looks exactly like a person who is not talking.
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

    private func request(for speaker: Speaker, settings: Settings) -> ChunkRequest {
        let hint = speaker == .you ? micSide.tracker.nextHint() : callSide.tracker.nextHint()
        return ChunkRequest(settings: settings, plan: LanguagePlan(languages: settings.languages, speaker: speaker == .you ? .user : .anyone), hint: hint)
    }

    private func send(_ chunk: AudioChunk, speaker: Speaker) {
        guard let settings else { return }
        heardSpeech = true
        let request = request(for: speaker, settings: settings)
        let wav = AudioMath.wav(AudioMath.normalized(chunk.samples))
        let transcribe = self.transcribe
        let retryDelay = self.retryDelay
        let log = self.log
        let attempts = Self.liveAttempts
        let upload = Task.detached(priority: .utility) { () -> Result<DetailedTranscript, Error> in
            await Self.attempt(wav: wav, request: request, tries: attempts, transcribe: transcribe, retryDelay: retryDelay, log: log)
        }
        uploads.append(Task { [weak self] in
            let result = await upload.value
            self?.finish(result, chunk: chunk, speaker: speaker, request: request)
        })
    }

    /// Rate limits are waited out (`GroqGate` has already waited what it can); other errors retried after a short pause.
    /// Past `deadline` no retry starts, and no pause runs beyond it.
    nonisolated private static func attempt(wav: Data, request: ChunkRequest, tries: Int, deadline: Date? = nil, transcribe: Transcribe,
                                            retryDelay: @Sendable (Int) -> Double, log: Logger) async -> Result<DetailedTranscript, Error> {
        var last: Error = ProviderError.badResponse
        for attempt in 1...tries {
            if attempt > 1, let deadline, Date() >= deadline { break }
            do {
                return .success(try await transcribe(wav, request))
            } catch {
                last = error
                log.error("Meeting chunk failed (try \(attempt)): \(String(describing: error), privacy: .private)")
                if case ProviderError.missingKey = error { break }
                if case ProviderError.dailyLimit = error { break }
                if attempt < tries {
                    var wait: Double
                    if case ProviderError.rateLimited(let retryAfter) = error { wait = min(60, (retryAfter ?? 10) + 1) } else { wait = retryDelay(attempt) }
                    if let deadline { wait = min(wait, max(0, deadline.timeIntervalSinceNow)) }
                    if wait > 0 { try? await Task.sleep(for: .seconds(wait)) }
                }
            }
        }
        return .failure(last)
    }

    private func finish(_ result: Result<DetailedTranscript, Error>, chunk: AudioChunk, speaker: Speaker, request: ChunkRequest) {
        switch result {
        case .success(let transcript):
            accept(transcript, map: chunk.map, voicedSeconds: chunk.voicedSeconds, speaker: speaker, request: request)
        case .failure(let error):
            noteDailyLimit(error)
            if case ProviderError.missingKey = error {
                markLost(map: chunk.map, seconds: chunk.voicedSeconds, speaker: speaker)
                return
            }
            let held = HeldChunk(samples: chunk.samples, map: chunk.map, speaker: speaker, voicedSeconds: chunk.voicedSeconds)
            for lost in hold.add(held) { markLost(map: lost.map, seconds: lost.voicedSeconds, speaker: lost.speaker) }
        }
    }

    private func accept(_ transcript: DetailedTranscript, map: TimeMap, voicedSeconds: Double, speaker: Speaker, request: ChunkRequest) {
        if speaker == .you { micLanguage = transcript.language ?? micLanguage } else { callLanguage = transcript.language ?? callLanguage }
        // A forced language produced this if it isn't what auto-detect would be trusted to say on its own.
        // A hint that lost to auto-detect (the stream moved to English) doesn't count, so the sticky language lets go.
        let viaOpinion = (request.hint != nil && transcript.language == request.hint)
            || (transcript.language.map { $0 != "en" && request.plan.languages.contains($0) } ?? false)
        if speaker == .you {
            micSide.tracker.observe(language: transcript.language, viaOpinion: viaOpinion, listed: request.plan.languages)
        } else {
            callSide.tracker.observe(language: transcript.language, viaOpinion: viaOpinion, listed: request.plan.languages)
        }
        if HallucinationFilter.isLikelyHallucination(transcript.text, voicedSeconds: voicedSeconds) { return }
        let lines = ChunkLines.lines(for: transcript, map: map, speaker: speaker, chunkFallbackTime: map.meetingTime(chunkSeconds: 0))
        append(lines)
    }

    private func append(_ lines: [TranscriptLine]) {
        guard !lines.isEmpty, var m = meeting else { return }
        m.lines = TranscriptMerge.merge(existing: m.lines, incoming: lines)
        meeting = m
        onUpdate?(m)
    }

    private func markLost(map: TimeMap, seconds: Double, speaker: Speaker) {
        lostSeconds += seconds
        append([TranscriptLine(time: map.meetingTime(chunkSeconds: 0), speaker: speaker, text: TranscriptFilter.lostMarker)])
    }

    private func noteDailyLimit(_ error: Error) {
        guard case ProviderError.dailyLimit = error, dailyLimit == nil, let e = error as? ProviderError else { return }
        dailyLimit = e
        onDailyLimit?("\(e) The rest of this meeting can't be transcribed until then.")
    }

    /// Second chance for chunks that failed. The marker appears only for what is still lost after this.
    private func retryHeld(enabled: Bool) async {
        let waiting = hold.takeAll()
        guard !waiting.isEmpty, let settings else { return }
        let deadline = Date().addingTimeInterval(finalRetryLimit)
        for chunk in waiting {
            guard enabled, dailyLimit == nil, Date() < deadline else {
                markLost(map: chunk.map, seconds: chunk.voicedSeconds, speaker: chunk.speaker)
                continue
            }
            let request = request(for: chunk.speaker, settings: settings)
            let wav = AudioMath.wav(AudioMath.normalized(chunk.floatSamples))
            let result = await Self.attempt(wav: wav, request: request, tries: Self.finalAttempts, deadline: deadline,
                                            transcribe: transcribe, retryDelay: retryDelay, log: log)
            switch result {
            case .success(let transcript):
                accept(transcript, map: chunk.map, voicedSeconds: chunk.voicedSeconds, speaker: chunk.speaker, request: request)
            case .failure(let error):
                noteDailyLimit(error)
                markLost(map: chunk.map, seconds: chunk.voicedSeconds, speaker: chunk.speaker)
            }
        }
    }

    private func writeGapNote() {
        guard lostSeconds > 0 else { return }
        let minutes = max(1, Int((lostSeconds / 60).rounded()))
        if let dailyLimit {
            meeting?.note = "\(dailyLimit) About \(minutes) min of speech could not be transcribed."
        } else {
            meeting?.note = "About \(minutes) min of speech could not be transcribed after retrying."
        }
    }
}
