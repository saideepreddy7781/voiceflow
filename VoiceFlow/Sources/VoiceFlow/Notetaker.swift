import AppKit
import EventKit
import Foundation
import Observation
import os
import VoiceFlowCore

/// Meeting notes: records a call, transcribes it live, writes notes, and saves them to your notes folder.
@MainActor
@Observable
final class Notetaker {
    enum State: Equatable { case idle, recording, writingNotes }

    private unowned let model: AppModel
    private let log = Logger(subsystem: "com.saideep.voiceflow", category: "notetaker")
    /// Nil in demo mode, which records nothing.
    private let recorder: MeetingRecorder!
    private let store: JSONFile<[Meeting]>
    private let calendar: EKEventStore?
    private var detectTimer: Timer?
    private var offeredForThisCall = false

    private(set) var meetings: [Meeting] { didSet { store.save(meetings) } }
    private(set) var state: State = .idle
    /// The meeting being recorded, with its live transcript.
    private(set) var live: Meeting?
    private(set) var startedAt: Date?
    var lastError: String?

    var isRecording: Bool { state == .recording }
    /// Whether the meeting's microphone is delivering usable audio, for the Meetings page.
    private(set) var micHealth: MicHealthState = .healthy
    var micMissingSeconds: Int { recorder?.micMissingSeconds ?? 0 }
    var micLevel: Float { demoInputs?.micLevel ?? recorder?.micLevel ?? 0 }
    var systemLevel: Float { demoInputs?.systemLevel ?? recorder?.systemLevel ?? 0 }
    /// The language last heard on each side, for the live view.
    var micLanguage: String? { demoInputs?.micLanguage ?? recorder?.micLanguage }
    var callLanguage: String? { demoInputs?.callLanguage ?? recorder?.callLanguage }
    private let demoInputs: DemoContent.LiveInputs?
    /// Bumped every second while recording so level meters redraw.
    private(set) var levelTick = 0
    private var warnedSilentSystem = false
    private var levelTimer: Timer?

