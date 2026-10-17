using VoiceFlow.Core;
using VoiceFlow.Windows.Dictation;
using VoiceFlow.Windows.Meetings;

namespace VoiceFlow.Windows.UI;

internal enum PageId { Home, Dictionary, Snippets, Style, Meetings, Settings }

/// <summary>
/// The main window: a black navigation column and white pages. Closing it hides it; VoiceFlow keeps running
/// in the notification area.
/// </summary>
internal sealed class MainWindow : Form
{
    readonly AppModel _model;
    readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = Theme.White };
    readonly Dictionary<PageId, PageBase> _pages = new();
    readonly Dictionary<PageId, Button> _nav = new();
    readonly Label _setup = new() { AutoSize = false, Dock = DockStyle.Bottom, Height = 64, ForeColor = Color.FromArgb(200, 200, 200), Font = Theme.Small, Padding = new Padding(16, 0, 12, 12) };
    PageId _current = PageId.Home;

    /// <summary>When false, closing really closes (app exit).</summary>
    public bool HideOnClose { get; set; } = true;

    public MainWindow(AppModel model, Notetaker notetaker, TextInserter inserter, Icon? icon)
    {
        _model = model;
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "VoiceFlow";
        Icon = icon;
        Font = Theme.Body;
        BackColor = Theme.White;
        MinimumSize = new Size(860, 600);
        Size = new Size(1040, 720);
        StartPosition = FormStartPosition.CenterScreen;

        var nav = new Panel { Dock = DockStyle.Left, Width = 200, BackColor = Theme.Black, Padding = new Padding(0, 18, 0, 0) };
        var brand = new Label
        {
            Text = "VoiceFlow", Font = new Font("Segoe UI Semibold", 15f), ForeColor = Theme.White, Dock = DockStyle.Top, Height = 52,
            Padding = new Padding(18, 6, 0, 0),
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Black };
        foreach (var (id, title) in new[]
                 {
                     (PageId.Home, "Home"), (PageId.Dictionary, "Dictionary"), (PageId.Snippets, "Snippets"), (PageId.Style, "Style"),
                     (PageId.Meetings, "Meetings"), (PageId.Settings, "Settings"),
                 })
        {
            var b = new Button
            {
                Text = title, Width = 184, Height = 38, FlatStyle = FlatStyle.Flat, ForeColor = Theme.White, BackColor = Theme.Black,
                TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0), Margin = new Padding(8, 1, 8, 1),
                Font = Theme.Body, Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(38, 38, 38);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(60, 60, 60);
            var page = id;
            b.Click += (_, _) => ShowPage(page);
            _nav[id] = b;
            buttons.Controls.Add(b);
        }
        nav.Controls.Add(buttons);
        nav.Controls.Add(_setup);
        nav.Controls.Add(brand);

        _pages[PageId.Home] = new HomePage(model, inserter);
        _pages[PageId.Dictionary] = new DictionaryPage(model);
        _pages[PageId.Snippets] = new SnippetsPage(model);
        _pages[PageId.Style] = new StylePage(model);
        _pages[PageId.Meetings] = new MeetingsPage(notetaker, inserter);
        _pages[PageId.Settings] = new SettingsPage(model);
        foreach (var page in _pages.Values)
        {
            page.Visible = false;
            _content.Controls.Add(page);
        }

        Controls.Add(_content);
        Controls.Add(nav);
        ResumeLayout(true);

        model.LibraryChanged += () => ReloadIfVisible(PageId.Home, PageId.Dictionary, PageId.Snippets);
        model.PreferencesChanged += (_, _) => { ReloadIfVisible(_current); UpdateSetup(); };
        model.KeysChanged += UpdateSetup;
        ShowPage(PageId.Home);
    }

    void ReloadIfVisible(params PageId[] ids)
    {
        if (!Visible || !IsHandleCreated) return;
        if (ids.Contains(_current)) _pages[_current].Reload();
    }

    void UpdateSetup()
    {
        _setup.Text = _model.HasKey(SecretKey.Groq)
            ? $"Hold {HotkeyInfo.DisplayName(_model.Preferences.Hotkey)} to dictate."
            : "Add your Groq key in Settings to start.";
    }

    public void ShowPage(PageId id)
    {
        _current = id;
        foreach (var (pid, page) in _pages)
        {
            var on = pid == id;
            if (on) page.Reload();
            page.Visible = on;
            _nav[pid].BackColor = on ? Color.FromArgb(38, 38, 38) : Theme.Black;
            _nav[pid].Font = on ? Theme.Strong : Theme.Body;
        }
        UpdateSetup();
    }

    /// <summary>Shows and focuses the window (from the tray or a second launch).</summary>
    public void Present(PageId? page = null)
    {
        if (page is { } p) ShowPage(p);
        else _pages[_current].Reload();
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (HideOnClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }
}
