import Foundation

/// Why a session ended the way it did. Stored on history entries and in the diagnostics log.
public enum DeliveryReason: String, Codable, Sendable, CaseIterable {
    case inserted
    case pastedVerified = "pasted-verified"
    case pastedUnverified = "pasted-unverified"
    case pasteNotLanded = "paste-not-landed"
    case copiedNoField = "copied-no-field"
    case copiedFocusChanged = "copied-focus-changed"
    case blocked
    case noSpeech = "no-speech"
    case tooShort = "too-short"
    case cancelled
    case transcriptionFailed = "transcription-failed"
    case micFailed = "mic-failed"
    case micRecovered = "mic-recovered"
    /// A reason written by a newer version.
    case unknown

    /// An unrecognised value must not make the whole history file undecodable.
    public init(from decoder: Decoder) throws {
        let raw = try decoder.singleValueContainer().decode(String.self)
        self = DeliveryReason(rawValue: raw) ?? .unknown
    }
}

/// One session in the debug log. Holds metadata only; there is deliberately no field that could carry
/// what was said, what was typed, a window title, or a key.
public struct DiagnosticEvent: Codable, Hashable, Sendable {
    public enum Session: String, Codable, Sendable { case dictation, command, meeting }

    public var date: Date
    public var session: Session
    public var reason: DeliveryReason
    public var bundleID: String
    public var audioSeconds: Double
    public var latencyMs: Int
    public var micRestarts: Int
    public var duringMeeting: Bool

    public init(date: Date = Date(), session: Session, reason: DeliveryReason, bundleID: String = "",
                audioSeconds: Double = 0, latencyMs: Int = 0, micRestarts: Int = 0, duringMeeting: Bool = false) {
        self.date = date
        self.session = session
        self.reason = reason
        self.bundleID = Self.cleanBundleID(bundleID)
        self.audioSeconds = audioSeconds
        self.latencyMs = latencyMs
        self.micRestarts = micRestarts
        self.duringMeeting = duringMeeting
    }

    /// Bundle identifiers are letters, digits, dots and hyphens. Anything else is not one, so it is dropped.
    static func cleanBundleID(_ value: String) -> String {
        let allowed = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: ".-"))
        guard value.count <= 120, value.unicodeScalars.allSatisfy({ allowed.contains($0) }) else { return "" }
        return value
    }
}

/// The last few hundred sessions, as metadata, including the ones that leave no history entry.
@MainActor
public final class DiagnosticsLog {
    public static let capacity = 200

    private let file: JSONFile<[DiagnosticEvent]>?
    public private(set) var events: [DiagnosticEvent]
    /// Off when the user chose "Never store my data": events stay in memory for this run only.
    public var persists: Bool {
        didSet {
            if persists { save() } else if let file { try? FileManager.default.removeItem(at: file.url) }
        }
    }

    public init(file: JSONFile<[DiagnosticEvent]>?, persists: Bool = true) {
        self.file = file
        self.persists = persists
        events = persists ? file?.load() ?? [] : []
    }

    public func record(_ event: DiagnosticEvent) {
        events.insert(event, at: 0)
        if events.count > Self.capacity { events.removeLast(events.count - Self.capacity) }
        if persists { save() }
    }

    public func clear() {
        events = []
        if persists { save() }
    }

    private func save() { file?.save(events) }
}

/// A report the user can paste into a bug report: versions, devices, reason codes and timings.
///
/// It is built only from the fields below, and takes history entries only to count their reason codes.
/// What someone said, typed, or discussed in a meeting cannot reach it, and neither can window titles or keys.
public enum DebugReport {
    public struct Facts: Sendable {
        public var appVersion = ""
        public var buildNumber = ""
        public var macOS = ""
        public var architecture = ""
        public var inputDevices: [String] = []
        public var selectedInput: String?
        public var defaultInput: String?
        public var defaultOutput: String?
        public var hotkeyMode = ""
        public var microphoneAllowed = false
        public var accessibilityAllowed = false
        public var inputMonitoringAllowed = false
        public var micState = ""
        public var micRestartsThisRun = 0
        public var meetingRecording = false
        public var meetingMicHealth: MicHealthState?
        public var meetingMicMissingSeconds = 0

        public init() {}
    }

    /// "Priya's AirPods Pro" becomes "AirPods Pro": the report gets pasted into public issues.
    static func deviceName(_ name: String) -> String {
        var clean = name
        for marker in ["\u{2019}s ", "'s "] {
            if let range = clean.range(of: marker) { clean = String(clean[range.upperBound...]) }
        }
        return String(clean.prefix(80))
    }

    /// How many history entries ended with each reason, most common first.
    static func reasonCounts(_ entries: [HistoryEntry]) -> [(DeliveryReason, Int)] {
        var counts: [DeliveryReason: Int] = [:]
        for entry in entries {
            if let reason = entry.reason { counts[reason, default: 0] += 1 }
        }
        return counts.sorted { ($1.value, $0.key.rawValue) < ($0.value, $1.key.rawValue) }.map { ($0.key, $0.value) }
    }

    public static func render(_ facts: Facts, events: [DiagnosticEvent], history: [HistoryEntry], now: Date = Date()) -> String {
        let stamp = ISO8601DateFormatter()
        var lines = ["VoiceFlow debug report (no transcripts, window titles or keys)"]
        lines.append("Generated: \(stamp.string(from: now))")
        lines.append("App: \(facts.appVersion) (\(facts.buildNumber)) \(facts.architecture)")
        lines.append("macOS: \(facts.macOS)")
        lines.append("Hotkey listener: \(facts.hotkeyMode)")
        lines.append("Permissions: microphone=\(facts.microphoneAllowed) accessibility=\(facts.accessibilityAllowed) input-monitoring=\(facts.inputMonitoringAllowed)")
        lines.append("Selected input: \(facts.selectedInput.map(deviceName) ?? "system default")")
        lines.append("Default input: \(facts.defaultInput.map(deviceName) ?? "unknown")")
        lines.append("Default output: \(facts.defaultOutput.map(deviceName) ?? "unknown")")
        lines.append("Input devices: \(facts.inputDevices.map(deviceName).joined(separator: ", "))")
        lines.append("Microphone engine: \(facts.micState), rebuilt \(facts.micRestartsThisRun) times this run")
        if facts.meetingRecording {
            lines.append("Meeting recording: mic \(facts.meetingMicHealth?.rawValue ?? "unknown"), \(facts.meetingMicMissingSeconds) s without mic audio")
        }
        let counts = reasonCounts(history)
        lines.append("Reasons in history (\(history.count) entries): " + (counts.isEmpty ? "none recorded" : counts.map { "\($0.0.rawValue)=\($0.1)" }.joined(separator: " ")))
        lines.append("Recent sessions, newest first:")
        if events.isEmpty { lines.append("  none recorded") }
        for event in events.prefix(100) {
            var line = "  \(stamp.string(from: event.date)) \(event.session.rawValue) \(event.reason.rawValue)"
            if !event.bundleID.isEmpty { line += " app=\(event.bundleID)" }
            line += String(format: " audio=%.1fs", event.audioSeconds)
            line += " latency=\(event.latencyMs)ms restarts=\(event.micRestarts)"
            if event.duringMeeting { line += " during-meeting" }
            lines.append(line)
        }
        return lines.joined(separator: "\n") + "\n"
    }
}
