import AppKit
import Observation
import SwiftUI
import UniformTypeIdentifiers
import VoiceFlowCore

enum SettingsSection: String, CaseIterable, Identifiable {
    case general, notetaker, system, keys, privacy, advanced, about
    var id: String { rawValue }

    var title: String {
        switch self {
        case .general: "General"
        case .notetaker: "Notetaker"
        case .system: "System"
        case .keys: "API keys"
        case .privacy: "Data & Privacy"
        case .advanced: "Advanced"
        case .about: "About"
        }
    }

    var symbol: String {
        switch self {
        case .general: "slider.horizontal.3"
        case .notetaker: "person.2"
        case .system: "macwindow"
        case .keys: "key"
        case .privacy: "lock.shield"
        case .advanced: "wrench.and.screwdriver"
        case .about: "info.circle"
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
                    SidebarRow(title: section.title, symbol: section.symbol, selected: selection.section == section) {
                        selection.section = section
                    }
                }
                Spacer()
            }
            .padding(.horizontal, 10).padding(.top, 44).padding(.bottom, 10)
            .frame(width: 200)
            .background(SidebarBackground())

            Rectangle().fill(Palette.hairline).frame(width: 1)

            VerticalScroll {
                VStack(alignment: .leading, spacing: Spacing.lg) {
                    Text(selection.section.title).font(TypeScale.pageTitle).foregroundStyle(Palette.foreground).accessibilityAddTraits(.isHeader)
                    switch selection.section {
                    case .general: GeneralSettings(model: model)
                    case .notetaker: NotetakerSettings(model: model, notetaker: model.notetaker)
                    case .system: SystemSettings(model: model)
                    case .keys: KeySettings(model: model)
                    case .privacy: PrivacySettings(model: model)
                    case .advanced: AdvancedSettings(model: model)
                    case .about: AboutSettings(model: model, updates: model.updates)
                    }
                }
                .padding(.horizontal, Spacing.xl).padding(.top, Spacing.xl).padding(.bottom, Metrics.pagePadding)
                .frame(maxWidth: 680, alignment: .leading)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .background(Palette.window)
        }
        .background(Palette.window)
        .tint(Palette.primary)
        .focusEffectDisabled()
    }
}

// MARK: Shared rows

struct SettingsGroup<Content: View>: View {
    let title: String
    @ViewBuilder var content: Content

    init(_ title: String, @ViewBuilder content: () -> Content) {
        self.title = title
        self.content = content()
    }

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.xs) {
            SectionHeading(text: title)
            VStack(alignment: .leading, spacing: Spacing.sm) { content }
                .padding(Spacing.md)
                .frame(maxWidth: .infinity, alignment: .leading)
                .cardSurface()
        }
    }
}

/// A label on the left, a control on the right.
struct SettingRow<Control: View>: View {
    let title: String
    var detail: String? = nil
    @ViewBuilder var control: Control

    var body: some View {
        HStack(alignment: detail == nil ? .center : .top, spacing: Spacing.md) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(TypeScale.body).foregroundStyle(Palette.foreground)
                if let detail { HelpText(detail) }
            }
            Spacer(minLength: Spacing.md)
            control
        }
        .frame(minHeight: 24)
    }
}

struct HelpText: View {
    let text: String
    init(_ text: String) { self.text = text }
    var body: some View {
        Text(text).font(TypeScale.callout).foregroundStyle(Palette.muted).fixedSize(horizontal: false, vertical: true)
    }
}

/// A switch row.
struct SettingToggle: View {
    let title: String
    var detail: String? = nil
    @Binding var isOn: Bool

    var body: some View {
        SettingRow(title: title, detail: detail) {
            Toggle(title, isOn: $isOn).labelsHidden().toggleStyle(VFSwitchStyle())
        }
    }
}

struct ShortcutLine: View {
    let action: String
    let keys: [String]
    var body: some View {
        HStack(spacing: Spacing.xs) {
            Text(action).font(TypeScale.body).foregroundStyle(Palette.foreground)
            Spacer(minLength: Spacing.md)
            ForEach(keys, id: \.self) { KeyCap(text: $0) }
        }
        .frame(minHeight: 24)
        .accessibilityElement(children: .combine)
    }
}

// MARK: General

private struct GeneralSettings: View {
    @Bindable var model: AppModel
    @State private var tester = MicTester()

    private var hotkey: String { model.preferences.hotkey.shortName.lowercased() }

