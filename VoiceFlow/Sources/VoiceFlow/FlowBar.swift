import AppKit
import Observation
import SwiftUI
import VoiceFlowCore

/// State of the Flow bar, the small pill at the bottom of the screen.
@MainActor
@Observable
final class PillState {
    enum Phase: Equatable {
        case idle
        case listening
        case handsFree
        case command
        case processing
        case message
    }

    struct Action {
        let title: String
        let run: @MainActor () -> Void
    }

    var phase: Phase = .idle
    /// A toast shown next to the bar. It never changes `phase`, so recording controls stay visible.
    var messageVisible = false
    var levels: [Float] = Array(repeating: 0, count: 11)
    var message = ""
    var detail = ""
    var action: Action?
    var showWhenIdle = false
    var commandLocked = false
    /// Set while Notetaker records a meeting: the resting bubble becomes a recording chip.
    var meetingSince: Date?
    var onStopMeeting: (@MainActor () -> Void)?

    /// Where the bar was dragged to, per display arrangement. Mirrors `Preferences.flowBarSpots`.
    var spots = FlowBarSpots()
    var draggable = true
    /// Called after a drag or a reset, with the spots to save.
    var onMoved: (@MainActor (FlowBarSpots) -> Void)?
    /// True while the pointer is over the drawn bar. The controller sets it, since the bar's window ignores
    /// the pointer everywhere else and so never sees it leave.
    var pointerOverBar = false

    var onStartHandsFree: (@MainActor () -> Void)?
    var onConfirm: (@MainActor () -> Void)?
    var onCancel: (@MainActor () -> Void)?
    var onHideForHour: (@MainActor () -> Void)?
    var onOpenHub: (@MainActor () -> Void)?
    var onPasteLast: (@MainActor () -> Void)?
    var onLanguage: (@MainActor (String?) -> Void)?
    var currentLanguages: (@MainActor () -> [String])?

    private var hideTask: Task<Void, Never>?
    private var history: [Float] = Array(repeating: 0, count: 40)

    func push(level: Float) {
        history.removeFirst()
        history.append(level)
        // Symmetric bars: loudest in the middle, recent levels spread outwards.
        let n = levels.count
        let mid = n / 2
        var bars = [Float](repeating: 0, count: n)
        for i in 0..<n {
            let distance = abs(i - mid)
            let sample = history[history.count - 1 - distance * 2]
            let falloff = 1 - Float(distance) / Float(mid + 2)
            bars[i] = max(0.08, sample * falloff)
        }
        levels = bars
    }

    func set(_ phase: Phase) {
        self.phase = phase
        if phase == .listening || phase == .handsFree || phase == .command {
            levels = Array(repeating: 0.08, count: levels.count)
            history = Array(repeating: 0, count: history.count)
        }
    }

    /// Shows a message next to the bar for `seconds`. A processing bar returns to rest, since the toast
    /// reports how that ended; a recording bar keeps recording.
    func show(_ message: String, detail: String = "", action: Action? = nil, seconds: Double = 4) {
        hideTask?.cancel()
        if phase == .processing || phase == .message { phase = .idle }
        self.message = message
        self.detail = detail
        self.action = action
        messageVisible = true
        hideTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(seconds))
            guard !Task.isCancelled else { return }
            self?.dismissMessage()
        }
    }

    func dismissMessage() {
        hideTask?.cancel()
        messageVisible = false
        action = nil
    }

    /// Puts the bar back at the bottom centre on every display arrangement.
    func resetPosition() {
        spots = FlowBarSpots()
        onMoved?(spots)
    }
}

/// A borderless, non-activating panel: it floats above everything on every Space and never takes
/// focus away from the field you are typing in.
final class FlowBarPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }

    convenience init() {
        self.init(contentRect: NSRect(x: 0, y: 0, width: 100, height: 40),
                  styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        // Fully transparent window: only what SwiftUI draws shows, with no window shadow or background.
        isOpaque = false
        backgroundColor = .clear
        hasShadow = false
        level = .statusBar
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        hidesOnDeactivate = false
        isMovable = false
        isReleasedWhenClosed = false
        acceptsMouseMovedEvents = true
        // Until the pointer is over the drawn shape, every click goes to the window behind.
        ignoresMouseEvents = true
    }
}

