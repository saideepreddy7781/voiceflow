import Foundation

/// Rewrites meeting transcript lines in the chosen Telugu or Hindi writing, through Groq's free chat model.
/// Only the writing changes: a batch whose reply doesn't line up with its input keeps the words as heard.
public struct TranscriptScriptWriter: Sendable {
    public struct Result: Sendable, Equatable {
        public var lines: [TranscriptLine]
        public var rewrittenLines: Int
        /// Plain-language reason some lines stayed as heard, if any did.
        public var note: String?
    }

    /// Input per request, in estimated tokens. The reply is about as long as the input.
    static let batchTokens = 1_500
    /// A rewritten line this much longer than the original has gained words.
    static let maximumGrowth = 1.6

    let chat: GroqChat
    let model: String

    public init(chat: GroqChat, model: String) {
        self.chat = chat
        self.model = model
    }

    /// Whether a line needs the model at all under these choices.
    public static func needsRewrite(_ text: String, telugu: TeluguScript?, hindi: HindiScript?) -> Bool {
        if let telugu, Script.share(of: .telugu, in: text) > 0 {
            // Pure Telugu script is already what "telugu" asks for.
            return telugu != .telugu || Script.share(of: .latin, in: text) > 0
        }
        if let hindi, Script.share(of: .devanagari, in: text) > 0 {
            return hindi != .devanagari || Script.share(of: .latin, in: text) > 0
        }
        return false
    }

    /// Indices of lines to rewrite, split into batches that fit one request.
    static func batches(_ lines: [TranscriptLine], telugu: TeluguScript?, hindi: HindiScript?) -> [[Int]] {
        var out: [[Int]] = []
        var current: [Int] = []
        var tokens = 0
        for (i, line) in lines.enumerated() where needsRewrite(line.text, telugu: telugu, hindi: hindi) {
            let n = TokenEstimate.count(line.text) + 4
            if tokens + n > batchTokens, !current.isEmpty {
                out.append(current)
                current = []
                tokens = 0
            }
            current.append(i)
            tokens += n
        }
        if !current.isEmpty { out.append(current) }
        return out
    }

    /// Builds the user message: one JSON object, so transcript text can't be mistaken for instructions.
    static func userMessage(_ texts: [String], telugu: TeluguScript?, hindi: HindiScript?) -> String {
        let object: [String: Any] = ["telugu": telugu?.promptName ?? NSNull(), "hindi": hindi?.promptName ?? NSNull(), "lines": texts]
        let data = (try? JSONSerialization.data(withJSONObject: object, options: [.sortedKeys, .withoutEscapingSlashes])) ?? Data()
        return String(decoding: data, as: UTF8.self)
    }

    /// The model's lines, or nil if the reply can't be trusted: wrong count, empty lines, or lines that grew.
    static func parse(_ json: String, expected: [String]) -> [String]? {
        guard let data = json.data(using: .utf8),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let lines = object["lines"] as? [String], lines.count == expected.count else { return nil }
        for (out, input) in zip(lines, expected) {
            if out.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return nil }
            let inWords = input.split(whereSeparator: \.isWhitespace).count
            let outWords = out.split(whereSeparator: \.isWhitespace).count
            if Double(outWords) > Double(inWords) * maximumGrowth + 2 { return nil }
        }
        return lines
    }

    public func rewrite(_ lines: [TranscriptLine], telugu: TeluguScript?, hindi: HindiScript?) async -> Result {
        var out = lines
        var rewritten = 0
        var note: String?
        for batch in Self.batches(lines, telugu: telugu, hindi: hindi) {
            let texts = batch.map { lines[$0].text }
            do {
                let reply = try await requestRewrite(texts, telugu: telugu, hindi: hindi)
                guard let converted = Self.parse(reply, expected: texts) else {
                    note = "Some lines stayed as heard because the rewrite didn't match them."
                    continue
                }
                for (i, text) in zip(batch, converted) where text != lines[i].text {
                    out[i].text = text
                    rewritten += 1
                }
            } catch let error as ProviderError {
                if case .dailyLimit = error {
                    note = "\(error) The rest of the transcript stays as heard."
                    break
                }
                note = "Some lines stayed as heard because Groq didn't respond."
            } catch {
                note = "Some lines stayed as heard because Groq didn't respond."
            }
        }
        return Result(lines: out, rewrittenLines: rewritten, note: note)
    }

    private func requestRewrite(_ texts: [String], telugu: TeluguScript?, hindi: HindiScript?) async throws -> String {
        var attempt = 0
        while true {
            do {
                return try await chat.complete(model: model, system: CleanupPrompt.meetingScript,
                                               user: Self.userMessage(texts, telugu: telugu, hindi: hindi),
                                               maxTokens: 3_000, json: true, timeout: 60, priority: .background)
            } catch ProviderError.rateLimited(let retryAfter) where attempt < 4 && (retryAfter ?? 0) <= MeetingNotesWriter.maxRateLimitWait {
                attempt += 1
                try await Task.sleep(for: .seconds((retryAfter ?? 8) + 1))
            }
        }
    }
}
