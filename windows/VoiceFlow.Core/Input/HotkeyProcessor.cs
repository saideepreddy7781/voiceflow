namespace VoiceFlow.Core.Input;

/// <summary>One event from the low-level keyboard hook, reduced to plain numbers so it can be unit-tested.</summary>
public readonly record struct KeyInput(int Vk, bool Down, int ScanCode = 0, bool OwnInjection = false);

public enum HookVerdict
{
    /// <summary>Let the key through (CallNextHookEx).</summary>
    Pass,
    /// <summary>Block the key.</summary>
    Swallow,
    /// <summary>
    /// Block this key-up and inject [mask down, mask up, key up] instead, so releasing Win does not open
    /// the Start menu and releasing Alt does not focus the app's menu bar.
    /// </summary>
    SwallowAndReleaseWithMask,
}

public enum HotkeyShortcut { PasteLast, Scratchpad }

public sealed record HookResult(HookVerdict Verdict, IReadOnlyList<GestureAction> Actions, HotkeyShortcut? Shortcut = null)
{
    public static readonly HookResult PassNothing = new(HookVerdict.Pass, []);
}

/// <summary>
/// The decision logic of the Windows key hook: tracks modifier state, forms the hotkey chord, feeds the
/// <see cref="GestureRecognizer"/>, and says which keys to swallow. Runs inside the hook callback, so it does
/// no I/O and allocates little; the Windows layer posts the resulting actions to the UI loop.
///
/// Hotkeys: Ctrl+Win (default), Right Alt, Right Ctrl. Command Mode adds Alt (Shift with Right Alt).
/// Alt+Shift+V pastes the last result and Alt+Shift+S opens the Scratchpad. Alt alone is avoided because it drives
/// app menus (Alt+S is "Send" or a menu in many apps); Alt+Shift is free in most apps, and the release is masked so
/// Windows does not switch the input language.
/// </summary>
public sealed class HotkeyProcessor
{
    public const int VK_BACK = 0x08, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_ESCAPE = 0x1B, VK_SPACE = 0x20,
        VK_S = 0x53, VK_V = 0x56, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2,
        VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;

    /// <summary>An unassigned virtual key, injected so Windows sees "another key" between Win down and up.</summary>
    public const int MaskVk = 0xE8;

    /// <summary>The fake Left Ctrl that AltGr layouts send with Right Alt.</summary>
    const int AltGrFakeCtrlScan = 0x21D;

    readonly GestureRecognizer _recognizer;
    readonly Func<int, bool>? _isPhysicallyDown;
    readonly HashSet<int> _down = new();
    readonly HashSet<int> _maskOnRelease = new();
    readonly HashSet<int> _swallowedDown = new();
    bool _chordActive;

    public Hotkey Hotkey { get; private set; }

    /// <param name="isPhysicallyDown">
    /// Optional check of the real key state (GetAsyncKeyState). Used to drop modifiers whose release the hook
    /// never saw, for example after Win+L, so a later plain Ctrl press cannot look like Ctrl+Win.
    /// </param>
    public HotkeyProcessor(Hotkey hotkey, GestureRecognizer recognizer, Func<int, bool>? isPhysicallyDown = null)
    {
        Hotkey = hotkey;
        _recognizer = recognizer;
        _isPhysicallyDown = isPhysicallyDown;
    }

    public GestureRecognizer Recognizer => _recognizer;

    public bool ChordActive => _chordActive;

    public void SetHotkey(Hotkey hotkey)
    {
        Hotkey = hotkey;
        ResetKeys();
    }

    /// <summary>Forgets key state (after the session locks or the hook is reinstalled).</summary>
    public void ResetKeys()
    {
        _down.Clear();
        _maskOnRelease.Clear();
        _swallowedDown.Clear();
        _chordActive = false;
    }

    static bool IsCtrl(int vk) => vk is VK_LCONTROL or VK_RCONTROL or VK_CONTROL;
    static bool IsWin(int vk) => vk is VK_LWIN or VK_RWIN;
    static bool IsAlt(int vk) => vk is VK_LMENU or VK_RMENU or VK_MENU;
    static bool IsShift(int vk) => vk is VK_LSHIFT or VK_RSHIFT or VK_SHIFT;
    public static bool IsModifier(int vk) => IsCtrl(vk) || IsWin(vk) || IsAlt(vk) || IsShift(vk);

    bool AnyDown(Func<int, bool> family) => _down.Any(family);

    bool HotkeyHeld() => HotkeyHeldIn(_down);

    bool HotkeyHeldIn(HashSet<int> keys) => Hotkey switch
    {
        Hotkey.CtrlWin => keys.Any(IsCtrl) && keys.Any(IsWin),
        Hotkey.RightAlt => keys.Contains(VK_RMENU),
        _ => keys.Contains(VK_RCONTROL),
    };

    bool IsCommandModifier(int vk) => Hotkey == Hotkey.RightAlt ? IsShift(vk) : IsAlt(vk);

