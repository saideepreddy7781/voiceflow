import AVFoundation
import SwiftUI
import VoiceFlowCore

/// First-run setup in eight steps, each checked before moving on.
struct OnboardingView: View {
    @Bindable var model: AppModel
    let done: () -> Void
    @State private var step = 0
    @State private var tester = MicTester()

    private let steps = ["Connect", "Permissions", "Microphone", "Shortcut", "Languages", "Try it", "See the difference", "Done"]

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(spacing: 6) {
                ForEach(steps.indices, id: \.self) { i in
                    Capsule().fill(i <= step ? Color.accentColor : Color.secondary.opacity(0.25)).frame(height: 4)
                }
            }
            .padding(.top, 10)
            Text("Step \(step + 1) of \(steps.count) · \(steps[step])").font(.caption).foregroundStyle(.secondary)

            Group {
                switch step {
                case 0: connect
                case 1: permissions
                case 2: microphone
                case 3: shortcut
                case 4: languages
                case 5: PracticeArea(model: model)
                case 6: transform
                default: finish
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)

            HStack {
                if step > 0 && step < steps.count - 1 { Button("Back") { go(step - 1) } }
                Spacer()
                if step == 5 || step == 6 { Button("Skip") { go(step + 1) } }
                if step < steps.count - 1 {
                    Button("Continue") { go(step + 1) }
                        .buttonStyle(.borderedProminent)
                        .keyboardShortcut(.defaultAction)
                        .disabled(!canContinue)
                } else {
                    Button("Explore VoiceFlow") { done() }.buttonStyle(.borderedProminent).keyboardShortcut(.defaultAction)
                }
            }
        }
        .padding(32)
        .frame(width: 760, height: 600)
        .onDisappear { tester.stop() }
    }

    private func go(_ next: Int) {
        if step == 2 { tester.stop() }
        step = next
        if step == 2 && model.microphoneStatus == .authorized { tester.start(deviceUID: model.preferences.inputDeviceUID) }
    }

    private var canContinue: Bool {
        switch step {
        case 0: model.storedKeys.contains(.groq)
        case 1: model.microphoneStatus == .authorized && model.hasAccessibility
        default: true
        }
    }

    // 1. Connect (stands in for sign-in: VoiceFlow has no accounts)
    private var connect: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 14) {
                LogoMark(size: 56)
                Text("Speak. VoiceFlow types.").font(.system(size: 30, weight: .bold))
            }
            Text("Hold a key in any app, talk, and let go. VoiceFlow writes what you meant: filler words gone, your corrections applied, punctuation and lists in place.")
                .font(.title3).foregroundStyle(.secondary)
            Divider().padding(.vertical, 4)
            Text("Connect Groq").font(.title3.bold())
            Text("VoiceFlow uses Groq for speech recognition and cleanup with your own free key. No account with VoiceFlow is needed.")
                .foregroundStyle(.secondary)
            Button("Get a free key at console.groq.com") { NSWorkspace.shared.open(AppLinks.groqKeys) }
            KeyRow(model: model, key: .groq)
        }
    }

    // 2. Permission cards
    private var permissions: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Two permissions").font(.system(size: 26, weight: .bold))
            PermissionCard(symbol: "mic.fill", title: "Microphone",
                           detail: "Used only while you hold the dictation key or use hands-free mode.",
                           granted: model.microphoneStatus == .authorized) { model.requestMicrophone() }
            PermissionCard(symbol: "accessibility", title: "Accessibility",
                           detail: "Lets VoiceFlow notice the dictation key, read the field you're typing in, and type into it. Your other keystrokes are never recorded.",
                           granted: model.hasAccessibility) { model.requestAccessibility() }
            if !model.hasAccessibility {
                Text("Already switched on but still shown here? Switch VoiceFlow off and on again in the list, or relaunch.")
                    .font(.callout).foregroundStyle(.secondary)
                Button("Relaunch VoiceFlow") { model.relaunch() }
            }
        }
    }

    // 3. Mic test
    private var microphone: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Test your microphone").font(.system(size: 26, weight: .bold))
            Text("Say something. The bars should move with your voice.").foregroundStyle(.secondary)
            LevelMeter(level: tester.level)
                .frame(maxWidth: .infinity)
                .padding(.vertical, 24)
                .background(RoundedRectangle(cornerRadius: 14).fill(Color.primary.opacity(0.05)))
            Picker("Microphone", selection: $model.preferences.inputDeviceUID) {
                Text("System default").tag("")
                ForEach(model.inputDevices) { Text($0.name).tag($0.uid) }
            }
            .onChange(of: model.preferences.inputDeviceUID) { _, uid in tester.start(deviceUID: uid) }
            if let error = tester.error { Text(error).foregroundStyle(.red).font(.callout) }
            Text("Bars not moving? Pick another microphone, or check that no other app is using it.")
                .font(.callout).foregroundStyle(.secondary)
        }
        .onAppear {
            model.refreshInputDevices()
            if model.microphoneStatus == .authorized { tester.start(deviceUID: model.preferences.inputDeviceUID) }
        }
    }

    // 4. Shortcut
    private var shortcut: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Your shortcut").font(.system(size: 26, weight: .bold))
            Picker("Dictation key", selection: $model.preferences.hotkey) {
                ForEach(Hotkey.allCases) { Text($0.displayName).tag($0) }
            }
            .pickerStyle(.segmented)
            HStack(alignment: .top, spacing: 14) {
                Card {
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Push to talk").font(.headline)
                        HStack { Text("Hold"); KeyCap(text: model.preferences.hotkey.shortName.lowercased()); Text(", speak, let go.") }
                    }
                }
                Card {
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Hands-free").font(.headline)
                        HStack { KeyCap(text: model.preferences.hotkey.shortName.lowercased() + " + space"); Text("or double-tap.") }
                        Text("Press again to finish.").foregroundStyle(.secondary)
                    }
                }
            }
            Card {
                HStack {
                    Image(systemName: model.pill.phase == .idle ? "keyboard" : "checkmark.circle.fill")
                        .foregroundStyle(model.pill.phase == .idle ? Color.secondary : Color.green)
                        .font(.title2)
                    Text(model.pill.phase == .idle ? "Try it: hold \(model.preferences.hotkey.shortName.lowercased()) now. The bar appears at the bottom of your screen."
                                                   : "Got it. Release to finish.")
                }
            }
            if model.fnKeyNeedsChange {
                Text("Your 🌐 key opens another macOS feature. Allow Accessibility (previous step) so VoiceFlow can take over the key, or set it to Do Nothing in Keyboard settings.")
                    .font(.callout).foregroundStyle(.orange)
            }
        }
    }

    // 5. Languages and privacy
    private var languages: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                Text("Languages and privacy").font(.system(size: 26, weight: .bold))
                Text("Pick the languages you speak. One language is most accurate; with several, VoiceFlow detects which one you're using.")
                    .foregroundStyle(.secondary)
                LanguagePicker(selection: $model.preferences.languages)
                if model.preferences.speaksTelugu {
                    TeluguWritingPicker(selection: $model.preferences.teluguScript)
                }
                if model.preferences.speaksHindi {
                    HindiWritingPicker(selection: $model.preferences.hindiScript)
                }
                Divider().padding(.vertical, 4)
                Picker("Keep my transcripts", selection: $model.preferences.historyRetentionDays) {
                    Text("30 days").tag(30)
                    Text("7 days").tag(7)
                    Text("Forever").tag(-1)
                    Text("Never store my data").tag(0)
                }
                Text("What leaves your Mac: audio while you dictate and the text needed to clean it up, sent to Groq with your key. Nothing from password fields or password managers. History stays on this Mac.")
                    .font(.callout).foregroundStyle(.secondary)
            }
        }
    }

    // 7. Before and after
    private var transform: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("See the difference").font(.system(size: 26, weight: .bold))
            BeforeAfter(said: "um so let's meet on tuesday no actually wednesday at uh three",
                        typed: "Let's meet on Wednesday at 3.", label: "Corrections and fillers")
            BeforeAfter(said: "i need three things milk eggs and bread",
                        typed: "I need three things: milk, eggs, and bread.", label: "Punctuation")
            BeforeAfter(said: "first clone the repo second run npm install third start the server",
                        typed: "1. Clone the repo.\n2. Run npm install.\n3. Start the server.", label: "Lists")
            BeforeAfter(said: "hey can you send me the report by friday thanks",
                        typed: "Hi, could you send me the report by Friday? Thanks.", label: "Style for email")
        }
    }

    // 8. Done
    private var finish: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("You're all set").font(.system(size: 30, weight: .bold))
            Text("Hold \(model.preferences.hotkey.shortName.lowercased()) in any app to dictate. The Flow bar at the bottom of your screen shows when VoiceFlow is listening.")
                .font(.title3).foregroundStyle(.secondary)
            if !model.setupProblems.isEmpty {
                Text("Still needed: " + model.setupProblems.joined(separator: ", ")).foregroundStyle(.orange)
            }
        }
    }
}