    var body: some View {
        SettingsGroup("Shortcuts") {
            SettingRow(title: "Push to talk", detail: "Hold, speak, let go.") {
                Picker("Push to talk", selection: $model.preferences.hotkey) {
                    ForEach(Hotkey.allCases) { Text($0.displayName).tag($0) }
                }
                .labelsHidden().fixedSize()
            }
            ShortcutLine(action: "Hands-free", keys: ["\(hotkey) + space", "double-tap"])
            ShortcutLine(action: "Command Mode", keys: ["\(hotkey) + ⌃"])
            ShortcutLine(action: "Paste last transcript", keys: ["⌃⌘V"])
            ShortcutLine(action: "Copy last transcript", keys: ["⌃⌘C"])
            ShortcutLine(action: "Open Scratchpad", keys: ["⌥S"])
            ShortcutLine(action: "Cancel", keys: ["esc"])
        }

        SettingsGroup("Microphone") {
            SettingRow(title: "Input") {
                Picker("Input", selection: $model.preferences.inputDeviceUID) {
                    Text("System default").tag("")
                    ForEach(model.inputDevices) { Text($0.name).tag($0.uid) }
                }
                .labelsHidden().fixedSize()
                .onAppear { model.refreshInputDevices() }
            }
            HStack(spacing: Spacing.md) {
                SegmentedMeter(level: AppRuntime.isDemo ? 0.6 : tester.level)
                Button(tester.running ? "Stop test" : "Test microphone") {
                    tester.running ? tester.stop() : tester.start(deviceUID: model.preferences.inputDeviceUID)
                }
                .buttonStyle(.vf(.secondary, compact: true))
            }
            if let error = tester.error { AttentionLine(text: error) }
        }
        .onDisappear { tester.stop() }

        SettingsGroup("My languages") {
            HelpText(model.preferences.languages.count == 1
                     ? "VoiceFlow listens for \(name(model.preferences.languages[0]))."
                     : "Pick every language you speak. VoiceFlow detects which one you're using and checks it against this list. In meetings, other people can speak anything.")
            LanguagePicker(selection: $model.preferences.languages)
            if model.preferences.speaksTelugu {
                ScriptPicker(title: "Write Telugu as", selection: $model.preferences.teluguScript, name: \.displayName, example: \.example)
            }
            if model.preferences.speaksHindi {
                ScriptPicker(title: "Write Hindi as", selection: $model.preferences.hindiScript, name: \.displayName, example: \.example)
            }
        }
    }

    private func name(_ code: String) -> String {
        Languages.options.first { $0.code == code }?.name ?? code
    }
}

/// Language chips. The common ones first; selected languages stay visible when the list is collapsed.
struct LanguagePicker: View {
    @Binding var selection: [String]
    @State private var showAll = false
    private static let commonCount = 11

    private var options: [(code: String, name: String)] {
        guard !showAll else { return Languages.options }
        return Languages.options.enumerated().filter { $0.offset < Self.commonCount || selection.contains($0.element.code) }.map(\.element)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.sm) {
            FlowLayout {
                ForEach(options, id: \.code) { option in
                    LanguageChip(name: option.name, selected: selection.contains(option.code)) {
                        if selection.contains(option.code) { selection.removeAll { $0 == option.code } }
                        else { selection.append(option.code) }
                    }
                }
            }
            Button(showAll ? "Show fewer languages" : "Show all languages") { showAll.toggle() }.buttonStyle(.vfLink)
        }
    }
}

private struct LanguageChip: View {
    let name: String
    let selected: Bool
    let toggle: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: toggle) {
            Text(name)
                .font(TypeScale.callout.weight(selected ? .medium : .regular))
                .foregroundStyle(selected ? Palette.onPrimary : Palette.muted)
                .padding(.horizontal, Spacing.sm).frame(height: 28)
                .background(RoundedRectangle(cornerRadius: 14, style: .circular).fill(selected ? Palette.primary : (hovering ? Palette.hover : Color.clear)))
                .overlay(RoundedRectangle(cornerRadius: 14, style: .circular).strokeBorder(selected ? Color.clear : Palette.hairlineStrong))
                .contentShape(Rectangle())
        }
        .buttonStyle(.quiet(radius: 14))
        .onHover { hovering = $0 }
        .accessibilityLabel(name)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}

