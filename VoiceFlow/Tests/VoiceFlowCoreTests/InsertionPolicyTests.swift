import Foundation
import Testing
@testable import VoiceFlowCore

struct InsertionPolicyTests {
    /// Same app still frontmost, nothing known about the field.
    private func facts(_ change: (inout InsertionFacts) -> Void = { _ in }) -> InsertionFacts {
        var f = InsertionFacts()
        f.contextPID = 42
        f.frontmostPID = 42
        change(&f)
        return f
    }

    @Test func protectedFocusIsAlwaysBlocked() {
        // Every other fact below says "go ahead"; the safety facts must still win.
        for protect in [\InsertionFacts.contextIsSecure, \.contextIsExcluded, \.focusIsProtected] {
            let f = facts { $0[keyPath: protect] = true; $0.elementIsEditable = true }
            #expect(InsertionPolicy.plan(f) == .block)
        }
    }

    @Test func protectionOutranksAFocusChange() {
        let f = facts { $0.focusIsProtected = true; $0.frontmostPID = 7; $0.windowChanged = true }
        #expect(InsertionPolicy.plan(f) == .block)
    }

    @Test func unknownRoleStillPastesInTheSameApp() {
        // The bug: an Electron app that answers late read as "no field", and the text was only copied.
        #expect(InsertionPolicy.plan(facts()) == .paste)
    }

    @Test func switchingAppsCopiesInstead() {
        #expect(InsertionPolicy.plan(facts { $0.frontmostPID = 7 }) == .copyFocusChanged)
        #expect(InsertionPolicy.plan(facts { $0.frontmostPID = nil }) == .copyFocusChanged)
    }

    @Test func aChangedWindowCopiesAnUnknownOneDoesNot() {
        #expect(InsertionPolicy.plan(facts { $0.windowChanged = true }) == .copyFocusChanged)
        #expect(InsertionPolicy.plan(facts { $0.windowChanged = false }) == .paste)
    }

    @Test func noKnownAppStillCopies() {
        var f = InsertionFacts()
        f.frontmostPID = nil
        #expect(InsertionPolicy.plan(f) == .copyNoField)
    }

    @Test func emptyTextIsNotTyped() {
        #expect(InsertionPolicy.plan(facts { $0.textIsEmpty = true }) == .copyNoField)
    }

    @Test func editableFieldsAreWrittenUnlessThePasteFirstListSaysOtherwise() {
        #expect(InsertionPolicy.plan(facts { $0.elementIsEditable = true }) == .writeThenPaste)
        #expect(InsertionPolicy.plan(facts { $0.elementIsEditable = true; $0.pasteFirst = true }) == .paste)
    }

    @Test func aChangedFieldContainingTheTextIsVerified() {
        #expect(InsertionPolicy.verifyPaste(before: "Hi ", after: "Hi there, Priya", inserted: "there, Priya") == .landed)
    }

    @Test func whitespaceReflowStillVerifies() {
        #expect(InsertionPolicy.verifyPaste(before: "", after: "line one\n\nline two", inserted: "line one line two") == .landed)
    }

    @Test func aReadableUnchangedFieldDidNotLand() {
        #expect(InsertionPolicy.verifyPaste(before: "same", after: "same", inserted: "new text") == .notLanded)
    }

    @Test func anUnreadableOrAlwaysEmptyFieldIsUnknown() {
        #expect(InsertionPolicy.verifyPaste(before: nil, after: nil, inserted: "x") == .unknown)
        #expect(InsertionPolicy.verifyPaste(before: "", after: "", inserted: "x") == .unknown)
        #expect(InsertionPolicy.verifyPaste(before: nil, after: "other", inserted: "x") == .unknown)
    }

    @Test func everyResultHasAnHonestReason() {
        #expect(InsertResult.accessibility.reason == .inserted)
        #expect(InsertResult.pastedVerified.reason == .pastedVerified)
        #expect(InsertResult.pastedUnverified.reason == .pastedUnverified)
        #expect(InsertResult.pasteNotLanded.reason == .pasteNotLanded)
        #expect(InsertResult.copiedOnly.reason == .copiedNoField)
        #expect(InsertResult.focusChanged.reason == .copiedFocusChanged)
        #expect(InsertResult.blocked.reason == .blocked)
    }
}

@MainActor
struct DiagnosticsTests {
    @Test func oldHistoryStillDecodesWithNoReason() throws {
        let old = #"[{"id":"\#(UUID().uuidString)","date":"2026-10-01T10:00:00Z","kind":"dictation","appName":"Notes","raw":"hi","final":"hi","audioSeconds":1.0,"latencyMs":100,"outcome":"pasted"}]"#
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let entries = try decoder.decode([HistoryEntry].self, from: Data(old.utf8))
        #expect(entries.first?.reason == nil)
        #expect(entries.first?.outcome == "pasted")
    }

    @Test func aReasonFromANewerVersionDoesNotBreakHistory() throws {
        let future = #"[{"id":"\#(UUID().uuidString)","date":"2026-10-01T10:00:00Z","kind":"dictation","appName":"Notes","raw":"hi","final":"hi","audioSeconds":1.0,"latencyMs":100,"outcome":"pasted","reason":"beamed-to-moon"}]"#
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        let entries = try decoder.decode([HistoryEntry].self, from: Data(future.utf8))
        #expect(entries.first?.reason == .unknown)
    }

