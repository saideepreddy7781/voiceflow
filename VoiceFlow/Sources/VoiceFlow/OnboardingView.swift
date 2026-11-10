import AVFoundation
import SwiftUI
import VoiceFlowCore

/// First-run setup in eight steps, each checked before moving on.
struct OnboardingView: View {
    @Bindable var model: AppModel
    let done: () -> Void
    @State private var step: Int
    @State private var tester = MicTester()

    private let steps = ["Connect", "Permissions", "Microphone", "Shortcut", "Languages", "Try it", "See the difference", "Done"]

    init(model: AppModel, initialStep: Int = 0, done: @escaping () -> Void) {
        self.model = model
        self.done = done
        _step = State(initialValue: initialStep)
    }

    private var hotkey: String { model.preferences.hotkey.shortName.lowercased() }

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.md) {
            VStack(alignment: .leading, spacing: Spacing.xs) {
                HStack(spacing: 6) {
                    ForEach(steps.indices, id: \.self) { i in
                        Capsule().fill(i <= step ? Palette.primary : Palette.track).frame(height: 4)
                    }
                }
                .accessibilityHidden(true)
                Text("Step \(step + 1) of \(steps.count) · \(steps[step])").font(TypeScale.caption).foregroundStyle(Palette.muted)
            }
            .padding(.top, Spacing.xs)

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

            HStack(spacing: Spacing.xs) {
                if step > 0 && step < steps.count - 1 {
                    Button("Back") { go(step - 1) }.buttonStyle(.vfSecondary)
                }
                Spacer()
                if step == 5 || step == 6 {
                    Button("Skip") { go(step + 1) }.buttonStyle(.vfSecondary)
                }
                if step < steps.count - 1 {
                    Button("Continue") { go(step + 1) }
                        .buttonStyle(.vfPrimary)
                        .keyboardShortcut(.defaultAction)
                        .disabled(!canContinue)
                } else {
                    Button("Explore VoiceFlow") { done() }.buttonStyle(.vfPrimary).keyboardShortcut(.defaultAction)
                }
            }
        }
        .padding(.horizontal, Spacing.xxl).padding(.top, Spacing.lg).padding(.bottom, Spacing.lg)
        .frame(width: 760, height: 600)
        .background(Palette.window)
        .tint(Palette.primary)
        .focusEffectDisabled()
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

    private func stepTitle(_ text: String) -> some View {
        Text(text).font(TypeScale.pageTitle).foregroundStyle(Palette.foreground).accessibilityAddTraits(.isHeader)
    }

    // 1. Connect (stands in for sign-in: VoiceFlow has no accounts)
    private var connect: some View {
        VStack(alignment: .leading, spacing: Spacing.sm) {
            HStack(spacing: Spacing.md) {
                LogoMark(size: 48)
                stepTitle("Speak. VoiceFlow types.")
            }
            Text("Hold a key in any app, talk, and let go. VoiceFlow writes what you meant: filler words gone, your corrections applied, punctuation and lists in place.")
                .font(TypeScale.body).foregroundStyle(Palette.muted).fixedSize(horizontal: false, vertical: true)
            RowDivider().padding(.vertical, Spacing.xxs)
            Text("Connect Groq").font(TypeScale.sectionTitle).foregroundStyle(Palette.foreground)
            Text("VoiceFlow uses Groq for speech recognition and cleanup with your own free key. No account with VoiceFlow is needed.")
                .font(TypeScale.callout).foregroundStyle(Palette.muted).fixedSize(horizontal: false, vertical: true)
            Card(padding: Spacing.md) {
                KeyRow(model: model, key: .groq)
            }
            ZeroDataRetentionNote()
        }
    }

    // 2. Permission cards
    private var permissions: some View {
        VStack(alignment: .leading, spacing: Spacing.sm) {
            stepTitle("Two permissions")
            PermissionCard(symbol: "mic", title: "Microphone",
                           detail: "Used only while you hold the dictation key or use hands-free mode.",
                           granted: model.microphoneStatus == .authorized) { model.requestMicrophone() }
            PermissionCard(symbol: "accessibility", title: "Accessibility",
                           detail: "Lets VoiceFlow notice the dictation key, read the field you're typing in, and type into it. Your other keystrokes are never recorded.",
                           granted: model.hasAccessibility) { model.requestAccessibility() }
            if !model.hasAccessibility {
                HelpText("Already switched on but still shown here? Switch VoiceFlow off and on again in the list, or relaunch.")
                Button("Relaunch VoiceFlow") { model.relaunch() }.buttonStyle(.vf(.secondary, compact: true))
            }
        }
    }

    // 3. Mic test
    private var microphone: some View {
        VStack(alignment: .leading, spacing: Spacing.sm) {
            stepTitle("Test your microphone")
            Text("Say something. The bars should move with your voice.").font(TypeScale.body).foregroundStyle(Palette.muted)
            LevelMeter(level: AppRuntime.isDemo ? 0.7 : tester.level)
                .frame(maxWidth: .infinity)
                .padding(.vertical, Spacing.lg)
                .cardSurface()
            SettingRow(title: "Microphone") {
                Picker("Microphone", selection: $model.preferences.inputDeviceUID) {
                    Text("System default").tag("")
                    ForEach(model.inputDevices) { Text($0.name).tag($0.uid) }
                }
                .labelsHidden().fixedSize()
                .onChange(of: model.preferences.inputDeviceUID) { _, uid in tester.start(deviceUID: uid) }
            }
            if let error = tester.error { AttentionLine(text: error) }
            HelpText("Bars not moving? Pick another microphone, or check that no other app is using it.")
        }
        .onAppear {
            model.refreshInputDevices()
            if model.microphoneStatus == .authorized { tester.start(deviceUID: model.preferences.inputDeviceUID) }
        }
    }

    // 4. Shortcut
    private var shortcut: some View {
        VStack(alignment: .leading, spacing: Spacing.sm) {
            stepTitle("Your shortcut")
            TabStrip(items: Hotkey.allCases, title: \.displayName, selection: $model.preferences.hotkey)
            HStack(alignment: .top, spacing: Spacing.sm) {
                Card(padding: Spacing.md, fillsHeight: true) {
                    VStack(alignment: .leading, spacing: Spacing.xs) {
                        SectionHeading(text: "Push to talk")
                        HStack(spacing: Spacing.xs) {
                            Text("Hold"); KeyCap(text: hotkey); Text("and speak. Let go to finish.")
                        }
                        .font(TypeScale.callout).foregroundStyle(Palette.muted)
                    }
                }
                Card(padding: Spacing.md, fillsHeight: true) {
                    VStack(alignment: .leading, spacing: Spacing.xs) {
                        SectionHeading(text: "Hands-free")
                        HStack(spacing: Spacing.xs) {
                            KeyCap(text: hotkey + " + space"); Text("or double-tap.")
                        }
                        .font(TypeScale.callout).foregroundStyle(Palette.muted)
                        Text("Press again to finish.").font(TypeScale.callout).foregroundStyle(Palette.muted)
                    }
                }
            }
            .fixedSize(horizontal: false, vertical: true)
            Card(padding: Spacing.md) {
                HStack(spacing: Spacing.sm) {
                    Image(systemName: model.pill.phase == .idle ? "keyboard" : "checkmark.circle")
                        .font(.system(size: 18))
                        .foregroundStyle(model.pill.phase == .idle ? Palette.muted : Palette.foreground)
                        .accessibilityHidden(true)
                    Text(model.pill.phase == .idle ? "Try it: hold \(hotkey) now. The bar appears at the bottom of your screen."
                                                   : "Got it. Release to finish.")
                        .font(TypeScale.callout).foregroundStyle(Palette.foreground)
                }
            }
            if model.fnKeyNeedsChange {
                AttentionLine(text: "Your 🌐 key opens another macOS feature. Allow Accessibility (previous step) so VoiceFlow can take over the key, or set it to Do Nothing in Keyboard settings.")
            }
        }
    }

    // 5. Languages and privacy
    private var languages: some View {
        VerticalScroll {
            VStack(alignment: .leading, spacing: Spacing.sm) {
                stepTitle("Languages and privacy")
                Text("Pick the languages you speak. One language is most accurate; with several, VoiceFlow detects which one you're using.")
                    .font(TypeScale.body).foregroundStyle(Palette.muted).fixedSize(horizontal: false, vertical: true)
                LanguagePicker(selection: $model.preferences.languages)
                if model.preferences.speaksTelugu {
                    ScriptPicker(title: "Write Telugu as", selection: $model.preferences.teluguScript, name: \.displayName, example: \.example)
                }
                if model.preferences.speaksHindi {
                    ScriptPicker(title: "Write Hindi as", selection: $model.preferences.hindiScript, name: \.displayName, example: \.example)
                }
                RowDivider().padding(.vertical, Spacing.xxs)
                SettingRow(title: "Keep my transcripts") {
                    Picker("Keep my transcripts", selection: $model.preferences.historyRetentionDays) {
                        Text("30 days").tag(30)
                        Text("7 days").tag(7)
                        Text("Forever").tag(-1)
                        Text("Never store my data").tag(0)
                    }
                    .labelsHidden().fixedSize()
                }
                HelpText("What leaves your Mac: audio while you dictate and the text needed to clean it up, sent to Groq with your key. Nothing from password fields or password managers. History stays on this Mac.")
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    // 7. Before and after
    private var transform: some View {
        VStack(alignment: .leading, spacing: Spacing.sm) {
            stepTitle("See the difference")
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
        VStack(alignment: .leading, spacing: Spacing.sm) {
            LogoMark(size: 64).padding(.bottom, Spacing.xs)
            stepTitle("You're all set")
            Text("Hold \(hotkey) in any app to dictate. The Flow bar at the bottom of your screen shows when VoiceFlow is listening.")
                .font(TypeScale.body).foregroundStyle(Palette.muted).fixedSize(horizontal: false, vertical: true)
            if !model.setupProblems.isEmpty {
                AttentionLine(text: "Still needed: " + model.setupProblems.joined(separator: ", "))
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
        HStack(alignment: .top, spacing: Spacing.md) {
            Image(systemName: symbol).font(.system(size: 16)).foregroundStyle(Palette.foreground)
                .frame(width: 36, height: 36)
                .background(Circle().fill(Palette.selected))
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: Spacing.xxs) {
                Text(title).font(TypeScale.cardTitle).foregroundStyle(Palette.foreground)
                HelpText(detail)
            }
            Spacer(minLength: Spacing.sm)
            if granted {
                Label("Allowed", systemImage: "checkmark.circle").font(TypeScale.callout).foregroundStyle(Palette.muted)
            } else {
                Button("Allow", action: request).buttonStyle(.vfPrimary)
            }
        }
        .padding(Spacing.md)
        .cardSurface()
        .accessibilityElement(children: .contain)
    }
}

private struct BeforeAfter: View {
    let said: String
    let typed: String
    let label: String

    var body: some View {
        HStack(alignment: .top, spacing: Spacing.sm) {
            VStack(alignment: .leading, spacing: Spacing.xxs) {
                Text("You said").font(TypeScale.label).foregroundStyle(Palette.muted)
                Text(said).font(TypeScale.callout).foregroundStyle(Palette.muted).fixedSize(horizontal: false, vertical: true)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            Image(systemName: "arrow.right").font(.system(size: 11)).foregroundStyle(Palette.subtle).padding(.top, Spacing.md).accessibilityHidden(true)
            VStack(alignment: .leading, spacing: Spacing.xxs) {
                Text(label).font(TypeScale.label).foregroundStyle(Palette.foreground)
                Text(typed).font(TypeScale.callout).foregroundStyle(Palette.foreground).fixedSize(horizontal: false, vertical: true)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(Spacing.sm + 2)
        .cardSurface()
    }
}

private enum PracticeTab: Int, CaseIterable, Identifiable {
    case email, notes
    var id: Int { rawValue }
    var title: String { self == .email ? "Email" : "Notes" }
}

/// Practice dictating into a mock email and a mock notes page.
private struct PracticeArea: View {
    let model: AppModel
    @State private var tab: PracticeTab = .email
    @State private var to = "priya@example.com"
    @State private var subject = "Proposal"
    @State private var emailBody = ""
    @State private var notes = ""

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.sm) {
            Text("Try it yourself").font(TypeScale.pageTitle).foregroundStyle(Palette.foreground).accessibilityAddTraits(.isHeader)
            Text("Click into the text, hold \(model.preferences.hotkey.shortName.lowercased()), and say the line below. Let go and watch it appear.")
                .font(TypeScale.body).foregroundStyle(Palette.muted).fixedSize(horizontal: false, vertical: true)
            TabStrip(items: PracticeTab.allCases, title: \.title, selection: $tab)

            if tab == .email {
                Text("Say: “Hi Priya, um, thanks for the proposal. I'll review it by Tuesday, no actually Wednesday.”")
                    .font(TypeScale.callout.italic()).foregroundStyle(Palette.foreground)
                VStack(spacing: 0) {
                    MockField(label: "To", text: $to)
                    RowDivider()
                    MockField(label: "Subject", text: $subject)
                    RowDivider()
                    practiceEditor($emailBody, prompt: "Your email").padding(Spacing.xs).frame(minHeight: 140)
                }
                .cardSurface(radius: Radius.overlay)
            } else {
                Text("Say: “Things to do this week, first finish the deck, second call the bank, third book train tickets.”")
                    .font(TypeScale.callout.italic()).foregroundStyle(Palette.foreground)
                VStack(alignment: .leading, spacing: Spacing.xs) {
                    Text("Weekly plan").font(TypeScale.sectionTitle).foregroundStyle(Palette.foreground)
                    practiceEditor($notes, prompt: "Your notes").frame(minHeight: 160)
                }
                .padding(Spacing.md)
                .frame(maxWidth: .infinity, alignment: .leading)
                .cardSurface(radius: Radius.overlay)
            }
        }
    }

    @ViewBuilder private func practiceEditor(_ text: Binding<String>, prompt: String) -> some View {
        if AppRuntime.isDemo {
            Color.clear
        } else {
            TextEditor(text: text).font(TypeScale.body).scrollContentBackground(.hidden).accessibilityLabel(prompt)
        }
    }
}

private struct MockField: View {
    let label: String
    @Binding var text: String
    var body: some View {
        HStack(spacing: Spacing.xs) {
            Text(label).font(TypeScale.callout).foregroundStyle(Palette.muted).frame(width: 60, alignment: .leading)
            TextField("", text: $text).textFieldStyle(.plain).font(TypeScale.callout).foregroundStyle(Palette.foreground)
                .accessibilityLabel(label)
        }
        .padding(.horizontal, Spacing.sm).padding(.vertical, Spacing.xs)
    }
}
