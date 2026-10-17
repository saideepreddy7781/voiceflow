using System.ComponentModel;
using System.Runtime.InteropServices;
using static VoiceFlow.Windows.Native.NativeMethods;

namespace VoiceFlow.Windows.Native;

/// <summary>
/// A WH_KEYBOARD_LL hook. It runs on the thread that installs it (HotkeyMonitor's hook thread), which must keep
/// pumping messages. Windows removes a low-level hook without notice when callbacks are slow (LowLevelHooksTimeout),
/// so the handler must only decide and return; real work is posted to the message loop.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    /// <summary>Return true to block the key.</summary>
    public delegate bool Handler(int vk, int scanCode, bool down, uint flags, IntPtr extraInfo);

    readonly LowLevelKeyboardProc _proc;   // kept in a field so the GC never collects the callback
    readonly Handler _handler;
    IntPtr _hook;

    public KeyboardHook(Handler handler)
    {
        _handler = handler;
        _proc = Callback;
    }

    public bool IsInstalled => _hook != IntPtr.Zero;

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(null), 0);
        if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not install the keyboard hook.");
    }

    public void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    /// <summary>Reinstalls the hook (after unlock or resume, when Windows may have dropped it).</summary>
    public void Reinstall()
    {
        Uninstall();
        Install();
    }

    IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var message = (int)wParam;
                var down = message is WM_KEYDOWN or WM_SYSKEYDOWN;
                if (_handler((int)data.vkCode, (int)data.scanCode, down, data.flags, (IntPtr)(long)data.dwExtraInfo.ToUInt64()))
                    return new IntPtr(1);
            }
            catch (Exception e)
            {
                // Never let an exception escape into the hook chain: the key would be lost or the hook removed.
                Logger.Error("Keyboard hook handler failed", e);
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}
