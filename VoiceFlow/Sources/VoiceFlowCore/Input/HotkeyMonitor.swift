import ApplicationServices
import CoreGraphics
import Foundation
import os

/// Watches the keyboard with a `CGEventTap` and reports dictation gestures and shortcuts.
///
/// With Accessibility permission the tap is *active*: it swallows the Fn key (so
/// macOS does not open the emoji picker), the Space of Fn+Space, Esc and Backspace while they cancel
/// a session, and the ⌘⌃V / ⌘⌃C shortcuts. Without it, the tap falls back to listen-only, which only
/// needs Input Monitoring but cannot stop those keys from reaching the app.
@MainActor
public final class HotkeyMonitor {
    public enum Output: Equatable, Sendable {
        case gesture(GestureRecognizer.Action)
        case shortcut(HotkeyEventMapper.Shortcut)
    }

    public enum Mode: Equatable, Sendable {
        case stopped
        case active      // can swallow keys
        case listenOnly  // observes only
    }

    public typealias Handler = @MainActor (Output) -> Void

    private let log = Logger(subsystem: "com.saideep.voiceflow", category: "hotkey")
    private var mapper: HotkeyEventMapper
    private var recognizer = GestureRecognizer()
    private var tap: CFMachPort?
    private var source: CFRunLoopSource?
    private var tickTimer: Timer?
    private let handler: Handler
    public private(set) var mode: Mode = .stopped

    public init(hotkey: Hotkey, handler: @escaping Handler) {
        self.mapper = HotkeyEventMapper(hotkey: hotkey)
        self.handler = handler
    }

    // MARK: Permissions

    public static var hasAccessibility: Bool { AXIsProcessTrusted() }
    public static var hasInputMonitoring: Bool { CGPreflightListenEventAccess() }

    @discardableResult
    public static func requestAccessibility() -> Bool {
        let key = "AXTrustedCheckOptionPrompt" as CFString
        return AXIsProcessTrustedWithOptions([key: true] as CFDictionary)
    }

    @discardableResult
    public static func requestInputMonitoring() -> Bool { CGRequestListenEventAccess() }

    // MARK: Lifecycle

    /// Starts (or upgrades) the tap. Prefers an active tap; falls back to listen-only.
    @discardableResult
    public func start() -> Mode {
        if mode == .active { return mode }
        if mode == .listenOnly && !Self.hasAccessibility { return mode }
        removeTap()

        if Self.hasAccessibility, installTap(options: .defaultTap) {
            mode = .active
        } else if Self.hasInputMonitoring, installTap(options: .listenOnly) {
            mode = .listenOnly
        } else {
            mode = .stopped
            log.notice("No Accessibility or Input Monitoring permission; hotkey monitor not started")
        }
        log.info("Hotkey monitor mode: \(String(describing: self.mode), privacy: .public)")
        return mode
    }

    public func stop() {
        removeTap()
        mode = .stopped
        resetSession()
    }

    public func setHotkey(_ hotkey: Hotkey) {
        guard hotkey != mapper.hotkey else { return }
        resetSession()
        mapper = HotkeyEventMapper(hotkey: hotkey)
    }

    /// Clears the current gesture. With `notify`, the app also gets a `.cancel`; without it (the app already
    /// finished or cancelled the session itself) the reset is silent.
    public func resetSession(notify: Bool = true) {
        tickTimer?.invalidate()
        tickTimer = nil
        if recognizer.isActive {
            recognizer = GestureRecognizer()
            if notify { handler(.gesture(.cancel)) }
        }
    }

    /// Hands-free started from outside the keyboard (Flow bar click): the next press finishes, Esc cancels.
    public func enterHandsFree() {
        tickTimer?.invalidate()
        tickTimer = nil
        recognizer = GestureRecognizer()
        recognizer.enterHandsFree()
    }

