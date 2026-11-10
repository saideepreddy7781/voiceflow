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

    public init(time: Double, speaker: Speaker, text: String) {
        self.time = time
        self.speaker = speaker
        self.text = text
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

    public init(title: String, start: Date = Date(), app: String = "") {
        self.title = title
        self.start = start
        self.app = app
    }

    public var duration: TimeInterval { (end ?? Date()).timeIntervalSince(start) }

    public var transcriptText: String {
        lines.map { "[\(Self.clock($0.time))] \($0.speaker.label): \($0.text)" }.joined(separator: "\n")
    }

    public static func clock(_ seconds: Double) -> String {
        let s = Int(seconds)
        return s >= 3600 ? String(format: "%d:%02d:%02d", s / 3600, (s % 3600) / 60, s % 60) : String(format: "%d:%02d", s / 60, s % 60)
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
        if !m.lines.isEmpty { out += "\n## Transcript\n\n" + m.lines.map { "**\($0.speaker.label)** [\(Meeting.clock($0.time))]: \($0.text)" }.joined(separator: "\n\n") + "\n" }
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

/// Decides who spoke during a stretch of audio by comparing your mic with the meeting audio.
public enum SpeakerAttribution {
    public static func speaker(mic: ArraySlice<Float>, system: ArraySlice<Float>) -> Speaker {
        let micDB = AudioMath.dbfs(AudioMath.rms(mic))
        let systemDB = AudioMath.dbfs(AudioMath.rms(system))
        // Your voice reaches the meeting audio only faintly (echo cancellation), and the meeting audio reaches
        // your mic only through speakers, so the louder source wins.
        return micDB > systemDB ? .you : .others
    }

    /// Mixes your mic and the meeting audio into one stream for transcription (one upload instead of two).
    public static func mix(_ mic: [Float], _ system: [Float]) -> [Float] {
        let n = max(mic.count, system.count)
        var out = [Float](repeating: 0, count: n)
        for i in 0..<n {
            let v = (i < mic.count ? mic[i] : 0) + (i < system.count ? system[i] : 0)
            out[i] = max(-1, min(1, v))
        }
        return out
    }

    /// Turns Whisper's timed segments for one chunk into transcript lines with speakers.
    public static func lines(for transcript: DetailedTranscript, chunkStart: Double, mic: [Float], system: [Float]) -> [TranscriptLine] {
        let rate = AudioMath.sampleRate
        if transcript.segments.isEmpty {
            let text = transcript.text
            return text.isEmpty ? [] : [TranscriptLine(time: chunkStart, speaker: speaker(mic: mic[...], system: system[...]), text: text)]
        }
        return transcript.segments.compactMap { seg in
            guard !seg.text.isEmpty else { return nil }
            let a = max(0, min(mic.count, Int(seg.start * rate)))
            let b = max(a, min(mic.count, Int(seg.end * rate)))
            let sa = max(0, min(system.count, a)), sb = max(sa, min(system.count, b))
            let who = speaker(mic: mic[a..<b], system: system[sa..<sb])
            return TranscriptLine(time: chunkStart + seg.start, speaker: who, text: seg.text)
        }
    }
}
