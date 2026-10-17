using System.Drawing.Drawing2D;

namespace VoiceFlow.Windows.UI;

internal enum BarPhase { Idle, Listening, HandsFree, Command, Processing }

internal sealed record ToastAction(string Title, Action Run);

/// <summary>
/// The Flow bar: a small black pill at the bottom centre of the monitor you are working on. White level bars
/// while recording, three moving dots while processing, ✕ and ✓ in hands-free mode, and brief toasts above it.
/// It never takes focus.
/// </summary>
internal sealed class FlowBarController : IDisposable
{
    readonly BarWindow _bar;
    readonly ToastWindow _toast;
    readonly System.Windows.Forms.Timer _animation = new() { Interval = 33 };
    readonly System.Windows.Forms.Timer _toastTimer = new();
    readonly float[] _history = new float[40];
    bool _showWhenIdle;
    DateTimeOffset? _meetingSince;

    public BarPhase Phase { get; private set; } = BarPhase.Idle;
    public bool MessageVisible => _toast.Visible;

    public event Action? StartHandsFree;
    public event Action? Confirm;
    public event Action? Cancel;
    public event Action? OpenMeetings;

    public FlowBarController()
    {
        _bar = new BarWindow(this);
        _toast = new ToastWindow(this);
        _animation.Tick += (_, _) =>
        {
            _bar.Frame++;
            if (Phase == BarPhase.Processing || _meetingSince != null) Redraw();
        };
        _toastTimer.Tick += (_, _) => DismissMessage();
    }

    public bool ShowWhenIdle
    {
        get => _showWhenIdle;
        set { _showWhenIdle = value; Redraw(); }
    }

    /// <summary>Set while Notetaker records: the resting bar becomes a recording chip.</summary>
    public DateTimeOffset? MeetingSince
    {
        get => _meetingSince;
        set { _meetingSince = value; UpdateAnimation(); Redraw(); }
    }

    public void Set(BarPhase phase)
    {
        Phase = phase;
        if (phase is BarPhase.Listening or BarPhase.HandsFree or BarPhase.Command)
        {
            Array.Clear(_history);
            Array.Fill(_bar.Levels, 0.08f);
        }
        UpdateAnimation();
        Redraw();
    }

    public void PushLevel(float level)
    {
        Array.Copy(_history, 1, _history, 0, _history.Length - 1);
        _history[^1] = level;
        // Symmetric bars: loudest in the middle, recent levels spread outwards.
        var n = _bar.Levels.Length;
        var mid = n / 2;
        for (int i = 0; i < n; i++)
        {
            var distance = Math.Abs(i - mid);
            var sample = _history[_history.Length - 1 - distance * 2];
            var falloff = 1 - distance / (float)(mid + 2);
            _bar.Levels[i] = Math.Max(0.08f, sample * falloff);
        }
        if (Phase is BarPhase.Listening or BarPhase.HandsFree or BarPhase.Command) Redraw();
    }

    /// <summary>
    /// Shows a message above the bar for <paramref name="seconds"/>. A processing bar returns to rest, since the
    /// toast reports how that ended; a recording bar keeps recording.
    /// </summary>
    public void Show(string message, string detail = "", ToastAction? action = null, double seconds = 4)
    {
        if (Phase == BarPhase.Processing) Phase = BarPhase.Idle;
        _toast.Message = message;
        _toast.Detail = detail;
        _toast.Action = action;
        _toastTimer.Stop();
        _toastTimer.Interval = (int)Math.Max(500, seconds * 1000);
        _toastTimer.Start();
        UpdateAnimation();
        Redraw(showToast: true);
    }

    public void DismissMessage()
    {
        _toastTimer.Stop();
        _toast.Action = null;
        if (_toast.Visible) _toast.Hide();
    }

    void UpdateAnimation()
    {
        var animate = Phase == BarPhase.Processing || (_meetingSince != null && Phase == BarPhase.Idle);
        if (animate) _animation.Start(); else _animation.Stop();
    }

    bool BarVisible => Phase != BarPhase.Idle || _showWhenIdle || _meetingSince != null;