final class ClickThroughHostingView<Content: View>: NSHostingView<Content> {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

/// Places the bar and its toast, moves them when dragged, and hit-tests the pointer against what is drawn.
///
/// Each window ignores the mouse except while the pointer is over its drawn shape, so clicks, scrolls and
/// hover over the transparent corners and margins reach the app behind. The pointer is followed with event
/// monitors while something is on screen, and not at all while the bar is hidden.
@MainActor
final class FlowBarController {
    /// Room around the bar, so its soft shadow fades out well inside the window and never shows an edge.
    static let margin: CGFloat = 14
    /// Widest a toast gets before its text wraps.
    static let toastWidth: CGFloat = 420

    enum Kind: Equatable { case none, listening, processing, handsFree, command, meeting, resting }

    private let state: PillState
    let barPanel = FlowBarPanel()
    let toastPanel = FlowBarPanel()
    private var barHost: ClickThroughHostingView<FlowBarView>!
    private let toastHost: NSHostingController<ToastView>

    /// The drawn bar, in the bar window's coordinates (origin top left), as SwiftUI last laid it out.
    private var barLocal: (kind: Kind, frame: CGRect)?
    /// The bar's centre on screen.
    private(set) var centre: CGPoint?
    /// A press on the bar: where the pointer and the bar started, and whether it has moved far enough to be a drag.
    private var drag: (pointer: CGPoint, centre: CGPoint, moving: Bool)?
    /// Points the pointer travels before a press becomes a drag.
    static let dragThreshold: CGFloat = 3
    private var monitors: [Any] = []
    private var safetyTimer: Timer?
    private var toastFading = false
    /// The self-test aims the hit test at a point without moving the real pointer.
    var pointerOverride: CGPoint? { didSet { refreshPointer() } }

