using System.ComponentModel;
using System.Runtime.InteropServices;
using static VoiceFlow.Windows.Native.NativeMethods;

namespace VoiceFlow.Windows.Native;

/// <summary>A hidden message-only window on the UI thread: the clipboard owner.</summary>
internal sealed class MessageWindow : IDisposable
{
    public IntPtr Handle { get; private set; }

    public MessageWindow()
    {
        // The built-in STATIC class needs no window procedure of ours, and the UI thread's message loop serves it.
        Handle = CreateWindowExW(0, "STATIC", "VoiceFlow", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the clipboard owner window.");
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        DestroyWindow(Handle);
        Handle = IntPtr.Zero;
    }
}
