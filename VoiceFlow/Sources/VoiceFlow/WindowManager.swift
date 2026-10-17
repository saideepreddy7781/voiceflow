import AppKit
import Observation
import SwiftUI
import VoiceFlowCore

/// Links shown in Help and the menu.
enum AppLinks {
    static let repository = URL(string: "https://github.com/saideepreddy7781/voiceflow")!
    static var issues: URL { repository.appending(path: "issues/new") }
    static var docs: URL { repository.appending(path: "blob/main/README.md") }
    static let groqKeys = URL(string: "https://console.groq.com/keys")!
    static let groqPrivacy = URL(string: "https://groq.com/privacy-policy/")!
}

/// Opens the Hub, Settings, onboarding, Scratchpad and Help windows from anywhere.
@MainActor
final class WindowManager {
    static let shared = WindowManager()

    weak var model: AppModel?
    private var windows: [String: NSWindow] = [:]
    private var flowBar: FlowBarController?
    let hubSelection = HubSelection()
    let settingsSelection = SettingsSelection()

    func start(model: AppModel) {
        self.model = model
        flowBar = FlowBarController(state: model.pill)
        model.updateFlowBarVisibility()
        model.pill.onStartHandsFree = { [weak model] in model?.controller.startHandsFreeFromBar() }
        model.pill.onConfirm = { [weak model] in
            // Finish first; the silent reset then just clears the keyboard gesture.
            model?.controller.finish()
            model?.resetHotkeySession()
        }
        model.pill.onCancel = { [weak model] in
            model?.controller.cancel()
            model?.resetHotkeySession()
        }
        model.pill.onHideForHour = { [weak model] in model?.hideFlowBarForAnHour() }
        model.pill.onOpenHub = { [weak self] in self?.showHub(.home) }
        model.pill.onPasteLast = { [weak model] in model?.controller.pasteLast() }
        model.pill.onLanguage = { [weak model] code in model?.setSingleLanguage(code) }
        model.pill.currentLanguages = { [weak model] in model?.preferences.languages ?? [] }
        model.pill.onStopMeeting = { [weak model] in model?.notetaker.stop() }
        if model.preferences.onboarded { showHub(.home) } else { showOnboarding() }
    }

    func showHub(_ page: HubPage = .home) {
        guard let model else { return }
        hubSelection.page = page
        show("hub", title: "VoiceFlow", size: NSSize(width: 980, height: 680), minSize: NSSize(width: 820, height: 560)) {
            HubView(model: model, selection: self.hubSelection)
        }
    }

    func showSettings(_ section: SettingsSection = .general) {
        guard let model else { return }
        settingsSelection.section = section
        show("settings", title: "Settings", size: NSSize(width: 760, height: 560), minSize: NSSize(width: 680, height: 480)) {
            SettingsView(model: model, selection: self.settingsSelection)
        }
    }

    func showOnboarding() {
        guard let model else { return }
        show("onboarding", title: "Welcome to VoiceFlow", size: NSSize(width: 760, height: 600), resizable: false) {
            OnboardingView(model: model) { [weak self] in self?.finishOnboarding() }
        }
    }

    func showHelp() {
        guard let model else { return }
        show("help", title: "VoiceFlow Help", size: NSSize(width: 560, height: 520), resizable: false) {
            HelpView(model: model)
        }
    }

    /// ⌥S: shows the Scratchpad, or hides it when it is already in front.
    func toggleScratchpad() {
        guard let model else { return }
        if let window = windows["scratchpad"], window.isVisible, window.isKeyWindow, NSApp.isActive {
            window.orderOut(nil)
            return
        }
        show("scratchpad", title: "Scratchpad", size: NSSize(width: 760, height: 520), minSize: NSSize(width: 560, height: 360)) {
            ScratchpadView(model: model, compact: false)
        }
    }

    func close(_ id: String) {
        windows[id]?.close()
    }

    private func finishOnboarding() {
        close("onboarding")
        windows["onboarding"] = nil
        model?.preferences.onboarded = true
        showHub(.home)
    }

    private func show<Content: View>(_ id: String, title: String, size: NSSize, minSize: NSSize? = nil, resizable: Bool = true,
                                     @ViewBuilder content: () -> Content) {
        if windows[id] == nil {
            var style: NSWindow.StyleMask = [.titled, .closable, .miniaturizable, .fullSizeContentView]
            if resizable { style.insert(.resizable) }
            let window = NSWindow(contentRect: NSRect(origin: .zero, size: size), styleMask: style, backing: .buffered, defer: false)
            window.title = title
            window.titlebarAppearsTransparent = true
            window.isReleasedWhenClosed = false
            // Open on the desktop (Space) you're using now, not the one VoiceFlow's window was last on.
            window.collectionBehavior.insert(.moveToActiveSpace)
            if let minSize { window.minSize = minSize }
            window.contentView = NSHostingView(rootView: content())
            window.center()
            windows[id] = window
        }
        // orderFrontRegardless shows the window even when macOS declines to activate VoiceFlow (an app launched
        // in the background). It opens on your current desktop; if you're in a full-screen app, macOS keeps it
        // on the desktop until you switch there or open VoiceFlow from the Dock or menu bar.
        guard let window = windows[id] else { return }
        window.orderFrontRegardless()
        window.makeKeyAndOrderFront(nil)
        NSApp.activate()
    }
}

/// Live microphone level for the mic test in Settings and onboarding (separate from dictation).
@MainActor
@Observable
final class MicTester {
    private let recorder = AudioRecorder()
    private var timer: Timer?
    private(set) var level: Float = 0
    private(set) var running = false
    private(set) var error: String?

    func start(deviceUID: String) {
        stop()
        do {
            try recorder.start(deviceUID: deviceUID.isEmpty ? nil : deviceUID)
            running = true
            error = nil
            timer = Timer.scheduledTimer(withTimeInterval: 1.0 / 30, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated {
                    guard let self else { return }
                    self.level = self.recorder.currentLevel()
                }
            }
        } catch {
            self.error = "\(error)"
        }
    }

    func stop() {
        timer?.invalidate()
        timer = nil
        if recorder.isRecording { recorder.stop() }
        running = false
        level = 0
    }
}

/// White level bars, like the Flow bar's, driven by a single level value.
struct LevelMeter: View {
    let level: Float
    var bars = 24

    var body: some View {
        HStack(alignment: .center, spacing: 3) {
            ForEach(0..<bars, id: \.self) { i in
                let mid = Float(bars) / 2
                let falloff = 1 - abs(Float(i) - mid) / (mid + 2)
                Capsule()
                    .fill(Color.primary.opacity(0.85))
                    .frame(width: 4, height: max(4, CGFloat(level * falloff) * 44))
            }
        }
        .frame(height: 48)
        .animation(.linear(duration: 0.08), value: level)
    }
}