    /// <summary>Places both windows on the active monitor and repaints.</summary>
    public void Redraw(bool showToast = false)
    {
        var screen = LayeredWindow.ActiveScreen();
        var scale = LayeredWindow.ScaleFor(screen);
        var area = screen.WorkingArea;
        var margin = (int)(14 * scale);
        int barTop = area.Bottom - margin;
        if (BarVisible)
        {
            var size = _bar.CurrentLogicalSize;
            var w = (int)Math.Ceiling(size.Width * scale);
            var h = (int)Math.Ceiling(size.Height * scale);
            var location = new Point(area.Left + (area.Width - w) / 2, area.Bottom - margin - h);
            _bar.Render(location, scale);
            _bar.ShowNoActivate();
            barTop = location.Y;
        }
        else if (_bar.Visible) _bar.Hide();

        if (showToast || _toast.Visible)
        {
            var size = _toast.Measure(scale);
            var w = (int)Math.Ceiling(size.Width * scale);
            var h = (int)Math.Ceiling(size.Height * scale);
            var location = new Point(area.Left + (area.Width - w) / 2, barTop - (int)(10 * scale) - h);
            _toast.Render(location, scale);
            _toast.ShowNoActivate();
        }
    }

    /// <summary>Self-test: draws every bar state and a toast off-screen without showing anything.</summary>
    internal void RenderOffscreen()
    {
        var offscreen = new Point(-32000, -32000);
        foreach (var phase in Enum.GetValues<BarPhase>())
        {
            Phase = phase;
            _bar.Render(offscreen, 1.5f);
        }
        Phase = BarPhase.Idle;
        _toast.Message = "Self-test";
        _toast.Detail = "Toast layout";
        _toast.Action = new ToastAction("Open", () => { });
        _toast.Measure(1.5f);
        _toast.Render(offscreen, 1.5f);
    }

    internal void OnBarClick(Point p, MouseButtons button)
    {
        if (button != MouseButtons.Left) return;
        switch (Phase)
        {
            case BarPhase.HandsFree:
                if (_bar.CancelRect.Contains(p)) Cancel?.Invoke();
                else if (_bar.ConfirmRect.Contains(p)) Confirm?.Invoke();
                break;
            case BarPhase.Idle:
                if (_meetingSince != null) OpenMeetings?.Invoke();
                else StartHandsFree?.Invoke();
                break;
        }
    }

    internal void OnToastClick(Point p)
    {
        var action = _toast.Action;
        if (action != null && _toast.ActionRect.Contains(p))
        {
            DismissMessage();
            action.Run();
            return;
        }
        DismissMessage();
    }

    public void Dispose()
    {
        _animation.Dispose();
        _toastTimer.Dispose();
        _bar.Dispose();
        _toast.Dispose();
    }

    /// <summary>The pill itself.</summary>
    sealed class BarWindow : LayeredWindow
    {
        readonly FlowBarController _owner;
        public readonly float[] Levels = Enumerable.Repeat(0.08f, 11).ToArray();
        public int Frame;
        public RectangleF CancelRect { get; private set; }
        public RectangleF ConfirmRect { get; private set; }

        public BarWindow(FlowBarController owner)
        {
            _owner = owner;
            MouseUp += (_, e) => _owner.OnBarClick(e.Location, e.Button);
        }

        public SizeF CurrentLogicalSize => LogicalSize;

        protected override SizeF LogicalSize => _owner.Phase switch
        {
            BarPhase.Idle when _owner._meetingSince != null => new SizeF(104, 26),
            BarPhase.Idle => new SizeF(44, 10),
            BarPhase.HandsFree => new SizeF(164, 34),
            BarPhase.Command => new SizeF(170, 34),
            _ => new SizeF(110, 34),
        };