private struct PermissionCard: View {
    let symbol: String
    let title: String
    let detail: String
    let granted: Bool
    let request: () -> Void

    var body: some View {
        HStack(alignment: .top, spacing: 14) {
            Image(systemName: symbol).font(.title2).frame(width: 36, height: 36)
                .background(Circle().fill(Color.accentColor.opacity(0.15)))
            VStack(alignment: .leading, spacing: 4) {
                Text(title).font(.headline)
                Text(detail).font(.callout).foregroundStyle(.secondary)
            }
            Spacer()
            if granted {
                Label("Allowed", systemImage: "checkmark.circle.fill").foregroundStyle(.green)
            } else {
                Button("Allow", action: request).buttonStyle(.borderedProminent)
            }
        }
        .padding(16)
        .background(RoundedRectangle(cornerRadius: 14).fill(Color.primary.opacity(0.04)))
        .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(granted ? Color.green.opacity(0.5) : Color.primary.opacity(0.1)))
    }
}

private struct BeforeAfter: View {
    let said: String
    let typed: String
    let label: String

    var body: some View {
        HStack(alignment: .top, spacing: 12) {
            VStack(alignment: .leading, spacing: 4) {
                Text("You said").font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                Text(said).font(.callout).foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            Image(systemName: "arrow.right").foregroundStyle(.secondary).padding(.top, 14)
            VStack(alignment: .leading, spacing: 4) {
                Text(label).font(.caption.weight(.semibold)).foregroundStyle(Color.accentColor)
                Text(typed).font(.callout)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 12).fill(Color.primary.opacity(0.04)))
    }
}

/// Practice dictating into a mock email and a mock notes page.
private struct PracticeArea: View {
    let model: AppModel
    @State private var tab = 0
    @State private var to = "priya@example.com"
    @State private var subject = "Proposal"
    @State private var emailBody = ""
    @State private var notes = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Try it yourself").font(.system(size: 26, weight: .bold))
            Text("Click into the text, hold \(model.preferences.hotkey.shortName.lowercased()), and say the line below. Let go and watch it appear.")
                .foregroundStyle(.secondary)
            Picker("", selection: $tab) {
                Text("Email").tag(0)
                Text("Notes").tag(1)
            }
            .pickerStyle(.segmented).labelsHidden().frame(width: 220)

