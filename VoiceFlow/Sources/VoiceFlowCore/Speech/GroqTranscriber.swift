import Foundation

public enum ProviderError: Error, Equatable, CustomStringConvertible {
    case missingKey
    case rateLimited(retryAfter: Double?)
    /// A per-day limit: waiting a minute won't help. `resource` is what ran out, in plain words.
    case dailyLimit(resource: String, retryAfter: Double?)
    case http(Int, String)
    case network(String)
    case badResponse

    public var description: String {
        switch self {
        case .missingKey: "No Groq key saved. Add it in Settings."
        case .rateLimited: "Groq rate limit reached. Try again in a moment."
        case .dailyLimit(let resource, let retryAfter):
            "Groq's free daily limit for \(resource) ran out." + (retryAfter.map { " It resets \(Self.relative($0))." } ?? " It resets within a day.")
        case .http(let code, let body): "Groq returned HTTP \(code). \(body.prefix(160))"
        case .network(let message): "Network problem: \(message)"
        case .badResponse: "Groq sent a response VoiceFlow could not read."
        }
    }

    private static func relative(_ seconds: Double) -> String {
        if seconds < 90 { return "in about a minute" }
        if seconds < 5_400 { return "in about \(Int((seconds / 60).rounded())) minutes" }
        return "in about \(Int((seconds / 3_600).rounded())) hours"
    }
}

public protocol Transcriber: Sendable {
    func transcribe(wav: Data, language: String?, prompt: String?) async throws -> String
}

/// A transcription with the detected language and timed segments (Whisper's verbose_json).
public struct DetailedTranscript: Sendable, Equatable {
    public struct Word: Sendable, Equatable {
        public var word: String
        public var start: Double
        public var end: Double

        public init(word: String, start: Double, end: Double) {
            self.word = word
            self.start = start
            self.end = end
        }
    }

    public struct Segment: Sendable, Equatable {
        public var start: Double
        public var end: Double
        public var text: String
        public var avgLogprob: Double?
        public var noSpeechProb: Double?
        public var compressionRatio: Double?
        public var words: [Word] = []

        public init(start: Double, end: Double, text: String, avgLogprob: Double? = nil, noSpeechProb: Double? = nil,
                    compressionRatio: Double? = nil, words: [Word] = []) {
            self.start = start
            self.end = end
            self.text = text
            self.avgLogprob = avgLogprob
            self.noSpeechProb = noSpeechProb
            self.compressionRatio = compressionRatio
            self.words = words
        }
    }
    public var text: String
    /// Whisper language code, when Whisper reported one.
    public var language: String?
    public var segments: [Segment]
    /// Duration-weighted average log-probability of the segments (closer to 0 = more confident).
    public var confidence: Double?

    public init(text: String, language: String?, segments: [Segment], confidence: Double? = nil) {
        self.text = text
        self.language = language
        self.segments = segments
        self.confidence = confidence
    }
}

/// How to get a transcript when several languages are in play.
public enum ResolveMode: Sendable, Equatable {
    /// Auto-detect and forced opinions at once (dictation: fastest).
    case parallel
    /// Auto-detect, then forced opinions only if it looks wrong (meetings: cheapest).
    /// `hint` starts with a forced language instead when the stream has been in one language for a while.
    case sequential(hint: String?)
}

/// Groq Whisper (`/audio/transcriptions`, OpenAI-compatible).
public struct GroqTranscriber: Transcriber {
    let apiKey: String
    let model: String
    let fallbackModel: String?
    let session: URLSession
    let gate: GroqGate

    public init(apiKey: String, model: String = "whisper-large-v3-turbo", fallbackModel: String? = "whisper-large-v3",
                session: URLSession = .shared, gate: GroqGate = .shared) {
        self.apiKey = apiKey
        self.model = model
        self.fallbackModel = fallbackModel == model ? nil : fallbackModel
        self.session = session
        self.gate = gate
    }

    public func transcribe(wav: Data, language: String?, prompt: String?) async throws -> String {
        do {
            return try await request(wav: wav, model: model, language: language, prompt: prompt)
        } catch let error as ProviderError {
            switch error {
            case .rateLimited(let retryAfter):
                // One wait-and-retry if Groq says it is short; otherwise surface it.
                guard let retryAfter, retryAfter <= 3 else { throw error }
                try await Task.sleep(for: .seconds(retryAfter + 0.2))
                return try await request(wav: wav, model: model, language: language, prompt: prompt)
            case .http, .network, .badResponse:
                guard let fallbackModel else { throw error }
                return try await request(wav: wav, model: fallbackModel, language: language, prompt: prompt)
            case .missingKey, .dailyLimit:
                throw error
            }
        }
    }

