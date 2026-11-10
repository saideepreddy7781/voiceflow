import Foundation

public enum Speaker: String, Codable, Sendable {
    /// Your microphone.
    case you
    /// Everyone else: the meeting app's audio.
    case others

    public var label: String { self == .you ? "You" : "Others" }
}

public struct TranscriptLine: Codable, Hashable, Sendable {
    /// Seconds since the meeting started.
    public var time: Double
    public var speaker: Speaker
    public var text: String
    /// When the line ended, for lining up the two sides of the call. Missing in older meetings.
    public var end: Double?
    /// Whisper's language for this line. Missing in older meetings.
    public var language: String?

    public init(time: Double, speaker: Speaker, text: String, end: Double? = nil, language: String? = nil) {
        self.time = time
        self.speaker = speaker
        self.text = text
        self.end = end
        self.language = language
    }
}

public struct ActionItem: Codable, Hashable, Sendable, Identifiable {
    public var id = UUID()
    public var task: String
    public var owner: String?
    public var due: String?
    public var done = false

    public init(task: String, owner: String? = nil, due: String? = nil) {
        self.task = task
        self.owner = owner
        self.due = due
    }

    private enum CodingKeys: String, CodingKey { case id, task, owner, due, done }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = (try? c.decodeIfPresent(UUID.self, forKey: .id)) ?? UUID()
        task = try c.decode(String.self, forKey: .task)
        owner = (try? c.decodeIfPresent(String.self, forKey: .owner)).flatMap { $0?.isEmpty == true ? nil : $0 }
        due = (try? c.decodeIfPresent(String.self, forKey: .due)).flatMap { $0?.isEmpty == true ? nil : $0 }
        done = (try? c.decodeIfPresent(Bool.self, forKey: .done)) ?? false
    }
}

public struct MeetingNotes: Codable, Hashable, Sendable {
    public var title: String
    public var summary: [String]
    public var decisions: [String]
    public var actionItems: [ActionItem]
    public var questions: [String]

    public init(title: String = "", summary: [String] = [], decisions: [String] = [], actionItems: [ActionItem] = [], questions: [String] = []) {
        self.title = title
        self.summary = summary
        self.decisions = decisions
        self.actionItems = actionItems
        self.questions = questions
    }

    private enum CodingKeys: String, CodingKey {
        case title, summary, decisions, questions
        case actionItems = "action_items"
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        title = (try? c.decodeIfPresent(String.self, forKey: .title)) ?? ""
        summary = (try? c.decodeIfPresent([String].self, forKey: .summary)) ?? []
        decisions = (try? c.decodeIfPresent([String].self, forKey: .decisions)) ?? []
        actionItems = (try? c.decodeIfPresent([ActionItem].self, forKey: .actionItems)) ?? []
        questions = (try? c.decodeIfPresent([String].self, forKey: .questions)) ?? []
    }
}

public extension MeetingNotes {
    /// Keeps the ticks on action items that are still there after the notes are rewritten.
    func carryingOverDone(from old: MeetingNotes?) -> MeetingNotes {
        guard let old else { return self }
        let done = Set(old.actionItems.filter(\.done).map { $0.task.lowercased() })
        var out = self
        for i in out.actionItems.indices where done.contains(out.actionItems[i].task.lowercased()) { out.actionItems[i].done = true }
        return out
    }
}

public struct Meeting: Codable, Hashable, Sendable, Identifiable {
    public enum Status: String, Codable, Sendable { case recording, writingNotes, done, failed }

    public var id = UUID()
    public var title: String
    public var start: Date
    public var end: Date?
    /// The meeting app, e.g. "Zoom".
    public var app: String
    public var attendees: [String] = []
    public var lines: [TranscriptLine] = []
    public var notes: MeetingNotes?
    public var status: Status = .recording
    public var error: String?
    /// True when only the microphone could be recorded (no system-audio permission).
    public var micOnly = false
    /// The Markdown file last written to the notes folder, so a rename replaces it.
    public var syncedFileName: String?
    /// Seconds with no usable microphone audio, when the microphone failed during the meeting.
    public var micMissingSeconds: Int?
    /// What "Others" is called in this meeting, after a rename. You stays yours.
    public var othersLabel: String?
    /// A plain-language note about gaps, shown with the transcript (for example a daily limit running out).
    public var note: String?
    /// Lines were rewritten into the chosen writing (Telugu or Hindi script choice).
    public var scriptRewritten: Bool?

    public init(title: String, start: Date = Date(), app: String = "") {
        self.title = title
        self.start = start
        self.app = app
    }

    public var duration: TimeInterval { (end ?? Date()).timeIntervalSince(start) }

