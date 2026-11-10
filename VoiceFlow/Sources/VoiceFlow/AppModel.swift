import AppKit
import AVFoundation
import Foundation
import Observation
import ServiceManagement
import VoiceFlowCore

/// The one shared app state every view reads.
@MainActor
@Observable
final class AppModel {
    var preferences: Preferences {
        didSet { preferencesChanged(from: oldValue) }
    }

    // Keys: only whether one is stored. Values stay in the Keychain.
    private(set) var storedKeys: Set<SecretKey> = []
    private(set) var keyCheck: [SecretKey: KeyCheckResult] = [:]
    private(set) var checking: Set<SecretKey> = []
    var lastError: String?

    // Permissions and system state
    private(set) var hasAccessibility = HotkeyMonitor.hasAccessibility
    private(set) var hasInputMonitoring = HotkeyMonitor.hasInputMonitoring
    private(set) var microphoneStatus = AudioRecorder.microphoneStatus
    private(set) var hotkeyMode: HotkeyMonitor.Mode = .stopped
    private(set) var fnSetting = FnKeySetting.current
    private(set) var inputDevices: [AudioInputDevice] = AudioDevices.inputDevices()

    // Library
    var dictionary: [DictionaryEntry] { didSet { dictionaryFile.save(dictionary) } }
    var snippets: [Snippet] { didSet { snippetsFile.save(snippets) } }
    var notes: [Note] { didSet { notesFile.save(notes.filter { !$0.isBlank }) } }
    private(set) var history: [HistoryEntry]
    /// Recent sessions as metadata only, for "Copy debug report".
    let diagnostics: DiagnosticsLog
    var stats: UsageStats { UsageStats.compute(history) }

    // The Flow bar
    let pill = PillState()
    /// "Hide for 1 hour" from the Flow bar's right-click menu.
    private(set) var flowBarHiddenUntil: Date?

    let secrets = SecretStore()
    private let preferenceStore = PreferenceStore()
    private let keyTester = KeyTester()
    private let dictionaryFile = JSONFile<[DictionaryEntry]>("dictionary.json")
    private let snippetsFile = JSONFile<[Snippet]>("snippets.json")
    private let historyFile = JSONFile<[HistoryEntry]>("history.json")
    private let notesFile = JSONFile<[Note]>("notes.json")
    private var monitor: HotkeyMonitor?
    private(set) var controller: DictationController!
    private(set) var notetaker: Notetaker!
    private var permissionTimer: Timer?

