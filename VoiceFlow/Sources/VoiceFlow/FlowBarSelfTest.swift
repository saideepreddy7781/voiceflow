import AppKit
import CoreGraphics
import SwiftUI
import VoiceFlowCore

/// `VoiceFlow --capsule-selftest <out.json>` checks the Flow bar on this Mac's real window server, then quits.
///
/// It shows the bar in each phase over a test window of its own, and checks that points just outside the bar
/// and in its transparent corners reach that window while the bar's buttons keep their clicks. It also drags
/// the bar, checks the saved spot, snapping and clamping, and captures the bar over a busy background.
/// It reads and writes no preferences, history or Keychain items: its `PillState` lives in memory only.
/// Synthetic clicks need permission to post events; without it the routing is checked by asking the window
/// server which window is under each point, and the report says which method ran.
@MainActor
enum FlowBarSelfTest {
    static func requestedOutput(_ arguments: [String]) -> URL? {
        guard let flag = arguments.firstIndex(of: "--capsule-selftest"), arguments.count > flag + 1 else { return nil }
        return URL(fileURLWithPath: (arguments[flag + 1] as NSString).expandingTildeInPath)
    }

    static func run(to output: URL) -> Never {
        AppRuntime.isDemo = true
        let app = NSApplication.shared
        app.setActivationPolicy(.accessory)
        let runner = Runner(output: output)
        Task { @MainActor in
            await runner.run()
            runner.finish()
        }
        // Never leave a stuck test on screen.
        Timer.scheduledTimer(withTimeInterval: 240, repeats: false) { _ in
            MainActor.assumeIsolated {
                runner.fail("watchdog: the test took longer than 240 seconds")
                runner.finish()
            }
        }
        app.run()
        exit(0)
    }
}

// MARK: Report

private struct Box: Codable {
    var x: Double, y: Double, width: Double, height: Double, radius: Double?
    init(_ r: CGRect, radius: CGFloat? = nil) {
        x = r.minX; y = r.minY; width = r.width; height = r.height
        self.radius = radius.map(Double.init)
    }
}

private struct Probe: Codable {
    var label: String
    var x: Double, y: Double
    var expected: String
    var got: String
    var pass: Bool
}

private struct PixelCheck: Codable {
    var method: String
    var file: String?
    /// Highest alpha (0-255) within 2 pixels of the window's edge. 0 means nothing is drawn at the edge.
    var edgeMaxAlpha: Int?
    /// Pixels with alpha above 8 farther than 10 points from the drawn shape.
    var strayPixels: Int?
    var pass: Bool
    var note: String?
}

private struct PhaseReport: Codable {
    var name: String
    var bar: Box?
    var barWindow: Box?
    var toast: Box?
    var windowServerRouting: [Probe] = []
    var syntheticEvents: [Probe] = []
    var pixels: [PixelCheck] = []
    var regionShot: String?
    var notes: [String] = []
}

private struct DragCheck: Codable {
    var label: String
    var expected: Box?
    var got: Box?
    var pass: Bool
    var note: String?
}

private struct Report: Codable {
    var started = Date()
    var macOS = ProcessInfo.processInfo.operatingSystemVersionString
    var displays: [Box] = []
    var arrangement = ""
    var postEventPreflight = false
    var postedEventsMoveThePointer = false
    var screenCapturePreflight = false
    var screenLocked = false
    /// Time for each routing probe to show the expected window: the hit test's latency on this Mac.
    var routingLatencyMs: [Int] = []
    /// Posted events not sent because something other than a test window was under the point.
    var skippedForSafety: [String] = []
    var phases: [PhaseReport] = []
    var drag: [DragCheck] = []
    var nonActivating: [Probe] = []
    var failures: [String] = []
    var passed = false
}

// MARK: Backdrop

/// The test window behind the bar: a busy pattern, and a log of the mouse events that reach it.
private final class BackdropView: NSView {
    struct Hit { let kind: String; let point: CGPoint }
    var hits: [Hit] = []

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func mouseDown(with event: NSEvent) { hits.append(Hit(kind: "click", point: NSEvent.mouseLocation)) }
    override func rightMouseDown(with event: NSEvent) { hits.append(Hit(kind: "right-click", point: NSEvent.mouseLocation)) }
    override func scrollWheel(with event: NSEvent) { hits.append(Hit(kind: "scroll", point: NSEvent.mouseLocation)) }

