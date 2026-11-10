import AppKit
import SwiftUI
import VoiceFlowCore

/// `VoiceFlow --screenshots <dir>` draws real SwiftUI views to PNG, light and dark, at 2x.
///
/// It only ever uses `DemoContent`. The demo `AppModel` has no way to reach this Mac's preferences, history,
/// meetings or Keychain, and has no dictation controller, recorder or event tap to start. Nothing is saved,
/// and the windows sit far outside every display.
@MainActor
enum ScreenshotMode {
    static func requestedDirectory(_ arguments: [String]) -> URL? {
        guard let flag = arguments.firstIndex(of: "--screenshots"), arguments.count > flag + 1 else { return nil }
        return URL(fileURLWithPath: (arguments[flag + 1] as NSString).expandingTildeInPath, isDirectory: true)
    }

    private struct Shot {
        let name: String
        let size: CGSize
        let make: @MainActor () -> AnyView

        init<V: View>(_ name: String, _ size: CGSize, @ViewBuilder view: @escaping @MainActor () -> V) {
            self.name = name
            self.size = size
            self.make = { AnyView(view()) }
        }
    }

    private static let hubSize = CGSize(width: 1180, height: 800)
    private static let settingsSize = CGSize(width: 860, height: 620)

    static func run(to directory: URL) -> Never {
        AppRuntime.isDemo = true
        let app = NSApplication.shared
        app.setActivationPolicy(.prohibited)
        app.finishLaunching()
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            // `--only <prefix>` renders a subset while iterating on one page.
            let arguments = CommandLine.arguments
            let only = arguments.firstIndex(of: "--only").flatMap { arguments.count > $0 + 1 ? arguments[$0 + 1] : nil }
            for shot in shots where only.map({ shot.name.hasPrefix($0) }) ?? true {
                for dark in [false, true] {
                    try render(shot, dark: dark, to: directory.appending(path: "\(shot.name)-\(dark ? "dark" : "light").png"))
                }
            }
        } catch {
            FileHandle.standardError.write(Data("Could not write screenshots: \(error)\n".utf8))
            exit(1)
        }
        print("Wrote screenshots to \(directory.path)")
        exit(0)
    }

    private static func hub(_ page: HubPage, _ content: DemoContent) -> AnyView {
        let selection = HubSelection()
        selection.page = page
        return AnyView(HubView(model: AppModel(demo: content), selection: selection))
    }

    private static func meetings(_ content: DemoContent, tab: Int = 0) -> AnyView {
        let model = AppModel(demo: content)
        let selection = HubSelection()
        selection.page = .meetings
        return AnyView(HubView(model: model, selection: selection, meetingsTab: tab))
    }

    private static func settings(_ section: SettingsSection, _ content: DemoContent = .populated()) -> AnyView {
        let selection = SettingsSelection()
        selection.section = section
        return AnyView(SettingsView(model: AppModel(demo: content), selection: selection))
    }

    private static func onboarding(_ step: Int, _ content: DemoContent = .populated()) -> AnyView {
        AnyView(OnboardingView(model: AppModel(demo: content), initialStep: step) {})
    }

    private static var shots: [Shot] {
        var list = [
            Shot("home", hubSize) { hub(.home, .populated()) },
            Shot("home-update", hubSize) { hub(.home, .updateAvailable()) },
            Shot("home-setup", hubSize) { hub(.home, .needsSetup()) },
            Shot("home-empty", hubSize) { hub(.home, .empty()) },
            Shot("meetings", hubSize) { meetings(.populated()) },
            Shot("meetings-transcript", hubSize) { meetings(.populated(), tab: 1) },
            Shot("meetings-live", hubSize) { meetings(.liveMeeting()) },
            Shot("meetings-live-mic-problem", hubSize) { meetings(.liveMeetingMicProblem()) },
            Shot("meetings-empty", hubSize) { meetings(.empty()) },
            Shot("insights", hubSize) { hub(.insights, .populated()) },
            Shot("insights-empty", hubSize) { hub(.insights, .empty()) },
            Shot("dictionary", hubSize) { hub(.dictionary, .populated()) },
            Shot("dictionary-empty", hubSize) { hub(.dictionary, .empty()) },
            Shot("snippets", hubSize) { hub(.snippets, .populated()) },
            Shot("snippets-empty", hubSize) { hub(.snippets, .empty()) },
            Shot("style", hubSize) { hub(.style, .populated()) },
            Shot("scratchpad", hubSize) { hub(.scratchpad, .populated()) },
            Shot("scratchpad-empty", hubSize) { hub(.scratchpad, .empty()) },
            Shot("help", CGSize(width: 620, height: 600)) { HelpView(model: AppModel(demo: .populated())) },
            Shot("flowbar", CGSize(width: 880, height: 420)) { FlowBarGallery() },
            Shot("menu", CGSize(width: 280, height: 300)) { MenuPreview(model: AppModel(demo: .populated())) },
        ]
        for section in SettingsSection.allCases {
            list.append(Shot("settings-\(section.rawValue)", settingsSize) { settings(section) })
        }
        list.append(Shot("settings-about-update", settingsSize) { settings(.about, .updateAvailable()) })
        let names = ["connect", "permissions", "microphone", "shortcut", "languages", "try-it", "difference", "done"]
        for (step, name) in names.enumerated() {
            list.append(Shot("onboarding-\(step + 1)-\(name)", CGSize(width: 760, height: 600)) { onboarding(step) })
        }
        list.append(Shot("onboarding-2-permissions-needed", CGSize(width: 760, height: 600)) { onboarding(1, .needsSetup()) })
        return list
    }

    private final class OffscreenWindow: NSWindow {
        // Keep it outside every display.
        override func constrainFrameRect(_ frameRect: NSRect, to screen: NSScreen?) -> NSRect { frameRect }
        override var canBecomeKey: Bool { false }
    }

    private static func render(_ shot: Shot, dark: Bool, to url: URL) throws {
        let appearance = NSAppearance(named: dark ? .darkAqua : .aqua)!
        NSApp.appearance = appearance

        let root = shot.make()
            .frame(width: shot.size.width, height: shot.size.height)
            .environment(\.locale, Locale(identifier: "en_US"))
            .preferredColorScheme(dark ? .dark : .light)
        let hosting = NSHostingView(rootView: root)
        hosting.appearance = appearance

        let origin = CGPoint(x: -30_000, y: -30_000)
        let window = OffscreenWindow(contentRect: CGRect(origin: origin, size: shot.size),
                                     styleMask: [.borderless], backing: .buffered, defer: false)
        window.appearance = appearance
        window.contentView = hosting
        window.setFrameOrigin(origin)
        window.orderFrontRegardless()

        // SwiftUI lays out on the main run loop; give it a few turns.
        RunLoop.main.run(until: Date().addingTimeInterval(0.25))

        let bounds = hosting.bounds
        let scale = 2
        guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: Int(bounds.width) * scale, pixelsHigh: Int(bounds.height) * scale,
                                         bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                         colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0) else {
            throw CocoaError(.fileWriteUnknown)
        }
        rep.size = bounds.size
        hosting.cacheDisplay(in: bounds, to: rep)
        window.orderOut(nil)
        window.close()
        guard let png = rep.representation(using: .png, properties: [:]) else { throw CocoaError(.fileWriteUnknown) }
        try png.write(to: url)
    }
}