    public var transcriptText: String {
        lines.map { "[\(Self.clock($0.time))] \(label(for: $0.speaker)): \($0.text)" }.joined(separator: "\n")
    }

    /// The name shown for a speaker, using this meeting's rename of "Others".
    public func label(for speaker: Speaker) -> String {
        if speaker == .others, let name = othersLabel?.trimmingCharacters(in: .whitespacesAndNewlines), !name.isEmpty { return name }
        return speaker.label
    }

    /// The language spoken most in this meeting, by word count, from the per-line languages.
    public var dominantLanguage: String? {
        var words: [String: Int] = [:]
        for line in lines { if let l = line.language { words[l, default: 0] += line.text.split(separator: " ").count } }
        return words.max { $0.value < $1.value }?.key
    }

    public static func clock(_ seconds: Double) -> String {
        let s = Int(seconds)
        return s >= 3600 ? String(format: "%d:%02d:%02d", s / 3600, (s % 3600) / 60, s % 60) : String(format: "%d:%02d", s / 60, s % 60)
    }
}

/// Finds meetings by title, notes or transcript.
public enum MeetingSearch {
    /// Whether the meeting mentions `query` anywhere. An empty query matches everything.
    public static func matches(_ meeting: Meeting, query: String) -> Bool {
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !q.isEmpty else { return true }
        return searchableParts(meeting).contains { $0.localizedCaseInsensitiveContains(q) }
    }

    /// A short piece of the first matching transcript line or note, for the results list. Nil when only the title matches.
    public static func snippet(_ meeting: Meeting, query: String, radius: Int = 40) -> String? {
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !q.isEmpty else { return nil }
        for part in searchableParts(meeting).dropFirst() {
            guard let range = part.range(of: q, options: [.caseInsensitive, .diacriticInsensitive]) else { continue }
            let start = part.index(range.lowerBound, offsetBy: -radius, limitedBy: part.startIndex) ?? part.startIndex
            let end = part.index(range.upperBound, offsetBy: radius, limitedBy: part.endIndex) ?? part.endIndex
            return (start > part.startIndex ? "…" : "") + part[start..<end] + (end < part.endIndex ? "…" : "")
        }
        return nil
    }

    private static func searchableParts(_ m: Meeting) -> [String] {
        var parts = [m.title]
        if let n = m.notes { parts += n.summary + n.decisions + n.actionItems.map(\.task) + n.questions }
        parts += m.lines.map(\.text)
        return parts
    }
}

/// Meeting notes as Markdown, for the notes folder and for copying.
public enum MeetingMarkdown {
    public static func render(_ m: Meeting) -> String {
        var out = "# \(m.title)\n\n"
        let date = m.start.formatted(date: .complete, time: .shortened)
        out += "\(date) · \(Int((m.duration / 60).rounded())) min"
        if !m.app.isEmpty { out += " · \(m.app)" }
        out += "\n"
        if !m.attendees.isEmpty { out += "Attendees: \(m.attendees.joined(separator: ", "))\n" }
        if let n = m.notes {
            if !n.summary.isEmpty { out += "\n## Summary\n\n" + n.summary.map { "- \($0)" }.joined(separator: "\n") + "\n" }
            if !n.decisions.isEmpty { out += "\n## Decisions\n\n" + n.decisions.map { "- \($0)" }.joined(separator: "\n") + "\n" }
            if !n.actionItems.isEmpty {
                out += "\n## Action items\n\n" + n.actionItems.map { item in
                    var line = "- [\(item.done ? "x" : " ")] \(item.task)"
                    if let owner = item.owner { line += " (\(owner))" }
                    if let due = item.due { line += ", due \(due)" }
                    return line
                }.joined(separator: "\n") + "\n"
            }
            if !n.questions.isEmpty { out += "\n## Open questions\n\n" + n.questions.map { "- \($0)" }.joined(separator: "\n") + "\n" }
        }
        if let note = m.note, !note.isEmpty { out += "\n_\(note)_\n" }
        if !m.lines.isEmpty { out += "\n## Transcript\n\n" + m.lines.map { "**\(m.label(for: $0.speaker))** [\(Meeting.clock($0.time))]: \($0.text)" }.joined(separator: "\n\n") + "\n" }
        return out
    }

    /// "2026-10-17 1430 Weekly sync.md", with characters that are unsafe in file names removed.
    public static func fileName(_ m: Meeting) -> String {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HHmm"
        let safeTitle = m.title.components(separatedBy: CharacterSet(charactersIn: "/\\:?*\"<>|\n\r\t")).joined(separator: " ")
            .trimmingCharacters(in: .whitespaces)
        return "\(f.string(from: m.start)) \(safeTitle.isEmpty ? "Meeting" : String(safeTitle.prefix(80))).md"
    }
}