    override func draw(_ dirtyRect: NSRect) {
        let colors: [NSColor] = [.systemYellow, .systemPink, .systemTeal, .white, .systemOrange, .systemGreen, .black, .systemPurple]
        let stripe: CGFloat = 14
        var i = 0
        var x: CGFloat = -bounds.height
        while x < bounds.width {
            colors[i % colors.count].setFill()
            let path = NSBezierPath()
            path.move(to: NSPoint(x: x, y: 0))
            path.line(to: NSPoint(x: x + stripe, y: 0))
            path.line(to: NSPoint(x: x + stripe + bounds.height, y: bounds.height))
            path.line(to: NSPoint(x: x + bounds.height, y: bounds.height))
            path.close()
            path.fill()
            x += stripe
            i += 1
        }
        let text = "VoiceFlow Flow bar self-test. Clicks outside the bar must land here."
        let attributes: [NSAttributedString.Key: Any] = [.font: NSFont.boldSystemFont(ofSize: 13), .foregroundColor: NSColor.black,
                                                         .backgroundColor: NSColor.white.withAlphaComponent(0.8)]
        for row in stride(from: CGFloat(10), to: bounds.height, by: 46) {
            (text as NSString).draw(at: NSPoint(x: 12, y: row), withAttributes: attributes)
        }
    }
}

// MARK: Runner

@MainActor
private final class Runner {
    let output: URL
    let directory: URL
    var report = Report()
    let state = PillState()
    var controller: FlowBarController!
    var backdrop: NSWindow!
    var backdropView = BackdropView()
    var taps: [String] = []
    var finished = false
    let originalPointer = NSEvent.mouseLocation
    let previousApp = NSWorkspace.shared.frontmostApplication
    var pointerMoved = false
    var display: DisplayArea!
    var testCentre = CGPoint.zero

    init(output: URL) {
        self.output = output
        directory = output.deletingLastPathComponent()
    }

    func fail(_ message: String) { report.failures.append(message) }

    func pause(_ ms: Int) async { try? await Task.sleep(for: .milliseconds(ms)) }

    // MARK: Run

    func run() async {
        try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let displays = FlowBarController.displays()
        guard let main = displays.first else {
            fail("no display")
            return
        }
        display = main
        report.displays = displays.map { Box($0.frame) }
        report.arrangement = FlowBarController.arrangement(displays)
        report.postEventPreflight = CGPreflightPostEventAccess()
        report.screenCapturePreflight = CGPreflightScreenCaptureAccess()
        // The lock screen covers every window. Routing is then asked for beneath the lock screen's own windows,
        // and each probe says so; region screenshots and posted events can't work until it's unlocked.
        let session = CGSessionCopyCurrentDictionary() as? [String: Any]
        report.screenLocked = (session?["CGSSessionScreenIsLocked"] as? Bool) ?? false

        // Away from the bottom centre, where a running VoiceFlow may show its own bar, and clear of any other
        // app's window that floats above ordinary windows (an alert, a palette).
        testCentre = clearSpot(in: main.visible, size: backdropSize)
        var spots = FlowBarSpots()
        spots.set(testCentre, for: report.arrangement)
        state.spots = spots
        state.levels = [0.2, 0.42, 0.68, 0.5, 0.82, 1, 0.6, 0.74, 0.4, 0.55, 0.22]
        state.onCancel = { [weak self] in self?.taps.append("cancel") }
        state.onConfirm = { [weak self] in self?.taps.append("confirm") }
        state.onStopMeeting = { [weak self] in self?.taps.append("stop") }
        state.onStartHandsFree = { [weak self] in self?.taps.append("resting") }

        backdrop = NSWindow(contentRect: FlowBarGeometry.rect(centre: testCentre, size: backdropSize),
                            styleMask: [.borderless], backing: .buffered, defer: false)
        backdrop.level = .floating
        backdrop.isReleasedWhenClosed = false
        backdrop.acceptsMouseMovedEvents = true
        backdrop.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        backdrop.contentView = backdropView
        backdrop.orderFrontRegardless()

        controller = FlowBarController(state: state)
        await pause(300)
        await checkPostedEvents()

        await phase("listening", buttons: []) { $0.set(.listening) }
        await phase("processing", buttons: []) { $0.set(.processing) }
        await phase("hands-free", buttons: handsFreeButtons) { $0.set(.handsFree) }
        await phase("command", buttons: handsFreeButtons) { $0.set(.command) }
        await phase("meeting-chip", buttons: [("stop", "stop", { b in CGPoint(x: b.maxX - 19, y: b.midY) })]) {
            $0.set(.idle)
            $0.meetingSince = Date().addingTimeInterval(-75)
        }
        await phase("resting", buttons: [("bubble", "resting", { b in CGPoint(x: b.midX, y: b.midY) })]) {
            $0.meetingSince = nil
            $0.showWhenIdle = true
        }
        await phase("toast", buttons: handsFreeButtons) {
            $0.showWhenIdle = false
            $0.set(.handsFree)
            $0.show("Pasted into Notes", detail: "Copied to the clipboard too.", action: .init(title: "Undo") { [weak self] in
                self?.taps.append("toast-action")
            }, seconds: 600)
        }
        await nonActivatingCheck()
        await dragChecks()
        await toastNearTopCheck()
    }

