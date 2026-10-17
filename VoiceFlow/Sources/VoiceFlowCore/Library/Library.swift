import Foundation

/// Where VoiceFlow keeps its data: ~/Library/Application Support/VoiceFlow/.
public enum AppPaths {
    public static var supportFolder: URL {
        let url = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appending(path: "VoiceFlow")
        try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        // Also tighten a folder created by an older version.
        try? FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: url.path)
        return url
    }
}

/// Small JSON-file store. Writes are atomic.
public struct JSONFile<Value: Codable>: Sendable {
    public let url: URL

    public init(_ name: String, folder: URL = AppPaths.supportFolder) {
        url = folder.appending(path: name)
    }

    public func load() -> Value? {
        guard let data = try? Data(contentsOf: url) else { return nil }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        return try? decoder.decode(Value.self, from: data)
    }

    public func save(_ value: Value) {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        guard let data = try? encoder.encode(value) else { return }
        // History and snippets can hold private text: readable by this user only.
        try? data.write(to: url, options: .atomic)
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }
}

// MARK: Dictionary

public struct DictionaryEntry: Codable, Identifiable, Hashable, Sendable {
    public enum Source: String, Codable, Sendable { case manual, autoLearned }
    public var id = UUID()
    /// The word as it should be typed (the correct spelling).
    public var term: String
    /// For corrections: what the speech recognizer types instead. nil for plain words.
    public var misspelling: String?
    public var source: Source
    public var added = Date()

    public init(term: String, misspelling: String? = nil, source: Source) {
        self.term = term
        self.misspelling = misspelling
        self.source = source
    }

    public static let maxLength = 60
}

/// Applies "Correct a misspelling" entries: whole-word, case-insensitive, typed with the saved casing.
public enum DictionaryCorrector {
    public static func apply(_ text: String, entries: [DictionaryEntry]) -> (text: String, fixes: Int) {
        var result = text
        var fixes = 0
        for entry in entries.sorted(by: { ($0.misspelling ?? "").count > ($1.misspelling ?? "").count }) {
            guard let wrong = entry.misspelling?.trimmingCharacters(in: .whitespaces), !wrong.isEmpty else { continue }
            let words = wrong.split(separator: " ").map { NSRegularExpression.escapedPattern(for: String($0)) }
            let pattern = "(?i)(?<![\\p{L}\\p{N}])" + words.joined(separator: "\\s+") + "(?![\\p{L}\\p{N}])"
            guard let regex = try? NSRegularExpression(pattern: pattern) else { continue }
            let range = NSRange(result.startIndex..., in: result)
            let count = regex.numberOfMatches(in: result, range: range)
            guard count > 0 else { continue }
            fixes += count
            result = regex.stringByReplacingMatches(in: result, range: range,
                                                    withTemplate: NSRegularExpression.escapedTemplate(for: entry.term))
        }
        return (result, fixes)
    }
}

// MARK: Snippets

public struct Snippet: Codable, Identifiable, Hashable, Sendable {
    public static let maxTrigger = 60
    public static let maxExpansion = 4_000
    public var id = UUID()
    /// What you say, e.g. "my address".
    public var trigger: String
    /// What gets typed.
    public var expansion: String

    public init(trigger: String, expansion: String) {
        self.trigger = trigger
        self.expansion = expansion
    }
}

public enum SnippetMatcher {
    static func normalize(_ s: String) -> String {
        s.lowercased().split { !$0.isLetter && !$0.isNumber }.joined(separator: " ")
    }

    /// The snippet when the whole dictation is just its trigger.
    public static func exactMatch(_ transcript: String, snippets: [Snippet]) -> Snippet? {
        let spoken = normalize(transcript)
        guard !spoken.isEmpty else { return nil }
        return snippets.first { normalize($0.trigger) == spoken }
    }

    /// Replaces triggers that appear inside longer text (case and punctuation between words ignored).
    public static func expandInline(_ text: String, snippets: [Snippet]) -> String {
        expandInlineCounting(text, snippets: snippets).text
    }

    public static func expandInlineCounting(_ text: String, snippets: [Snippet]) -> (text: String, count: Int) {
        var result = text
        var count = 0
        for snippet in snippets.sorted(by: { $0.trigger.count > $1.trigger.count }) {
            let words = normalize(snippet.trigger).split(separator: " ").map { NSRegularExpression.escapedPattern(for: String($0)) }
            guard !words.isEmpty else { continue }
            let pattern = "(?i)\\b" + words.joined(separator: "[\\s,.;:!?-]+") + "\\b"
            guard let regex = try? NSRegularExpression(pattern: pattern) else { continue }
            let template = NSRegularExpression.escapedTemplate(for: snippet.expansion)
            let range = NSRange(result.startIndex..., in: result)
            count += regex.numberOfMatches(in: result, range: range)
            result = regex.stringByReplacingMatches(in: result, range: range, withTemplate: template)
        }
        return (result, count)
    }

