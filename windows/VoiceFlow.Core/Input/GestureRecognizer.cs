namespace VoiceFlow.Core.Input;

public enum SessionKind { Dictation, Command }

public enum GestureEventKind { HotkeyDown, HotkeyUp, ControlDown, Space, Escape, Backspace, OtherKey, Tick }

/// <summary>
/// One keyboard event for the recognizer. <see cref="ControlHeld"/> is meaningful for HotkeyDown only: the
/// Command Mode modifier was already down when the hotkey went down.
/// </summary>
public readonly record struct GestureEvent(GestureEventKind Kind, double At, bool ControlHeld = false)
{
    public static GestureEvent HotkeyDown(double at, bool controlHeld) => new(GestureEventKind.HotkeyDown, at, controlHeld);
    public static GestureEvent HotkeyUp(double at) => new(GestureEventKind.HotkeyUp, at);
    public static GestureEvent ControlDown(double at) => new(GestureEventKind.ControlDown, at);
    public static GestureEvent Space(double at) => new(GestureEventKind.Space, at);
    public static GestureEvent Escape(double at) => new(GestureEventKind.Escape, at);
    public static GestureEvent Backspace(double at) => new(GestureEventKind.Backspace, at);
    public static GestureEvent OtherKey(double at) => new(GestureEventKind.OtherKey, at);
    /// <summary>Send when <see cref="GestureRecognizer.Deadline"/> passes.</summary>
    public static GestureEvent Tick(double at) => new(GestureEventKind.Tick, at);
}

public enum GestureActionKind { Begin, BecomeCommand, Lock, Finish, Cancel }

public readonly record struct GestureAction(GestureActionKind Kind, SessionKind Session = SessionKind.Dictation)
{
    public static GestureAction Begin(SessionKind k) => new(GestureActionKind.Begin, k);
    public static readonly GestureAction BecomeCommand = new(GestureActionKind.BecomeCommand);
    public static readonly GestureAction Lock = new(GestureActionKind.Lock);
    public static GestureAction Finish(SessionKind k) => new(GestureActionKind.Finish, k);
    public static readonly GestureAction Cancel = new(GestureActionKind.Cancel);

    public override string ToString() => Kind is GestureActionKind.Begin or GestureActionKind.Finish ? $"{Kind}({Session})" : Kind.ToString();
}

/// <summary>
/// Turns hotkey presses into dictation actions. Pure and clock-free: callers pass timestamps, so every
/// gesture is unit-testable without a keyboard. A port of the macOS GestureRecognizer.
///
/// Gestures:
/// - Hold the hotkey: push-to-talk. Recording starts on press so the first word is never lost.
/// - Hotkey+Space: hands-free lock until the next hotkey press.
/// - Tap and release quickly, then press again within the double-tap window: hands-free lock too.
///   A single quick tap is cancelled when the window ends.
/// - Command modifier held at press, or pressed while holding: Command Mode. Double-press locks it hands-free.
/// - Esc cancels anything. Backspace cancels Command Mode.
/// - Any other key while holding cancels (the user is using a shortcut such as Ctrl+Win+Right).
/// </summary>
public sealed class GestureRecognizer
{
    enum StateKind { Idle, Holding, AwaitingSecondTap, Locked }

    StateKind _state = StateKind.Idle;
    double _since;      // Holding: press time
    double _until;      // AwaitingSecondTap: window end
    SessionKind _kind;

    public double TapThreshold { get; set; } = 0.25;
    public double DoubleTapWindow { get; set; } = 0.5;

    /// <summary>When the caller should send a Tick, if anything is pending.</summary>
    public double? Deadline => _state == StateKind.AwaitingSecondTap ? _until : null;

    public bool IsActive => _state != StateKind.Idle;

    public SessionKind? ActiveKind => _state == StateKind.Idle ? null : _kind;

    /// <summary>
    /// Puts the recognizer in hands-free mode, as if the hotkey had been double-tapped (the Flow bar was
    /// clicked). The next press finishes; Esc cancels.
    /// </summary>
    public void EnterHandsFree(SessionKind kind = SessionKind.Dictation)
    {
        _state = StateKind.Locked;
        _kind = kind;
    }

    public void Reset() => _state = StateKind.Idle;

    public IReadOnlyList<GestureAction> Handle(GestureEvent e)
    {
        switch (_state)
        {
            case StateKind.Idle:
                if (e.Kind == GestureEventKind.HotkeyDown)
                {
                    _kind = e.ControlHeld ? SessionKind.Command : SessionKind.Dictation;
                    _state = StateKind.Holding;
                    _since = e.At;
                    return [GestureAction.Begin(_kind)];
                }
                return [];

            case StateKind.Holding:
                switch (e.Kind)
                {
                    case GestureEventKind.HotkeyUp:
                        if (e.At - _since < TapThreshold)
                        {
                            _state = StateKind.AwaitingSecondTap;
                            _until = e.At + DoubleTapWindow;
                            return [];
                        }
                        _state = StateKind.Idle;
                        return [GestureAction.Finish(_kind)];
                    case GestureEventKind.ControlDown when _kind == SessionKind.Dictation:
                        _kind = SessionKind.Command;
                        return [GestureAction.BecomeCommand];
                    case GestureEventKind.Space:
                        _state = StateKind.Locked;
                        return [GestureAction.Lock];
                    case GestureEventKind.Escape:
                    case GestureEventKind.OtherKey:
                    case GestureEventKind.Backspace:
                        _state = StateKind.Idle;
                        return [GestureAction.Cancel];
                    default:
                        return [];
                }

            case StateKind.AwaitingSecondTap:
                switch (e.Kind)
                {
                    case GestureEventKind.HotkeyDown:
                        if (e.At <= _until)
                        {
                            _state = StateKind.Locked;
                            return [GestureAction.Lock];
                        }
                        // The tick was missed: the first tap expired, and this press starts a new session.
                        _state = StateKind.Holding;
                        _since = e.At;
                        _kind = SessionKind.Dictation;
                        return [GestureAction.Cancel, GestureAction.Begin(SessionKind.Dictation)];
                    case GestureEventKind.Tick when e.At >= _until:
                    case GestureEventKind.Escape:
                    case GestureEventKind.OtherKey:
                    case GestureEventKind.Space:
                    case GestureEventKind.Backspace:
                        _state = StateKind.Idle;
                        return [GestureAction.Cancel];
                    default:
                        return [];
                }

            case StateKind.Locked:
                switch (e.Kind)
                {
                    case GestureEventKind.HotkeyDown:
                        _state = StateKind.Idle;
                        return [GestureAction.Finish(_kind)];
                    case GestureEventKind.Escape:
                        _state = StateKind.Idle;
                        return [GestureAction.Cancel];
                    case GestureEventKind.Backspace when _kind == SessionKind.Command:
                        _state = StateKind.Idle;
                        return [GestureAction.Cancel];
                    default:
                        // Releases after a lock, typing during hands-free: nothing to do.
                        return [];
                }
        }
        return [];
    }
}