    init(state: PillState) {
        self.state = state
        toastHost = NSHostingController(rootView: ToastView(state: state))
        toastHost.sizingOptions = []
        barHost = ClickThroughHostingView(rootView: FlowBarView(state: state, report: { [weak self] frame in
            self?.barLaidOut(frame)
        }, drag: { [weak self] ended in
            self?.pointerDragged(to: NSEvent.mouseLocation, ended: ended) ?? false
        }))
        barHost.sizingOptions = []
        barPanel.contentView = barHost
        toastPanel.contentViewController = toastHost
        layout()
        observe()
        NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.layout() }
        }
    }

    // MARK: Shapes

    var kind: Kind {
        switch state.phase {
        case .listening: .listening
        case .processing: .processing
        case .handsFree: .handsFree
        case .command: .command
        case .idle, .message: state.meetingSince != nil ? .meeting : state.showWhenIdle ? .resting : .none
        }
    }

    /// The bar's size for placing it. Close to what SwiftUI draws; the drawn frame replaces it once laid out.
    private func estimatedSize(_ kind: Kind) -> CGSize {
        switch kind {
        case .listening, .processing, .none: CGSize(width: 66, height: 30)
        case .handsFree: CGSize(width: 120, height: 30)
        case .command: CGSize(width: 140, height: 30)
        case .meeting: CGSize(width: 190, height: 28)
        // The resting bubble grows to 200 by 28 under the pointer; 5 points of padding around it take clicks.
        case .resting: CGSize(width: 210, height: 38)
        }
    }

    /// Size used to keep the bar on screen and snap it: what is drawn once SwiftUI has laid it out. The resting
    /// bubble counts at its grown size, so hovering it can't push it off an edge.
    private func placementSize(_ kind: Kind) -> CGSize {
        let estimate = estimatedSize(kind)
        guard let local = barLocal, local.kind == kind else { return estimate }
        if kind == .resting {
            return CGSize(width: max(estimate.width, local.frame.width), height: max(estimate.height, local.frame.height))
        }
        return local.frame.size
    }

    private func panelSize(_ kind: Kind) -> CGSize {
        let size = placementSize(kind)
        // Extra width so a bar that grows while animating is never clipped.
        return CGSize(width: ceil(size.width) + 2 * Self.margin + 40, height: ceil(size.height) + 2 * Self.margin)
    }

    /// The drawn bar on screen.
    var barShape: RoundedBox? {
        guard barPanel.isVisible else { return nil }
        let frame = barPanel.frame
        // SwiftUI reports the frame whenever it changes, so the last report is current even if the phase changed
        // without changing the frame (listening to processing).
        if let local = barLocal, local.frame.width > 0 {
            return .capsule(CGRect(x: frame.minX + local.frame.minX, y: frame.maxY - local.frame.maxY,
                                   width: local.frame.width, height: local.frame.height))
        }
        guard let centre else { return nil }
        return .capsule(FlowBarGeometry.rect(centre: centre, size: estimatedSize(kind)))
    }

    var toastShape: RoundedBox? {
        guard toastPanel.isVisible, !toastFading else { return nil }
        return RoundedBox(rect: toastPanel.frame, radius: Radius.card)
    }

    // MARK: Layout

    static func displays() -> [DisplayArea] {
        NSScreen.screens.map { DisplayArea(frame: $0.frame, visible: $0.visibleFrame) }
    }

    static func arrangement(_ displays: [DisplayArea]) -> String {
        FlowBarGeometry.arrangementKey(displays.map(\.frame))
    }

    func layout() {
        let kind = kind
        let displays = Self.displays()
        guard kind != .none || state.messageVisible, !displays.isEmpty else {
            hideBar()
            hideToast()
            stopTracking()
            return
        }
        let size = placementSize(kind)
        let c: CGPoint
        if drag?.moving == true, let current = centre {
            c = current
        } else {
            let saved = state.spots.centre(for: Self.arrangement(displays))
            // With no saved spot the bar opens on the display with the pointer, and stays there while shown.
            let anchor = (barPanel.isVisible ? centre : nil) ?? NSEvent.mouseLocation
            let fallback = FlowBarGeometry.display(for: anchor, among: displays)
            c = FlowBarGeometry.place(saved: saved, size: size, displays: displays, fallback: fallback) ?? .zero
        }
        centre = c
        if kind == .none {
            hideBar()
        } else {
            barPanel.setFrame(FlowBarGeometry.rect(centre: c, size: panelSize(kind)), display: true)
            if !barPanel.isVisible { barPanel.orderFrontRegardless() }
            barHost.layoutSubtreeIfNeeded()
        }
        if state.messageVisible { placeToast(displays) } else { hideToast() }
        startTracking()
        refreshPointer()
    }

    private func placeToast(_ displays: [DisplayArea]) {
        guard let centre, let display = FlowBarGeometry.display(for: centre, among: displays) else { return }
        let bar = FlowBarGeometry.rect(centre: centre, size: barShape?.rect.size ?? estimatedSize(kind))
        let fit = toastHost.sizeThatFits(in: CGSize(width: Self.toastWidth, height: 600))
        let size = CGSize(width: ceil(fit.width), height: ceil(fit.height))
        toastPanel.setFrame(FlowBarGeometry.toastFrame(size: size, bar: bar, in: display.visible), display: true)
        if !toastPanel.isVisible || toastFading {
            toastFading = false
            if !toastPanel.isVisible { toastPanel.alphaValue = 0 }
            toastPanel.orderFrontRegardless()
            NSAnimationContext.runAnimationGroup { context in
                context.duration = 0.15
                toastPanel.animator().alphaValue = 1
            }
        }
    }

    private func hideBar() {
        guard barPanel.isVisible else { return }
        barPanel.orderOut(nil)
        barPanel.ignoresMouseEvents = true
        barLocal = nil
        drag = nil
        if state.pointerOverBar { state.pointerOverBar = false }
    }

    private func hideToast() {
        guard toastPanel.isVisible, !toastFading else { return }
        toastFading = true
        toastPanel.ignoresMouseEvents = true
        NSAnimationContext.runAnimationGroup { context in
            context.duration = 0.15
            toastPanel.animator().alphaValue = 0
        } completionHandler: { [weak self] in
            MainActor.assumeIsolated {
                guard let self, self.toastFading else { return }
                self.toastFading = false
                self.toastPanel.orderOut(nil)
            }
        }
    }

    private func barLaidOut(_ frame: CGRect?) {
        guard let frame, frame.width > 0 else {
            barLocal = nil
            return
        }
        let previous = barLocal
        barLocal = (kind, frame)
        // A bar that came out bigger than its estimate may need moving back on screen. Not from inside
        // SwiftUI's layout pass, so on the next turn of the run loop.
        if previous?.kind != kind || (previous?.frame.size ?? .zero) != frame.size {
            let size = estimatedSize(kind)
            if frame.width > size.width + 1 || frame.height > size.height + 1 {
                Task { @MainActor [weak self] in self?.layout() }
            }
        }
        refreshPointer()
    }

    // MARK: Dragging

    /// Follows a press on the bar. `pointer` is in screen points, so the moving window doesn't skew it.
    /// Returns true when the press ended as a drag, false when it was a click (or dragging is off).
    @discardableResult
    func pointerDragged(to pointer: CGPoint, ended: Bool) -> Bool {
        guard state.draggable, barPanel.isVisible, let current = centre else {
            drag = nil
            return false
        }
        var press = drag ?? (pointer, current, false)
        if !press.moving, hypot(pointer.x - press.pointer.x, pointer.y - press.pointer.y) >= Self.dragThreshold {
            press.moving = true
        }
        drag = ended ? nil : press
        guard press.moving else { return false }
        let displays = Self.displays()
        let c = FlowBarGeometry.dragged(start: press.centre, pointerStart: press.pointer, pointer: pointer,
                                        size: placementSize(kind), displays: displays)
        centre = c
        barPanel.setFrame(FlowBarGeometry.rect(centre: c, size: panelSize(kind)), display: true)
        if state.messageVisible { placeToast(displays) }
        guard ended else { return true }
        var spots = state.spots
        spots.set(c, for: Self.arrangement(displays))
        state.spots = spots
        state.onMoved?(spots)
        refreshPointer()
        return true
    }

    // MARK: Pointer

    /// Lets each window take the mouse only while the pointer is over its drawn shape.
    func refreshPointer() {
        let point = pointerOverride ?? NSEvent.mouseLocation
        let overBar = barPanel.isVisible && (drag != nil || barShape?.contains(point) == true)
        let overToast = toastShape?.contains(point) == true
        if barPanel.ignoresMouseEvents == overBar { barPanel.ignoresMouseEvents = !overBar }
        if toastPanel.ignoresMouseEvents == overToast { toastPanel.ignoresMouseEvents = !overToast }
        if state.pointerOverBar != overBar { state.pointerOverBar = overBar }
    }

    private func startTracking() {
        guard monitors.isEmpty else { return }
        let mask: NSEvent.EventTypeMask = [.mouseMoved, .leftMouseDragged, .rightMouseDragged, .otherMouseDragged]
        // Global: the pointer over other apps. Local: the pointer over VoiceFlow's own windows.
        if let global = NSEvent.addGlobalMonitorForEvents(matching: mask, handler: { [weak self] _ in
            MainActor.assumeIsolated { self?.refreshPointer() }
        }) { monitors.append(global) }
        if let local = NSEvent.addLocalMonitorForEvents(matching: mask, handler: { [weak self] event in
            MainActor.assumeIsolated { self?.refreshPointer() }
            return event
        }) { monitors.append(local) }
        // A slow check catches a shape that moved under a still pointer, or a missed event.
        safetyTimer = Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.refreshPointer() }
        }
        safetyTimer?.tolerance = 0.1
    }

    private func stopTracking() {
        monitors.forEach(NSEvent.removeMonitor)
        monitors = []
        safetyTimer?.invalidate()
        safetyTimer = nil
    }

    private func observe() {
        withObservationTracking {
            _ = state.phase
            _ = state.showWhenIdle
            _ = state.meetingSince
            _ = state.messageVisible
            _ = state.message
            _ = state.detail
            _ = state.action
            _ = state.spots
        } onChange: { [weak self] in
            Task { @MainActor in
                self?.layout()
                self?.observe()
            }
        }
    }
}