/// Telugu and Hindi writing choices: one radio row per script, with an example.
struct ScriptPicker<Script: CaseIterable & Identifiable & Hashable>: View where Script.AllCases: RandomAccessCollection {
    let title: String
    @Binding var selection: Script
    let name: KeyPath<Script, String>
    let example: KeyPath<Script, String>

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.xs) {
            Text(title).font(TypeScale.calloutMedium).foregroundStyle(Palette.foreground).accessibilityAddTraits(.isHeader)
            ForEach(Script.allCases) { script in
                Button { selection = script } label: {
                    HStack(alignment: .top, spacing: Spacing.xs) {
                        RadioMark(isOn: selection == script).padding(.top, 1)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(script[keyPath: name]).font(TypeScale.callout).foregroundStyle(Palette.foreground)
                            Text(script[keyPath: example]).font(TypeScale.callout).foregroundStyle(Palette.muted)
                        }
                    }
                    .contentShape(Rectangle())
                }
                .buttonStyle(.quiet())
                .accessibilityLabel("\(script[keyPath: name]): \(script[keyPath: example])")
                .accessibilityAddTraits(selection == script ? .isSelected : [])
            }
        }
        .padding(.top, Spacing.xxs)
    }
}

// MARK: Notetaker

private struct NotetakerSettings: View {
    @Bindable var model: AppModel
    let notetaker: Notetaker

    var body: some View {
        SettingsGroup("Meetings") {
            SettingToggle(title: "Offer to take notes when a call starts", detail: "Zoom, Meet, Teams, Webex, FaceTime", isOn: $model.preferences.meetingsDetect)
            SettingToggle(title: "Record the other people (system audio)",
                          detail: "macOS asks once for permission to record this computer's audio. Without it, only your microphone is recorded.",
                          isOn: $model.preferences.meetingsSystemAudio)
            SettingToggle(title: "Remind me to tell everyone I'm taking notes", isOn: $model.preferences.meetingsConsentReminder)
        }
        SettingsGroup("Notes") {
            SettingRow(title: "Write notes in") {
                Picker("Write notes in", selection: $model.preferences.meetingsNotesLanguage) {
                    ForEach(MeetingNotesWriter.NotesLanguage.allCases) { Text($0.displayName).tag($0) }
                }
                .labelsHidden().fixedSize()
            }
            SettingToggle(title: "Use my calendar for meeting titles and names", isOn: Binding(
                get: { model.preferences.meetingsUseCalendar },
                set: { on in
                    model.preferences.meetingsUseCalendar = on
                    if on && !notetaker.calendarAllowed { notetaker.requestCalendar() }
                }))
        }
        SettingsGroup("Sync") {
            HStack(spacing: Spacing.xs) {
                if let folder = notetaker.folderURL {
                    Label(folder.path, systemImage: "folder")
                        .font(TypeScale.callout).foregroundStyle(Palette.foreground).lineLimit(1).truncationMode(.middle)
                        .help(folder.path)
                } else {
                    Text("Notes stay in VoiceFlow only.").font(TypeScale.callout).foregroundStyle(Palette.muted)
                }
                Spacer(minLength: Spacing.sm)
                Button(notetaker.folderURL == nil ? "Choose folder…" : "Change…") { notetaker.chooseFolder() }.buttonStyle(.vf(.secondary, compact: true))
                if notetaker.folderURL != nil {
                    Button("Stop syncing") { model.preferences.meetingsFolder = "" }.buttonStyle(.vf(.secondary, compact: true))
                }
            }
            HelpText("Each meeting is saved as a Markdown file. Choose a folder in iCloud Drive to read your notes on iPhone and other Macs, or your Obsidian vault.")
            if let error = notetaker.lastError { AttentionLine(text: error) }
        }
        SettingsGroup("Consent") {
            HelpText("Recording a call can require everyone's permission where you live. VoiceFlow shows a red Notetaker chip while recording; tell the people on the call.")
        }
    }
}

// MARK: System

private struct SystemSettings: View {
    @Bindable var model: AppModel