            if tab == 0 {
                Text("Say: “Hi Priya, um, thanks for the proposal. I'll review it by Tuesday, no actually Wednesday.”")
                    .font(.callout.italic())
                VStack(spacing: 0) {
                    MockField(label: "To", text: $to)
                    Divider()
                    MockField(label: "Subject", text: $subject)
                    Divider()
                    TextEditor(text: $emailBody).font(.body).padding(8).frame(minHeight: 180)
                        .scrollContentBackground(.hidden)
                }
                .background(RoundedRectangle(cornerRadius: 10).fill(Color(nsColor: .textBackgroundColor)))
                .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(Color.primary.opacity(0.15)))
            } else {
                Text("Say: “Things to do this week, first finish the deck, second call the bank, third book train tickets.”")
                    .font(.callout.italic())
                VStack(alignment: .leading, spacing: 8) {
                    Text("Weekly plan").font(.title2.bold())
                    TextEditor(text: $notes).font(.body).frame(minHeight: 200).scrollContentBackground(.hidden)
                }
                .padding(14)
                .background(RoundedRectangle(cornerRadius: 10).fill(Color(nsColor: .textBackgroundColor)))
                .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(Color.primary.opacity(0.15)))
            }
        }
    }
}

private struct MockField: View {
    let label: String
    @Binding var text: String
    var body: some View {
        HStack {
            Text(label).foregroundStyle(.secondary).frame(width: 60, alignment: .leading)
            TextField("", text: $text).textFieldStyle(.plain)
        }
        .padding(.horizontal, 10).padding(.vertical, 8)
    }
}