    init(model: AppModel) {
        self.model = model
        recorder = MeetingRecorder()
        store = JSONFile<[Meeting]>("meetings.json")
        calendar = EKEventStore()
        demoInputs = nil
        let loaded = store.load() ?? []
        meetings = loaded.map { m in
            guard m.status == .recording || m.status == .writingNotes else { return m }
            var fixed = m
            fixed.status = .failed
            fixed.end = fixed.end ?? fixed.lines.last.map { m.start.addingTimeInterval($0.time + 5) } ?? m.start
            fixed.error = "VoiceFlow quit before the notes were finished. Press Try again."
            return fixed
        }
        recorder.onUpdate = { [weak self] meeting in
            self?.live = meeting
            self?.saveLiveSnapshot(meeting)
        }
        recorder.onDailyLimit = { [weak self] message in
            self?.model.pill.show("Groq's daily limit ran out", detail: message, seconds: 12)
        }
        detectTimer = Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.detect() }
        }
    }

    /// Demo mode: the given meetings in memory, no recorder, no timers, no calendar.
    init(demoModel: AppModel, content: DemoContent) {
        model = demoModel
        recorder = nil
        store = .discarding("meetings.json")
        calendar = nil
        demoInputs = content.liveInputs
        meetings = content.meetings
        if let live = content.liveMeeting {
            self.live = live
            state = .recording
            startedAt = content.now.addingTimeInterval(-767)
            micHealth = content.liveInputs?.micHealth ?? .healthy
        }
    }

    // MARK: Recording

    func start(app detectedApp: String? = nil) {
        guard state == .idle else { return }
        guard model.hasGroqKey else {
            model.pill.show("Add your Groq key to take meeting notes.", seconds: 4)
            return
        }
        Task { await startRecording(app: detectedApp) }
    }

    private func startRecording(app detectedApp: String?) async {
        let prefs = model.preferences
        guard let key = await model.keyAsync() else {
            model.pill.show("VoiceFlow couldn't read your Groq key from the Keychain.", seconds: 5)
            return
        }
        guard state == .idle else { return }
        guard AudioRecorder.microphoneStatus == .authorized else {
            model.requestMicrophone()
            model.pill.show("Allow microphone access, then start Notetaker again.", seconds: 4)
            return
        }
        let event = prefs.meetingsUseCalendar ? currentCalendarEvent() : nil
        let app = detectedApp ?? MeetingDetector.current() ?? ""
        let title = event?.title ?? (app.isEmpty ? "Meeting" : "\(app) meeting")
        let attendees = event?.attendees ?? []
        let names = model.dictionaryTerms + attendees
        let settings = MeetingRecorder.Settings(
            apiKey: key, model: prefs.transcriptionModel, languages: prefs.languages,
            prompts: WhisperPrompts(terms: names, telugu: prefs.teluguScript, hindi: prefs.hindiScript),
            micDeviceUID: prefs.inputDeviceUID.isEmpty ? nil : prefs.inputDeviceUID,
            recordSystemAudio: prefs.meetingsSystemAudio)
        do {
            try recorder.start(title: title, app: app, attendees: attendees, settings: settings)
        } catch {
            model.pill.show("Notetaker could not start", detail: "\(error)", seconds: 6)
            return
        }
        live = recorder.meeting
        startedAt = Date()
        warnedSilentSystem = false
        levelTimer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.levelTick &+= 1
                self.updateMicHealth()
                if !self.warnedSilentSystem, self.recorder.systemSilentSeconds >= 90, self.live?.micOnly == false {
                    self.warnedSilentSystem = true
                    self.model.pill.show("Not hearing the meeting audio",
                                         detail: "Allow VoiceFlow under System Settings → Privacy & Security → Screen & System Audio Recording. Your microphone is still recorded.",
                                         action: .init(title: "Open") { self.model.openPrivacyPane("Privacy_ScreenCapture") }, seconds: 12)
                }
            }
        }
        state = .recording
        // A dictation that started just before this may have muted the Mac.
        model.controller.unmuteForMeeting()
        micHealth = .healthy
        model.pill.meetingSince = startedAt
        lastError = recorder.lastError
        offeredForThisCall = true
        if let error = recorder.lastError {
            model.pill.show("Recording your microphone only", detail: error, seconds: 8)
        } else if prefs.meetingsConsentReminder {
            model.pill.show("Notetaker is on", detail: "Let everyone on the call know you're taking notes.", seconds: 6)
        }
        log.info("Notetaker started: \(title, privacy: .private)")
    }

    /// Tells the user when the microphone stops delivering audio during a meeting. A dictation in progress
    /// keeps its toast: this waits until it has finished.
    private func updateMicHealth() {
        let current = recorder.micHealth
        guard current != micHealth, !model.controller.isBusy else { return }
        let previous = micHealth
        micHealth = current
        model.diagnostics.record(DiagnosticEvent(session: .meeting, reason: current == .healthy ? .micRecovered : .micFailed,
                                                 micRestarts: recorder.micRestartsRequested, duringMeeting: true))
        switch current {
        case .silent, .noAudio:
            model.pill.show("Not hearing your microphone",
                            detail: current == .silent
                                ? "It is sending silence. If you didn't mute it, check the input in Settings → General. The meeting keeps recording."
                                : "VoiceFlow lost your mic and couldn't get it back. Check the input in Settings → General. The meeting keeps recording.",
                            action: .init(title: "Open") { WindowManager.shared.showSettings(.general) }, seconds: 12)
        case .healthy:
            if previous == .silent || previous == .noAudio { model.pill.show("Your microphone is back", seconds: 4) }
        case .restarting:
            break
        }
    }

    func stop() {
        guard state == .recording else { return }
        Task { _ = await finishRecording(writeNotes: true) }
    }

    /// Stops recording and saves the meeting. Notes are written afterwards (unless quitting); a new
    /// recording can start as soon as this returns.
    @discardableResult
    func finishRecording(writeNotes: Bool) async -> Meeting.ID? {
        guard state == .recording else { return nil }
        state = .writingNotes
        model.pill.meetingSince = nil
        levelTimer?.invalidate()
        levelTimer = nil
        guard var meeting = await recorder.stop(retryFailed: writeNotes) else { state = .idle; return nil }
        meeting.status = writeNotes ? .writingNotes : .failed
        if !writeNotes { meeting.error = "VoiceFlow quit before the notes were written. Press Try again." }
        meetings.removeAll { $0.id == meeting.id }   // the live snapshot
        meetings.insert(meeting, at: 0)
        live = nil
        state = .idle
        lastStoppedID = meeting.id
        if writeNotes { Task { await self.writeNotes(for: meeting.id) } }
        return meeting.id
    }

    /// The meeting most recently stopped, for the Meetings page to select.
    private(set) var lastStoppedID: Meeting.ID?

    /// Keeps a copy of the live meeting on disk, so a crash loses at most the last chunk.
    private func saveLiveSnapshot(_ meeting: Meeting) {
        guard state == .recording else { return }
        var snapshot = meeting
        snapshot.status = .recording
        if let i = meetings.firstIndex(where: { $0.id == meeting.id }) { meetings[i] = snapshot } else { meetings.insert(snapshot, at: 0) }
    }

    /// Writes (or rewrites) the notes for a meeting, then saves them to the notes folder.
    func writeNotes(for id: Meeting.ID) async {
        guard let first = meetings.firstIndex(where: { $0.id == id }) else { return }
        if meetings[first].lines.isEmpty {
            meetings[first].status = .failed
            if meetings[first].error == nil {
                meetings[first].error = "No speech was picked up. Check that your microphone works (Settings → General → Test microphone) and that the call's audio plays on this Mac."
            }
            model.pill.show("No speech was recorded", detail: meetings[first].error ?? "", seconds: 8)
            return
        }
        meetings[first].status = .writingNotes
        meetings[first].error = nil
        let key = await model.keyAsync()
        // Look the meeting up again: the list can change while waiting for the Keychain.
        guard let i = meetings.firstIndex(where: { $0.id == id }) else { return }
        guard let key else {
            meetings[i].status = .failed
            meetings[i].error = "No Groq key available. Add it in Settings → API keys, then press Try again."
            return
        }
        let prefs = model.preferences
        let chat = GroqChat(apiKey: key)
        await rewriteScript(for: id, chat: chat, prefs: prefs)
        guard let current = meetings.first(where: { $0.id == id }) else { return }
        let writer = MeetingNotesWriter(chat: chat, model: prefs.commandModel)
        do {
            let notes = try await writer.write(meeting: current, language: prefs.meetingsNotesLanguage,
                                               telugu: prefs.teluguScript, hindi: prefs.hindiScript)
            guard let j = meetings.firstIndex(where: { $0.id == id }) else { return }
            meetings[j].notes = notes.carryingOverDone(from: meetings[j].notes)
            if meetings[j].title.hasSuffix("meeting") || meetings[j].title == "Meeting", !notes.title.isEmpty {
                meetings[j].title = notes.title
            }
            meetings[j].status = .done
            saveToFolder(meetings[j])
            model.pill.show("Meeting notes are ready", detail: meetings[j].title,
                            action: .init(title: "Open") { WindowManager.shared.showHub(.meetings) }, seconds: 8)
        } catch {
            guard let j = meetings.firstIndex(where: { $0.id == id }) else { return }
            meetings[j].status = .failed
            if case ProviderError.dailyLimit = error {
                meetings[j].error = "\(error) Your transcript is saved. Press Try again after that."
                model.pill.show("Notes are waiting on Groq's daily limit", detail: meetings[j].error ?? "", seconds: 10)
            } else {
                meetings[j].error = "\(error)"
            }
            saveToFolder(meetings[j])
        }
    }

    /// Applies the Telugu and Hindi writing choices to a newly recorded transcript. Older meetings stay as they are.
    private func rewriteScript(for id: Meeting.ID, chat: GroqChat, prefs: Preferences) async {
        guard let meeting = meetings.first(where: { $0.id == id }), meeting.scriptRewritten != true,
              meeting.lines.contains(where: { $0.language != nil }) else { return }
        let telugu = prefs.speaksTelugu ? prefs.teluguScript : nil
        let hindi = prefs.speaksHindi ? prefs.hindiScript : nil
        guard telugu != nil || hindi != nil else { return }
        let writer = TranscriptScriptWriter(chat: chat, model: prefs.cleanupModel)
        let result = await writer.rewrite(meeting.lines, telugu: telugu, hindi: hindi)
        guard let i = meetings.firstIndex(where: { $0.id == id }) else { return }
        meetings[i].lines = result.lines
        meetings[i].scriptRewritten = result.note == nil
        if let note = result.note, meetings[i].note?.contains(note) != true {
            meetings[i].note = [meetings[i].note, note].compactMap { $0 }.joined(separator: " ")
        }
    }

    // MARK: Editing

    func rename(_ id: Meeting.ID, to title: String) {
        guard let i = meetings.firstIndex(where: { $0.id == id }) else { return }
        let old = meetings[i]
        meetings[i].title = title
        saveToFolder(meetings[i], replacing: old)
    }

    /// Names the other side of a meeting. Empty goes back to "Others".
    func renameOthers(_ id: Meeting.ID, to name: String) {
        guard let i = meetings.firstIndex(where: { $0.id == id }) else { return }
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        meetings[i].othersLabel = trimmed.isEmpty ? nil : trimmed
        saveToFolder(meetings[i])
    }

    func toggleAction(_ id: Meeting.ID, item: ActionItem.ID) {
        guard let i = meetings.firstIndex(where: { $0.id == id }),
              let k = meetings[i].notes?.actionItems.firstIndex(where: { $0.id == item }) else { return }
        meetings[i].notes?.actionItems[k].done.toggle()
        saveToFolder(meetings[i])
    }

    func delete(_ id: Meeting.ID) {
        meetings.removeAll { $0.id == id }
    }

    // MARK: Notes folder ("sync")

    var folderURL: URL? {
        let path = model.preferences.meetingsFolder
        return path.isEmpty ? nil : URL(fileURLWithPath: path, isDirectory: true)
    }

    func chooseFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.prompt = "Use this folder"
        panel.message = "Meeting notes will be saved here as Markdown. Pick a folder in iCloud Drive to see them on your other devices."
        guard panel.runModal() == .OK, let url = panel.url else { return }
        model.preferences.meetingsFolder = url.path
        meetings.filter { $0.status == .done }.forEach { saveToFolder($0) }
    }

    func saveToFolder(_ meeting: Meeting, replacing old: Meeting? = nil) {
        guard let folder = folderURL, meeting.status != .recording else { return }
        let name = MeetingMarkdown.fileName(meeting)
        for previous in [old.map(MeetingMarkdown.fileName), meeting.syncedFileName].compactMap({ $0 }) where previous != name {
            try? FileManager.default.removeItem(at: folder.appending(path: previous))
        }
        do {
            try MeetingMarkdown.render(meeting).write(to: folder.appending(path: name), atomically: true, encoding: .utf8)
            if let i = meetings.firstIndex(where: { $0.id == meeting.id }) { meetings[i].syncedFileName = name }
            lastError = nil
        } catch {
            lastError = "Could not save notes to \(folder.lastPathComponent): \(error.localizedDescription)"
        }
    }

    func saveMarkdownAs(_ meeting: Meeting) {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = MeetingMarkdown.fileName(meeting)
        panel.allowedContentTypes = [.init(filenameExtension: "md") ?? .plainText]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        try? MeetingMarkdown.render(meeting).write(to: url, atomically: true, encoding: .utf8)
    }

    // MARK: Detection and calendar

    private func detect() {
        guard model.preferences.meetingsDetect, state == .idle, !model.controller.isRecording else { return }
        guard let app = MeetingDetector.current() else {
            offeredForThisCall = false   // the call ended; offer again next time
            return
        }
        guard !offeredForThisCall else { return }
        offeredForThisCall = true
        model.pill.show("\(app) call detected", detail: "Take notes of this meeting?",
                        action: .init(title: "Take notes") { [weak self] in self?.start(app: app) }, seconds: 15)
    }

    var calendarAllowed: Bool { EKEventStore.authorizationStatus(for: .event) == .fullAccess }

    func requestCalendar() {
        guard let calendar else { return }
        Task {
            let granted = (try? await calendar.requestFullAccessToEvents()) ?? false
            if !granted {
                model.preferences.meetingsUseCalendar = false
                model.openPrivacyPane("Privacy_Calendars")
            }
        }
    }

    private func currentCalendarEvent() -> (title: String, attendees: [String])? {
        guard calendarAllowed, let calendar else { return nil }
        let now = Date()
        let predicate = calendar.predicateForEvents(withStart: now.addingTimeInterval(-3 * 3_600), end: now.addingTimeInterval(15 * 60), calendars: nil)
        let event = calendar.events(matching: predicate)
            .filter { !$0.isAllDay && $0.startDate <= now.addingTimeInterval(10 * 60) && $0.endDate >= now }
            .min { abs($0.startDate.timeIntervalSince(now)) < abs($1.startDate.timeIntervalSince(now)) }
        guard let event else { return nil }
        let names = (event.attendees ?? []).compactMap { $0.name }.filter { !$0.contains("@") }
        return (event.title ?? "Meeting", names)
    }
}