/// The Flow bar in each state on a wallpaper-like background, using the real `FlowBarView`.
private struct FlowBarGallery: View {
    private func state(_ configure: (PillState) -> Void) -> PillState {
        let state = PillState()
        state.levels = [0.2, 0.42, 0.68, 0.5, 0.82, 1, 0.6, 0.74, 0.4, 0.55, 0.22]
        configure(state)
        return state
    }

    private func tile(_ title: String, _ state: PillState, width: CGFloat = 360, height: CGFloat = 54) -> some View {
        VStack(alignment: .leading, spacing: Spacing.xs) {
            Text(title).font(TypeScale.callout.weight(.medium)).foregroundStyle(.white)
            FlowBarView(state: state)
                .frame(width: width, height: height)
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: Spacing.lg) {
            HStack(alignment: .top, spacing: Spacing.xl) {
                tile("Listening", state { $0.phase = .listening })
                tile("Hands-free", state { $0.phase = .handsFree })
            }
            HStack(alignment: .top, spacing: Spacing.xl) {
                tile("Command Mode", state { $0.phase = .command })
                tile("Processing", state { $0.phase = .processing })
            }
            HStack(alignment: .top, spacing: Spacing.xl) {
                tile("Toast while listening", state {
                    $0.phase = .listening
                    $0.show("Pasted into Notes", detail: "Copied to the clipboard too.",
                            action: .init(title: "Undo") {}, seconds: 600)
                }, height: 96)
                tile("Meeting chip", state { $0.meetingSince = Date().addingTimeInterval(-720) })
            }
        }
        .padding(Spacing.xxl)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(LinearGradient(colors: [Color(red: 0.2, green: 0.36, blue: 0.5), Color(red: 0.74, green: 0.4, blue: 0.5)],
                                   startPoint: .topLeading, endPoint: .bottomTrailing))
    }
}

/// The menu-bar menu is a native NSMenu that can't be drawn offscreen, so this draws its items.
private struct MenuPreview: View {
    let model: AppModel

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            MenuContent(model: model).buttonStyle(MenuRowStyle())
        }
        .padding(Spacing.xs)
        .frame(width: 240, alignment: .leading)
        .cardSurface(radius: Radius.overlay)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Palette.window)
    }

    private struct MenuRowStyle: ButtonStyle {
        func makeBody(configuration: Configuration) -> some View {
            configuration.label.font(TypeScale.callout).foregroundStyle(Palette.foreground)
                .frame(maxWidth: .infinity, minHeight: 26, alignment: .leading).padding(.horizontal, Spacing.xs)
        }
    }
}
