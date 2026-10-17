import AppKit
import SwiftUI

/// The menu-bar menu: kept short, like the rest of the app's chrome.
struct MenuContent: View {
    let model: AppModel

    var body: some View {
        Button("Open VoiceFlow") { WindowManager.shared.showHub(.home) }
        if model.notetaker.isRecording {
            Button("Stop meeting notes") { model.notetaker.stop() }
        } else {
            Button("Take meeting notes") { model.notetaker.start() }
                .disabled(model.notetaker.state != .idle)
        }
        if !model.setupProblems.isEmpty {
            Button("Finish setup…") { WindowManager.shared.showOnboarding() }
        }
        Divider()
        Button("Help center") { WindowManager.shared.showHelp() }
        Button("Report an issue") { NSWorkspace.shared.open(AppLinks.issues) }
        Divider()
        Button("Settings…") { WindowManager.shared.showSettings() }
            .keyboardShortcut(",")
        Button("Quit VoiceFlow") { NSApp.terminate(nil) }
            .keyboardShortcut("q")
            .onAppear { model.refreshSystemState() }
    }
}
