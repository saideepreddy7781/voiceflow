namespace VoiceFlow.Windows.UI;

/// <summary>Small factory helpers so every page uses the same black-and-white controls.</summary>
internal static class Ui
{
    public static Button Primary(string text, EventHandler? click = null)
    {
        var b = Base(text, click);
        b.BackColor = Theme.Black;
        b.ForeColor = Theme.White;
        b.FlatAppearance.BorderColor = Theme.Black;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(48, 48, 48);
        b.FlatAppearance.MouseDownBackColor = Color.FromArgb(80, 80, 80);
        return b;
    }

    public static Button Secondary(string text, EventHandler? click = null)
    {
        var b = Base(text, click);
        b.BackColor = Theme.White;
        b.ForeColor = Theme.Black;
        b.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
        b.FlatAppearance.MouseOverBackColor = Theme.Soft;
        b.FlatAppearance.MouseDownBackColor = Theme.Line;
        return b;
    }

    static Button Base(string text, EventHandler? click)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 4, 10, 4),
            Margin = new Padding(0, 4, 8, 4),
            Font = Theme.Body,
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderSize = 1;
        if (click != null) b.Click += click;
        return b;
    }

    public static Label Heading(string text) => new()
    {
        Text = text, Font = Theme.Heading, ForeColor = Theme.Black, AutoSize = true, Margin = new Padding(0, 0, 0, 4),
    };

    public static Label Section(string text) => new()
    {
        Text = text, Font = Theme.Subheading, ForeColor = Theme.Black, AutoSize = true, Margin = new Padding(0, 18, 0, 4),
    };

    public static Label Text(string text, int maxWidth = 640) => new()
    {
        Text = text, Font = Theme.Body, ForeColor = Theme.Ink, AutoSize = true, MaximumSize = new Size(maxWidth, 0),
        Margin = new Padding(0, 2, 0, 2),
    };

    public static Label Hint(string text, int maxWidth = 640) => new()
    {
        Text = text, Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = true, MaximumSize = new Size(maxWidth, 0),
        Margin = new Padding(0, 2, 0, 6),
    };

    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 2, 0, 2),
        };
        row.Controls.AddRange(controls);
        return row;
    }

    public static FlowLayoutPanel Column()
    {
        return new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0),
        };
    }

    public static TextBox Input(int width = 260, string placeholder = "") => new()
    {
        Width = width, Font = Theme.Body, BorderStyle = BorderStyle.FixedSingle, PlaceholderText = placeholder,
        Margin = new Padding(0, 4, 8, 4),
    };

    public static ComboBox Choice(int width = 260) => new()
    {
        Width = width, DropDownStyle = ComboBoxStyle.DropDownList, Font = Theme.Body, FlatStyle = FlatStyle.Flat,
        Margin = new Padding(0, 4, 8, 4),
    };

    public static CheckBox Check(string text, bool value, Action<bool> changed)
    {
        var c = new CheckBox { Text = text, Checked = value, AutoSize = true, Font = Theme.Body, Margin = new Padding(0, 4, 0, 4) };
        c.CheckedChanged += (_, _) => changed(c.Checked);
        return c;
    }

    public static ListView List(params (string Title, int Width)[] columns)
    {
        var list = new ListView
        {
            View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true, Font = Theme.Body,
            BorderStyle = BorderStyle.FixedSingle, HeaderStyle = ColumnHeaderStyle.Nonclickable, Dock = DockStyle.Fill,
            ShowItemToolTips = true,
        };
        foreach (var (title, width) in columns) list.Columns.Add(title, width);
        return list;
    }

    /// <summary>A page: heading, optional intro, body rows, and an optional element that fills the remaining height.</summary>
    public static TableLayoutPanel Page(string title, string? intro, Control? fill, params Control[] rows)
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(28, 22, 28, 22), BackColor = Theme.White,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Add(Control c, SizeType type)
        {
            table.RowStyles.Add(type == SizeType.Percent ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            table.Controls.Add(c, 0, table.RowCount++);
        }
        Add(Heading(title), SizeType.AutoSize);
        if (intro != null) Add(Hint(intro, 760), SizeType.AutoSize);
        foreach (var r in rows) Add(r, SizeType.AutoSize);
        if (fill != null)
        {
            fill.Dock = DockStyle.Fill;
            fill.Margin = new Padding(0, 10, 0, 0);
            Add(fill, SizeType.Percent);
        }
        return table;
    }

    /// <summary>A stat tile for Home: big number, small caption.</summary>
    public static Panel Stat(out Label value, string caption)
    {
        var panel = new Panel { Width = 170, Height = 78, Margin = new Padding(0, 8, 12, 8), BackColor = Theme.Paper };
        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Line);
            e.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
        };
        value = new Label { Text = "0", Font = Theme.Stat, ForeColor = Theme.Black, AutoSize = true, Location = new Point(14, 10), BackColor = Color.Transparent };
        var label = new Label { Text = caption, Font = Theme.Small, ForeColor = Theme.Muted, AutoSize = true, Location = new Point(16, 52), BackColor = Color.Transparent };
        panel.Controls.Add(value);
        panel.Controls.Add(label);
        return panel;
    }

    /// <summary>A thin level meter (black on light grey).</summary>
    public sealed class LevelMeter : Control
    {
        float _level;

        public LevelMeter()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Size = new Size(220, 8);
            Margin = new Padding(0, 10, 8, 4);
        }

        public float Level
        {
            get => _level;
            set { _level = Math.Clamp(value, 0, 1); Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Line);
            using var brush = new SolidBrush(Theme.Black);
            e.Graphics.FillRectangle(brush, 0, 0, Width * _level, Height);
        }
    }
}
