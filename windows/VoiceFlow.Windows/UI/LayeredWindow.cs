using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using VoiceFlow.Windows.Native;
using static VoiceFlow.Windows.Native.NativeMethods;

namespace VoiceFlow.Windows.UI;

/// <summary>The black-and-white palette and fonts shared by every window.</summary>
internal static class Theme
{
    public static readonly Color Black = Color.FromArgb(0, 0, 0);
    public static readonly Color Ink = Color.FromArgb(17, 17, 17);
    public static readonly Color White = Color.White;
    public static readonly Color Paper = Color.FromArgb(250, 250, 250);
    public static readonly Color Line = Color.FromArgb(229, 229, 229);
    public static readonly Color Muted = Color.FromArgb(107, 107, 107);
    public static readonly Color Soft = Color.FromArgb(242, 242, 242);
    public static readonly Color Error = Color.FromArgb(17, 17, 17);

    public const string FontName = "Segoe UI";
    public static Font Body { get; } = new(FontName, 9.75f);
    public static Font Small { get; } = new(FontName, 8.75f);
    public static Font Strong { get; } = new(FontName, 9.75f, FontStyle.Bold);
    public static Font Heading { get; } = new("Segoe UI Semibold", 18f);
    public static Font Subheading { get; } = new("Segoe UI Semibold", 11.25f);
    public static Font Stat { get; } = new("Segoe UI Semibold", 20f);

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

/// <summary>
/// A borderless, per-pixel-alpha window that never takes focus: WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW +
/// WS_EX_TOPMOST, shown without activation, and WM_MOUSEACTIVATE answered with MA_NOACTIVATE so clicking it
/// leaves the keyboard focus in the app the user is typing in. Painted with UpdateLayeredWindow, so rounded
/// edges are antialiased.
/// </summary>
internal abstract class LayeredWindow : Form
{
    Point _location;
    Size _size;

    protected LayeredWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        ControlBox = false;
        Text = "VoiceFlow";
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = (IntPtr)MA_NOACTIVATE;
            return;
        }
        // Moving to a monitor with another DPI: WinForms would rescale the form and SetWindowPos it to the
        // suggested rectangle, which stretches the UpdateLayeredWindow bitmap. Render() sizes it for the DPI itself.
        if (m.Msg == WM_DPICHANGED)
        {
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }

    /// <summary>The DPI scale this window was last drawn at (1.0 = 96 DPI).</summary>
    protected float CurrentScale { get; private set; } = 1;

    /// <summary>Size in device-independent pixels for the current state.</summary>
    protected abstract SizeF LogicalSize { get; }

    protected abstract void PaintContent(Graphics g, float scale);

    /// <summary>Draws the window at <paramref name="location"/> (physical pixels) with <paramref name="scale"/>.</summary>
    public void Render(Point location, float scale)
    {
        CurrentScale = scale;
        var logical = LogicalSize;
        var size = new Size(Math.Max(1, (int)Math.Ceiling(logical.Width * scale)), Math.Max(1, (int)Math.Ceiling(logical.Height * scale)));
        using var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            // ClearType needs an opaque background; on a transparent bitmap grayscale antialiasing is correct.
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            PaintContent(g, scale);
        }
        Apply(bitmap, location);
        _location = location;
        _size = size;
    }

    void Apply(Bitmap bitmap, Point location)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = IntPtr.Zero;
        var old = IntPtr.Zero;
        try
        {
            hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
            old = SelectObject(memDc, hBitmap);
            var size = new SIZE { CX = bitmap.Width, CY = bitmap.Height };
            var src = new POINT();
            var dst = new POINT { X = location.X, Y = location.Y };
            var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(memDc, old);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public Rectangle ScreenBounds => new(_location, _size);

    /// <summary>Shows without activating and keeps it above other topmost windows.</summary>
    public void ShowNoActivate()
    {
        if (!Visible) Show();
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>The monitor the user is working on: the foreground window's, or the one under the mouse.</summary>
    public static Screen ActiveScreen()
    {
        var fg = GetForegroundWindow();
        if (fg != IntPtr.Zero && !FocusInfo.IsOwnWindow(fg)) return Screen.FromHandle(fg);
        return Screen.FromPoint(Cursor.Position);
    }

    /// <summary>Effective DPI scale of a screen (Per-Monitor V2: each monitor has its own).</summary>
    public static float ScaleFor(Screen screen)
    {
        var center = new POINT { X = screen.Bounds.X + screen.Bounds.Width / 2, Y = screen.Bounds.Y + screen.Bounds.Height / 2 };
        try
        {
            var monitor = MonitorFromPoint(center, MONITOR_DEFAULTTONEAREST);
            if (GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0) return dpiX / 96f;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return 1;
    }
}