// MARK: Views

/// The bar is always dark, on any wallpaper in either appearance, so it uses the dark values of the palette.
private enum BarStyle {
    static let fill = Color(red: 0.067, green: 0.067, blue: 0.067)
    static let border = Color.white.opacity(0.16)
    static let bars = Color(white: 0.97)
    static let signal = Color(red: 0.949, green: 0.380, blue: 0.247)
}

struct FlowBarView: View {
    @Bindable var state: PillState
    /// The drawn bar's frame in the window, origin top left; nil when nothing is drawn.
    var report: @MainActor (CGRect?) -> Void = { _ in }
    /// A press on the bar moved (false) or ended (true). Returns true once the press has become a drag.
    var drag: @MainActor (Bool) -> Bool = { _ in false }

    var body: some View {
        ZStack {
            bar
                .onGeometryChange(for: CGRect.self) { $0.frame(in: .named(Self.space)) } action: { report($0) }
                .onDisappear { report(nil) }
        }
        .animation(.spring(duration: 0.22), value: state.phase)
        .animation(.easeOut(duration: 0.15), value: state.pointerOverBar)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .coordinateSpace(.named(Self.space))
    }

    private nonisolated static let space = "flowbar"

    @ViewBuilder private var bar: some View {
        switch state.phase {
        case .idle, .message:
            if let since = state.meetingSince {
                MeetingChip(since: since) { state.onStopMeeting?() }
                    .gesture(move())
                    .contextMenu { positionMenu }
            } else if state.showWhenIdle {
                resting
            }
        case .listening:
            pill { Bars(levels: state.levels, color: BarStyle.bars, frozen: false) }
                .accessibilityLabel("Listening")
        case .processing:
            pill { Bars(levels: state.levels, color: BarStyle.bars, frozen: true) }
                .accessibilityLabel("Processing")
        case .handsFree, .command:
            pill {
                HStack(spacing: 7) {
                    RoundButton(symbol: "xmark", filled: false, help: "Cancel (Esc)") { state.onCancel?() }
                    if state.phase == .command {
                        Image(systemName: "sparkles").font(.system(size: 11)).foregroundStyle(BarStyle.bars)
                            .accessibilityLabel("Command Mode")
                    }
                    Bars(levels: state.levels, color: BarStyle.bars, frozen: false)
                    RoundButton(symbol: "checkmark", filled: true, help: "Finish") { state.onConfirm?() }
                }
            }
        }
    }

