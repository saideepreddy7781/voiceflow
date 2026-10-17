import AppKit
import Observation
import SwiftUI
import UniformTypeIdentifiers
import VoiceFlowCore

enum SettingsSection: String, CaseIterable, Identifiable {
    case general, notetaker, system, keys, privacy, advanced
    var id: String { rawValue }

    var title: String {
        switch self {
        case .general: "General"
        case .notetaker: "Notetaker"
        case .system: "System"
        case .keys: "API keys"
        case .privacy: "Data & Privacy"
        case .advanced: "Advanced"
        }
    }

    var symbol: String {
        switch self {
        case .general: "slider.horizontal.3"
        case .notetaker: "person.2.wave.2"
        case .system: "macwindow"
        case .keys: "key"
        case .privacy: "lock.shield"
        case .advanced: "wrench.and.screwdriver"
        }
    }
}

@MainActor
@Observable
final class SettingsSelection {
    var section: SettingsSection = .general
}

struct SettingsView: View {
    @Bindable var model: AppModel
    @Bindable var selection: SettingsSelection

    var body: some View {
        HStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 2) {
                ForEach(SettingsSection.allCases) { section in
                    Button { selection.section = section } label: {
                        Label(section.title, systemImage: section.symbol)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(.horizontal, 10).padding(.vertical, 7)
                            .background(RoundedRectangle(cornerRadius: 7).fill(selection.section == section ? Color.accentColor.opacity(0.15) : .clear))
                            .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                }
                Spacer()
            }
            .padding(.horizontal, 10).padding(.top, 34).padding(.bottom, 10)
            .frame(width: 190)
            .background(Color.primary.opacity(0.03))

            Divider()

            ScrollView {
                VStack(alignment: .leading, spacing: 18) {
                    Text(selection.section.title).font(.system(size: 22, weight: .bold))
                    switch selection.section {
                    case .general: GeneralSettings(model: model)
                    case .notetaker: NotetakerSettings(model: model, notetaker: model.notetaker)
                    case .system: SystemSettings(model: model)
                    case .keys: KeySettings(model: model)
                    case .privacy: PrivacySettings(model: model)
                    case .advanced: AdvancedSettings(model: model)
                    }
                }
                .padding(.horizontal, 26).padding(.top, 30).padding(.bottom, 26)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
    }
}

// MARK: General

private struct GeneralSettings: View {
    @Bindable var model: AppModel
    @State private var tester = MicTester()

    var body: some View {
        SettingsGroup("Shortcuts") {
            Picker("Push to talk", selection: $model.preferences.hotkey) {
                ForEach(Hotkey.allCases) { Text($0.displayName).tag($0) }
            }
            ShortcutLine(action: "Hands-free", keys: ["\(model.preferences.hotkey.shortName.lowercased()) + space", "double-tap"])
            ShortcutLine(action: "Command Mode", keys: ["\(model.preferences.hotkey.shortName.lowercased()) + ⌃"])
            ShortcutLine(action: "Paste last transcript", keys: ["⌃⌘V"])
            ShortcutLine(action: "Copy last transcript", keys: ["⌃⌘C"])
            ShortcutLine(action: "Open Scratchpad", keys: ["⌥S"])
            ShortcutLine(action: "Cancel", keys: ["esc"])
        }

        SettingsGroup("Microphone") {
            Picker("Input", selection: $model.preferences.inputDeviceUID) {
                Text("System default").tag("")
                ForEach(model.inputDevices) { Text($0.name).tag($0.uid) }
            }
            .onAppear { model.refreshInputDevices() }
            HStack {
                LevelMeter(level: tester.level, bars: 20)
                Spacer()
                Button(tester.running ? "Stop test" : "Test microphone") {
                    tester.running ? tester.stop() : tester.start(deviceUID: model.preferences.inputDeviceUID)
                }
            }
            if let error = tester.error { Text(error).font(.callout).foregroundStyle(.red) }
        }
        .onDisappear { tester.stop() }

        SettingsGroup("My languages") {
            Text(model.preferences.languages.count == 1
                 ? "VoiceFlow listens for \(name(model.preferences.languages[0]))."
                 : "VoiceFlow detects the language automatically. Choosing just one is most accurate.")
                .font(.callout).foregroundStyle(.secondary)
            LanguagePicker(selection: $model.preferences.languages)
            if model.preferences.speaksTelugu {
                TeluguWritingPicker(selection: $model.preferences.teluguScript)
            }
        }
    }