    /// Validation used by the Snippets editor. Returns an error message, or nil when valid.
    public static func validate(trigger: String, expansion: String, existing: [Snippet], editing: Snippet.ID? = nil) -> String? {
        let t = trigger.trimmingCharacters(in: .whitespacesAndNewlines)
        if t.isEmpty || expansion.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return "Please enter a snippet and its expansion."
        }
        if normalize(t).isEmpty { return "A snippet needs at least one letter or number." }
        if t.count > Snippet.maxTrigger { return "Snippets can be up to \(Snippet.maxTrigger) characters." }
        if expansion.count > Snippet.maxExpansion { return "This expansion is too long. Please shorten it and try again." }
        if existing.contains(where: { $0.id != editing && normalize($0.trigger) == normalize(t) }) {
            return "“\(t)” already exists in your snippets."
        }
        return nil
    }
}

// MARK: History

public struct HistoryEntry: Codable, Identifiable, Hashable, Sendable {
    public enum Kind: String, Codable, Sendable { case dictation, command }
    public var id = UUID()
    public var date = Date()
    public var kind: Kind
    public var appName: String
    public var raw: String
    public var final: String
    public var audioSeconds: Double
    public var latencyMs: Int
    /// "inserted", "pasted", "copied", "search", "answer", "failed".
    public var outcome: String
    /// Insights: usage category, words the cleanup changed, dictionary and snippet substitutions.
    public var usage: UsageCategory?
    public var wordsCorrected: Int?
    public var dictionaryFixes: Int?
    public var flagged: Bool?

    public init(kind: Kind, appName: String, raw: String, final: String, audioSeconds: Double, latencyMs: Int, outcome: String) {
        self.kind = kind
        self.appName = appName
        self.raw = raw
        self.final = final
        self.audioSeconds = audioSeconds
        self.latencyMs = latencyMs
        self.outcome = outcome
    }

    public var wordCount: Int { final.split(whereSeparator: \.isWhitespace).count }

    /// Words in what was said that did not survive into the typed text (fillers, corrections, fixes).
    public static func wordsChanged(raw: String, final: String) -> Int {
        func words(_ s: String) -> [String] { s.lowercased().split { !$0.isLetter && !$0.isNumber && $0 != "'" }.map(String.init) }
        var remaining = Dictionary(words(final).map { ($0, 1) }, uniquingKeysWith: +)
        var changed = 0
        for w in words(raw) {
            if let n = remaining[w], n > 0 { remaining[w] = n - 1 } else { changed += 1 }
        }
        return changed
    }
}

public struct UsageStats: Equatable, Sendable {
    public var totalWords = 0
    public var wordsThisWeek = 0
    public var dictations = 0
    /// Words per minute of speaking time.
    public var wordsPerMinute = 0
    /// Consecutive days with at least one dictation, ending today.
    public var dayStreak = 0
    public var topApps: [(String, Int)] = []
    public var longestStreak = 0
    public var wordsCorrected = 0
    public var dictionaryFixes = 0
    public var byCategory: [(UsageCategory, Int)] = []
    /// Words per calendar day (start of day).
    public var dailyWords: [Date: Int] = [:]

    public static func == (a: UsageStats, b: UsageStats) -> Bool {
        a.longestStreak == b.longestStreak && a.wordsCorrected == b.wordsCorrected && a.dictionaryFixes == b.dictionaryFixes
            && a.totalWords == b.totalWords && a.wordsThisWeek == b.wordsThisWeek && a.dictations == b.dictations
            && a.wordsPerMinute == b.wordsPerMinute && a.dayStreak == b.dayStreak
            && a.topApps.map(\.0) == b.topApps.map(\.0) && a.topApps.map(\.1) == b.topApps.map(\.1)
    }

