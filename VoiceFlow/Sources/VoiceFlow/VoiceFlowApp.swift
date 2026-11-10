import AppKit
import SwiftUI

/// `--screenshots <dir>` renders the UI with demo data and quits. Everything else starts the real app.
@main
@MainActor
enum Launch {
    static func main() {
        if let directory = ScreenshotMode.requestedDirectory(CommandLine.arguments) {
            ScreenshotMode.run(to: directory)
        }
        VoiceFlowApp.main()
    }
}

struct VoiceFlowApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    @State private var model: AppModel

    init() {
        Self.quitIfAlreadyRunning()
        let model = AppModel()
        _model = State(initialValue: model)
        AppDelegate.model = model
    }

    var body: some Scene {
        MenuBarExtra {
            MenuContent(model: model)
        } label: {
            switch model.menuBarState {
            case .listening: Image(systemName: "waveform.circle.fill")
            case .attention, .recordingMeeting: Image(nsImage: MenuBarIcon.image(badge: .dot))
            case .ready: Image(nsImage: MenuBarIcon.image())
            }
        }
    }

    /// Only one VoiceFlow may run: two copies would both listen for the hotkey and type twice.
    /// A second launch asks the running copy to show its window, then quits.
    private static func quitIfAlreadyRunning() {
        guard let id = Bundle.main.bundleIdentifier else { return }   // `swift run` has no bundle id
        let me = ProcessInfo.processInfo.processIdentifier
        guard let running = NSRunningApplication.runningApplications(withBundleIdentifier: id)
            .first(where: { $0.processIdentifier != me }) else { return }
        DistributedNotificationCenter.default().postNotificationName(AppDelegate.showHubNotification, object: nil,
                                                                     userInfo: nil, deliverImmediately: true)
        running.activate()
        exit(0)
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    static let showHubNotification = Notification.Name("com.saideep.voiceflow.showHub")
    @MainActor static var model: AppModel?

    /// Dock or menu-bar-only is decided before launch finishes: switching later, right before showing a
    /// window, left the window created but never shown.
    func applicationWillFinishLaunching(_ notification: Notification) {
        MainActor.assumeIsolated { Self.model?.applyDockVisibility() }
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        MainActor.assumeIsolated {
            guard let model = Self.model else { return }
            WindowManager.shared.start(model: model)
        }
        DistributedNotificationCenter.default().addObserver(forName: Self.showHubNotification, object: nil, queue: .main) { _ in
            MainActor.assumeIsolated { WindowManager.shared.showHub() }
        }
    }

    /// Clicking the Dock icon opens the Hub.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        MainActor.assumeIsolated { WindowManager.shared.showHub() }
        return true
    }

    /// Quitting (or logging out) during a meeting: save the meeting before exiting.
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        let recording = MainActor.assumeIsolated { Self.model?.notetaker.isRecording == true }
        guard recording else { return .terminateNow }
        Task { @MainActor in
            await Self.model?.notetaker.finishRecording(writeNotes: false)
            Self.model?.controller.shutdown()
            sender.reply(toApplicationShouldTerminate: true)
        }
        return .terminateLater
    }

    /// Closing the last window keeps VoiceFlow running in the menu bar.
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
}
