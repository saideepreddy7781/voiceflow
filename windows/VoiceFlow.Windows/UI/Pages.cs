using VoiceFlow.Core;
using VoiceFlow.Core.Library;
using VoiceFlow.Core.Text;
using VoiceFlow.Windows.Dictation;

namespace VoiceFlow.Windows.UI;

/// <summary>A page in the main window. <see cref="Reload"/> runs when the page is shown or the data changes.</summary>
internal abstract class PageBase : UserControl
{
    protected PageBase()
    {
        BackColor = Theme.White;
        Font = Theme.Body;
        Dock = DockStyle.Fill;
        AutoScaleMode = AutoScaleMode.Inherit;
    }

    public abstract void Reload();
}

internal sealed class HomePage : PageBase
{
    readonly AppModel _model;
    readonly TextInserter _inserter;
    readonly Label _week, _streak, _total, _wpm;
    readonly ListView _list = Ui.List(("When", 120), ("App", 130), ("Text", 520));
    readonly Label _hint;

    public HomePage(AppModel model, TextInserter inserter)
    {
        _model = model;
        _inserter = inserter;
        var stats = Ui.Row(Ui.Stat(out _week, "Words this week"), Ui.Stat(out _streak, "Day streak"),
                           Ui.Stat(out _total, "Words in history"), Ui.Stat(out _wpm, "Words per minute"));
        _hint = Ui.Hint("", 760);
        var buttons = Ui.Row(Ui.Secondary("Copy", (_, _) => CopySelected()), Ui.Secondary("Delete", (_, _) => DeleteSelected()),
                             Ui.Secondary("Clear history", (_, _) => ClearAll()));
        _list.DoubleClick += (_, _) => CopySelected();
        _list.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.C) CopySelected();
            if (e.KeyCode == Keys.Delete) DeleteSelected();
        };
        Controls.Add(Ui.Page("Home", null, _list, _hint, stats, Ui.Section("Recent"), buttons));
    }

    public override void Reload()
    {
        var hotkey = _model.Preferences.Hotkey;
        _hint.Text = $"Hold {HotkeyInfo.DisplayName(hotkey)} and speak; release to type. {HotkeyInfo.HandsFree(hotkey)} for hands-free, Esc to cancel. "
                     + $"Add {HotkeyInfo.CommandModifier(hotkey)} for Command Mode. Alt+Shift+V pastes the last result.";
        var stats = _model.Stats;
        _week.Text = stats.WordsThisWeek.ToString("N0");
        _streak.Text = stats.DayStreak.ToString("N0");
        _total.Text = stats.TotalWords.ToString("N0");
        _wpm.Text = stats.WordsPerMinute.ToString("N0");
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var e in _model.History.Take(300))
        {
            var text = e.Outcome == "failed" ? "(transcription failed)" : e.Final.ReplaceLineEndings(" ");
            var item = new ListViewItem([e.Date.LocalDateTime.ToString("g"), e.AppName, TextUtil.Prefix(text, 300)])
            {
                Tag = e.Id, ToolTipText = TextUtil.Prefix(e.Final, 1000),
            };
            _list.Items.Add(item);
        }
        _list.EndUpdate();
        if (_model.Preferences.HistoryRetentionDays == 0) _list.Items.Add(new ListViewItem(["", "", "History is off (Settings → History)."]));
    }

    IEnumerable<Guid> Selected => _list.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<Guid>();

    void CopySelected()
    {
        var ids = Selected.ToHashSet();
        var text = string.Join("\n\n", _model.History.Where(e => ids.Contains(e.Id)).Select(e => e.Final).Where(t => t.Length > 0));
        if (text.Length > 0) _ = _inserter.CopyAsync(text);
    }

    void DeleteSelected() => _model.DeleteHistory(Selected.ToList());

    void ClearAll()
    {
        if (MessageBox.Show(this, "Delete every dictation in your history?", "VoiceFlow", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
            _model.ClearHistory();
    }
}

internal sealed class DictionaryPage : PageBase
{
    readonly AppModel _model;
    readonly TextBox _term = Ui.Input(220, "Word, e.g. Saideep");
    readonly TextBox _wrong = Ui.Input(220, "Heard as (optional), e.g. sai deep");
    readonly Label _error = Ui.Hint("");
    readonly ListView _list = Ui.List(("Word", 220), ("Corrects", 220), ("Added by", 120));

    public DictionaryPage(AppModel model)
    {
        _model = model;
        var add = Ui.Primary("Add", (_, _) => Add());
        _term.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { Add(); e.SuppressKeyPress = true; } };
        _wrong.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { Add(); e.SuppressKeyPress = true; } };
        var remove = Ui.Secondary("Remove selected", (_, _) =>
            _model.RemoveDictionaryEntries(_list.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<Guid>().ToList()));
        Controls.Add(Ui.Page("Dictionary",
            "Names and terms VoiceFlow should spell your way. Words help recognition; a correction replaces what the recognizer types with your spelling.",
            _list, Ui.Row(_term, _wrong, add), _error, remove));
    }

    void Add()
    {
        var error = _model.AddDictionaryEntry(_term.Text, _wrong.Text);
        _error.Text = error ?? "";
        if (error == null) { _term.Clear(); _wrong.Clear(); _term.Focus(); }
    }

    public override void Reload()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var e in _model.Dictionary)
            _list.Items.Add(new ListViewItem([e.Term, e.Misspelling ?? "", e.Source == DictionarySource.AutoLearned ? "Learned" : "You"]) { Tag = e.Id });
        _list.EndUpdate();
    }
}

