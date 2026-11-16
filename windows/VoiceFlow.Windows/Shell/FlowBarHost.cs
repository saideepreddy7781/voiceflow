using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using VoiceFlow.Core.FlowBar;
using VoiceFlow.Desktop.Services;
using VoiceFlow.Desktop.ViewModels;
using VoiceFlow.Desktop.Views;
using VoiceFlow.Windows.Native;
using static VoiceFlow.Windows.Native.NativeMethods;

namespace VoiceFlow.Windows.Shell;

/// <summary>
/// Shows the Flow bar window. It is borderless, transparent and always on top, and it never takes focus. It is on
/// screen only while there is something to show.
///
/// Only the visible parts of the bar (the capsule, a toast, the menu) take clicks; everywhere else in its window
/// belongs to the app underneath, in any process. That is done with a window region shaped like those parts:
/// Windows hit-tests a window against its region before any message is sent, so clicks outside it go straight to
/// the window below, with no polling and no hook. WM_NCHITTEST answers HTTRANSPARENT outside the shape as a second
/// line, which covers our own windows. If a probe at first show finds Windows still hit-testing the whole
/// rectangle, the bar falls back to switching WS_EX_TRANSPARENT from the cursor position on its 33 ms timer.
///
/// The user can drag the bar anywhere. Its position is saved per arrangement of displays and clamped to the work
/// area whenever displays, the taskbar or the scale change.
/// </summary>
internal sealed class FlowBarHost : IDisposable
{
    /// <summary>How far outside a part's outline the region reaches, in physical pixels, so anti-aliased edges are not cut.</summary>
    const double EdgeAllowance = 1;
    /// <summary>The menu closes once the pointer has been away from the bar this long.</summary>
    static readonly TimeSpan MenuIdle = TimeSpan.FromSeconds(2.5);

    readonly FlowBarController _bar;
    readonly FlowBarViewModel _viewModel;
    readonly IPreferencesService _preferences;
    readonly FlowBarWindow _window;
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly IntPtr _handle;
    readonly LowLevelKeyboardProc _focusGuard;   // kept in a field so the GC never collects the callback
    readonly IntPtr _focusHook;
    RoundedBox[] _parts = [];   // the visible parts in physical pixels from the client area's top-left corner
    bool _clickThrough;          // WS_EX_TRANSPARENT, used only by the fallback
    bool _probing;
    DateTime? _menuAwaySince;

    // Dragging: the capsule's centre on screen, and where the pointer holds it, in device-independent pixels.
    bool _dragging;
    Vector _grab;
    PxPoint _dragCentre;
    bool _dragHome;

    public FlowBarHost(FlowBarController bar, FlowBarViewModel viewModel, IPreferencesService preferences)
    {
        _bar = bar;
        _viewModel = viewModel;
        _preferences = preferences;
        _window = new FlowBarWindow { DataContext = viewModel, AllowDrag = !preferences.Current.FlowBarLocked };
        _handle = _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        // Avalonia calls SetFocus on every mouse press, and SetFocus activates a window even with WS_EX_NOACTIVATE.
        // Refusing it here keeps the keyboard focus in the app being typed in.
        _focusGuard = OnFocusRequest;
        _focusHook = SetWindowsHookExW(WH_CBT, _focusGuard, IntPtr.Zero, GetCurrentThreadId());
        if (_focusHook == IntPtr.Zero) Logger.Warn($"Could not guard the Flow bar's focus (error {Marshal.GetLastWin32Error()})");
        ApplyStyles();
        RemoveSystemFrame();
        _tick.Tick += (_, _) => OnTick();
        _window.SizeChanged += (_, _) => Place();
        _window.ScalingChanged += (_, _) => Place();
        _window.LayoutUpdated += (_, _) => UpdateShape();
        _window.DragStarted += OnDragStarted;
        _window.DragMoved += _ => OnDragMoved();
        _window.DragEnded += _ => OnDragEnded();
        _window.MenuRequested += () => _viewModel.ShowMenu = !_viewModel.ShowMenu;
        // Avalonia rebuilds the extended style from nothing whenever it shows the window, so these flags live in its callback too.
        Win32Properties.AddWindowStylesCallback(_window, (style, exStyle) => (style, exStyle | ExtendedStyles));
        Win32Properties.AddWndProcHookCallback(_window, HookWndProc);
        bar.Changed += OnChanged;
        preferences.Changed += OnPreferencesChanged;
    }

