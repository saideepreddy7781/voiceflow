using VoiceFlow.Core.Meetings;
using VoiceFlow.Windows.Dictation;
using VoiceFlow.Windows.Meetings;

namespace VoiceFlow.Windows.UI;

/// <summary>Meetings: start and stop Notetaker, watch the live transcript, read notes, export Markdown.</summary>
internal sealed class MeetingsPage : PageBase
{
    readonly Notetaker _notetaker;
    readonly TextInserter _inserter;
    readonly Button _startStop;
    readonly Label _status = Ui.Hint("", 760);
    readonly Ui.LevelMeter _mic = new(), _system = new();
    readonly ListBox _list = new() { Font = Theme.Body, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false, Dock = DockStyle.Fill };
    readonly TextBox _title = Ui.Input(360);
    readonly TextBox _body = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = Theme.Body, BorderStyle = BorderStyle.FixedSingle,
        BackColor = Theme.Paper, Dock = DockStyle.Fill, WordWrap = true,
    };
    readonly FlowLayoutPanel _actions;
    bool _reloading;

    public MeetingsPage(Notetaker notetaker, TextInserter inserter)
    {
        _notetaker = notetaker;
        _inserter = inserter;
        _startStop = Ui.Primary("Start meeting notes", (_, _) =>
        {
            if (_notetaker.IsRecording) _notetaker.Stop(); else _notetaker.Start();
        });
        var meters = Ui.Row(new Label { Text = "You", AutoSize = true, Font = Theme.Small, Margin = new Padding(0, 6, 6, 0) }, _mic,
                            new Label { Text = "Others", AutoSize = true, Font = Theme.Small, Margin = new Padding(12, 6, 6, 0) }, _system);

        _list.DisplayMember = nameof(Item.Label);
        _list.SelectedIndexChanged += (_, _) => { if (!_reloading) ShowSelected(); };
        _actions = Ui.Row(
            Ui.Secondary("Rename", (_, _) => { if (SelectedId is { } id) _notetaker.Rename(id, _title.Text); }),
            Ui.Secondary("Export Markdown…", (_, _) => Export()),
            Ui.Secondary("Copy notes", (_, _) => { if (Selected is { } m) _ = _inserter.CopyAsync(MeetingMarkdown.Render(m)); }),
            Ui.Secondary("Try again", (_, _) => { if (SelectedId is { } id) _ = _notetaker.WriteNotesAsync(id); }),
            Ui.Secondary("Delete", (_, _) => Delete()));

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Margin = new Padding(12, 0, 0, 0) };
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.Controls.Add(_title, 0, 0);
        right.Controls.Add(_actions, 0, 1);
        right.Controls.Add(_body, 0, 2);

        // Sized first: SplitContainer clamps SplitterDistance to its current width (150 by default) when set.
        var split = new SplitContainer
        {
            Size = new Size(900, 500), Orientation = Orientation.Vertical, SplitterDistance = 240, FixedPanel = FixedPanel.Panel1, BackColor = Theme.White,
        };
        split.Panel1.Controls.Add(_list);
        split.Panel2.Controls.Add(right);

        Controls.Add(Ui.Page("Meetings",
            "Notetaker records your microphone and what your PC plays, writes the transcript as the call goes, and turns it into notes. Tell people on the call that you are taking notes.",
            split, Ui.Row(_startStop, meters), _status));
        _notetaker.Changed += () => { if (Visible) Reload(); };
    }

    sealed record Item(Guid Id, string Label);

    Guid? SelectedId => (_list.SelectedItem as Item)?.Id;
    Meeting? Selected => SelectedId is { } id ? _notetaker.Meetings.FirstOrDefault(m => m.Id == id) : null;

    public override void Reload()
    {
        _startStop.Text = _notetaker.Current switch
        {
            Notetaker.State.Recording => "Stop and write notes",
            Notetaker.State.WritingNotes => "Finishing…",
            _ => "Start meeting notes",
        };
        _startStop.Enabled = _notetaker.Current != Notetaker.State.WritingNotes;
        _mic.Level = _notetaker.IsRecording ? _notetaker.MicLevel : 0;
        _system.Level = _notetaker.IsRecording ? _notetaker.SystemLevel : 0;
        _status.Text = _notetaker.IsRecording && _notetaker.StartedAt is { } since
            ? $"Recording for {(DateTimeOffset.Now - since):h\\:mm\\:ss}{(_notetaker.Live?.MicOnly == true ? " (microphone only)" : "")}. The transcript appears below every 20–45 seconds."
            : _notetaker.LastError ?? "";

        var keep = SelectedId ?? _notetaker.LastStoppedId;
        _reloading = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        if (_notetaker.Live is { } live) _list.Items.Add(new Item(live.Id, "● Recording now"));
        foreach (var m in _notetaker.Meetings.Where(m => m.Id != _notetaker.Live?.Id))
        {
            var state = m.Status switch { MeetingStatus.WritingNotes => " (writing notes)", MeetingStatus.Failed => " (needs attention)", _ => "" };
            _list.Items.Add(new Item(m.Id, $"{m.Start.LocalDateTime:MMM d, HH:mm}  {m.Title}{state}"));
        }
        var index = _list.Items.Cast<Item>().ToList().FindIndex(i => i.Id == keep);
        if (index < 0 && _list.Items.Count > 0) index = 0;
        if (index >= 0) _list.SelectedIndex = index;
        _list.EndUpdate();
        _reloading = false;
        ShowSelected();
    }

    void ShowSelected()
    {
        var live = _notetaker.Live is { } l && SelectedId == l.Id ? l : null;
        var m = live ?? Selected;
        _actions.Enabled = live == null && m != null;
        if (m == null)
        {
            _title.Text = "";
            _body.Text = "No meetings yet. Press Start meeting notes when a call begins.";
            return;
        }
        if (!_title.Focused) _title.Text = m.Title;
        var text = live != null
            ? (m.Lines.Count == 0 ? "Listening… the first part of the transcript appears after about 20 seconds of talk." : m.TranscriptText)
            : (m.Error is { } error ? $"{error}\n\n" : "") + MeetingMarkdown.Render(m);
        text = text.ReplaceLineEndings("\r\n");
        if (_body.Text != text)
        {
            _body.Text = text;
            if (live != null) { _body.SelectionStart = _body.TextLength; _body.ScrollToCaret(); }
        }
    }

    void Export()
    {
        if (Selected is not { } m) return;
        using var dialog = new SaveFileDialog
        {
            FileName = MeetingMarkdown.FileName(m), Filter = "Markdown (*.md)|*.md|All files (*.*)|*.*", DefaultExt = "md", AddExtension = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dialog.FileName, MeetingMarkdown.Render(m));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, e.Message, "Could not export", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    void Delete()
    {
        if (SelectedId is not { } id) return;
        if (MessageBox.Show(this, "Delete this meeting and its notes from VoiceFlow? Files in your notes folder are kept.", "VoiceFlow",
                            MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
            _notetaker.Delete(id);
    }
}