internal sealed class SnippetsPage : PageBase
{
    readonly AppModel _model;
    readonly TextBox _trigger = Ui.Input(260, "Say this, e.g. my calendar link");
    readonly TextBox _expansion = new()
    {
        Multiline = true, Height = 70, Width = 520, ScrollBars = ScrollBars.Vertical, Font = Theme.Body, BorderStyle = BorderStyle.FixedSingle,
        PlaceholderText = "…and VoiceFlow types this", Margin = new Padding(0, 4, 8, 4), AcceptsReturn = true,
    };
    readonly Label _error = Ui.Hint("");
    readonly ListView _list = Ui.List(("Say", 220), ("Types", 480));
    readonly Button _save;
    Guid? _editing;

    public SnippetsPage(AppModel model)
    {
        _model = model;
        _save = Ui.Primary("Add snippet", (_, _) => Save());
        var cancel = Ui.Secondary("Clear", (_, _) => ResetForm());
        var edit = Ui.Secondary("Edit selected", (_, _) => EditSelected());
        var remove = Ui.Secondary("Remove selected", (_, _) =>
            _model.RemoveSnippets(_list.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<Guid>().ToList()));
        _list.DoubleClick += (_, _) => EditSelected();
        Controls.Add(Ui.Page("Snippets",
            $"Say a short cue and VoiceFlow types the full text. Cues up to {Snippet.MaxTrigger} characters, text up to {Snippet.MaxExpansion:N0}.",
            _list, _trigger, _expansion, Ui.Row(_save, cancel), _error, Ui.Row(edit, remove)));
    }

    void Save()
    {
        var error = _model.SaveSnippet(_trigger.Text, _expansion.Text, _editing);
        _error.Text = error ?? "";
        if (error == null) ResetForm();
    }

    void EditSelected()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not Guid id) return;
        var s = _model.Snippets.FirstOrDefault(x => x.Id == id);
        if (s == null) return;
        _editing = id;
        _trigger.Text = s.Trigger;
        _expansion.Text = s.Expansion.ReplaceLineEndings("\r\n");
        _save.Text = "Save changes";
    }

    void ResetForm()
    {
        _editing = null;
        _trigger.Clear();
        _expansion.Clear();
        _save.Text = "Add snippet";
    }

    public override void Reload()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var s in _model.Snippets)
            _list.Items.Add(new ListViewItem([s.Trigger, TextUtil.Prefix(s.Expansion.ReplaceLineEndings(" "), 200)]) { Tag = s.Id });
        _list.EndUpdate();
    }
}

internal sealed class StylePage : PageBase
{
    readonly AppModel _model;
    readonly Dictionary<(StyleCategory, WritingStyle), RadioButton> _radios = new();

    public StylePage(AppModel model)
    {
        _model = model;
        var rows = new List<Control>();
        foreach (var category in Enum.GetValues<StyleCategory>())
        {
            rows.Add(Ui.Section(category.DisplayName()));
            rows.Add(Ui.Hint(category.AppsHint()));
            var row = new FlowLayoutPanel { AutoSize = true, WrapContents = true, MaximumSize = new Size(820, 0), Margin = new Padding(0) };
            foreach (var style in category.Options())
            {
                var card = Ui.Column();
                card.Margin = new Padding(0, 0, 18, 6);
                card.Width = 250;
                var radio = new RadioButton { Text = $"{style.DisplayName()}  ·  {style.Summary()}", AutoSize = true, Font = Theme.Body };
                var cat = category;
                var st = style;
                radio.CheckedChanged += (_, _) =>
                {
                    if (radio.Checked && _model.Preferences.StyleFor(cat) != st) _model.UpdatePreferences(p => p.Styles[cat] = st);
                };
                _radios[(category, style)] = radio;
                card.Controls.Add(radio);
                card.Controls.Add(Ui.Hint(style.Example(category), 240));
                row.Controls.Add(card);
            }
            rows.Add(row);
        }
        rows.Add(Ui.Hint("Code editors and terminals always keep identifiers and commands exactly as spoken.", 760));
        var scroller = new Panel { AutoScroll = true };
        var column = Ui.Column();
        column.Controls.AddRange(rows.ToArray());
        scroller.Controls.Add(column);
        Controls.Add(Ui.Page("Style", "How dictated text should sound in each kind of app.", scroller));
    }

    public override void Reload()
    {
        foreach (var ((category, style), radio) in _radios)
        {
            var on = _model.Preferences.StyleFor(category) == style;
            if (radio.Checked != on) radio.Checked = on;
        }
    }
}