    /// Drags the bar. Buttons inside keep their clicks, since a child's gesture wins over this one.
    /// With `tap`, a press that never moves counts as a click instead.
    private func move(tap: (@MainActor () -> Void)? = nil) -> some Gesture {
        DragGesture(minimumDistance: tap == nil ? FlowBarController.dragThreshold : 0, coordinateSpace: .global)
            .onChanged { _ in _ = drag(false) }
            .onEnded { _ in if !drag(true) { tap?() } }
    }

    @ViewBuilder private var positionMenu: some View {
        Button("Reset position") { state.resetPosition() }.disabled(state.spots.isEmpty)
    }

    private func pill<Content: View>(@ViewBuilder _ content: () -> Content) -> some View {
        content()
            .padding(.horizontal, 6)
            .frame(height: 30)
            .background(RoundedRectangle(cornerRadius: 15, style: .circular).fill(BarStyle.fill))
            .overlay(RoundedRectangle(cornerRadius: 15, style: .circular).strokeBorder(BarStyle.border, lineWidth: 1))
            .contentShape(Capsule())
            // Soft and small: the window leaves `FlowBarController.margin` around the bar, so it fades out before any edge.
            .shadow(color: .black.opacity(0.22), radius: 4, y: 1)
            .gesture(move())
            .contextMenu { positionMenu }
    }

    private var resting: some View {
        let open = state.pointerOverBar
        return Capsule()
            .fill(BarStyle.fill.opacity(open ? 0.95 : 0.75))
            .overlay(Capsule().strokeBorder(BarStyle.border, lineWidth: 1))
            .frame(width: open ? 200 : 40, height: open ? 28 : 10)
            .overlay {
                if open {
                    Text("Click or hold fn to dictate")
                        .font(.system(size: 11, weight: .medium))
                        .foregroundStyle(.white.opacity(0.9))
                        .lineLimit(1)
                }
            }
            // A small bubble is hard to hit; the padding around it takes clicks too.
            .padding(5)
            .contentShape(Capsule())
            .gesture(move { state.onStartHandsFree?() })
            .contextMenu {
                Button("Hide for 1 hour") { state.onHideForHour?() }
                Menu("Language") {
                    let current = state.currentLanguages?() ?? []
                    Button { state.onLanguage?(nil) } label: {
                        Label("Auto-detect", systemImage: current.count != 1 ? "checkmark" : "")
                    }
                    Divider()
                    ForEach(Languages.options.prefix(12), id: \.code) { option in
                        Button { state.onLanguage?(option.code) } label: {
                            Label(option.name, systemImage: current == [option.code] ? "checkmark" : "")
                        }
                    }
                }
                Divider()
                Button("Paste last transcript") { state.onPasteLast?() }
                Button("Open VoiceFlow") { state.onOpenHub?() }
                Divider()
                positionMenu
            }
    }
}