    /// The transcript for one piece of audio, for speakers of one or more languages (see `LanguageResolver`).
    public func transcribeResolving(wav: Data, plan: LanguagePlan, prompts: WhisperPrompts,
                                    mode: ResolveMode = .parallel, priority: GroqPriority = .interactive,
                                    wordTimestamps: Bool = false) async throws -> DetailedTranscript {
        func run(_ language: String?) async throws -> DetailedTranscript {
            try await transcribeDetailed(wav: wav, language: language, prompt: prompts.text(forLanguage: language),
                                         priority: priority, wordTimestamps: wordTimestamps)
        }
        if let forced = plan.forcedFirst {
            return WhisperSegmentFilter.filtered(try await run(forced))
        }
        guard !plan.languages.isEmpty else {
            return WhisperSegmentFilter.filtered(try await run(nil))
        }

        if case .sequential(let hint) = mode {
            if let hint {
                let hinted = try await run(hint)
                if LanguageResolver.forcedResultAcceptable(hinted) { return WhisperSegmentFilter.filtered(hinted) }
                // The sticky language no longer fits; fall through to a full look.
            }
            let auto = try await run(nil)
            let languages = LanguageResolver.opinionLanguages(after: auto, plan: plan)
            guard !languages.isEmpty else { return WhisperSegmentFilter.filtered(auto) }
            var opinions: [LanguageOpinion] = []
            for language in languages {
                if let t = try? await run(language) { opinions.append(LanguageOpinion(language: language, transcript: t)) }
            }
            return WhisperSegmentFilter.filtered(LanguageResolver.choose(auto: auto, opinions: opinions, plan: plan))
        }

        let languages = Array(plan.opinionCandidates.prefix(LanguageResolver.maxOpinions))
        async let auto = run(nil)
        var opinions: [LanguageOpinion] = []
        await withTaskGroup(of: LanguageOpinion?.self) { group in
            for language in languages {
                group.addTask {
                    guard let t = try? await run(language) else { return nil }
                    return LanguageOpinion(language: language, transcript: t)
                }
            }
            for await opinion in group { if let opinion { opinions.append(opinion) } }
        }
        let autoResult: DetailedTranscript
        do {
            autoResult = try await auto
        } catch {
            // Auto-detect failed but a forced language worked: use it rather than losing the speech.
            if let best = opinions.max(by: { ($0.transcript.confidence ?? -10) < ($1.transcript.confidence ?? -10) }) {
                return WhisperSegmentFilter.filtered(best.transcript)
            }
            throw error
        }
        return WhisperSegmentFilter.filtered(LanguageResolver.choose(auto: autoResult, opinions: opinions, plan: plan))
    }

    /// Text, detected language and timed segments.
    public func transcribeDetailed(wav: Data, language: String?, prompt: String?, priority: GroqPriority = .interactive,
                                   wordTimestamps: Bool = false) async throws -> DetailedTranscript {
        do {
            return try await detailedRequest(wav: wav, model: model, language: language, prompt: prompt, priority: priority, wordTimestamps: wordTimestamps)
        } catch let error as ProviderError {
            if case .rateLimited(let retryAfter) = error, let retryAfter, retryAfter <= 3 {
                try await Task.sleep(for: .seconds(retryAfter + 0.2))
                return try await detailedRequest(wav: wav, model: model, language: language, prompt: prompt, priority: priority, wordTimestamps: wordTimestamps)
            }
            if case .dailyLimit = error { throw error }
            guard let fallbackModel, error != .missingKey else { throw error }
            return try await detailedRequest(wav: wav, model: fallbackModel, language: language, prompt: prompt, priority: priority, wordTimestamps: wordTimestamps)
        }
    }

    func detailedRequest(wav: Data, model: String, language: String?, prompt: String?, priority: GroqPriority, wordTimestamps: Bool) async throws -> DetailedTranscript {
        let data = try await post(wav: wav, model: model, language: language, prompt: prompt, format: "verbose_json", priority: priority, wordTimestamps: wordTimestamps)
        return try Self.parseVerbose(data)
    }

