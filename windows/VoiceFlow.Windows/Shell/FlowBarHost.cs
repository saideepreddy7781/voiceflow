using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using VoiceFlow.Desktop.ViewModels;
using VoiceFlow.Desktop.Views;
using VoiceFlow.Windows.Native;
using static VoiceFlow.Windows.Native.NativeMethods;

namespace VoiceFlow.Windows.Shell;

/// <summary>
/// Shows the Flow bar window. It is borderless, transparent and always on top, it never takes focus, and clicks
/// pass through it except on its buttons and toasts. It is on screen only while there is something to show.
/// </summary>
internal sealed class FlowBarHost : IDisposable
{
    readonly FlowBarController _bar;
    readonly FlowBarWindow _window;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly IntPtr _handle;
    readonly LowLevelKeyboardProc _focusGuard;   // kept in a field so the GC never collects the callback
    readonly IntPtr _focusHook;
    bool _clickThrough = true;

    public FlowBarHost(FlowBarController bar, FlowBarViewModel viewModel)
    {
        _bar = bar;
        _window = new FlowBarWindow { DataContext = viewModel };
        _handle = _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        // Avalonia calls SetFocus on every mouse press, and SetFocus activates a window even with WS_EX_NOACTIVATE.
        // Refusing it here keeps the keyboard focus in the app being typed in.
        _focusGuard = OnFocusRequest;
        _focusHook = SetWindowsHookExW(WH_CBT, _focusGuard, IntPtr.Zero, GetCurrentThreadId());
        if (_focusHook == IntPtr.Zero) Logger.Warn($"Could not guard the Flow bar's focus (error {Marshal.GetLastWin32Error()})");
        ApplyStyles();
        _tick.Tick += (_, _) => OnTick();
        _window.SizeChanged += (_, _) => Place();
        _window.ScalingChanged += (_, _) => Place();
        // Avalonia rebuilds the extended style from nothing whenever it shows the window, so these flags live in its callback too.
        Win32Properties.AddWindowStylesCallback(_window, (style, exStyle) => (style, exStyle | ExtendedStyles));
        // Clicking the bar must leave the keyboard focus in the app the user is typing in.
        Win32Properties.AddWndProcHookCallback(_window, HookWndProc);
        bar.Changed += OnChanged;
    }

    /// <summary>The native handle, for the self-test.</summary>
    public IntPtr Handle => _handle;

    public FlowBarWindow Window => _window;

    static IntPtr HookWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_MOUSEACTIVATE) return IntPtr.Zero;
        handled = true;
        return new IntPtr(MA_NOACTIVATE);
    }

    IntPtr OnFocusRequest(int code, IntPtr wParam, IntPtr lParam)
    {
        if ((code == HCBT_SETFOCUS || code == HCBT_ACTIVATE) && wParam == _handle) return new IntPtr(1);
        return CallNextHookEx(_focusHook, code, wParam, lParam);
    }

    uint ExtendedStyles => (uint)(WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_LAYERED | (_clickThrough ? WS_EX_TRANSPARENT : 0));

    void ApplyStyles()
    {
        if (_handle == IntPtr.Zero) return;
        var style = GetWindowLongPtrW(_handle, GWL_EXSTYLE).ToInt64() & ~(long)WS_EX_TRANSPARENT;
        SetWindowLongPtrW(_handle, GWL_EXSTYLE, new IntPtr(style | ExtendedStyles));
        // A layered window draws nothing until this has been called once; 255 leaves per-pixel alpha alone.
        SetLayeredWindowAttributes(_handle, 0, 255, LWA_ALPHA);
    }

    void OnChanged(object? sender, EventArgs e)
    {
        if (_bar.ShouldShow)
        {
            if (!_window.IsVisible)
            {
                Place();
                _window.Show();
                ApplyStyles();
                _tick.Start();
            }
            Place();
        }
        else if (_window.IsVisible)
        {
            _tick.Stop();
            _window.Hide();
            _clickThrough = true;   // a click on a button may have hidden the bar with the pointer still over it
            ApplyStyles();
        }
    }

    void OnTick()
    {
        if (!_window.IsVisible) return;
        Place();
        if (GetCursorPos(out var point)) SetClickThrough(!_window.IsInteractiveAt(new PixelPoint(point.X, point.Y)));
    }

    void SetClickThrough(bool value)
    {
        if (value == _clickThrough) return;
        _clickThrough = value;
        ApplyStyles();
    }

    /// <summary>Bottom centre of the monitor the user is working on, in that monitor's own DPI.</summary>
    void Place()
    {
        var monitor = ActiveMonitor();
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfoW(monitor, ref info)) return;
        var scale = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : _window.RenderScaling;
        var r = info.rcWork;
        var size = PixelSize.FromSize(_window.ClientSize, scale);
        var position = FlowBarWindow.BottomCentre(new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), size, scale);
        if (_window.Position != position) _window.Position = position;
    }

    static IntPtr ActiveMonitor()
    {
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && !FocusInfo.IsOwnWindow(foreground)) return MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);
        return GetCursorPos(out var point) ? MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST) : IntPtr.Zero;
    }

    public void Dispose()
    {
        _bar.Changed -= OnChanged;
        _tick.Stop();
        if (_focusHook != IntPtr.Zero) UnhookWindowsHookEx(_focusHook);
        _window.Close();
    }
}
