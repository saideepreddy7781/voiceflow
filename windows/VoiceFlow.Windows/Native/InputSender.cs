using System.Runtime.InteropServices;
using VoiceFlow.Core.Input;
using static VoiceFlow.Windows.Native.NativeMethods;

namespace VoiceFlow.Windows.Native;

/// <summary>Synthetic key presses. Every event carries <see cref="Marker"/> so VoiceFlow's own hook ignores it.</summary>
internal static class InputSender
{
    /// <summary>dwExtraInfo on VoiceFlow's injected events ("VFLW").</summary>
    public static readonly IntPtr Marker = new(0x56464C57);

    public static int InputSize => Marshal.SizeOf<INPUT>();

    const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_INSERT = 0x2D, VK_A = 0x41, VK_C = 0x43, VK_V = 0x56,
        VK_Z = 0x5A, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5,
        VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1;

    static bool IsExtended(int vk) => vk is VK_RCONTROL or VK_RMENU or VK_LWIN or VK_RWIN or VK_INSERT;

    public static INPUT Key(int vk, bool up)
    {
        var flags = up ? KEYEVENTF_KEYUP : 0;
        if (IsExtended(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = (ushort)vk,
                    wScan = (ushort)MapVirtualKeyW((uint)vk, 0),
                    dwFlags = flags,
                    dwExtraInfo = Marker,
                },
            },
        };
    }

    public static bool Send(params INPUT[] inputs) =>
        inputs.Length == 0 || SendInput((uint)inputs.Length, inputs, InputSize) == inputs.Length;

    /// <summary>
    /// Called from inside the hook for a Win or Alt release that ended a VoiceFlow gesture: an unassigned key
    /// goes down and up first, so Windows does not treat the release as "Win pressed alone" (Start menu) or
    /// "Alt pressed alone" (menu bar).
    /// </summary>
    public static void ReleaseWithMask(int vk) =>
        Send(Key(HotkeyProcessor.MaskVk, false), Key(HotkeyProcessor.MaskVk, true), Key(vk, true));

    static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// Waits (up to <paramref name="timeout"/>) until the user has let go of Win, Alt, Shift and Ctrl, so a
    /// synthetic Ctrl+V does not become Win+Ctrl+V. Returns false if keys were still held.
    /// </summary>
    public static async Task<bool> WaitForModifiersReleasedAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            var held = Down(VK_LWIN) || Down(VK_RWIN) || Down(VK_LMENU) || Down(VK_RMENU) || Down(VK_LSHIFT) || Down(VK_RSHIFT)
                       || Down(VK_LCONTROL) || Down(VK_RCONTROL);
            if (!held) return true;
            if (DateTime.UtcNow >= until) return false;
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Last resort when <see cref="WaitForModifiersReleasedAsync"/> timed out: injects key-ups for the modifiers
    /// still down, so the next chord reaches the app as Ctrl+V and not Win+Ctrl+V or Alt+Ctrl+V. A masked key goes
    /// first so the Win and Alt releases do not open the Start menu or the menu bar.
    /// </summary>
    public static void ReleaseHeldModifiers()
    {
        var inputs = new List<INPUT>();
        foreach (var vk in new[] { VK_LWIN, VK_RWIN, VK_LMENU, VK_RMENU, VK_LSHIFT, VK_RSHIFT, VK_LCONTROL, VK_RCONTROL })
            if (Down(vk)) inputs.Add(Key(vk, true));
        if (inputs.Count == 0) return;
        inputs.Insert(0, Key(HotkeyProcessor.MaskVk, true));
        inputs.Insert(0, Key(HotkeyProcessor.MaskVk, false));
        Send(inputs.ToArray());
    }

    /// <summary>Waits for the user to let go of modifiers; if they are still held after the timeout, releases them.</summary>
    public static async Task PrepareForChordAsync()
    {
        if (await WaitForModifiersReleasedAsync(TimeSpan.FromSeconds(1.5))) return;
        Logger.Warn("Keys still held before a synthetic shortcut; releasing them");
        ReleaseHeldModifiers();
    }

    static bool Chord(int modifier, int key) =>
        Send(Key(modifier, false), Key(key, false), Key(key, true), Key(modifier, true));

    public static bool CtrlV() => Chord(VK_CONTROL, VK_V);
    public static bool ShiftInsert() => Chord(VK_SHIFT, VK_INSERT);
    public static bool CtrlC() => Chord(VK_CONTROL, VK_C);
    public static bool CtrlA() => Chord(VK_CONTROL, VK_A);
    public static bool CtrlZ() => Chord(VK_CONTROL, VK_Z);

    /// <summary>A key nothing listens to, for the self-test.</summary>
    public static bool TapMaskKey() => Send(Key(HotkeyProcessor.MaskVk, false), Key(HotkeyProcessor.MaskVk, true));
}
