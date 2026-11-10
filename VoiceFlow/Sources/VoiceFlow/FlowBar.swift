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
    /// A toast shown above the bar. It never changes `phase`, so recording controls stay visible.
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

    /// Shows a message above the bar for `seconds`. A processing bar returns to rest, since the toast
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
}

/// A borderless, non-activating panel: it floats above everything on every Space and never takes
/// focus away from the field you are typing in.
final class FlowBarPanel: NSPanel {
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }
}

private final class ClickThroughHostingView<Content: View>: NSHostingView<Content> {
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
}

@MainActor
final class FlowBarController {
    private let panel: FlowBarPanel
    private let state: PillState

    init(state: PillState) {
        self.state = state
        panel = FlowBarPanel(contentRect: NSRect(x: 0, y: 0, width: 220, height: 40),
                             styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.level = .statusBar
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        panel.hidesOnDeactivate = false
        panel.isMovable = false
        panel.contentView = ClickThroughHostingView(rootView: FlowBarView(state: state))
        layout()
        panel.orderFrontRegardless()
        observe()
        NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.layout() }
        }
    }

    /// Panel size per phase, so it never blocks clicks beyond the pill itself.
    private var size: NSSize {
        if state.messageVisible { return NSSize(width: 440, height: 96) }
        return switch state.phase {
        case .idle where state.meetingSince != nil: NSSize(width: 230, height: 40)
        case .idle: state.showWhenIdle ? NSSize(width: 230, height: 40) : NSSize(width: 1, height: 1)
        case .listening, .processing: NSSize(width: 120, height: 40)
        case .handsFree: NSSize(width: 150, height: 40)
        case .command: NSSize(width: 170, height: 40)
        case .message: NSSize(width: 440, height: 96)
        }
    }

    /// Bottom centre of the display with the mouse, clear of the Dock (visibleFrame excludes it).
    func layout() {
        let mouse = NSEvent.mouseLocation
        let screen = NSScreen.screens.first { NSMouseInRect(mouse, $0.frame, false) } ?? NSScreen.main
        guard let frame = screen?.visibleFrame else { return }
        let size = size
        panel.setFrame(NSRect(x: frame.midX - size.width / 2, y: frame.minY + 6, width: size.width, height: size.height), display: true)
        panel.ignoresMouseEvents = !state.messageVisible
            && (state.phase == .processing || (state.phase == .idle && !state.showWhenIdle && state.meetingSince == nil))
    }

    private func observe() {
        withObservationTracking {
            _ = state.phase
            _ = state.showWhenIdle
            _ = state.meetingSince
            _ = state.messageVisible
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
    @State private var hovering = false

    var body: some View {
        VStack(spacing: 6) {
            if state.messageVisible { toast }
            bar
        }
        .animation(.spring(duration: 0.22), value: state.phase)
        .animation(.spring(duration: 0.22), value: state.messageVisible)
        .animation(.easeOut(duration: 0.15), value: hovering)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottom)
        .padding(.bottom, 2)
    }

    @ViewBuilder private var bar: some View {
        switch state.phase {
        case .idle, .message:
            if let since = state.meetingSince { MeetingChip(since: since) { state.onStopMeeting?() } }
            else if state.showWhenIdle { resting }
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

    private func pill<Content: View>(@ViewBuilder _ content: () -> Content) -> some View {
        content()
            .padding(.horizontal, 6)
            .frame(height: 30)
            .background(RoundedRectangle(cornerRadius: 15, style: .circular).fill(BarStyle.fill))
            .overlay(RoundedRectangle(cornerRadius: 15, style: .circular).strokeBorder(BarStyle.border, lineWidth: 1))
            .shadow(color: .black.opacity(0.25), radius: 6, y: 2)
    }

    private var resting: some View {
        Capsule()
            .fill(BarStyle.fill.opacity(hovering ? 0.95 : 0.75))
            .overlay(Capsule().strokeBorder(BarStyle.border, lineWidth: 1))
            .frame(width: hovering ? 200 : 40, height: hovering ? 28 : 10)
            .overlay {
                if hovering {
                    Text("Click or hold fn to dictate")
                        .font(.system(size: 11, weight: .medium))
                        .foregroundStyle(.white.opacity(0.9))
                        .lineLimit(1)
                }
            }
            .contentShape(Capsule())
            .onHover { hovering = $0 }
            .onTapGesture { state.onStartHandsFree?() }
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
            }
    }

    /// Hugs short messages; long ones wrap at 420.
    private var toast: some View {
        ViewThatFits(in: .horizontal) {
            toastContent.fixedSize()
            toastContent.frame(width: 420)
        }
        .frame(maxWidth: 420)
        .transition(.move(edge: .bottom).combined(with: .opacity))
    }

    private var toastContent: some View {
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
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Recording meeting notes")
    }
}