    private func name(_ code: String) -> String {
        Languages.options.first { $0.code == code }?.name ?? code
    }
}

struct LanguagePicker: View {
    @Binding var selection: [String]

    var body: some View {
        LazyVGrid(columns: [GridItem(.adaptive(minimum: 140), alignment: .leading)], alignment: .leading, spacing: 6) {
            ForEach(Languages.options, id: \.code) { option in
                Toggle(option.name, isOn: Binding(
                    get: { selection.contains(option.code) },
                    set: { on in
                        if on { selection.append(option.code) } else { selection.removeAll { $0 == option.code } }
                    }))
                .toggleStyle(.checkbox)
            }
        }
    }
}

private struct ShortcutLine: View {
    let action: String
    let keys: [String]
    var body: some View {
        HStack {
            Text(action)
            Spacer()
            ForEach(keys, id: \.self) { KeyCap(text: $0) }
        }
    }
}

struct TeluguWritingPicker: View {
    @Binding var selection: TeluguScript

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Write Telugu as").font(.callout.weight(.semibold))
            ForEach(TeluguScript.allCases) { script in
                Button { selection = script } label: {
                    HStack(alignment: .top, spacing: 8) {
                        Image(systemName: selection == script ? "largecircle.fill.circle" : "circle")
                            .foregroundStyle(selection == script ? Color.accentColor : .secondary)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(script.displayName)
                            Text(script.example).font(.callout).foregroundStyle(.secondary)
                        }
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
        }
    }
}

// MARK: Notetaker

private struct NotetakerSettings: View {
    @Bindable var model: AppModel
    let notetaker: Notetaker

    var body: some View {
        SettingsGroup("Meetings") {
            Toggle("Offer to take notes when a call starts (Zoom, Meet, Teams, Webex, FaceTime)", isOn: $model.preferences.meetingsDetect)
            Toggle("Record the other people (system audio)", isOn: $model.preferences.meetingsSystemAudio)
            Text("macOS asks once for permission to record this computer's audio. Without it, only your microphone is recorded.")
                .font(.callout).foregroundStyle(.secondary)
            Toggle("Remind me to tell everyone I'm taking notes", isOn: $model.preferences.meetingsConsentReminder)
        }
        SettingsGroup("Notes") {
            Picker("Write notes in", selection: $model.preferences.meetingsNotesLanguage) {
                ForEach(MeetingNotesWriter.NotesLanguage.allCases) { Text($0.displayName).tag($0) }
            }
            Toggle("Use my calendar for meeting titles and names", isOn: Binding(
                get: { model.preferences.meetingsUseCalendar },
                set: { on in
                    model.preferences.meetingsUseCalendar = on
                    if on && !notetaker.calendarAllowed { notetaker.requestCalendar() }
                }))
        }
        SettingsGroup("Sync") {
            HStack {
                if let folder = notetaker.folderURL {
                    Label(folder.path, systemImage: "folder").lineLimit(1).truncationMode(.middle)
                } else {
                    Text("Notes stay in VoiceFlow only.").foregroundStyle(.secondary)
                }
                Spacer()
                Button(notetaker.folderURL == nil ? "Choose folder…" : "Change…") { notetaker.chooseFolder() }
                if notetaker.folderURL != nil { Button("Stop syncing") { model.preferences.meetingsFolder = "" } }
            }
            Text("Each meeting is saved as a Markdown file. Choose a folder in iCloud Drive to read your notes on iPhone and other Macs, or your Obsidian vault.")
                .font(.callout).foregroundStyle(.secondary)
            if let error = notetaker.lastError { Text(error).font(.callout).foregroundStyle(.orange) }
        }
        SettingsGroup("Consent") {
            Text("Recording a call can require everyone's permission where you live. VoiceFlow shows a red Notetaker chip while recording; tell the people on the call.")
                .font(.callout).foregroundStyle(.secondary)
        }
    }
}

// MARK: System

private struct SystemSettings: View {
    @Bindable var model: AppModel

