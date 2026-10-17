import Foundation

public enum ProviderError: Error, Equatable, CustomStringConvertible {
    case missingKey
    case rateLimited(retryAfter: Double?)
    case http(Int, String)
    case network(String)
    case badResponse

    public var description: String {
        switch self {
        case .missingKey: "No Groq key saved. Add it in Settings."
        case .rateLimited: "Groq rate limit reached. Try again in a moment."
        case .http(let code, let body): "Groq returned HTTP \(code). \(body.prefix(160))"
        case .network(let message): "Network problem: \(message)"
        case .badResponse: "Groq sent a response VoiceFlow could not read."
        }
    }
}

public protocol Transcriber: Sendable {
    func transcribe(wav: Data, language: String?, prompt: String?) async throws -> String
}

/// A transcription with the detected language and timed segments (Whisper's verbose_json).
public struct DetailedTranscript: Sendable, Equatable {
    public struct Segment: Sendable, Equatable {
        public var start: Double
        public var end: Double
        public var text: String
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

/// Groq Whisper (`/audio/transcriptions`, OpenAI-compatible).
public struct GroqTranscriber: Transcriber {
    let apiKey: String
    let model: String
    let fallbackModel: String?
    let session: URLSession

    public init(apiKey: String, model: String = "whisper-large-v3-turbo", fallbackModel: String? = "whisper-large-v3",
                session: URLSession = .shared) {
        self.apiKey = apiKey
        self.model = model
        self.fallbackModel = fallbackModel == model ? nil : fallbackModel
        self.session = session
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
            case .missingKey:
                throw error
            }
        }
    }

    /// For speakers of several languages: auto-detect and, in parallel, the given language; then the
    /// better result (see `LanguageResolver`). With `parallelLanguage` nil this is one request.
    ///
    /// `sequential` (meetings): ask for the second language only when auto-detect looks wrong, which halves
    /// the audio counted against Groq's hourly limit for a long meeting.
    public func transcribeResolving(wav: Data, language: String?, parallelLanguage: String?, languages: [String],
                                    prompt: String?, sequential: Bool = false) async throws -> DetailedTranscript {
        guard language == nil, let parallelLanguage else {
            return try await transcribeDetailed(wav: wav, language: language, prompt: prompt)
        }
        if sequential {
            let auto = try await transcribeDetailed(wav: wav, language: nil, prompt: prompt)
            guard LanguageResolver.needsSecondOpinion(auto, languages: languages) else { return auto }
            let forced = try? await transcribeDetailed(wav: wav, language: parallelLanguage, prompt: prompt)
            return LanguageResolver.choose(auto: auto, forced: forced, forcedLanguage: parallelLanguage, languages: languages)
        }
        async let auto = transcribeDetailed(wav: wav, language: nil, prompt: prompt)
        async let forced = transcribeDetailed(wav: wav, language: parallelLanguage, prompt: prompt)
        let forcedResult = try? await forced
        let autoResult: DetailedTranscript
        do {
            autoResult = try await auto
        } catch {
            // Auto-detect failed but the forced language worked: use it rather than losing the speech.
            if let forcedResult { return forcedResult }
            throw error
        }
        return LanguageResolver.choose(auto: autoResult, forced: forcedResult, forcedLanguage: parallelLanguage, languages: languages)
    }

    /// Text, detected language and timed segments.
    public func transcribeDetailed(wav: Data, language: String?, prompt: String?) async throws -> DetailedTranscript {
        do {
            return try await detailedRequest(wav: wav, model: model, language: language, prompt: prompt)
        } catch let error as ProviderError {
            if case .rateLimited(let retryAfter) = error, let retryAfter, retryAfter <= 3 {
                try await Task.sleep(for: .seconds(retryAfter + 0.2))
                return try await detailedRequest(wav: wav, model: model, language: language, prompt: prompt)
            }
            guard let fallbackModel, error != .missingKey else { throw error }
            return try await detailedRequest(wav: wav, model: fallbackModel, language: language, prompt: prompt)
        }
    }

    func detailedRequest(wav: Data, model: String, language: String?, prompt: String?) async throws -> DetailedTranscript {
        let data = try await post(wav: wav, model: model, language: language, prompt: prompt, format: "verbose_json")
        return try Self.parseVerbose(data)
    }

    static func parseVerbose(_ data: Data) throws -> DetailedTranscript {
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let text = json["text"] as? String else { throw ProviderError.badResponse }
        let language = (json["language"] as? String).flatMap { Languages.code(forWhisperName: $0) ?? ($0.count <= 3 ? $0 : nil) }
        let raw = json["segments"] as? [[String: Any]] ?? []
        let segments = raw.compactMap { s -> DetailedTranscript.Segment? in
            guard let start = (s["start"] as? NSNumber)?.doubleValue, let end = (s["end"] as? NSNumber)?.doubleValue,
                  let t = s["text"] as? String else { return nil }
            return .init(start: start, end: end, text: t.trimmingCharacters(in: .whitespacesAndNewlines))
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
        let data = try await post(wav: wav, model: model, language: language, prompt: prompt, format: "json")
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let text = json["text"] as? String else { throw ProviderError.badResponse }
        return text.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private func post(wav: Data, model: String, language: String?, prompt: String?, format: String) async throws -> Data {
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
        body.append("--\(boundary)\r\nContent-Disposition: form-data; name=\"file\"; filename=\"audio.wav\"\r\nContent-Type: audio/wav\r\n\r\n".data(using: .utf8)!)
        body.append(wav)
        body.append("\r\n--\(boundary)--\r\n".data(using: .utf8)!)

        var request = URLRequest(url: Groq.baseURL.appending(path: "audio/transcriptions"))
        request.httpMethod = "POST"
        request.timeoutInterval = 30
        request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        request.httpBody = body

        let (data, _) = try await GroqHTTP.send(request, session: session)
        return data
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
            throw ProviderError.rateLimited(retryAfter: retry)
        default:
            throw ProviderError.http(http.statusCode, String(data: data, encoding: .utf8) ?? "")
        }
    }
}