    @Test func reasonRoundTrips() throws {
        var entry = HistoryEntry(kind: .dictation, appName: "A", raw: "r", final: "f", audioSeconds: 1, latencyMs: 2, outcome: "pasted", reason: .copiedFocusChanged)
        entry.usage = nil
        let back = try JSONDecoder().decode(HistoryEntry.self, from: JSONEncoder().encode(entry))
        #expect(back.reason == .copiedFocusChanged)
    }

    @Test func oldMeetingsDecode() throws {
        let m = Meeting(title: "t")
        var json = try JSONSerialization.jsonObject(with: JSONEncoder().encode(m)) as! [String: Any]
        json.removeValue(forKey: "micMissingSeconds")
        let back = try JSONDecoder().decode(Meeting.self, from: JSONSerialization.data(withJSONObject: json))
        #expect(back.micMissingSeconds == nil)
    }

    @Test func theLogIsARingBuffer() {
        let log = DiagnosticsLog(file: nil)
        for i in 0..<(DiagnosticsLog.capacity + 10) {
            log.record(DiagnosticEvent(session: .dictation, reason: .inserted, latencyMs: i))
        }
        #expect(log.events.count == DiagnosticsLog.capacity)
        #expect(log.events.first?.latencyMs == DiagnosticsLog.capacity + 9)
    }

    @Test func theLogSurvivesRelaunchAndStaysLocal() throws {
        let folder = FileManager.default.temporaryDirectory.appending(path: "vf-diag-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let file = JSONFile<[DiagnosticEvent]>("diagnostics.json", folder: folder)
        DiagnosticsLog(file: file).record(DiagnosticEvent(session: .meeting, reason: .micFailed))
        #expect(DiagnosticsLog(file: file).events.first?.reason == .micFailed)
        // "Never store my data": the file goes away.
        let log = DiagnosticsLog(file: file)
        log.persists = false
        #expect(!FileManager.default.fileExists(atPath: file.url.path))
        log.record(DiagnosticEvent(session: .dictation, reason: .inserted))
        #expect(!FileManager.default.fileExists(atPath: file.url.path))
        #expect(log.events.count == 2)
    }

    @Test func nonBundleTextIsDroppedFromEvents() {
        let event = DiagnosticEvent(session: .dictation, reason: .inserted, bundleID: "meet Priya at the Taj on Tuesday")
        #expect(event.bundleID.isEmpty)
        #expect(DiagnosticEvent(session: .dictation, reason: .inserted, bundleID: "com.microsoft.VSCode").bundleID == "com.microsoft.VSCode")
    }

    @Test func theReportCannotContainTranscriptText() {
        let transcript = "SECRET-TRANSCRIPT please wire the money"
        let finalText = "SECRET-FINAL Dear Priya"
        let appName = "SECRET-APPNAME"
        let history = [
            HistoryEntry(kind: .dictation, appName: appName, raw: transcript, final: finalText, audioSeconds: 2, latencyMs: 90, outcome: "pasted", reason: .pastedVerified),
            HistoryEntry(kind: .command, appName: appName, raw: transcript, final: finalText, audioSeconds: 3, latencyMs: 91, outcome: "copied", reason: .copiedFocusChanged),
        ]
        var facts = DebugReport.Facts()
        facts.appVersion = "1.0"
        facts.macOS = "26.0"
        let events = [DiagnosticEvent(session: .dictation, reason: .noSpeech, bundleID: transcript, audioSeconds: 1.5, latencyMs: 40)]
        let report = DebugReport.render(facts, events: events, history: history)
        for secret in [transcript, finalText, appName, "SECRET"] {
            #expect(!report.contains(secret))
        }
        #expect(report.contains("pasted-verified=1"))
        #expect(report.contains("no-speech"))
    }

    @Test func theReportHasWhatABugReportNeeds() {
        var facts = DebugReport.Facts()
        facts.appVersion = "0.2.0"
        facts.buildNumber = "7"
        facts.macOS = "Version 26.0"
        facts.inputDevices = ["Priya's AirPods Pro", "MacBook Pro Microphone"]
        facts.defaultOutput = "Priya’s AirPods Pro"
        facts.micRestartsThisRun = 3
        facts.meetingRecording = true
        facts.meetingMicHealth = .noAudio
        let date = Date(timeIntervalSince1970: 1_800_000_000)
        let events = [DiagnosticEvent(date: date, session: .dictation, reason: .micFailed, bundleID: "com.microsoft.VSCode",
                                      audioSeconds: 4.2, latencyMs: 12, micRestarts: 1, duringMeeting: true)]
        let report = DebugReport.render(facts, events: events, history: [], now: date)
        #expect(report.contains("0.2.0 (7)"))
        #expect(report.contains("AirPods Pro"))
        #expect(!report.contains("Priya"))   // the owner's name in a device name stays out
        #expect(report.contains("rebuilt 3 times"))
        #expect(report.contains("mic noAudio"))
        #expect(report.contains("mic-failed app=com.microsoft.VSCode audio=4.2s latency=12ms restarts=1 during-meeting"))
    }
}