/// The message shown next to the bar, in a window of its own sized to fit it.
struct ToastView: View {
    @Bindable var state: PillState

    /// Hugs short messages; long ones wrap at `FlowBarController.toastWidth`.
    var body: some View {
        ViewThatFits(in: .horizontal) {
            content.fixedSize()
            content.frame(width: FlowBarController.toastWidth).fixedSize(horizontal: false, vertical: true)
        }
    }

    private var content: some View {
        HStack(spacing: 10) {
            VStack(alignment: .leading, spacing: 2) {
                Text(state.message).font(.system(size: 12, weight: .semibold)).foregroundStyle(.white).lineLimit(2)
                if !state.detail.isEmpty {
                    Text(state.detail).font(.system(size: 11)).foregroundStyle(.white.opacity(0.75)).lineLimit(3)
                }
            }
            if let action = state.action {
                Button { action.run() } label: {
                    Text(action.title)
                        .font(.system(size: 12, weight: .semibold))
                        .foregroundStyle(Color.black)
                        .padding(.horizontal, 12).frame(height: 28)
                        .background(RoundedRectangle(cornerRadius: Radius.control, style: .continuous).fill(BarStyle.bars))
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
                .accessibilityLabel(action.title)
            }
        }
        .padding(.horizontal, 14).padding(.vertical, 9)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: Radius.card, style: .continuous).fill(BarStyle.fill))
        .overlay(RoundedRectangle(cornerRadius: Radius.card, style: .continuous).strokeBorder(BarStyle.border, lineWidth: 1))
        .accessibilityElement(children: .contain)
    }
}

private struct RoundButton: View {
    let symbol: String
    let filled: Bool
    let help: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 9, weight: .bold))
                .foregroundStyle(filled ? Color.black : Color.white)
                .frame(width: 20, height: 20)
                .background(Circle().fill(filled ? Color.white : Color.white.opacity(0.35)))
        }
        .buttonStyle(.plain)
        .help(help)
        .accessibilityLabel(help)
    }
}

private struct Bars: View {
    let levels: [Float]
    let color: Color
    let frozen: Bool

    var body: some View {
        HStack(alignment: .center, spacing: 2.5) {
            ForEach(levels.indices, id: \.self) { i in
                Capsule()
                    .fill(color.opacity(frozen ? 0.45 : 0.95))
                    .frame(width: 2.5, height: max(3, CGFloat(levels[i]) * 20))
            }
        }
        .frame(height: 22)
        .animation(frozen ? .easeInOut(duration: 0.6).repeatForever(autoreverses: true) : .linear(duration: 0.06), value: levels)
    }
}

/// Shown while Notetaker records: a red dot, the elapsed time and a stop button.
private struct MeetingChip: View {
    let since: Date
    let stop: () -> Void

    var body: some View {
        HStack(spacing: 8) {
            Circle().fill(BarStyle.signal).frame(width: 8, height: 8).accessibilityHidden(true)
            TimelineView(.periodic(from: since, by: 1)) { context in
                Text(Meeting.clock(context.date.timeIntervalSince(since)))
                    .font(.system(size: 12, weight: .semibold).monospacedDigit())
                    .foregroundStyle(.white)
            }
            Text("Notetaker").font(.system(size: 11)).foregroundStyle(.white.opacity(0.7))
            Button(action: stop) {
                Image(systemName: "stop.fill").font(.system(size: 8, weight: .bold)).foregroundStyle(.black)
                    .frame(width: 18, height: 18).background(Circle().fill(Color.white))
            }
            .buttonStyle(.plain)
            .help("Stop and write notes")
            .accessibilityLabel("Stop and write notes")
        }
        .padding(.horizontal, 10)
        .frame(height: 28)
        .background(RoundedRectangle(cornerRadius: 14, style: .circular).fill(BarStyle.fill))
        .overlay(RoundedRectangle(cornerRadius: 14, style: .circular).strokeBorder(BarStyle.signal.opacity(0.5), lineWidth: 1))
        .contentShape(Capsule())
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Recording meeting notes")
    }
}
