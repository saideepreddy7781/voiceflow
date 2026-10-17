import Foundation

public enum SessionKind: Equatable, Sendable {
    case dictation
    case command
}

/// Turns hotkey presses into dictation actions. Pure and clock-free: callers pass timestamps,
/// so every gesture is unit-testable without a keyboard.
///
/// Gestures:
/// - Hold the hotkey: push-to-talk. Recording starts on press so the first word is never lost.
/// - Hotkey+Space: hands-free lock until the next hotkey press.
/// - Tap and release quickly, then press again within `doubleTapWindow`: hands-free lock too.
///   A single quick tap is cancelled when the window ends.
/// - Control held at press, or pressed while holding: Command Mode. Double-press locks it hands-free.
/// - Esc cancels anything. Backspace cancels Command Mode.
/// - Any other key while holding cancels (the user is using an Fn shortcut such as Fn+↑).
public struct GestureRecognizer: Sendable {
    public enum Event: Equatable, Sendable {
        case hotkeyDown(at: TimeInterval, controlHeld: Bool)
        case hotkeyUp(at: TimeInterval)
        case controlDown(at: TimeInterval)
        case space(at: TimeInterval)
        case escape(at: TimeInterval)
        case backspace(at: TimeInterval)
        case otherKey(at: TimeInterval)
        /// Call when `deadline` passes.
        case tick(at: TimeInterval)
    }

    public enum Action: Equatable, Sendable {
        case begin(SessionKind)
        case becomeCommand
        case lock
        case finish(SessionKind)
        case cancel
    }

    enum State: Equatable, Sendable {
        case idle
        case holding(since: TimeInterval, kind: SessionKind)
        case awaitingSecondTap(until: TimeInterval, kind: SessionKind)
        case locked(SessionKind)
    }

    public var tapThreshold: TimeInterval = 0.25
    public var doubleTapWindow: TimeInterval = 0.5

    private(set) var state: State = .idle

    public init() {}

    /// When the caller should send `.tick`, if anything is pending.
    public var deadline: TimeInterval? {
        if case .awaitingSecondTap(let until, _) = state { return until }
        return nil
    }

    public var isActive: Bool { state != .idle }

    /// Puts the recognizer in hands-free mode, as if the hotkey had been double-tapped (the Flow bar was
    /// clicked). The next press finishes; Esc cancels.
    public mutating func enterHandsFree(_ kind: SessionKind = .dictation) {
        state = .locked(kind)
    }

    public var activeKind: SessionKind? {
        switch state {
        case .idle: nil
        case .holding(_, let kind), .awaitingSecondTap(_, let kind), .locked(let kind): kind
        }
    }

    public mutating func handle(_ event: Event) -> [Action] {
        switch (state, event) {

        case (.idle, .hotkeyDown(let t, let control)):
            let kind: SessionKind = control ? .command : .dictation
            state = .holding(since: t, kind: kind)
            return [.begin(kind)]

        case (.holding(let since, let kind), .hotkeyUp(let t)):
            if t - since < tapThreshold {
                state = .awaitingSecondTap(until: t + doubleTapWindow, kind: kind)
                return []
            }
            state = .idle
            return [.finish(kind)]

        case (.holding(let since, .dictation), .controlDown):
            state = .holding(since: since, kind: .command)
            return [.becomeCommand]

        case (.holding(_, let kind), .space):
            state = .locked(kind)
            return [.lock]

        case (.holding(_, .command), .backspace), (.locked(.command), .backspace),
             (.awaitingSecondTap(_, .command), .backspace):
            state = .idle
            return [.cancel]

        case (.holding, .escape), (.holding, .otherKey), (.holding, .backspace):
            state = .idle
            return [.cancel]

        case (.awaitingSecondTap(let until, let kind), .hotkeyDown(let t, _)):
            if t <= until {
                state = .locked(kind)
                return [.lock]
            }
            // The tick was missed: the first tap expired, and this press starts a new session.
            state = .holding(since: t, kind: .dictation)
            return [.cancel, .begin(.dictation)]

        case (.awaitingSecondTap(let until, _), .tick(let t)) where t >= until:
            state = .idle
            return [.cancel]

        case (.awaitingSecondTap, .escape), (.awaitingSecondTap, .otherKey), (.awaitingSecondTap, .space),
             (.awaitingSecondTap, .backspace):
            state = .idle
            return [.cancel]

        case (.locked(let kind), .hotkeyDown):
            state = .idle
            return [.finish(kind)]

        case (.locked, .escape):
            state = .idle
            return [.cancel]

        default:
            // Releases after a lock or finish, typing during hands-free, repeated downs,
            // ticks with nothing pending: nothing to do.
            return []
        }
    }
}