    private func installTap(options: CGEventTapOptions) -> Bool {
        let mask = (1 << CGEventType.flagsChanged.rawValue) | (1 << CGEventType.keyDown.rawValue)
        let refcon = Unmanaged.passUnretained(self).toOpaque()
        guard let tap = CGEvent.tapCreate(
            tap: .cgSessionEventTap,
            place: .headInsertEventTap,
            options: options,
            eventsOfInterest: CGEventMask(mask),
            callback: { _, type, event, refcon in
                guard let refcon else { return Unmanaged.passUnretained(event) }
                let monitor = Unmanaged<HotkeyMonitor>.fromOpaque(refcon).takeUnretainedValue()
                let keyCode = UInt16(event.getIntegerValueField(.keyboardEventKeycode))
                let flags = event.flags.rawValue
                // The run loop source is on the main run loop, so this runs on the main thread.
                let swallow = MainActor.assumeIsolated {
                    monitor.receive(type: type, keyCode: keyCode, flags: flags)
                }
                return swallow ? nil : Unmanaged.passUnretained(event)
            },
            userInfo: refcon
        ) else {
            log.error("CGEvent.tapCreate failed for \(options == .defaultTap ? "active" : "listen-only", privacy: .public) tap")
            return false
        }
        let source = CFMachPortCreateRunLoopSource(nil, tap, 0)
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        self.tap = tap
        self.source = source
        return true
    }

    private func removeTap() {
        if let source { CFRunLoopRemoveSource(CFRunLoopGetMain(), source, .commonModes) }
        if let tap { CGEvent.tapEnable(tap: tap, enable: false) }
        tap = nil
        source = nil
    }

    // MARK: Events

    /// Returns true when the event should be swallowed (active mode only).
    private func receive(type: CGEventType, keyCode: UInt16, flags: UInt64) -> Bool {
        let canSwallow = mode == .active
        switch type {
        case .tapDisabledByTimeout, .tapDisabledByUserInput:
            // macOS disables slow taps; turn it back on.
            if let tap { CGEvent.tapEnable(tap: tap, enable: true) }
            log.notice("Event tap was disabled by the system; re-enabled")
            return false

        case .flagsChanged:
            let produced = dispatch(mapper.map(kind: .flagsChanged, keyCode: keyCode, flags: flags, at: now()))
            // Swallow Fn itself so macOS does not run its 🌐 action. Right Option / Command pass through.
            let isFn = mapper.hotkey == .fn && mapper.isHotkeyModifierEvent(kind: .flagsChanged, keyCode: keyCode)
            return canSwallow && isFn && produced != nil

        case .keyDown:
            if !recognizer.isActive {
                // Plain typing costs nothing; only the two shortcuts are checked.
                if let shortcut = mapper.shortcut(keyCode: keyCode, flags: flags) {
                    deliver(.shortcut(shortcut))
                    return canSwallow
                }
                return false
            }
            let event = mapper.map(kind: .keyDown, keyCode: keyCode, flags: flags, at: now())
            let actions = dispatch(event) ?? []
            // Swallow the keys that controlled the session; let ordinary keys through.
            switch event {
            case .space, .escape, .backspace: return canSwallow && !actions.isEmpty
            default: return false
            }

        default:
            return false
        }
    }

    /// Feeds the recognizer. Returns the actions produced, or nil if the event was not relevant.
    @discardableResult
    private func dispatch(_ event: GestureRecognizer.Event?) -> [GestureRecognizer.Action]? {
        guard let event else { return nil }
        let actions = recognizer.handle(event)
        for action in actions {
            log.debug("gesture \(String(describing: action), privacy: .public)")
            deliver(.gesture(action))
        }
        scheduleTick()
        return actions
    }

    /// Hands work to the app *after* the tap callback returns. Starting the microphone takes long enough
    /// that doing it inside the callback made macOS disable the tap, losing key events such as the release
    /// of the dictation key. The main queue keeps the order of actions.
    private func deliver(_ output: Output) {
        let handler = self.handler
        DispatchQueue.main.async {
            MainActor.assumeIsolated { handler(output) }
        }
    }

    private func scheduleTick() {
        tickTimer?.invalidate()
        tickTimer = nil
        guard let deadline = recognizer.deadline else { return }
        let delay = max(0, deadline - now())
        tickTimer = Timer.scheduledTimer(withTimeInterval: delay, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self else { return }
                self.dispatch(.tick(at: self.now()))
            }
        }
    }

    private func now() -> TimeInterval { ProcessInfo.processInfo.systemUptime }
}