        protected override void PaintContent(Graphics g, float s)
        {
            var size = LogicalSize;
            var rect = new RectangleF(0.5f, 0.5f, size.Width * s - 1, size.Height * s - 1);
            using (var path = Theme.RoundedRect(rect, rect.Height / 2))
            {
                using var fill = new SolidBrush(Color.FromArgb(_owner.Phase == BarPhase.Idle && _owner._meetingSince == null ? 210 : 245, 0, 0, 0));
                g.FillPath(fill, path);
                using var border = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(1, s));
                g.DrawPath(border, path);
            }
            CancelRect = ConfirmRect = RectangleF.Empty;
            switch (_owner.Phase)
            {
                case BarPhase.Idle when _owner._meetingSince is { } since:
                    PaintMeetingChip(g, s, rect, since);
                    break;
                case BarPhase.Listening:
                    PaintBars(g, s, rect.Width / 2);
                    break;
                case BarPhase.Command:
                    using (var font = new Font(Theme.FontName, 8.25f * s, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var brush = new SolidBrush(Color.FromArgb(200, 255, 255, 255)))
                    {
                        var label = "COMMAND";
                        var m = g.MeasureString(label, font);
                        g.DrawString(label, font, brush, 16 * s, (rect.Height - m.Height) / 2 + 0.5f);
                    }
                    PaintBars(g, s, rect.Width - 16 * s - 33 * s);
                    break;
                case BarPhase.HandsFree:
                    PaintBars(g, s, rect.Width / 2);
                    var d = 22 * s;
                    var y = (rect.Height - d) / 2 + 0.5f;
                    CancelRect = new RectangleF(6 * s, y, d, d);
                    ConfirmRect = new RectangleF(rect.Width - 6 * s - d, y, d, d);
                    using (var dark = new SolidBrush(Color.FromArgb(255, 58, 58, 58)))
                        g.FillEllipse(dark, CancelRect);
                    g.FillEllipse(Brushes.White, ConfirmRect);
                    using (var x = new Pen(Color.White, 1.8f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        var c = CancelRect;
                        var k = 6.5f * s;
                        g.DrawLine(x, c.Left + k, c.Top + k, c.Right - k, c.Bottom - k);
                        g.DrawLine(x, c.Right - k, c.Top + k, c.Left + k, c.Bottom - k);
                    }
                    using (var check = new Pen(Color.Black, 2f * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    {
                        var c = ConfirmRect;
                        g.DrawLines(check, new[]
                        {
                            new PointF(c.Left + 6.2f * s, c.Top + 11.5f * s), new PointF(c.Left + 9.6f * s, c.Top + 14.8f * s),
                            new PointF(c.Left + 16f * s, c.Top + 7.6f * s),
                        });
                    }
                    break;
                case BarPhase.Processing:
                    for (int i = 0; i < 3; i++)
                    {
                        var phase = (Frame / 6.0 + i * 0.8) % 3.0;
                        var alpha = (int)(90 + 165 * Math.Max(0, 1 - Math.Abs(phase - 1)));
                        using var dot = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255));
                        var r = 2.6f * s;
                        var cx = rect.Width / 2 + (i - 1) * 10 * s;
                        g.FillEllipse(dot, cx - r, rect.Height / 2 - r, r * 2, r * 2);
                    }
                    break;
            }
        }

        void PaintBars(Graphics g, float s, float centerX)
        {
            var size = LogicalSize;
            var barWidth = 3f * s;
            var gap = 3f * s;
            var n = Levels.Length;
            var total = n * barWidth + (n - 1) * gap;
            var left = centerX - total / 2;
            var maxHeight = (size.Height - 12) * s;
            for (int i = 0; i < n; i++)
            {
                var h = Math.Max(3 * s, Levels[i] * maxHeight);
                var r = new RectangleF(left + i * (barWidth + gap), (size.Height * s - h) / 2, barWidth, h);
                using var path = Theme.RoundedRect(r, barWidth / 2);
                g.FillPath(Brushes.White, path);
            }
        }

        void PaintMeetingChip(Graphics g, float s, RectangleF rect, DateTimeOffset since)
        {
            var pulse = (float)(0.55 + 0.45 * Math.Sin(Frame / 8.0));
            using (var dot = new SolidBrush(Color.FromArgb((int)(255 * pulse), 255, 255, 255)))
                g.FillEllipse(dot, 11 * s, rect.Height / 2 - 3.5f * s, 7 * s, 7 * s);
            var elapsed = DateTimeOffset.Now - since;
            var text = elapsed.TotalHours >= 1 ? elapsed.ToString(@"h\:mm\:ss") : elapsed.ToString(@"m\:ss");
            using var font = new Font(Theme.FontName, 9f * s, FontStyle.Regular, GraphicsUnit.Pixel);
            var label = $"Notes  {text}";
            var m = g.MeasureString(label, font);
            g.DrawString(label, font, Brushes.White, 24 * s, (rect.Height - m.Height) / 2 + 0.5f);
        }
    }