    /// <summary>The native handle, for the self-test.</summary>
    public IntPtr Handle => _handle;

    public FlowBarWindow Window => _window;

    public FlowBarViewModel ViewModel => _viewModel;

    /// <summary>The bar's visible parts in physical pixels from the client area's top-left corner.</summary>
    public IReadOnlyList<RoundedBox> Parts => _parts;

    /// <summary>Null until the bar has been shown once; then whether Windows honours the region for clicks.</summary>
    public bool? RegionHonoured { get; private set; }

    /// <summary>Where the capsule's centre is on screen, once it has been placed.</summary>
    public PxPoint? Anchor { get; private set; }

    // MARK: Window procedure and focus

    IntPtr HookWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_MOUSEACTIVATE:
                // Clicking the bar must leave the keyboard focus in the app the user is typing in.
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
            case WM_NCHITTEST when !_probing && !IsInShape(ScreenPoint(lParam)):
                handled = true;
                return new IntPtr(HTTRANSPARENT);
            default:
                return IntPtr.Zero;
        }
    }

    static POINT ScreenPoint(IntPtr lParam)
    {
        var value = lParam.ToInt64();
        return new POINT { X = (short)(value & 0xFFFF), Y = (short)((value >> 16) & 0xFFFF) };
    }

    bool IsInShape(POINT screen)
    {
        if (!ScreenToClient(_handle, ref screen)) return true;
        return BarShape.HitTest(_parts, screen.X, screen.Y);
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

    /// <summary>No rounded corners or outline from Windows 11 around the transparent rectangle.</summary>
    void RemoveSystemFrame()
    {
        if (_handle == IntPtr.Zero) return;
        var corners = (uint)DWMWCP_DONOTROUND;
        var border = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(_handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(uint));
        DwmSetWindowAttribute(_handle, DWMWA_BORDER_COLOR, ref border, sizeof(uint));
    }

    // MARK: Shape

    /// <summary>Sets the window region again, for the self-test after it has removed it.</summary>
    public void ReapplyShape()
    {
        _parts = [];
        UpdateShape();
    }

    void UpdateShape()
    {
        if (_handle == IntPtr.Zero || !_window.IsVisible) return;
        var parts = BarShape.ToPixels(_window.ShapeParts(), _window.RenderScaling).Select(p => p.Inflated(EdgeAllowance)).ToArray();
        if (parts.SequenceEqual(_parts)) return;
        _parts = parts;
        ApplyRegion();
    }

    void ApplyRegion()
    {
        // The region is in window coordinates; the client area of a borderless window normally starts at its corner.
        var origin = new POINT();
        if (!ClientToScreen(_handle, ref origin) || !GetWindowRect(_handle, out var frame)) return;
        int dx = origin.X - frame.Left, dy = origin.Y - frame.Top;
        var region = CreateRectRgn(0, 0, 0, 0);
        foreach (var p in _parts)
        {
            var d = (int)Math.Ceiling(2 * p.Radius);
            // CreateRoundRectRgn leaves out its right and bottom edges, hence the + 1.
            var part = CreateRoundRectRgn((int)Math.Floor(p.X) + dx, (int)Math.Floor(p.Y) + dy,
                                          (int)Math.Ceiling(p.Right) + dx + 1, (int)Math.Ceiling(p.Bottom) + dy + 1, d, d);
            CombineRgn(region, region, part, RGN_OR);
            DeleteObject(part);
        }
        if (SetWindowRgn(_handle, region, true) == 0)
        {
            DeleteObject(region);
            Logger.Warn($"Could not shape the Flow bar (error {Marshal.GetLastWin32Error()})");
        }
    }

    /// <summary>
    /// Once, after the bar first shows: asks Windows which window owns a point inside the bar's rectangle but
    /// outside its shape. Any answer but the bar means the region works for clicks.
    /// </summary>
    void ProbeRegion()
    {
        if (RegionHonoured != null || _parts.Length == 0 || !GetWindowRect(_handle, out var frame)) return;
        var origin = new POINT();
        ClientToScreen(_handle, ref origin);
        foreach (var (x, y) in new[] { (frame.Left, frame.Top), (frame.Right - 1, frame.Top), (frame.Left, frame.Bottom - 1), (frame.Right - 1, frame.Bottom - 1) })
        {
            if (BarShape.HitTest(_parts, x - origin.X, y - origin.Y)) continue;
            _probing = true;   // WM_NCHITTEST would otherwise answer for the region
            IntPtr hit;
            try { hit = WindowFromPoint(new POINT { X = x, Y = y }); }
            finally { _probing = false; }
            RegionHonoured = GetAncestor(hit, GA_ROOT) != _handle;
            if (RegionHonoured == false) Logger.Warn("The Flow bar's window region does not pass clicks through; switching click-through from the cursor instead");
            return;
        }
    }

    // MARK: Showing

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
            if (_dragging) OnDragEnded();
            _viewModel.ShowMenu = false;
            _window.Hide();
            SetClickThrough(false);
        }
    }

    void OnPreferencesChanged(object? sender, EventArgs e)
    {
        _window.AllowDrag = !_preferences.Current.FlowBarLocked;
        if (_window.IsVisible) Place();
    }

    void OnTick()
    {
        if (!_window.IsVisible) return;
        Place();
        UpdateShape();
        ProbeRegion();
        if (!GetCursorPos(out var cursor)) return;
        var inside = IsInShape(cursor);
        if (RegionHonoured == false && !_dragging) SetClickThrough(!inside);
        if (!_viewModel.ShowMenu || inside) _menuAwaySince = null;
        else if (_menuAwaySince is not { } since) _menuAwaySince = DateTime.UtcNow;
        else if (DateTime.UtcNow - since > MenuIdle) _viewModel.ShowMenu = false;
    }

    void SetClickThrough(bool value)
    {
        if (value == _clickThrough) return;
        _clickThrough = value;
        ApplyStyles();
    }

    // MARK: Placement

    /// <summary>Puts the capsule where it belongs: where the user left it on this arrangement of displays, or bottom centre.</summary>
    void Place()
    {
        var monitors = Monitors(out var active);
        if (monitors.Count == 0) return;
        // Before the first layout pass the whole window stands in for the capsule.
        var anchor = _window.AnchorRect() ?? new Rect(_window.ClientSize);
        PxPoint centre;
        BarMonitor monitor;
        if (_dragging)
        {
            centre = _dragCentre;
            monitor = BarPlacement.MonitorFor(centre, monitors);
        }
        else
        {
            (centre, monitor, _) = BarPlacement.Resolve(_preferences.Current.FlowBarPositions, monitors, active, anchor.Width, anchor.Height);
        }
        Anchor = centre;
        var scale = monitor.Scale;
        PlaceExtras(centre, monitor);
        var position = new PixelPoint(centre.X - (int)Math.Round(anchor.Center.X * scale), centre.Y - (int)Math.Round(anchor.Center.Y * scale));
        if (_window.Position != position) _window.Position = position;
    }

    /// <summary>Opens the toast and menu on the side of the capsule with room, shifted to stay on screen.</summary>
    void PlaceExtras(PxPoint centre, BarMonitor monitor)
    {
        if (_window.BarLayout is not { } layout) return;
        var capsule = layout.CapsuleSize;
        var extras = layout.ExtrasSize;
        if (capsule.Width <= 0 || extras.Width <= 0) return;
        var scale = monitor.Scale;
        var (below, offset) = BarPlacement.Extras(centre, BarPlacement.ToPixels(capsule.Width, capsule.Height, scale),
            BarPlacement.ToPixels(extras.Width, extras.Height, scale), (int)Math.Round(layout.Spacing * scale), monitor.WorkArea, scale);
        _window.SetExtras(below, offset / scale);
    }

    /// <summary>Every display, and the one the user is working on: the foreground window's, else the pointer's.</summary>
    static List<BarMonitor> Monitors(out BarMonitor active)
    {
        var handles = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) => { handles.Add(h); return true; }, IntPtr.Zero);
        var monitors = new List<BarMonitor>();
        active = default;
        var activeHandle = ActiveMonitor();
        foreach (var handle in handles)
        {
            if (Describe(handle) is not { } m) continue;
            monitors.Add(m);
            if (handle == activeHandle || monitors.Count == 1) active = m;
        }
        return monitors;
    }

    static BarMonitor? Describe(IntPtr monitor)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfoW(monitor, ref info)) return null;
        var scale = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;
        static PxRect R(RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        return new BarMonitor(R(info.rcMonitor), R(info.rcWork), scale);
    }

    static IntPtr ActiveMonitor()
    {
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && !FocusInfo.IsOwnWindow(foreground)) return MonitorFromWindow(foreground, MONITOR_DEFAULTTONEAREST);
        return GetCursorPos(out var point) ? MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST) : IntPtr.Zero;
    }

    // MARK: Dragging

    /// <param name="pressed">Where the button went down, on screen. The pointer is already a few pixels further on.</param>
    void OnDragStarted(PixelPoint pressed)
    {
        if (Anchor is not { } anchor) return;
        var monitors = Monitors(out _);
        if (monitors.Count == 0) return;
        var scale = BarPlacement.MonitorFor(new PxPoint(pressed.X, pressed.Y), monitors).Scale;
        _grab = new Vector((anchor.X - pressed.X) / scale, (anchor.Y - pressed.Y) / scale);
        _dragCentre = anchor;
        _dragHome = false;
        _dragging = true;
        _viewModel.ShowMenu = false;
        SetClickThrough(false);
    }

    void OnDragMoved()
    {
        if (!_dragging || !GetCursorPos(out var cursor)) return;
        var monitors = Monitors(out _);
        if (monitors.Count == 0) return;
        var pointer = new PxPoint(cursor.X, cursor.Y);
        var monitor = BarPlacement.MonitorFor(pointer, monitors);
        var anchor = _window.AnchorRect() ?? new Rect(_window.ClientSize);
        var size = BarPlacement.ToPixels(anchor.Width, anchor.Height, monitor.Scale);
        var held = new PxPoint(pointer.X + (int)Math.Round(_grab.X * monitor.Scale), pointer.Y + (int)Math.Round(_grab.Y * monitor.Scale));
        (_dragCentre, _dragHome) = BarPlacement.Snap(held, size, monitor.WorkArea, monitor.Scale);
        Place();
    }

    /// <summary>Saves where the bar was dropped. Dropped on its default spot, it goes back to following the active display.</summary>
    void OnDragEnded()
    {
        if (!_dragging) return;
        _dragging = false;
        var layout = BarPlacement.LayoutKey(Monitors(out _));
        var centre = _dragCentre;
        var home = _dragHome;
        _preferences.Update(p => p.FlowBarPositions = home ? BarPlacement.Forget(p.FlowBarPositions, layout) : BarPlacement.Remember(p.FlowBarPositions, layout, centre));
        Place();
    }

    public void Dispose()
    {
        _bar.Changed -= OnChanged;
        _preferences.Changed -= OnPreferencesChanged;
        _tick.Stop();
        if (_focusHook != IntPtr.Zero) UnhookWindowsHookEx(_focusHook);
        _window.Close();
    }
}