    private let backdropSize = CGSize(width: 480, height: 300)

    /// Rectangles of other processes' windows above the normal level, in AppKit screen coordinates.
    private func floatingWindows() -> [CGRect] {
        let top = FlowBarController.displays().first?.frame.maxY ?? 0
        let me = ProcessInfo.processInfo.processIdentifier
        let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly], kCGNullWindowID) as? [[String: Any]] ?? []
        return list.compactMap { info in
            guard (info[kCGWindowLayer as String] as? Int ?? 0) > 0,
                  (info[kCGWindowOwnerPID as String] as? Int32) != me,
                  let bounds = info[kCGWindowBounds as String] as? NSDictionary,
                  let rect = CGRect(dictionaryRepresentation: bounds) else { return nil }
            return CGRect(x: rect.minX, y: top - rect.maxY, width: rect.width, height: rect.height)
        }
    }

    private func clearSpot(in visible: CGRect, size: CGSize) -> CGPoint {
        let others = floatingWindows()
        let xs = [visible.midX, visible.minX + size.width / 2 + 20, visible.maxX - size.width / 2 - 20]
        let ys = [visible.midY + 60, visible.maxY - size.height / 2 - 20, visible.minY + size.height / 2 + 80]
        for y in ys {
            for x in xs {
                let rect = FlowBarGeometry.rect(centre: CGPoint(x: x, y: y), size: size)
                if visible.contains(rect), !others.contains(where: { $0.intersects(rect) }) { return CGPoint(x: x, y: y) }
            }
        }
        report.failures.append("no spot on screen is clear of other apps' floating windows; results may be wrong")
        return CGPoint(x: visible.midX, y: visible.midY + 60)
    }

    private var handsFreeButtons: [(String, String, (CGRect) -> CGPoint)] {
        [("cancel button", "cancel", { b in CGPoint(x: b.minX + 16, y: b.midY) }),
         ("finish button", "confirm", { b in CGPoint(x: b.maxX - 16, y: b.midY) })]
    }

    // MARK: Phases

    private func phase(_ name: String, buttons: [(String, String, (CGRect) -> CGPoint)], configure: (PillState) -> Void) async {
        configure(state)
        await pause(700)
        var phase = PhaseReport(name: name)
        guard let bar = controller.barShape else {
            fail("\(name): the bar is not on screen")
            report.phases.append(phase)
            return
        }
        phase.bar = Box(bar.rect, radius: bar.radius)
        phase.barWindow = Box(controller.barPanel.frame)
        let toast = controller.toastShape
        if let toast { phase.toast = Box(toast.rect, radius: toast.radius) }

        var shapes: [(String, RoundedBox, NSWindow)] = [("bar", bar, controller.barPanel)]
        if let toast { shapes.append(("toast", toast, controller.toastPanel)) }
        var outside: [(String, CGPoint)] = []
        for (label, shape, window) in shapes {
            outside += probesOutside(label, shape, window: window.frame)
        }
        outside = outside.filter { point in !shapes.contains { $0.1.contains(point.1) } }
        var inside: [(String, CGPoint, NSWindow, String?)] = []
        // The resting bubble's middle is its own button.
        if !buttons.contains(where: { $0.0 == "bubble" }) {
            inside.append(("bar middle", CGPoint(x: bar.rect.midX, y: bar.rect.midY), controller.barPanel, nil))
        }
        for (label, tap, place) in buttons { inside.append((label, place(bar.rect), controller.barPanel, tap)) }
        if let toast {
            inside.append(("toast text", CGPoint(x: toast.rect.minX + 30, y: toast.rect.midY), controller.toastPanel, nil))
            inside.append(("toast Undo button", CGPoint(x: toast.rect.maxX - 43, y: toast.rect.midY), controller.toastPanel, "toast-action"))
        }

        // 1. Window server: which window is under each point once the hit test has run there.
        for (label, point) in outside {
            phase.windowServerRouting.append(await route(label, point, expect: backdrop))
        }
        for (label, point, window, _) in inside {
            phase.windowServerRouting.append(await route(label, point, expect: window))
        }
        controller.pointerOverride = nil
        await pause(300)

        // 2. Real synthetic events, when this Mac lets the test post them. Each one is posted only after
        //    checking that a test window is what's under the point, so nothing lands in another app.
        if report.postedEventsMoveThePointer {
            for (index, (label, point)) in outside.enumerated() {
                phase.syntheticEvents.append(await click(label, point, expectBackdrop: true, tap: nil))
                if index == 0 {
                    phase.syntheticEvents.append(await scroll(label, point))
                }
            }
            for (label, point, _, tap) in inside {
                phase.syntheticEvents.append(await click(label, point, expectBackdrop: false, tap: tap))
            }
            phase.syntheticEvents.append(await hover(bar))
        } else {
            phase.notes.append("synthetic events skipped: this process may not post events")
        }

        // 3. Pixels: nothing drawn at the window's edge or away from the shape. Captured last, so a capture
        //    prompt from macOS can never sit under the synthetic clicks above.
        // Let a hover animation finish, then measure against the shape drawn now.
        await pause(400)
        let settled = controller.barShape ?? bar
        phase.pixels.append(viewRender(name, bar: settled))
        phase.pixels.append(await windowCapture(name, bar: settled))
        phase.regionShot = await regionShot(name, around: shapes.map(\.1.rect))

        for probe in phase.windowServerRouting + phase.syntheticEvents where !probe.pass {
            fail("\(name): \(probe.label): expected \(probe.expected), got \(probe.got)")
        }
        for check in phase.pixels where !check.pass {
            fail("\(name): pixels (\(check.method)): \(check.note ?? "edge alpha \(check.edgeMaxAlpha ?? -1), stray \(check.strayPixels ?? -1)")")
        }
        report.phases.append(phase)
    }

    private func probesOutside(_ label: String, _ shape: RoundedBox, window: CGRect) -> [(String, CGPoint)] {
        let r = shape.rect
        let inset: CGFloat = 1.5
        return [
            ("\(label): 3 pt left", CGPoint(x: r.minX - 3, y: r.midY)),
            ("\(label): 3 pt right", CGPoint(x: r.maxX + 3, y: r.midY)),
            ("\(label): 3 pt above", CGPoint(x: r.midX, y: r.maxY + 3)),
            ("\(label): 3 pt below", CGPoint(x: r.midX, y: r.minY - 3)),
            ("\(label): bottom-left corner", CGPoint(x: r.minX + inset, y: r.minY + inset)),
            ("\(label): bottom-right corner", CGPoint(x: r.maxX - inset, y: r.minY + inset)),
            ("\(label): top-left corner", CGPoint(x: r.minX + inset, y: r.maxY - inset)),
            ("\(label): top-right corner", CGPoint(x: r.maxX - inset, y: r.maxY - inset)),
            ("\(label): window margin, bottom left", CGPoint(x: window.minX + 3, y: window.minY + 3)),
            ("\(label): window margin, top right", CGPoint(x: window.maxX - 3, y: window.maxY - 3)),
        ]
    }

    private func name(of number: Int) -> String {
        switch number {
        case backdrop.windowNumber: "test window behind"
        case controller.barPanel.windowNumber: "bar window"
        case controller.toastPanel.windowNumber: "toast window"
        default: "another window (\(owner(of: number)))"
        }
    }

    private func isLockScreen(_ number: Int) -> Bool {
        guard report.screenLocked else { return false }
        let info = (CGWindowListCopyWindowInfo([.optionIncludingWindow], CGWindowID(number)) as? [[String: Any]])?.first
        return info?[kCGWindowOwnerName as String] as? String == "loginwindow"
    }

    private func owner(of number: Int) -> String {
        let info = (CGWindowListCopyWindowInfo([.optionIncludingWindow], CGWindowID(number)) as? [[String: Any]])?.first
        return "\(info?[kCGWindowOwnerName as String] as? String ?? "?") #\(number), level \(info?[kCGWindowLayer as String] as? Int ?? 0)"
    }

    private func route(_ label: String, _ point: CGPoint, expect window: NSWindow) async -> Probe {
        controller.pointerOverride = point
        // The window server applies a window's change to `ignoresMouseEvents` on its own schedule: poll briefly
        // and report how long it took.
        let started = Date()
        var got = 0
        var note = ""
        repeat {
            await pause(10)
            got = NSWindow.windowNumber(at: point, belowWindowWithWindowNumber: 0)
            for _ in 0..<4 where isLockScreen(got) {
                got = NSWindow.windowNumber(at: point, belowWindowWithWindowNumber: got)
                note = " (beneath the lock screen)"
            }
        } while got != window.windowNumber && Date().timeIntervalSince(started) < 0.5
        let ms = Int(Date().timeIntervalSince(started) * 1000)
        report.routingLatencyMs.append(ms)
        return Probe(label: label, x: point.x, y: point.y, expected: name(of: window.windowNumber),
                     got: name(of: got) + note + " after \(ms) ms", pass: got == window.windowNumber)
    }

    // MARK: Synthetic events

    private func cg(_ p: CGPoint) -> CGPoint {
        CGPoint(x: p.x, y: (FlowBarController.displays().first?.frame.maxY ?? 0) - p.y)
    }

    private func post(_ type: CGEventType, _ p: CGPoint, button: CGMouseButton = .left) {
        let source = CGEventSource(stateID: .hidSystemState)
        CGEvent(mouseEventSource: source, mouseType: type, mouseCursorPosition: cg(p), mouseButton: button)?.post(tap: .cghidEventTap)
        pointerMoved = true
    }

    /// Posts one move and sees whether the pointer went there: the real answer to "may this process post events".
    private func checkPostedEvents() async {
        let target = CGPoint(x: testCentre.x - 300, y: testCentre.y - 150)
        post(.mouseMoved, target)
        await pause(150)
        let now = NSEvent.mouseLocation
        report.postedEventsMoveThePointer = abs(now.x - target.x) < 2 && abs(now.y - target.y) < 2
    }

    /// True when one of this test's windows is the topmost window at `point`. Checked before every posted
    /// press, so a click can never land on another app's window or a system alert.
    private func safe(_ point: CGPoint, _ label: String) -> Bool {
        let top = NSWindow.windowNumber(at: point, belowWindowWithWindowNumber: 0)
        let ours = [backdrop.windowNumber, controller.barPanel.windowNumber, controller.toastPanel.windowNumber]
        if ours.contains(top) { return true }
        report.skippedForSafety.append("\(label): \(owner(of: top)) was on top")
        return false
    }

    private func click(_ label: String, _ point: CGPoint, expectBackdrop: Bool, tap: String?) async -> Probe {
        let before = backdropView.hits.count
        let tapsBefore = taps.count
        post(.mouseMoved, point)
        await pause(90)
        guard safe(point, "click \(label)") else {
            return Probe(label: "click \(label)", x: point.x, y: point.y, expected: "a test window on top", got: "another window; not clicked", pass: false)
        }
        post(.leftMouseDown, point)
        await pause(40)
        post(.leftMouseUp, point)
        await pause(160)
        let landed = backdropView.hits[before...].contains { $0.kind == "click" && abs($0.point.x - point.x) < 2 && abs($0.point.y - point.y) < 2 }
        let newTaps = Array(taps[tapsBefore...])
        if expectBackdrop {
            return Probe(label: "click \(label)", x: point.x, y: point.y, expected: "test window behind",
                         got: landed ? "test window behind" : (newTaps.isEmpty ? "nothing" : "bar: \(newTaps)"), pass: landed && newTaps.isEmpty)
        }
        let expected = tap.map { "bar action \($0)" } ?? "bar, no action"
        let got = landed ? "test window behind" : (newTaps.isEmpty ? "bar, no action" : "bar action \(newTaps.joined(separator: ","))")
        return Probe(label: "click \(label)", x: point.x, y: point.y, expected: expected, got: got, pass: !landed && got == expected)
    }

    private func scroll(_ label: String, _ point: CGPoint) async -> Probe {
        let before = backdropView.hits.count
        post(.mouseMoved, point)
        await pause(90)
        guard safe(point, "scroll \(label)") else {
            return Probe(label: "scroll \(label)", x: point.x, y: point.y, expected: "a test window on top", got: "another window; not scrolled", pass: false)
        }
        let source = CGEventSource(stateID: .hidSystemState)
        CGEvent(scrollWheelEvent2Source: source, units: .line, wheelCount: 1, wheel1: -2, wheel2: 0, wheel3: 0)?.post(tap: .cghidEventTap)
        await pause(160)
        let landed = backdropView.hits[before...].contains { $0.kind == "scroll" }
        return Probe(label: "scroll \(label)", x: point.x, y: point.y, expected: "test window behind",
                     got: landed ? "test window behind" : "nothing", pass: landed)
    }

    /// Moving onto the bar must make it take the mouse; moving off must give the mouse back.
    private func hover(_ bar: RoundedBox) async -> Probe {
        let inside = CGPoint(x: bar.rect.midX, y: bar.rect.midY)
        // Far enough left to be clear of the resting bubble once it has grown under the pointer.
        let outside = CGPoint(x: bar.rect.minX - 120, y: bar.rect.midY)
        post(.mouseMoved, inside)
        await pause(120)
        let takesInside = !controller.barPanel.ignoresMouseEvents && state.pointerOverBar
        post(.mouseMoved, outside)
        await pause(120)
        let releasesOutside = controller.barPanel.ignoresMouseEvents && !state.pointerOverBar
        return Probe(label: "pointer monitor: over the bar, then 120 pt left of it", x: inside.x, y: inside.y,
                     expected: "takes mouse, then ignores it", got: "\(takesInside ? "takes mouse" : "ignores it"), then \(releasesOutside ? "ignores it" : "takes mouse")",
                     pass: takesInside && releasesOutside)
    }

    /// A click on a bar button must not make VoiceFlow the active app or the bar a key window.
    private func nonActivatingCheck() async {
        guard report.postedEventsMoveThePointer, let bar = controller.barShape else { return }
        state.dismissMessage()
        await pause(300)
        // Give the focus back to the app that was in front before the test; the click must not take it.
        (previousApp ?? NSRunningApplication.runningApplications(withBundleIdentifier: "com.apple.finder").first)?.activate()
        await pause(600)
        let wasActive = NSApp.isActive
        let point = CGPoint(x: bar.rect.maxX - 16, y: bar.rect.midY)
        let tapsBefore = taps.count
        post(.mouseMoved, point)
        await pause(90)
        guard safe(point, "non-activating check") else { return }
        post(.leftMouseDown, point)
        await pause(40)
        post(.leftMouseUp, point)
        await pause(250)
        let tapped = taps.count > tapsBefore
        let ok = tapped && !NSApp.isActive && !controller.barPanel.isKeyWindow
        report.nonActivating.append(Probe(label: "finish button while another app is active", x: point.x, y: point.y,
                                          expected: "action runs, VoiceFlow stays inactive",
                                          got: "action \(tapped ? "runs" : "missing"), VoiceFlow \(NSApp.isActive ? "became active" : "stays inactive")\(wasActive ? " (was active before the click)" : "")",
                                          pass: ok))
        if wasActive {
            report.nonActivating[report.nonActivating.count - 1].got += "; inconclusive, another app could not be put in front"
        } else if let probe = report.nonActivating.last, !probe.pass {
            fail("non-activating: \(probe.got)")
        }
    }

    // MARK: Drag

    private func spot() -> CGPoint? { state.spots.centre(for: report.arrangement) }

    private func check(_ label: String, expected: CGPoint, tolerance: CGFloat = 0.5, note: String? = nil) {
        let got = spot()
        let pass = got.map { abs($0.x - expected.x) <= tolerance && abs($0.y - expected.y) <= tolerance } ?? false
        let size = CGSize(width: 1, height: 1)
        report.drag.append(DragCheck(label: label, expected: Box(FlowBarGeometry.rect(centre: expected, size: size)),
                                     got: got.map { Box(FlowBarGeometry.rect(centre: $0, size: size)) }, pass: pass, note: note))
        if !pass { fail("drag: \(label): expected \(expected), got \(got.map { "\($0)" } ?? "no saved spot")") }
    }

    private func drag(by delta: CGPoint, steps: Int = 8) async {
        guard let start = controller.centre else { return }
        controller.pointerDragged(to: start, ended: false)
        for i in 1...steps {
            let f = CGFloat(i) / CGFloat(steps)
            controller.pointerDragged(to: CGPoint(x: start.x + delta.x * f, y: start.y + delta.y * f), ended: i == steps)
            await pause(16)
        }
        await pause(300)
    }

    private func dragChecks() async {
        state.dismissMessage()
        state.set(.handsFree)
        var spots = FlowBarSpots()
        spots.set(testCentre, for: report.arrangement)
        state.spots = spots
        await pause(500)
        guard let start = controller.centre, let bar = controller.barShape else {
            fail("drag: the bar is not on screen")
            return
        }
        let size = bar.rect.size
        let visible = display.visible

        // A plain move, away from every snap line.
        let delta = CGPoint(x: 150, y: -90)
        await drag(by: delta)
        check("drag by (150, -90) is saved", expected: CGPoint(x: start.x + delta.x, y: start.y + delta.y))

        // Dropped 10 points from the left edge line: snaps onto it.
        guard let here = controller.centre else { return }
        let leftLine = visible.minX + FlowBarGeometry.edgeInset + size.width / 2
        await drag(by: CGPoint(x: leftLine + 10 - here.x, y: 0))
        check("dropped 10 pt from the left edge snaps to it", expected: CGPoint(x: leftLine, y: here.y))

        // Dragged far past the right edge: stays wholly on screen.
        guard let there = controller.centre else { return }
        await drag(by: CGPoint(x: visible.maxX + 400 - there.x, y: 0))
        if let bar = controller.barShape {
            // With a display to the right the bar may cross onto it; either way it must sit wholly on one display.
            let inside = FlowBarController.displays().contains { $0.visible.contains(bar.rect) }
            report.drag.append(DragCheck(label: "dragged 400 pt past the right edge stays on screen", expected: Box(visible),
                                         got: Box(bar.rect), pass: inside, note: nil))
            if !inside { fail("drag: bar left the screen at \(bar.rect)") }
        }

        // A press that doesn't move is a click, not a drag.
        let before = spot()
        if let c = controller.centre {
            controller.pointerDragged(to: c, ended: false)
            let moved = controller.pointerDragged(to: CGPoint(x: c.x + 1, y: c.y + 1), ended: true)
            let pass = !moved && spot() == before
            report.drag.append(DragCheck(label: "a 1 pt wobble is a click, not a drag", expected: nil, got: nil, pass: pass, note: nil))
            if !pass { fail("drag: a 1 pt press moved the bar") }
        }

        // A real drag with posted events, from the middle of the bar.
        if report.postedEventsMoveThePointer {
            spots = FlowBarSpots()
            spots.set(testCentre, for: report.arrangement)
            state.spots = spots
            await pause(500)
            if let bar = controller.barShape, let c = controller.centre {
                let tapsBefore = taps.count
                let from = CGPoint(x: bar.rect.midX, y: bar.rect.midY)
                let by = CGPoint(x: -140, y: -70)
                post(.mouseMoved, from)
                await pause(120)
                guard safe(from, "posted drag") else { return }
                post(.leftMouseDown, from)
                await pause(60)
                for i in 1...12 {
                    let f = CGFloat(i) / 12
                    post(.leftMouseDragged, CGPoint(x: from.x + by.x * f, y: from.y + by.y * f))
                    await pause(20)
                }
                post(.leftMouseUp, CGPoint(x: from.x + by.x, y: from.y + by.y))
                await pause(400)
                check("posted-event drag by (-140, -70) is saved", expected: CGPoint(x: c.x + by.x, y: c.y + by.y), tolerance: 2,
                      note: taps.count > tapsBefore ? "a button fired during the drag: \(taps[tapsBefore...])" : nil)
            }
        }

        // Reset: back to the bottom centre of this display.
        state.resetPosition()
        await pause(500)
        if let c = controller.centre {
            let expected = FlowBarGeometry.defaultCentre(size: CGSize(width: 120, height: 30), in: visible)
            let pass = state.spots.isEmpty && abs(c.x - expected.x) < 1 && abs(c.y - expected.y) < 1
            report.drag.append(DragCheck(label: "Reset position returns to the bottom centre", expected: Box(FlowBarGeometry.rect(centre: expected, size: size)),
                                         got: Box(FlowBarGeometry.rect(centre: c, size: size)), pass: pass, note: nil))
            if !pass { fail("drag: reset put the bar at \(c), expected \(expected)") }
        }
    }

    /// With the bar near the top of the screen, the toast opens below it and stays on screen.
    private func toastNearTopCheck() async {
        var spots = FlowBarSpots()
        spots.set(CGPoint(x: display.visible.maxX - 60, y: display.visible.maxY - 20), for: report.arrangement)
        state.spots = spots
        state.set(.listening)
        state.show("A long message to check that the toast stays on screen near the corner of the display",
                   detail: "It should open below the bar and inside the screen.", seconds: 600)
        await pause(700)
        var phase = PhaseReport(name: "toast-near-top-right")
        if let bar = controller.barShape, let toast = controller.toastShape {
            phase.bar = Box(bar.rect, radius: bar.radius)
            phase.toast = Box(toast.rect, radius: toast.radius)
            let below = toast.rect.maxY <= bar.rect.minY
            let onScreen = display.visible.contains(toast.rect) && display.visible.contains(bar.rect)
            phase.notes.append("toast below the bar: \(below); bar and toast inside the visible frame: \(onScreen)")
            if !below || !onScreen { fail("toast near the top: below \(below), on screen \(onScreen)") }
            phase.regionShot = await regionShot("toast-near-top-right", around: [bar.rect, toast.rect])
        } else {
            fail("toast near the top: not shown")
        }
        report.phases.append(phase)
        state.dismissMessage()
    }

    // MARK: Pixels

    /// Draws the bar window's view offscreen and checks its alpha. Always available; shows what the app draws.
    private func viewRender(_ name: String, bar: RoundedBox) -> PixelCheck {
        guard let view = controller.barPanel.contentView,
              let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else {
            return PixelCheck(method: "view render", pass: false, note: "could not render")
        }
        view.cacheDisplay(in: view.bounds, to: rep)
        guard let image = rep.cgImage else { return PixelCheck(method: "view render", pass: false, note: "no image") }
        return analyse(image, method: "view render", file: nil, bar: bar, window: controller.barPanel.frame)
    }

    /// Captures the bar window alone, through the window server, with `screencapture -l`.
    private func windowCapture(_ name: String, bar: RoundedBox) async -> PixelCheck {
        let file = directory.appending(path: "capsule-\(name)-window.png")
        try? FileManager.default.removeItem(at: file)
        let ok = runCapture(["-x", "-o", "-l", "\(controller.barPanel.windowNumber)", file.path])
        guard ok, let image = NSImage(contentsOf: file)?.cgImage(forProposedRect: nil, context: nil, hints: nil) else {
            return PixelCheck(method: "screencapture -l", file: nil, pass: true, note: "capture refused or failed; not counted")
        }
        var check = analyse(image, method: "screencapture -l", file: file.lastPathComponent, bar: bar, window: controller.barPanel.frame)
        if check.strayPixels == 0, check.edgeMaxAlpha == 0, isBlank(image) {
            check.note = "capture came back empty (screen recording not allowed?); not counted"
        }
        return check
    }

    private func regionShot(_ name: String, around rects: [CGRect]) async -> String? {
        guard var area = rects.first else { return nil }
        for r in rects.dropFirst() { area = area.union(r) }
        area = area.insetBy(dx: -40, dy: -30)
        let top = (FlowBarController.displays().first?.frame.maxY ?? 0) - area.maxY
        let file = directory.appending(path: "capsule-\(name)-over-backdrop.png")
        try? FileManager.default.removeItem(at: file)
        let ok = runCapture(["-x", "-R", "\(Int(area.minX)),\(Int(top)),\(Int(area.width)),\(Int(area.height))", file.path])
        return ok && FileManager.default.fileExists(atPath: file.path) ? file.lastPathComponent : nil
    }

    private func runCapture(_ arguments: [String]) -> Bool {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
        process.arguments = arguments
        do {
            try process.run()
            process.waitUntilExit()
            return process.terminationStatus == 0
        } catch {
            return false
        }
    }

    private func pixels(_ image: CGImage) -> [UInt8]? {
        let w = image.width, h = image.height
        var data = [UInt8](repeating: 0, count: w * h * 4)
        let ok = data.withUnsafeMutableBytes { buffer -> Bool in
            guard let context = CGContext(data: buffer.baseAddress, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                                          space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return false }
            context.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
            return true
        }
        return ok ? data : nil
    }

    private func isBlank(_ image: CGImage) -> Bool {
        guard let data = pixels(image) else { return true }
        return !stride(from: 3, to: data.count, by: 4).contains { data[$0] > 0 }
    }

    /// Edge alpha and stray pixels for an image of the whole bar window.
    private func analyse(_ image: CGImage, method: String, file: String?, bar: RoundedBox, window: CGRect) -> PixelCheck {
        guard let data = pixels(image) else { return PixelCheck(method: method, file: file, pass: false, note: "unreadable image") }
        let w = image.width, h = image.height
        let scale = CGFloat(w) / window.width
        // The shape in image pixels, origin bottom left like the bitmap context.
        let shape = RoundedBox(rect: CGRect(x: (bar.rect.minX - window.minX) * scale, y: (bar.rect.minY - window.minY) * scale,
                                            width: bar.rect.width * scale, height: bar.rect.height * scale), radius: bar.radius * scale)
        let grow = 10 * scale
        let halo = RoundedBox(rect: shape.rect.insetBy(dx: -grow, dy: -grow), radius: shape.radius + grow)
        var edge = 0, stray = 0
        for y in 0..<h {
            for x in 0..<w {
                // The context's rows run top to bottom in memory.
                let alpha = Int(data[((h - 1 - y) * w + x) * 4 + 3])
                if x < 2 || y < 2 || x >= w - 2 || y >= h - 2 { edge = max(edge, alpha) }
                if alpha > 8 && !halo.contains(CGPoint(x: CGFloat(x) + 0.5, y: CGFloat(y) + 0.5)) { stray += 1 }
            }
        }
        return PixelCheck(method: method, file: file, edgeMaxAlpha: edge, strayPixels: stray, pass: edge == 0 && stray == 0)
    }

    // MARK: Finish

    func finish() {
        guard !finished else { return }
        finished = true
        state.dismissMessage()
        state.set(.idle)
        state.showWhenIdle = false
        state.meetingSince = nil
        controller?.pointerOverride = nil
        controller?.layout()
        backdrop?.orderOut(nil)
        backdrop?.close()
        // Clicking the test window brought this process forward; hand the focus back.
        if NSApp.isActive, let previousApp, previousApp != NSRunningApplication.current { previousApp.activate() }
        if pointerMoved {
            CGWarpMouseCursorPosition(cg(originalPointer))
            CGAssociateMouseAndMouseCursorPosition(1)
        }
        report.passed = report.failures.isEmpty
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        encoder.dateEncodingStrategy = .iso8601
        do {
            try encoder.encode(report).write(to: output, options: .atomic)
            print("Flow bar self-test \(report.passed ? "passed" : "FAILED (\(report.failures.count) problems)"). Report: \(output.path)")
        } catch {
            FileHandle.standardError.write(Data("Could not write \(output.path): \(error)\n".utf8))
        }
        exit(report.passed ? 0 : 1)
    }
}