    static func parseVerbose(_ data: Data) throws -> DetailedTranscript {
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let text = json["text"] as? String else { throw ProviderError.badResponse }
        let language = (json["language"] as? String).flatMap { Languages.code(forWhisperName: $0) ?? ($0.count <= 3 ? $0 : nil) }
        let raw = json["segments"] as? [[String: Any]] ?? []
        let wordsRaw = json["words"] as? [[String: Any]] ?? []
        let words = wordsRaw.compactMap { w -> DetailedTranscript.Word? in
            guard let start = (w["start"] as? NSNumber)?.doubleValue, let end = (w["end"] as? NSNumber)?.doubleValue,
                  let t = w["word"] as? String else { return nil }
            return .init(word: t.trimmingCharacters(in: .whitespacesAndNewlines), start: start, end: end)
        }
        var wordIndex = 0
        let segments = raw.compactMap { s -> DetailedTranscript.Segment? in
            guard let start = (s["start"] as? NSNumber)?.doubleValue, let end = (s["end"] as? NSNumber)?.doubleValue,
                  let t = s["text"] as? String else { return nil }
            var own: [DetailedTranscript.Word] = []
            // Words are reported in order; hand each to the segment whose span holds its midpoint.
            while wordIndex < words.count, (words[wordIndex].start + words[wordIndex].end) / 2 <= end + 0.01 {
                own.append(words[wordIndex])
                wordIndex += 1
            }
            return .init(start: start, end: end, text: t.trimmingCharacters(in: .whitespacesAndNewlines),
                         avgLogprob: (s["avg_logprob"] as? NSNumber)?.doubleValue,
                         noSpeechProb: (s["no_speech_prob"] as? NSNumber)?.doubleValue,
                         compressionRatio: (s["compression_ratio"] as? NSNumber)?.doubleValue,
                         words: own)
        }
        var weighted = 0.0, total = 0.0
        for s in raw {
            guard let lp = (s["avg_logprob"] as? NSNumber)?.doubleValue, let a = (s["start"] as? NSNumber)?.doubleValue,
                  let b = (s["end"] as? NSNumber)?.doubleValue else { continue }
            let d = max(0.01, b - a)
            weighted += lp * d
            total += d
        }
        return DetailedTranscript(text: text.trimmingCharacters(in: .whitespacesAndNewlines), language: language,
                                  segments: segments, confidence: total > 0 ? weighted / total : nil)
    }

    func request(wav: Data, model: String, language: String?, prompt: String?) async throws -> String {
        let data = try await post(wav: wav, model: model, language: language, prompt: prompt, format: "json", priority: .interactive, wordTimestamps: false)
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let text = json["text"] as? String else { throw ProviderError.badResponse }
        return text.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private func post(wav: Data, model: String, language: String?, prompt: String?, format: String,
                      priority: GroqPriority, wordTimestamps: Bool) async throws -> Data {
        guard !apiKey.isEmpty else { throw ProviderError.missingKey }
        let boundary = "vf-\(UUID().uuidString)"
        var body = Data()
        func field(_ name: String, _ value: String) {
            body.append("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"\r\n\r\n\(value)\r\n".data(using: .utf8)!)
        }
        field("model", model)
        field("response_format", format)
        field("temperature", "0")
        if let language, !language.isEmpty { field("language", language) }
        if let prompt, !prompt.isEmpty { field("prompt", prompt) }
        if wordTimestamps {
            field("timestamp_granularities[]", "segment")
            field("timestamp_granularities[]", "word")
        }
        body.append("--\(boundary)\r\nContent-Disposition: form-data; name=\"file\"; filename=\"audio.wav\"\r\nContent-Type: audio/wav\r\n\r\n".data(using: .utf8)!)
        body.append(wav)
        body.append("\r\n--\(boundary)--\r\n".data(using: .utf8)!)

        var request = URLRequest(url: Groq.baseURL.appending(path: "audio/transcriptions"))
        request.httpMethod = "POST"
        request.timeoutInterval = 45
        request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        request.httpBody = body

        let seconds = max(0.01, Double(max(0, wav.count - 44)) / 2 / AudioMath.sampleRate)
        return try await gate.perform(.transcription(model: model, audioSeconds: seconds), priority: priority) { [session, request] in
            try await GroqHTTP.send(request, session: session)
        }
    }
}

/// Shared HTTP handling for Groq calls.
enum GroqHTTP {
    static func send(_ request: URLRequest, session: URLSession) async throws -> (Data, HTTPURLResponse) {
        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await session.data(for: request)
        } catch {
            throw ProviderError.network(error.localizedDescription)
        }
        guard let http = response as? HTTPURLResponse else { throw ProviderError.badResponse }
        switch http.statusCode {
        case 200: return (data, http)
        case 429:
            let retry = http.value(forHTTPHeaderField: "retry-after").flatMap(Double.init)
            let body = String(data: data, encoding: .utf8) ?? ""
            if let resource = GroqLimitMessage.dailyResource(in: body) { throw ProviderError.dailyLimit(resource: resource, retryAfter: retry) }
            throw ProviderError.rateLimited(retryAfter: retry)
        default:
            throw ProviderError.http(http.statusCode, String(data: data, encoding: .utf8) ?? "")
        }
    }
}

/// Reads Groq's rate-limit error text, e.g. "...on tokens per day (TPD): Limit 200000, Used 199000...".
enum GroqLimitMessage {
    static func dailyResource(in body: String) -> String? {
        let b = body.lowercased()
        guard b.contains("per day") || b.contains("(tpd)") || b.contains("(rpd)") || b.contains("(aspd)") else { return nil }
        if b.contains("seconds of audio") || b.contains("aspd") { return "transcription" }
        if b.contains("requests per day") || b.contains("rpd") { return "requests" }
        return "tokens"
    }
}