    /// <summary>A message above the bar: title, optional detail, optional action button.</summary>
    sealed class ToastWindow : LayeredWindow
    {
        readonly FlowBarController _owner;
        public string Message = "";
        public string Detail = "";
        public ToastAction? Action;
        public RectangleF ActionRect { get; private set; }
        SizeF _logical = new(320, 48);
        const float ToastWidth = 340, Pad = 14;

        public ToastWindow(FlowBarController owner)
        {
            _owner = owner;
            MouseUp += (_, e) => _owner.OnToastClick(e.Location);
        }

        protected override SizeF LogicalSize => _logical;

        Font TitleFont(float s) => new(Theme.FontName, 13f * s, FontStyle.Bold, GraphicsUnit.Pixel);
        Font DetailFont(float s) => new(Theme.FontName, 12f * s, FontStyle.Regular, GraphicsUnit.Pixel);

        /// <summary>Lays out the text for this scale and returns the logical size.</summary>
        public SizeF Measure(float s)
        {
            using var bmp = new Bitmap(1, 1);
            using var g = Graphics.FromImage(bmp);
            using var title = TitleFont(s);
            using var detail = DetailFont(s);
            var textWidth = (ToastWidth - Pad * 2) * s;
            var h = Pad * s + g.MeasureString(Message, title, (int)textWidth).Height;
            if (Detail.Length > 0) h += 3 * s + Math.Min(g.MeasureString(Detail, detail, (int)textWidth).Height, detail.GetHeight(g) * 4);
            if (Action != null) h += 10 * s + 26 * s;
            h += Pad * s;
            _logical = new SizeF(ToastWidth, h / s);
            return _logical;
        }

        protected override void PaintContent(Graphics g, float s)
        {
            var rect = new RectangleF(0.5f, 0.5f, _logical.Width * s - 1, _logical.Height * s - 1);
            using (var path = Theme.RoundedRect(rect, 12 * s))
            {
                using var fill = new SolidBrush(Color.FromArgb(248, 0, 0, 0));
                g.FillPath(fill, path);
                using var border = new Pen(Color.FromArgb(60, 255, 255, 255), Math.Max(1, s));
                g.DrawPath(border, path);
            }
            using var title = TitleFont(s);
            using var detail = DetailFont(s);
            var textWidth = (ToastWidth - Pad * 2) * s;
            var y = Pad * s;
            var titleSize = g.MeasureString(Message, title, (int)textWidth);
            g.DrawString(Message, title, Brushes.White, new RectangleF(Pad * s, y, textWidth, titleSize.Height));
            y += titleSize.Height;
            if (Detail.Length > 0)
            {
                y += 3 * s;
                var dh = Math.Min(g.MeasureString(Detail, detail, (int)textWidth).Height, detail.GetHeight(g) * 4);
                using var grey = new SolidBrush(Color.FromArgb(200, 200, 200));
                using var format = new StringFormat { Trimming = StringTrimming.EllipsisWord };
                g.DrawString(Detail, detail, grey, new RectangleF(Pad * s, y, textWidth, dh), format);
                y += dh;
            }
            ActionRect = RectangleF.Empty;
            if (Action != null)
            {
                y += 10 * s;
                var label = g.MeasureString(Action.Title, title);
                var w = label.Width + 24 * s;
                ActionRect = new RectangleF(rect.Right - Pad * s - w, y, w, 26 * s);
                using var pill = Theme.RoundedRect(ActionRect, 13 * s);
                g.FillPath(Brushes.White, pill);
                g.DrawString(Action.Title, title, Brushes.Black, ActionRect.X + 12 * s, ActionRect.Y + (ActionRect.Height - label.Height) / 2);
            }
        }
    }
}
