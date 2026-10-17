import Foundation

public struct CleanupRequest: Sendable {
    public var transcript: String
    public var style: WritingStyle
    public var appName: String
    public var names: [String]
    public var beforeCursor: String
    public var files: [String]
    public var tagFiles: Bool
    public var level: CleanupLevel
    /// Set when the dictation may be Telugu: how to write it.
    public var telugu: TeluguScript?

    public init(transcript: String, style: WritingStyle, appName: String = "", names: [String] = [],
                beforeCursor: String = "", files: [String] = [], tagFiles: Bool = false, level: CleanupLevel = .light,
                telugu: TeluguScript? = nil) {
        self.level = level
        self.telugu = telugu
        self.transcript = transcript
        self.style = style
        self.appName = appName
        self.names = names
        self.beforeCursor = beforeCursor
        self.files = files
        self.tagFiles = tagFiles
    }
}

public struct CleanupOutcome: Sendable, Equatable {
    public var text: String
    public var usedAI: Bool
    /// Why the AI result was not used, if it wasn't.
    public var note: String?
}

/// AI cleanup with fallbacks: a dictation never comes back empty because of the network or the model.
public struct TextCleaner: Sendable {
    let chat: GroqChat
    let model: String

    public init(chat: GroqChat, model: String) {
        self.chat = chat
        self.model = model
    }

    public func clean(_ request: CleanupRequest) async -> CleanupOutcome {
        let raw = request.transcript.trimmingCharacters(in: .whitespacesAndNewlines)
        if raw.isEmpty { return CleanupOutcome(text: "", usedAI: false, note: nil) }
        if request.level == .none {
            return CleanupOutcome(text: raw, usedAI: false, note: nil)
        }
        // Telugu always goes through the model: script conversion and recognition fixes need it.
        let hasTelugu = request.telugu != nil && raw.unicodeScalars.contains { (0x0C00...0x0C7F).contains($0.value) }
        if !hasTelugu && Self.canSkipAI(raw) {
            return CleanupOutcome(text: Self.tidy(raw, style: request.style, beforeCursor: request.beforeCursor), usedAI: false, note: nil)
        }
        let user = CleanupPrompt.userMessage(
            transcript: raw, style: request.style.promptName, app: request.appName, names: request.names,
            beforeCursor: request.beforeCursor, files: request.files, tagFiles: request.tagFiles,
            cleanup: request.level.rawValue, telugu: hasTelugu ? request.telugu?.promptName : nil)
        do {
            let out = try await chat.complete(model: model, system: CleanupPrompt.system, user: user, maxTokens: 1_200)
            if let problem = Self.problem(with: out, raw: raw) {
                return CleanupOutcome(text: Self.tidy(raw, style: request.style, beforeCursor: request.beforeCursor), usedAI: false, note: problem)
            }
            return CleanupOutcome(text: out, usedAI: true, note: nil)
        } catch {
            return CleanupOutcome(text: Self.tidy(raw, style: request.style, beforeCursor: request.beforeCursor), usedAI: false,
                                  note: "\(error)")
        }
    }

    static let fillers: Set<String> = ["um", "uh", "er", "ah", "hmm", "like", "actually", "scratch", "mean", "no", "wait"]
    static let spokenCommands = ["new line", "new paragraph", "comma", "period", "full stop", "question mark"]

    /// Very short dictations with nothing to fix skip the model (saves latency and rate limit).
    static func canSkipAI(_ raw: String) -> Bool {
        let words = raw.lowercased().split { !$0.isLetter && !$0.isNumber && $0 != "'" }.map(String.init)
        guard words.count <= 3 else { return false }
        if words.contains(where: fillers.contains) { return false }
        let lower = raw.lowercased()
        return !spokenCommands.contains(where: lower.contains)
    }

    /// Local cleanup used when the model is skipped or fails.
    public static func tidy(_ raw: String, style: WritingStyle, beforeCursor: String) -> String {
        var text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let first = text.first else { return text }
        let midSentence = isMidSentence(beforeCursor)
        switch style {
        case .veryCasual, .code:
            break
        case .formal, .casual, .excited:
            if !midSentence { text = first.uppercased() + text.dropFirst() }
        }
        return text
    }

    /// True when the text before the cursor ends inside a sentence (a letter, digit or comma).
    public static func isMidSentence(_ beforeCursor: String) -> Bool {
        guard let last = beforeCursor.last(where: { !$0.isWhitespace }) else { return false }
        return !".!?:;\n".contains(last)
    }

    /// Detects outputs that ignored the rules: empty, wrapped in tags, or far longer than the input
    /// (the model answered or added content instead of cleaning).
    static func problem(with out: String, raw: String) -> String? {
        if out.isEmpty { return "empty model output" }
        if out.contains("<transcript>") || out.contains("</transcript>") { return "model echoed the prompt" }
        let inWords = raw.split(whereSeparator: \.isWhitespace).count
        let outWords = out.split(whereSeparator: \.isWhitespace).count
        if outWords > inWords * 2 + 12 { return "model output much longer than speech" }
        return nil
    }
}