    var body: some View {
        SettingsGroup("App") {
            Toggle("Launch at login", isOn: $model.preferences.launchAtLogin)
            Toggle("Show app in Dock", isOn: $model.preferences.showInDock)
            if let error = model.lastError { Text(error).font(.callout).foregroundStyle(.red) }
        }
        SettingsGroup("Flow Bar") {
            Toggle(model.flowBarHiddenForHour ? "Show Flow Bar at all times (hidden for one hour)" : "Show Flow Bar at all times",
                   isOn: $model.preferences.showBarWhenIdle)
            Text("Right-click the resting bar to hide it for an hour or switch language.").font(.callout).foregroundStyle(.secondary)
        }
        SettingsGroup("Sound") {
            Toggle("Play a sound when dictation starts", isOn: $model.preferences.playSounds)
            Toggle("Mute other audio while dictating", isOn: $model.preferences.muteAudioWhileDictating)
        }
        SettingsGroup("Permissions") {
            PermissionRow(title: "Microphone", granted: model.microphoneStatus == .authorized) { model.requestMicrophone() }
            PermissionRow(title: "Accessibility", granted: model.hasAccessibility) { model.requestAccessibility() }
            HStack {
                Text("Hotkey listener")
                Spacer()
                Text(model.hotkeyMode == .active ? "Active" : model.hotkeyMode == .listenOnly ? "Watch-only" : "Off")
                    .foregroundStyle(model.hotkeyMode == .active ? .green : .orange)
            }
            if model.fnKeyNeedsChange, let fn = model.fnSetting {
                HStack {
                    Text("🌐 key is set to “\(fn.displayName)”").foregroundStyle(.orange)
                    Spacer()
                    Button("Keyboard Settings…") { model.openFnKeySettings() }
                }
            }
        }
    }
}

// MARK: Keys

private struct KeySettings: View {
    let model: AppModel

    var body: some View {
        SettingsGroup("Groq") {
            KeyRow(model: model, key: .groq)
        }
        SettingsGroup("Gemini (optional)") {
            KeyRow(model: model, key: .gemini)
        }
        if let error = model.lastError { Text(error).foregroundStyle(.red).font(.callout) }
        Text("Keys are stored in your macOS Keychain and sent only to their own provider.")
            .font(.callout).foregroundStyle(.secondary)
    }
}

// MARK: Data & Privacy

private struct PrivacySettings: View {
    @Bindable var model: AppModel
    @State private var confirmClear = false
    @State private var notice: String?

    var body: some View {
        SettingsGroup("History") {
            Picker("Keep transcripts", selection: $model.preferences.historyRetentionDays) {
                Text("Forever").tag(-1)
                Text("30 days").tag(30)
                Text("7 days").tag(7)
                Text("Never store my data").tag(0)
            }
            Text("Transcripts stay on this Mac, readable only by your account.").font(.callout).foregroundStyle(.secondary)
            HStack {
                Button("Export my data…") { export() }
                Button("Show data folder") { NSWorkspace.shared.open(AppPaths.supportFolder) }
                Spacer()
                Button("Delete all history", role: .destructive) { confirmClear = true }
            }
            if let notice { Text(notice).font(.callout).foregroundStyle(.secondary) }
        }
        SettingsGroup("What leaves your Mac") {
            Text("Sent to Groq: audio while you dictate, the transcript, the app name, up to 120 characters before the cursor and names in that field; in Command Mode, the selected text and the field's text. Nothing from password fields, password managers or secure input, and no screen text from terminals. VoiceFlow never stores audio.")
                .font(.callout).foregroundStyle(.secondary)
            Button("Groq's privacy policy") { NSWorkspace.shared.open(AppLinks.groqPrivacy) }
        }
        .alert("Delete all history?", isPresented: $confirmClear) {
            Button("Delete", role: .destructive) { model.clearHistory() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("This removes every saved transcript from this Mac.")
        }
    }

    private func export() {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = "VoiceFlow-export.json"
        panel.allowedContentTypes = [.json]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            try model.exportData(to: url)
            notice = "Exported to \(url.lastPathComponent)."
        } catch {
            notice = "Export failed: \(error.localizedDescription)"
        }
    }
}

// MARK: Advanced

private struct AdvancedSettings: View {
    @Bindable var model: AppModel

    var body: some View {
        SettingsGroup("Features") {
            Toggle("Command Mode (\(model.preferences.hotkey.shortName.lowercased()) + ⌃)", isOn: $model.preferences.commandModeEnabled)
            Toggle("Learn words from my corrections", isOn: $model.preferences.autoLearnWords)
            Toggle("Whisper mode: pick up quiet speech", isOn: $model.preferences.whisperMode)
        }
        SettingsGroup("Models (Groq)") {
            Picker("Speech", selection: $model.preferences.transcriptionModel) {
                ForEach(Groq.transcriptionModels, id: \.self) { Text($0).tag($0) }
            }
            Picker("Cleanup", selection: $model.preferences.cleanupModel) {
                ForEach(Groq.textModels, id: \.self) { Text($0).tag($0) }
            }
            Picker("Command Mode", selection: $model.preferences.commandModel) {
                ForEach(Groq.textModels, id: \.self) { Text($0).tag($0) }
            }
        }
        SettingsGroup("Setup") {
            Button("Run setup again") { WindowManager.shared.showOnboarding() }
        }
    }
}

// MARK: Shared

struct SettingsGroup<Content: View>: View {
    let title: String
    @ViewBuilder var content: Content