    bool CommandModifierHeld() => _down.Any(IsCommandModifier);

    /// <summary>Win and Alt releases trigger the Start menu and menu-bar focus; those get masked.</summary>
    static bool NeedsMask(int vk) => IsWin(vk) || IsAlt(vk);

    public HookResult Handle(KeyInput k, double now)
    {
        if (k.OwnInjection) return HookResult.PassNothing;
        var vk = k.Vk;

        if (IsModifier(vk))
        {
            if (vk == VK_LCONTROL && k.ScanCode == AltGrFakeCtrlScan) return HookResult.PassNothing;
            return HandleModifier(vk, k.Down, now);
        }

        if (!k.Down)
        {
            return _swallowedDown.Remove(vk) ? new HookResult(HookVerdict.Swallow, []) : HookResult.PassNothing;
        }
        // Auto-repeat of a key whose first press controlled the session.
        if (_swallowedDown.Contains(vk)) return new HookResult(HookVerdict.Swallow, []);

        if (!_recognizer.IsActive && !_chordActive)
        {
            // Plain typing costs nothing; only the shortcut is checked.
            if (vk is VK_V or VK_S && AnyDown(IsAlt) && AnyDown(IsShift) && !AnyDown(IsCtrl) && !AnyDown(IsWin))
            {
                _swallowedDown.Add(vk);
                return new HookResult(HookVerdict.Swallow, [], vk == VK_V ? HotkeyShortcut.PasteLast : HotkeyShortcut.Scratchpad);
            }
            return HookResult.PassNothing;
        }

        var ev = vk switch
        {
            VK_SPACE => GestureEvent.Space(now),
            VK_ESCAPE => GestureEvent.Escape(now),
            VK_BACK => GestureEvent.Backspace(now),
            _ => GestureEvent.OtherKey(now),
        };
        var actions = _recognizer.Handle(ev);
        // Swallow the keys that controlled the session; let ordinary keys through. Space is always swallowed
        // while the chord is held: Ctrl+Win+Space would otherwise switch the input language.
        var controls = vk is VK_SPACE or VK_ESCAPE or VK_BACK;
        var swallow = controls && (actions.Count > 0 || (vk == VK_SPACE && _chordActive));
        if (swallow) _swallowedDown.Add(vk);
        return new HookResult(swallow ? HookVerdict.Swallow : HookVerdict.Pass, actions);
    }

    HookResult HandleModifier(int vk, bool down, double now)
    {
        var repeat = down && _down.Contains(vk);
        if (down)
        {
            if (!repeat) PruneStale(vk);
            _down.Add(vk);
        }
        else _down.Remove(vk);

        var held = HotkeyHeld();
        if (!_chordActive && held && down)
        {
            _chordActive = true;
            foreach (var d in _down) if (NeedsMask(d)) _maskOnRelease.Add(d);
            var actions = _recognizer.Handle(GestureEvent.HotkeyDown(now, CommandModifierHeld()));
            return new HookResult(HookVerdict.Pass, actions);
        }
        if (_chordActive && !held && !down)
        {
            _chordActive = false;
            var actions = _recognizer.Handle(GestureEvent.HotkeyUp(now));
            return new HookResult(_maskOnRelease.Remove(vk) ? HookVerdict.SwallowAndReleaseWithMask : HookVerdict.Pass, actions);
        }
        if (_chordActive && down && !repeat)
        {
            if (NeedsMask(vk)) _maskOnRelease.Add(vk);
            if (IsCommandModifier(vk))
                return new HookResult(HookVerdict.Pass, _recognizer.Handle(GestureEvent.ControlDown(now)));
            return HookResult.PassNothing;
        }
        if (!down && _maskOnRelease.Remove(vk)) return new HookResult(HookVerdict.SwallowAndReleaseWithMask, []);
        return HookResult.PassNothing;
    }

    /// <summary>
    /// A UAC prompt or another secure desktop hides key releases from the hook. Called now and then while the chord
    /// is held: if the hotkey is no longer really down, it ends as if it had been released. Without this the
    /// microphone would stay on until the next press of the hotkey.
    /// </summary>
    public IReadOnlyList<GestureAction> ReleaseChordIfUp(double now)
    {
        if (!_chordActive || _isPhysicallyDown is null) return [];
        var held = _down.Where(_isPhysicallyDown).ToHashSet();
        if (HotkeyHeldIn(held)) return [];
        _down.IntersectWith(held);
        _maskOnRelease.IntersectWith(held);
        _chordActive = false;
        return _recognizer.Handle(GestureEvent.HotkeyUp(now));
    }

    void PruneStale(int current)
    {
        if (_isPhysicallyDown is null || _down.Count == 0) return;
        _down.RemoveWhere(d => d != current && !_isPhysicallyDown(d));
        _maskOnRelease.RemoveWhere(d => !_down.Contains(d) && d != current);
    }
}