    init() {
        OutputMuter.recoverAfterCrash()
        let loaded = preferenceStore.load()
        preferences = loaded
        dictionary = dictionaryFile.load() ?? []
        snippets = snippetsFile.load() ?? []
        notes = notesFile.load() ?? []
        history = historyFile.load() ?? []
        diagnostics = DiagnosticsLog(file: JSONFile("diagnostics.json"), persists: loaded.historyRetentionDays != 0)
        controller = DictationController(model: self)
        notetaker = Notetaker(model: self)
        history = applyRetention(history, days: preferences.historyRetentionDays)
        refreshStoredKeys()
        startHotkeyMonitor()
        // Dock visibility is applied by WindowManager.start, once NSApp exists.
        // Permissions can change in System Settings at any time; keep the UI and the tap current.
        permissionTimer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
        NotificationCenter.default.addObserver(forName: NSApplication.willTerminateNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.controller.shutdown() }
        }
    }

    private func tick() {
        refreshSystemState()
        if let until = flowBarHiddenUntil, until <= Date() {
            flowBarHiddenUntil = nil
            updateFlowBarVisibility()
        }
    }

    // MARK: Status

    var isReady: Bool {
        storedKeys.contains(.groq) && hotkeyMode != .stopped && microphoneStatus == .authorized && hasAccessibility
    }

    /// Fn needs the 🌐 setting changed only when the tap cannot swallow the key itself.
    var fnKeyNeedsChange: Bool {
        preferences.hotkey == .fn && hotkeyMode != .active && fnSetting != nil && fnSetting != .doNothing
    }

    var setupProblems: [String] {
        var problems: [String] = []
        if !storedKeys.contains(.groq) { problems.append("Add your Groq key") }
        if microphoneStatus != .authorized { problems.append("Allow microphone access") }
        if !hasAccessibility { problems.append("Allow Accessibility (to type text into apps)") }
        if hotkeyMode == .stopped { problems.append("Hotkey not active yet") }
        if fnKeyNeedsChange { problems.append("Set the 🌐 key to Do Nothing") }
        return problems
    }

    enum MenuBarState { case ready, attention, listening, recordingMeeting }

    var menuBarState: MenuBarState {
        switch pill.phase {
        case .listening, .handsFree, .command: return .listening
        default: break
        }
        if notetaker?.isRecording == true { return .recordingMeeting }
        return isReady ? .ready : .attention
    }

    var statusSymbol: String {
        switch pill.phase {
        case .listening, .handsFree, .command: "waveform.circle.fill"
        case .processing: "ellipsis.circle"
        default: isReady ? "waveform" : "waveform.badge.exclamationmark"
        }
    }

    func refreshSystemState() {
        hasAccessibility = HotkeyMonitor.hasAccessibility
        hasInputMonitoring = HotkeyMonitor.hasInputMonitoring
        microphoneStatus = AudioRecorder.microphoneStatus
        fnSetting = FnKeySetting.current
        // Upgrade the tap (listen-only → active) as soon as Accessibility is granted.
        if (hotkeyMode == .stopped && (hasAccessibility || hasInputMonitoring)) || (hotkeyMode == .listenOnly && hasAccessibility) {
            startHotkeyMonitor()
        }
    }

    func refreshInputDevices() { inputDevices = AudioDevices.inputDevices() }

    // MARK: Permissions

    func requestMicrophone() {
        Task {
            _ = await AudioRecorder.requestMicrophone()
            microphoneStatus = AudioRecorder.microphoneStatus
            if microphoneStatus == .denied { openPrivacyPane("Privacy_Microphone") }
        }
    }

    func requestAccessibility() {
        if !HotkeyMonitor.requestAccessibility() { openPrivacyPane("Privacy_Accessibility") }
        refreshSystemState()
    }

    func openPrivacyPane(_ anchor: String) {
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?\(anchor)")!)
    }

    func openFnKeySettings() { NSWorkspace.shared.open(FnKeySetting.settingsURL) }

    /// Quits and opens again. The new copy starts after this one has gone (VoiceFlow allows one copy).
    func relaunch() {
        let path = Bundle.main.bundleURL.path.replacingOccurrences(of: "'", with: "'\\''")
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/bin/sh")
        task.arguments = ["-c", "while kill -0 \(ProcessInfo.processInfo.processIdentifier) 2>/dev/null; do sleep 0.2; done; /usr/bin/open '\(path)'"]
        try? task.run()
        NSApp.terminate(nil)
    }

    // MARK: Hotkey

    private func startHotkeyMonitor() {
        if monitor == nil {
            monitor = HotkeyMonitor(hotkey: preferences.hotkey) { [weak self] output in
                self?.handle(output)
            }
        }
        hotkeyMode = monitor?.start() ?? .stopped
    }

    private func handle(_ output: HotkeyMonitor.Output) {
        switch output {
        case .gesture(.begin(let kind)):
            if kind == .command && !preferences.commandModeEnabled {
                controller.begin(.dictation)
            } else {
                controller.begin(kind)
            }
        case .gesture(.becomeCommand):
            if preferences.commandModeEnabled { controller.becomeCommand() }
        case .gesture(.lock): controller.lock()
        case .gesture(.finish): controller.finish()
        case .gesture(.cancel): controller.cancel()
        case .shortcut(.pasteLast): controller.pasteLast()
        case .shortcut(.copyLast): controller.copyLast()
        case .shortcut(.scratchpad): WindowManager.shared.toggleScratchpad()
        }
    }

    /// Called when the app ends a session on its own (finish button, time limit, error), so the
    /// keyboard gesture state resets without sending a second, conflicting action.
    func resetHotkeySession() { monitor?.resetSession(notify: false) }

    func enterHandsFreeGesture() { monitor?.enterHandsFree() }

    // MARK: Flow bar and Dock

    func hideFlowBarForAnHour() {
        flowBarHiddenUntil = Date().addingTimeInterval(3_600)
        updateFlowBarVisibility()
    }

    var flowBarHiddenForHour: Bool { flowBarHiddenUntil.map { $0 > Date() } ?? false }

    func updateFlowBarVisibility() {
        pill.showWhenIdle = preferences.onboarded && preferences.showBarWhenIdle && !flowBarHiddenForHour
    }

    /// Call only after launch: during App.init the shared NSApplication does not exist yet.
    func applyDockVisibility() {
        NSApp.setActivationPolicy(preferences.showInDock ? .regular : .accessory)
    }

    /// Quick language switch from the Flow bar: one language, or auto-detect.
    func setSingleLanguage(_ code: String?) {
        preferences.languages = code.map { [$0] } ?? []
    }

    // MARK: Preferences

    private func preferencesChanged(from old: Preferences) {
        preferenceStore.save(preferences)
        if preferences.hotkey != old.hotkey { monitor?.setHotkey(preferences.hotkey) }
        if preferences.historyRetentionDays != old.historyRetentionDays {
            history = applyRetention(history, days: preferences.historyRetentionDays)
            historyFile.save(history)
            diagnostics.persists = preferences.historyRetentionDays != 0
        }
        if preferences.launchAtLogin != old.launchAtLogin { applyLaunchAtLogin() }
        if preferences.showBarWhenIdle != old.showBarWhenIdle || preferences.onboarded != old.onboarded { updateFlowBarVisibility() }
        if preferences.showInDock != old.showInDock { applyDockVisibility() }
    }

    private func applyLaunchAtLogin() {
        do {
            if preferences.launchAtLogin { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
        } catch {
            lastError = "Launch at login: \(error.localizedDescription)"
        }
    }

    // MARK: History

    func record(_ entry: HistoryEntry) {
        guard preferences.historyRetentionDays != 0 else { return }
        history.insert(entry, at: 0)
        historyFile.save(history)
    }

    func deleteHistory(_ ids: Set<HistoryEntry.ID>) {
        history.removeAll { ids.contains($0.id) }
        historyFile.save(history)
    }

    func toggleFlag(_ id: HistoryEntry.ID) {
        guard let i = history.firstIndex(where: { $0.id == id }) else { return }
        history[i].flagged = !(history[i].flagged ?? false)
        historyFile.save(history)
    }

    func clearHistory() {
        history = []
        historyFile.save(history)
        diagnostics.clear()
    }

    // MARK: Debug report

    func copyDebugReport() {
        TextInserter.copyToClipboard(debugReport())
    }

    /// Everything here is metadata. See `DebugReport` for why nothing else can reach it.
    private func debugReport() -> String {
        var facts = DebugReport.Facts()
        let info = Bundle.main.infoDictionary
        facts.appVersion = info?["CFBundleShortVersionString"] as? String ?? "dev"
        facts.buildNumber = info?["CFBundleVersion"] as? String ?? "dev"
        facts.macOS = ProcessInfo.processInfo.operatingSystemVersionString
        #if arch(arm64)
        facts.architecture = "arm64"
        #else
        facts.architecture = "x86_64"
        #endif
        let devices = AudioDevices.inputDevices()
        facts.inputDevices = devices.map(\.name)
        facts.selectedInput = devices.first { $0.uid == preferences.inputDeviceUID }?.name
        facts.defaultInput = AudioDevices.defaultInputName()
        facts.defaultOutput = AudioDevices.defaultOutputName()
        facts.hotkeyMode = "\(hotkeyMode)"
        facts.microphoneAllowed = microphoneStatus == .authorized
        facts.accessibilityAllowed = hasAccessibility
        facts.inputMonitoringAllowed = hasInputMonitoring
        facts.micState = "\(SharedMicrophone.shared.state)"
        facts.micRestartsThisRun = SharedMicrophone.shared.restartCount
        facts.meetingRecording = notetaker.isRecording
        if notetaker.isRecording {
            facts.meetingMicHealth = notetaker.micHealth
            facts.meetingMicMissingSeconds = notetaker.micMissingSeconds
        }
        return DebugReport.render(facts, events: diagnostics.events, history: history)
    }

    /// Writes history, dictionary, snippets and notes to one JSON file the user chooses.
    func exportData(to url: URL) throws {
        struct Export: Codable {
            var exported = Date()
            var history: [HistoryEntry]
            var dictionary: [DictionaryEntry]
            var snippets: [Snippet]
            var notes: [Note]
        }
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        let data = try encoder.encode(Export(history: history, dictionary: dictionary, snippets: snippets, notes: notes))
        try data.write(to: url, options: .atomic)
    }

    // MARK: Dictionary

    var dictionaryTerms: [String] { dictionary.map(\.term) }

    /// Adds a word or a correction. Returns an error message, or nil when added.
    @discardableResult
    func addDictionaryEntry(term: String, misspelling: String? = nil, source: DictionaryEntry.Source = .manual) -> String? {
        let t = term.trimmingCharacters(in: .whitespacesAndNewlines)
        let m = misspelling?.trimmingCharacters(in: .whitespacesAndNewlines)
        if t.isEmpty || t.allSatisfy({ $0.isPunctuation || $0.isSymbol }) { return "Word cannot be empty." }
        if t.count > DictionaryEntry.maxLength || (m?.count ?? 0) > DictionaryEntry.maxLength {
            return "Entries can be up to \(DictionaryEntry.maxLength) characters."
        }
        if let m, m.isEmpty { return "Enter the misspelling to correct." }
        if dictionary.contains(where: { $0.term.caseInsensitiveCompare(t) == .orderedSame && $0.misspelling?.lowercased() == m?.lowercased() }) {
            return "“\(t)” is already in your dictionary."
        }
        dictionary.insert(DictionaryEntry(term: t, misspelling: m, source: source), at: 0)
        return nil
    }

    func addDictionaryTerm(_ term: String, source: DictionaryEntry.Source = .manual) {
        addDictionaryEntry(term: term, source: source)
    }

    func updateDictionaryEntry(_ entry: DictionaryEntry) {
        guard let i = dictionary.firstIndex(where: { $0.id == entry.id }) else { return }
        dictionary[i] = entry
    }

    func removeDictionaryEntries(_ ids: Set<DictionaryEntry.ID>) {
        dictionary.removeAll { ids.contains($0.id) }
    }

    func removeDictionaryTerm(_ term: String) {
        dictionary.removeAll { $0.term == term }
    }

    /// Imports one word per line (or comma-separated). Up to 1,000 items; returns how many were added.
    func importDictionary(from url: URL) -> Int {
        guard let text = try? String(contentsOf: url, encoding: .utf8) else { return 0 }
        let items = text.split(whereSeparator: { $0.isNewline || $0 == "," }).prefix(1_000)
        return items.reduce(0) { $0 + (addDictionaryEntry(term: String($1)) == nil ? 1 : 0) }
    }

    // MARK: Snippets

    func importSnippets(from url: URL) -> Int {
        // "cue<TAB>text" or "cue,text" per line.
        guard let text = try? String(contentsOf: url, encoding: .utf8) else { return 0 }
        var added = 0
        for line in text.split(whereSeparator: \.isNewline).prefix(1_000) {
            let parts = line.contains("\t") ? line.split(separator: "\t", maxSplits: 1) : line.split(separator: ",", maxSplits: 1)
            guard parts.count == 2 else { continue }
            let trigger = String(parts[0]).trimmingCharacters(in: .whitespaces)
            let expansion = String(parts[1]).trimmingCharacters(in: .whitespaces)
            if SnippetMatcher.validate(trigger: trigger, expansion: expansion, existing: snippets) == nil {
                snippets.insert(Snippet(trigger: trigger, expansion: expansion), at: 0)
                added += 1
            }
        }
        return added
    }

    // MARK: Notes

    @discardableResult
    func newNote(body: String = "") -> Note {
        let note = Note(body: body)
        notes.insert(note, at: 0)
        return note
    }

    func updateNote(_ note: Note) {
        guard let i = notes.firstIndex(where: { $0.id == note.id }) else { return }
        var n = note
        n.updated = Date()
        notes[i] = n
    }

    func deleteNote(_ id: Note.ID) { notes.removeAll { $0.id == id } }

    var sortedNotes: [Note] {
        notes.sorted { ($0.pinned ? 1 : 0, $0.updated) > ($1.pinned ? 1 : 0, $1.updated) }
    }

    // MARK: Keys

    /// Keys read from the Keychain this run. Reading only ever happens off the main thread, when a key is
    /// first needed: after an update macOS may show a "VoiceFlow wants to use your confidential
    /// information" prompt, and the app (and the keyboard listener) must keep running while it is up.
    private var cachedKeys: [SecretKey: String] = [:]

    /// Whether a Groq key is saved. Never prompts.
    var hasGroqKey: Bool { storedKeys.contains(.groq) }

    func keyAsync(_ key: SecretKey = .groq) async -> String? {
        if let cached = cachedKeys[key] { return cached }
        guard storedKeys.contains(key) else { return nil }
        let secrets = self.secrets
        let value = await Task.detached(priority: .userInitiated) { (try? secrets.read(key)) ?? nil }.value
        if let value { cachedKeys[key] = value }
        return value
    }

    func saveKey(_ value: String, for key: SecretKey) {
        do {
            try secrets.save(value, for: key)
            cachedKeys[key] = value.trimmingCharacters(in: .whitespacesAndNewlines)
            keyCheck[key] = nil
            lastError = nil
        } catch {
            lastError = "\(error)"
        }
        refreshStoredKeys()
    }

    func deleteKey(_ key: SecretKey) {
        do {
            try secrets.delete(key)
            cachedKeys[key] = nil
            keyCheck[key] = nil
        } catch {
            lastError = "\(error)"
        }
        refreshStoredKeys()
    }

    func checkKey(_ key: SecretKey) async {
        guard let value = await keyAsync(key) else {
            keyCheck[key] = .failed("no key saved")
            return
        }
        checking.insert(key)
        defer { checking.remove(key) }
        keyCheck[key] = await keyTester.check(key, value: value)
    }

    private func refreshStoredKeys() {
        storedKeys = Set(SecretKey.allCases.filter { secrets.contains($0) })
    }
}