    var body: some View {
        SettingsGroup("App") {
            SettingToggle(title: "Launch at login", isOn: $model.preferences.launchAtLogin)
            SettingToggle(title: "Show app in Dock", isOn: $model.preferences.showInDock)
            if let error = model.lastError { AttentionLine(text: error) }
        }
        SettingsGroup("Flow Bar") {
            SettingToggle(title: model.flowBarHiddenForHour ? "Show Flow Bar at all times (hidden for one hour)" : "Show Flow Bar at all times",
                          detail: "Off by default: the bar appears only while you dictate or when VoiceFlow has something to tell you. Right-click the resting bar to hide it for an hour or switch language.",
                          isOn: $model.preferences.showBarWhenIdle)
        }
        SettingsGroup("Sound") {
            SettingToggle(title: "Play a sound when dictation starts", isOn: $model.preferences.playSounds)
            SettingToggle(title: "Mute other audio while dictating", isOn: $model.preferences.muteAudioWhileDictating)
        }
        SettingsGroup("Permissions") {
            PermissionRow(title: "Microphone", granted: model.microphoneStatus == .authorized) { model.requestMicrophone() }
            PermissionRow(title: "Accessibility", granted: model.hasAccessibility) { model.requestAccessibility() }
            HStack(spacing: Spacing.xs) {
                Image(systemName: model.hotkeyMode == .active ? "checkmark.circle" : "exclamationmark.triangle")
                    .foregroundStyle(model.hotkeyMode == .active ? Palette.muted : Palette.signal).accessibilityHidden(true)
                Text("Hotkey listener").font(TypeScale.body).foregroundStyle(Palette.foreground)
                Spacer()
                Text(model.hotkeyMode == .active ? "Active" : model.hotkeyMode == .listenOnly ? "Watch-only" : "Off")
                    .font(TypeScale.callout)
                    .foregroundStyle(model.hotkeyMode == .active ? Palette.muted : Palette.signal)
            }
            .frame(minHeight: 24)
            if model.fnKeyNeedsChange, let fn = model.fnSetting {
                HStack(spacing: Spacing.xs) {
                    AttentionLine(text: "🌐 key is set to “\(fn.displayName)”")
                    Spacer()
                    Button("Keyboard Settings…") { model.openFnKeySettings() }.buttonStyle(.vf(.secondary, compact: true))
                }
            }
        }
    }
}

struct PermissionRow: View {
    let title: String
    let granted: Bool
    let request: () -> Void

    var body: some View {
        HStack(spacing: Spacing.xs) {
            Image(systemName: granted ? "checkmark.circle" : "exclamationmark.triangle")
                .foregroundStyle(granted ? Palette.muted : Palette.signal).accessibilityHidden(true)
            Text(title).font(TypeScale.body).foregroundStyle(Palette.foreground)
            Spacer()
            if granted {
                Text("Allowed").font(TypeScale.callout).foregroundStyle(Palette.muted)
            } else {
                Button("Allow…", action: request).buttonStyle(.vf(.secondary, compact: true))
            }
        }
        .frame(minHeight: 24)
        .accessibilityElement(children: .combine)
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
        if let error = model.lastError { AttentionLine(text: error) }
        HelpText("Keys are stored in your macOS Keychain and sent only to their own provider.")
    }
}

struct KeyRow: View {
    let model: AppModel
    let key: SecretKey
    @State private var draft = ""
    @FocusState private var focused: Bool

    private var saved: Bool { model.storedKeys.contains(key) }

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.sm) {
            HStack(spacing: Spacing.xs) {
                Badge(text: saved ? "Saved in Keychain" : "Not set")
                Spacer()
                if saved {
                    Button("Test") { Task { await model.checkKey(key) } }
                        .buttonStyle(.vf(.secondary, compact: true))
                        .disabled(model.checking.contains(key))
                    Button("Remove") { model.deleteKey(key) }
                        .buttonStyle(RemoveLinkStyle())
                }
            }
            HStack(spacing: Spacing.xs) {
                ZStack(alignment: .leading) {
                    if draft.isEmpty {
                        Text(saved ? "Paste a new key to replace it" : "Paste key")
                            .font(TypeScale.callout).foregroundStyle(Palette.subtle).allowsHitTesting(false).accessibilityHidden(true)
                    }
                    SecureField("", text: $draft)
                        .textFieldStyle(.plain).font(TypeScale.callout)
                        .focused($focused)
                }
                .fieldChrome(focused: focused)
                .accessibilityLabel("API key")
                Button("Save") {
                    model.saveKey(draft, for: key)
                    draft = ""
                    Task { await model.checkKey(key) }
                }
                .buttonStyle(.vfPrimary)
                .disabled(draft.trimmingCharacters(in: .whitespaces).isEmpty)
            }
            if model.checking.contains(key) {
                ProgressView().controlSize(.small)
            } else if let result = model.keyCheck[key] {
                if result.isOK {
                    Label(result.summary, systemImage: "checkmark.circle").font(TypeScale.callout).foregroundStyle(Palette.muted)
                } else {
                    AttentionLine(text: result.summary)
                }
            } else if key == .groq {
                OutboundLink(title: "Get a free key at console.groq.com", url: AppLinks.groqKeys)
            } else {
                HelpText("Get one at \(key.whereToGetIt).")
            }
        }
    }
}