    init(_ title: String, @ViewBuilder content: () -> Content) {
        self.title = title
        self.content = content()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(title).font(.headline)
            VStack(alignment: .leading, spacing: 10) { content }
                .padding(14)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(RoundedRectangle(cornerRadius: 12).fill(Color.primary.opacity(0.04)))
                .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(Color.primary.opacity(0.08)))
        }
    }
}

struct PermissionRow: View {
    let title: String
    let granted: Bool
    let request: () -> Void

    var body: some View {
        HStack {
            Image(systemName: granted ? "checkmark.circle.fill" : "exclamationmark.circle.fill")
                .foregroundStyle(granted ? .green : .orange)
            Text(title)
            Spacer()
            if granted { Text("Allowed").foregroundStyle(.secondary) } else { Button("Allow…", action: request) }
        }
    }
}

struct KeyRow: View {
    let model: AppModel
    let key: SecretKey
    @State private var draft = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text(model.storedKeys.contains(key) ? "Saved in Keychain" : "Not set")
                    .foregroundStyle(model.storedKeys.contains(key) ? .green : .secondary)
                Spacer()
                if model.storedKeys.contains(key) {
                    Button("Test") { Task { await model.checkKey(key) } }.disabled(model.checking.contains(key))
                    Button("Remove", role: .destructive) { model.deleteKey(key) }
                }
            }
            HStack {
                SecureField(model.storedKeys.contains(key) ? "Paste a new key to replace it" : "Paste key", text: $draft)
                    .textFieldStyle(.roundedBorder)
                Button("Save") {
                    model.saveKey(draft, for: key)
                    draft = ""
                    Task { await model.checkKey(key) }
                }
                .disabled(draft.trimmingCharacters(in: .whitespaces).isEmpty)
            }
            if model.checking.contains(key) {
                ProgressView().controlSize(.small)
            } else if let result = model.keyCheck[key] {
                Text(result.summary).font(.callout).foregroundStyle(result.isOK ? .green : .red)
            } else {
                Text("Get one at \(key.whereToGetIt).").font(.callout).foregroundStyle(.secondary)
            }
        }
    }
}

extension KeyCheckResult {
    var isOK: Bool {
        if case .ok = self { return true }
        return false
    }
}

// MARK: Help

struct HelpView: View {
    let model: AppModel

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                HStack(spacing: 10) {
                    LogoMark(size: 34)
                    Text("VoiceFlow Help").font(.system(size: 22, weight: .bold))
                }
                SettingsGroup("Shortcuts") {
                    ShortcutLine(action: "Dictate (hold)", keys: [model.preferences.hotkey.shortName.lowercased()])
                    ShortcutLine(action: "Hands-free", keys: ["\(model.preferences.hotkey.shortName.lowercased()) + space", "double-tap"])
                    ShortcutLine(action: "Command Mode", keys: ["\(model.preferences.hotkey.shortName.lowercased()) + ⌃"])
                    ShortcutLine(action: "Paste / copy last transcript", keys: ["⌃⌘V", "⌃⌘C"])
                    ShortcutLine(action: "Scratchpad", keys: ["⌥S"])
                    ShortcutLine(action: "Cancel", keys: ["esc"])
                }
                SettingsGroup("Tips") {
                    Text("• Correct yourself out loud: “Tuesday, no actually Wednesday” types “Wednesday”.")
                    Text("• Say “new line”, “new paragraph”, “comma” or “question mark” when you want them exactly.")
                    Text("• Select text, hold \(model.preferences.hotkey.shortName.lowercased()) + ⌃ and say “make this more formal” or “translate to Hindi”.")
                    Text("• Nothing typed? Press ⌃⌘V to paste your last transcript.")
                }
                .font(.callout)
                HStack {
                    Button("Documentation") { NSWorkspace.shared.open(AppLinks.docs) }
                    Button("Report an issue") { NSWorkspace.shared.open(AppLinks.issues) }
                    Spacer()
                    Text("Version \(Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "dev")")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            .padding(24)
        }
    }
}