    public static func compute(_ entries: [HistoryEntry], now: Date = Date(), calendar: Calendar = .current) -> UsageStats {
        var s = UsageStats()
        let weekAgo = now.addingTimeInterval(-7 * 86_400)
        var seconds = 0.0
        var apps: [String: Int] = [:]
        var days = Set<DateComponents>()
        var categories: [UsageCategory: Int] = [:]
        for e in entries where e.outcome != "failed" {
            s.totalWords += e.wordCount
            s.dictations += 1
            if e.date >= weekAgo { s.wordsThisWeek += e.wordCount }
            seconds += e.audioSeconds
            apps[e.appName, default: 0] += e.wordCount
            days.insert(calendar.dateComponents([.year, .month, .day], from: e.date))
            s.wordsCorrected += e.wordsCorrected ?? 0
            s.dictionaryFixes += e.dictionaryFixes ?? 0
            categories[e.usage ?? .other, default: 0] += e.wordCount
            s.dailyWords[calendar.startOfDay(for: e.date), default: 0] += e.wordCount
        }
        s.byCategory = categories.filter { $0.value > 0 }.sorted { $0.value > $1.value }.map { ($0.key, $0.value) }
        // Longest run of consecutive days.
        let sortedDays = s.dailyWords.keys.sorted()
        var run = 0
        var previous: Date?
        for day in sortedDays {
            if let p = previous, calendar.date(byAdding: .day, value: 1, to: p) == day { run += 1 } else { run = 1 }
            s.longestStreak = max(s.longestStreak, run)
            previous = day
        }
        s.wordsPerMinute = seconds > 0 ? Int(Double(s.totalWords) / (seconds / 60)) : 0
        s.topApps = apps.sorted { $0.value > $1.value }.prefix(5).map { ($0.key, $0.value) }
        var day = now
        while days.contains(calendar.dateComponents([.year, .month, .day], from: day)) {
            s.dayStreak += 1
            day = calendar.date(byAdding: .day, value: -1, to: day)!
        }
        return s
    }
}

/// Drops entries older than the retention setting (-1 keeps everything, 0 keeps nothing).
public func applyRetention(_ entries: [HistoryEntry], days: Int, now: Date = Date()) -> [HistoryEntry] {
    if days < 0 { return entries }
    if days == 0 { return [] }
    let cutoff = now.addingTimeInterval(-Double(days) * 86_400)
    return entries.filter { $0.date >= cutoff }
}

// MARK: Auto-learn

/// Learns spellings from your edits.
/// After a dictation, the field is read again; a word you replaced with a similar-looking one that
/// looks like a name or term becomes a dictionary entry.
public enum AutoLearner {
    public static func learnedTerms(inserted: String, fieldBefore: String, fieldAfter: String, known: Set<String>) -> [String] {
        let insertedWords = tokens(inserted)
        let afterWords = tokens(fieldAfter)
        guard !insertedWords.isEmpty, !afterWords.isEmpty else { return [] }
        let beforeWords = Set(tokens(fieldBefore))

        var learned: [String] = []
        let afterSet = Set(afterWords)
        for word in insertedWords where !afterSet.contains(word) {
            // The edited version: a new word in the field, not there before, that looks like `word`.
            let candidates = afterWords.filter { !insertedWords.contains($0) && !beforeWords.contains($0) }
            guard let best = candidates.max(by: { similarity(word, $0) < similarity(word, $1) }),
                  similarity(word, best) >= 0.5, looksLikeTerm(best),
                  !known.contains(best.lowercased()), !learned.contains(best) else { continue }
            learned.append(best)
        }
        return learned
    }

    static func tokens(_ s: String) -> [String] {
        s.split { !$0.isLetter && !$0.isNumber && $0 != "'" && $0 != "-" && $0 != "_" }.map(String.init)
    }

    /// Names, acronyms, mixed case or words with digits: worth remembering. Plain lowercase words are not.
    static func looksLikeTerm(_ w: String) -> Bool {
        guard (2...32).contains(w.count) else { return false }
        // Account numbers, phone numbers and codes are not vocabulary.
        if w.filter(\.isNumber).count >= 4 { return false }
        let hasUpper = w.contains { $0.isUppercase }
        let hasDigit = w.contains { $0.isNumber }
        return hasUpper || hasDigit
    }

    /// 1 − normalized Levenshtein distance, case-insensitive.
    static func similarity(_ a: String, _ b: String) -> Double {
        let x = Array(a.lowercased()), y = Array(b.lowercased())
        if x.isEmpty || y.isEmpty { return 0 }
        var row = Array(0...y.count)
        for i in 1...x.count {
            var prev = row[0]
            row[0] = i
            for j in 1...y.count {
                let cur = row[j]
                row[j] = min(row[j] + 1, row[j - 1] + 1, prev + (x[i - 1] == y[j - 1] ? 0 : 1))
                prev = cur
            }
        }
        return 1 - Double(row[y.count]) / Double(max(x.count, y.count))
    }
}


// MARK: Scratchpad notes

public struct Note: Codable, Identifiable, Hashable, Sendable {
    public var id = UUID()
    public var title: String = ""
    public var body: String = ""
    public var created = Date()
    public var updated = Date()
    public var pinned = false

    public init(body: String = "") {
        self.body = body
    }

    /// The title shown in lists: the saved title, else the first words of the note.
    public var displayTitle: String {
        let t = title.trimmingCharacters(in: .whitespacesAndNewlines)
        if !t.isEmpty { return t }
        let words = body.split(whereSeparator: \.isWhitespace).prefix(6).joined(separator: " ")
        return words.isEmpty ? "Untitled" : words
    }

    public var isBlank: Bool {
        title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty && body.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }
}