/// The one quiet destructive action on the key row.
private struct RemoveLinkStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        StyledBody(configuration: configuration)
    }

    private struct StyledBody: View {
        let configuration: Configuration
        @Environment(\.isFocused) private var focused

        var body: some View {
            configuration.label
                .font(TypeScale.calloutMedium)
                .foregroundStyle(Palette.signal.opacity(configuration.isPressed ? 0.6 : 1))
                .padding(.horizontal, Spacing.xs)
                .overlay(FocusRing(radius: 3, visible: focused))
        }
    }
}

extension KeyCheckResult {
    var isOK: Bool {
        if case .ok = self { return true }
        return false
    }
}

/// The Zero Data Retention line, shown in onboarding's Groq step and in Settings.
struct ZeroDataRetentionNote: View {
    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.xs) {
            HelpText("Groq's services agreement doesn't let it train on your API data. It may keep logs for up to 30 days for abuse and reliability. Turning on Zero Data Retention in the Groq console stops that.")
            OutboundLink(title: "Open Data Controls in the Groq console", url: AppLinks.groqDataControls)
        }
    }
}

// MARK: Data & Privacy

private struct PrivacySettings: View {
    @Bindable var model: AppModel
    @State private var confirmClear = false
    @State private var notice: String?

    var body: some View {
        SettingsGroup("History") {
            SettingRow(title: "Keep transcripts", detail: "Transcripts stay on this Mac, readable only by your account.") {
                Picker("Keep transcripts", selection: $model.preferences.historyRetentionDays) {
                    Text("Forever").tag(-1)
                    Text("30 days").tag(30)
                    Text("7 days").tag(7)
                    Text("Never store my data").tag(0)
                }
                .labelsHidden().fixedSize()
            }
            HStack(spacing: Spacing.xs) {
                Button("Export my data…") { export() }.buttonStyle(.vf(.secondary, compact: true))
                Button("Show data folder") { NSWorkspace.shared.open(AppPaths.supportFolder) }.buttonStyle(.vf(.secondary, compact: true))
                Spacer()
                Button("Delete all history") { confirmClear = true }.buttonStyle(RemoveLinkStyle())
            }
            if let notice { HelpText(notice) }
        }
        SettingsGroup("What leaves your Mac") {
            HelpText("Sent to Groq: audio while you dictate, the transcript, the app name, up to 120 characters before the cursor and names in that field; in Command Mode, the selected text and the field's text. Nothing from password fields, password managers or secure input, and no screen text from terminals. VoiceFlow never stores audio.")
            HelpText("Sent to api.github.com: a request for the latest release, at launch and once a day. It carries nothing about you or your dictations. Turn it off in About.")
            OutboundLink(title: "Groq's privacy policy", url: AppLinks.groqPrivacy)
        }
        SettingsGroup("Zero Data Retention") {
            ZeroDataRetentionNote()
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
    @State private var debugReportCopied = false

    var body: some View {
        SettingsGroup("Features") {
            SettingToggle(title: "Command Mode (\(model.preferences.hotkey.shortName.lowercased()) + ⌃)", isOn: $model.preferences.commandModeEnabled)
            SettingToggle(title: "Learn words from my corrections", isOn: $model.preferences.autoLearnWords)
            SettingToggle(title: "Whisper mode: pick up quiet speech", isOn: $model.preferences.whisperMode)
        }
        SettingsGroup("Models (Groq)") {
            modelPicker("Speech", selection: $model.preferences.transcriptionModel, options: Groq.transcriptionModels)
            modelPicker("Cleanup", selection: $model.preferences.cleanupModel, options: Groq.textModels)
            modelPicker("Command Mode", selection: $model.preferences.commandModel, options: Groq.textModels)
        }
        SettingsGroup("Troubleshooting") {
            HStack(spacing: Spacing.xs) {
                Button("Copy debug report") {
                    model.copyDebugReport()
                    debugReportCopied = true
                }
                .buttonStyle(.vf(.secondary, compact: true))
                if debugReportCopied { Text("Copied").font(TypeScale.callout).foregroundStyle(Palette.muted) }
            }
            HelpText("Versions, device names, recent results and timings, to paste into a bug report. It has no transcripts, window titles or API keys.")
        }
        SettingsGroup("Setup") {
            Button("Run setup again") { WindowManager.shared.showOnboarding() }.buttonStyle(.vf(.secondary, compact: true))
        }
    }

    private func modelPicker(_ title: String, selection: Binding<String>, options: [String]) -> some View {
        SettingRow(title: title) {
            Picker(title, selection: selection) {
                ForEach(options, id: \.self) { Text($0).tag($0) }
            }
            .labelsHidden().fixedSize()
        }
    }
}

// MARK: About

private struct AboutSettings: View {
    @Bindable var model: AppModel
    let updates: UpdateService

    private var statusText: String {
        switch updates.state {
        case .checking: "Checking for updates…"
        case .upToDate: "You have the latest version."
        case .available(let release): "Version \(release.version) is available."
        case .failed: "Could not check for updates."
        case .idle: model.preferences.checkForUpdates ? "Checked at launch and once a day." : "Automatic checks are off."
        }
    }

    var body: some View {
        Card {
            HStack(spacing: Spacing.md) {
                LogoMark(size: 56)
                VStack(alignment: .leading, spacing: 2) {
                    Text("VoiceFlow").font(.system(size: 20, weight: .bold)).foregroundStyle(Palette.foreground)
                    Text("Version \(updates.currentVersion)").font(TypeScale.callout).foregroundStyle(Palette.muted)
                    Text(statusText).font(TypeScale.callout).foregroundStyle(Palette.muted)
                }
                Spacer(minLength: Spacing.md)
                if updates.available != nil {
                    Button("View release") { updates.openRelease() }.buttonStyle(.vfPrimary)
                } else {
                    Button("Check for updates") { Task { await updates.check() } }.buttonStyle(.vfSecondary)
                        .disabled(updates.state == .checking)
                }
            }
        }
        SettingsGroup("Updates") {
            SettingToggle(title: "Check for updates automatically",
                          detail: "Asks GitHub for the latest release at launch and once a day. VoiceFlow never downloads an update by itself.",
                          isOn: $model.preferences.checkForUpdates)
        }
        HelpText("Free and open source under the MIT License. Speech and cleanup run on your own Groq key.")
        OutboundLink(title: "github.com/saideepreddy7781/voiceflow", url: AppLinks.repository)
    }
}

// MARK: Help

struct HelpView: View {
    let model: AppModel

    private var hotkey: String { model.preferences.hotkey.shortName.lowercased() }

    var body: some View {
        VerticalScroll {
            VStack(alignment: .leading, spacing: Spacing.lg) {
                HStack(spacing: Spacing.sm) {
                    LogoMark(size: 36)
                    Text("VoiceFlow Help").font(TypeScale.pageTitle).foregroundStyle(Palette.foreground).accessibilityAddTraits(.isHeader)
                }
                SettingsGroup("Shortcuts") {
                    ShortcutLine(action: "Dictate (hold)", keys: [hotkey])
                    ShortcutLine(action: "Hands-free", keys: ["\(hotkey) + space", "double-tap"])
                    ShortcutLine(action: "Command Mode", keys: ["\(hotkey) + ⌃"])
                    ShortcutLine(action: "Paste / copy last transcript", keys: ["⌃⌘V", "⌃⌘C"])
                    ShortcutLine(action: "Scratchpad", keys: ["⌥S"])
                    ShortcutLine(action: "Cancel", keys: ["esc"])
                }
                SettingsGroup("Tips") {
                    tip("Correct yourself out loud: “Tuesday, no actually Wednesday” types “Wednesday”.")
                    tip("Say “new line”, “new paragraph”, “comma” or “question mark” when you want them exactly.")
                    tip("Select text, hold \(hotkey) + ⌃ and say “make this more formal” or “translate to Hindi”.")
                    tip("Nothing typed? Press ⌃⌘V to paste your last transcript.")
                }
                HStack(spacing: Spacing.md) {
                    OutboundLink(title: "Documentation", url: AppLinks.docs)
                    OutboundLink(title: "Report an issue", url: AppLinks.issues)
                    Spacer()
                    Text("Version \(model.updates.currentVersion)").font(TypeScale.caption).foregroundStyle(Palette.muted)
                }
            }
            .padding(Spacing.xl)
        }
        .background(Palette.window)
        .tint(Palette.primary)
    }

    private func tip(_ text: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: Spacing.xs) {
            Circle().fill(Palette.subtle).frame(width: 4, height: 4).alignmentGuide(.firstTextBaseline) { $0[.bottom] + 3 }
                .accessibilityHidden(true)
            Text(text).font(TypeScale.callout).foregroundStyle(Palette.foreground).fixedSize(horizontal: false, vertical: true)
        }
    }
}
