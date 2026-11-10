using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceFlow.Core;
using VoiceFlow.Core.Meetings;
using VoiceFlow.Desktop.Services;

namespace VoiceFlow.Desktop.ViewModels;

public sealed partial class MeetingListItem : ObservableObject
{
    public Meeting? Meeting { get; init; }
    public bool Recording { get; init; }
    public string Title { get; init; } = "";
    public string Meta { get; init; } = "";
    public bool NeedsAttention { get; init; }
    /// <summary>Where the search matched, when it wasn't only the title.</summary>
    public string? Snippet { get; init; }
    public bool HasSnippet => Snippet != null;
}

public sealed partial class MeetingsViewModel : ObservableObject
{
    readonly IAppServices _services;
    readonly Action<object?> _showDialog;

    public MeetingsViewModel(IAppServices services, Action<object?> showDialog)
    {
        _services = services;
        _showDialog = showDialog;
        Service.Changed += (_, _) => Refresh();
        Refresh();
    }

    IMeetingsService Service => _services.Meetings;

    public ObservableCollection<MeetingListItem> Items { get; } = [];

    [ObservableProperty] string _searchText = "";
    [ObservableProperty] MeetingListItem? _selected;
    [ObservableProperty] object? _pane;
    [ObservableProperty] bool _noMeetings;
    [ObservableProperty] string _state = "Idle";

    public bool IsIdle => Service.State == NotetakerState.Idle;
    public bool IsRecording => Service.State == NotetakerState.Recording;
    public bool IsWriting => Service.State == NotetakerState.WritingNotes;
    public string? NotesFolder => Service.NotesFolder is { Length: > 0 } f ? System.IO.Path.GetFileName(f) : null;
    public bool HasFolder => NotesFolder != null;

    partial void OnSearchTextChanged(string value) => Refresh();

    partial void OnSelectedChanged(MeetingListItem? value)
    {
        if (value == null) { Pane = null; return; }
        // Refresh rebuilds the list; keep the open pane so edits in progress are not lost.
        if (value.Recording && Pane is LiveMeetingViewModel) return;
        var open = Pane as MeetingDetailViewModel;
        if (open != null && value.Meeting != null && open.IsCurrent(value.Meeting)) return;
        (Pane as IDisposable)?.Dispose();
        Pane = value.Recording
            ? new LiveMeetingViewModel(_services)
            : new MeetingDetailViewModel(value.Meeting!, _services, _showDialog)
            {
                // The same meeting with new notes: stay on the tab the user was reading.
                ShowTranscript = open?.Meeting.Id == value.Meeting?.Id && open?.ShowTranscript == true,
            };
    }

    void Refresh()
    {
        var keep = Selected?.Meeting?.Id;
        var recording = Service.State == NotetakerState.Recording && Service.Live != null;
        var query = SearchText.Trim();
        Items.Clear();
        if (recording)
            Items.Add(new MeetingListItem { Recording = true, Title = Service.Live!.Title, Meta = "Recording now" });
        foreach (var m in Service.Meetings.Where(m => MeetingSearch.Matches(m, query)))
        {
            var meta = m.Start.ToLocalTime().ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture) +
                       $"  ·  {Math.Max(1, (int)Math.Round(m.Duration.TotalMinutes))} min";
            if (m.Status == MeetingStatus.WritingNotes) meta += "  ·  writing notes";
            Items.Add(new MeetingListItem { Meeting = m, Title = m.Title, Meta = meta, NeedsAttention = m.Status == MeetingStatus.Failed, Snippet = MeetingSearch.Snippet(m, query) });
        }
        NoMeetings = Items.Count == 0;
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(IsWriting));
        Selected = recording ? Items.FirstOrDefault(i => i.Recording)
            : Items.FirstOrDefault(i => i.Meeting?.Id == keep) ?? Items.FirstOrDefault();
    }

    [RelayCommand]
    Task TakeNotes() => Service.StartAsync();

    [RelayCommand]
    Task Stop() => Service.StopAsync();
}

public sealed partial class LiveMeetingViewModel : ObservableObject, IDisposable
{
    readonly IMeetingsService _meetings;
    readonly DispatcherTimer _timer;

    public LiveMeetingViewModel(IAppServices services)
    {
        _meetings = services.Meetings;
        Refresh();
        Tick();
        _meetings.LevelsChanged += OnLevels;
        _meetings.Changed += OnMeetingChanged;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public ObservableCollection<TranscriptRow> Lines { get; } = [];
    public Meeting? Meeting => _meetings.Live;
    public string Title => Meeting?.Title ?? "Meeting";
    public bool MicOnly => Meeting?.MicOnly == true;
    public bool HasOthers => !MicOnly;

    [ObservableProperty] string _elapsed = "0:00";
    [ObservableProperty] double _micLevel;
    [ObservableProperty] double _systemLevel;
    [ObservableProperty] string _healthText = "";
    [ObservableProperty] bool _healthProblem;
    [ObservableProperty] string? _micLanguage;
    [ObservableProperty] string? _callLanguage;

    public bool HasMicLanguage => MicLanguage != null;
    public bool HasCallLanguage => CallLanguage != null;

    partial void OnMicLanguageChanged(string? value) => OnPropertyChanged(nameof(HasMicLanguage));
    partial void OnCallLanguageChanged(string? value) => OnPropertyChanged(nameof(HasCallLanguage));

    void Refresh()
    {
        Lines.Clear();
        foreach (var l in Meeting?.Lines ?? [])
            Lines.Add(new TranscriptRow(l, Meeting!));
        OnLevels(this, EventArgs.Empty);
    }

    /// <summary>New transcript chunks arrive while the meeting runs.</summary>
    void OnMeetingChanged(object? sender, EventArgs e)
    {
        if (Meeting is not { } live || live.Lines.Count == Lines.Count) return;
        Refresh();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(MicOnly));
        OnPropertyChanged(nameof(HasOthers));
    }

    void OnLevels(object? sender, EventArgs e)
    {
        MicLevel = _meetings.MicLevel;
        SystemLevel = _meetings.SystemLevel;
        MicLanguage = _meetings.MicLanguage is { } mine ? Languages.DisplayName(mine) : null;
        CallLanguage = _meetings.CallLanguage is { } theirs ? Languages.DisplayName(theirs) : null;
        (HealthText, HealthProblem) = _meetings.MicHealth switch
        {
            MicHealth.Silent => ("No sound from your microphone", true),
            MicHealth.Quiet => ("Your microphone is very quiet", true),
            _ => ("Microphone sounds good", false),
        };
    }

    void Tick()
    {
        Elapsed = Meeting.Clock(_meetings.LiveElapsed.TotalSeconds);
    }

    [RelayCommand]
    Task Stop() => _meetings.StopAsync();

    public void Dispose()
    {
        _timer.Stop();
        _meetings.LevelsChanged -= OnLevels;
        _meetings.Changed -= OnMeetingChanged;
    }
}

public sealed class TranscriptRow
{
    public TranscriptRow(TranscriptLine line, Meeting meeting)
    {
        Time = Meeting.Clock(line.Time);
        Speaker = meeting.LabelFor(line.Speaker);
        IsYou = line.Speaker == Core.Meetings.Speaker.You;
        Text = line.Text;
    }

    public string Time { get; }
    public string Speaker { get; }
    public bool IsYou { get; }
    public string Text { get; }
}

public sealed partial class ActionItemRow : ObservableObject
{
    readonly ActionItem _item;
    readonly Action _toggle;

    public ActionItemRow(ActionItem item, Action toggle)
    {
        _item = item;
        _toggle = toggle;
        _done = item.Done;
        var bits = new List<string>();
        if (item.Owner is { Length: > 0 } o) bits.Add(o);
        if (item.Due is { Length: > 0 } d) bits.Add("due " + d);
        Meta = string.Join("  ·  ", bits);
    }

    public string Task => _item.Task;
    public string Meta { get; }
    public bool HasMeta => Meta.Length > 0;

    [ObservableProperty] bool _done;

    partial void OnDoneChanged(bool value) => _toggle();
}

public sealed partial class MeetingDetailViewModel : ObservableObject
{
    readonly IAppServices _services;
    readonly Action<object?> _showDialog;
    readonly MeetingStatus _shownStatus;
    readonly MeetingNotes? _shownNotes;

    public MeetingDetailViewModel(Meeting meeting, IAppServices services, Action<object?> showDialog)
    {
        Meeting = meeting;
        _services = services;
        _showDialog = showDialog;
        _shownStatus = meeting.Status;
        _shownNotes = meeting.Notes;
        _title = meeting.Title;
        _youName = meeting.LabelFor(Speaker.You);
        _othersName = meeting.LabelFor(Speaker.Others);
        Summary = meeting.Notes?.Summary ?? [];
        Decisions = meeting.Notes?.Decisions ?? [];
        Questions = meeting.Notes?.Questions ?? [];
        ActionItems = (meeting.Notes?.ActionItems ?? [])
            .Select(a => new ActionItemRow(a, () => _services.Meetings.ToggleActionItem(Meeting.Id, a.Id))).ToList();
        Lines = new ObservableCollection<TranscriptRow>(meeting.Lines.Select(l => new TranscriptRow(l, meeting)));
    }

    public Meeting Meeting { get; }

    /// <summary>False once the notes shown here were replaced or the status moved on (notes finished writing, for example).</summary>
    public bool IsCurrent(Meeting meeting) =>
        ReferenceEquals(meeting, Meeting) && meeting.Status == _shownStatus && ReferenceEquals(meeting.Notes, _shownNotes);

    public IReadOnlyList<string> Summary { get; }
    public IReadOnlyList<string> Decisions { get; }
    public IReadOnlyList<ActionItemRow> ActionItems { get; }
    public IReadOnlyList<string> Questions { get; }
    public ObservableCollection<TranscriptRow> Lines { get; }

    public bool HasSummary => Summary.Count > 0;
    public bool HasDecisions => Decisions.Count > 0;
    public bool HasActions => ActionItems.Count > 0;
    public bool HasQuestions => Questions.Count > 0;
    public bool HasNotes => HasSummary || HasDecisions || HasActions || HasQuestions;

    public string Meta
    {
        get
        {
            var m = Meeting;
            var bits = new List<string>
            {
                m.Start.ToLocalTime().ToString("dddd, MMMM d, h:mm tt", CultureInfo.CurrentCulture),
                $"{Math.Max(1, (int)Math.Round(m.Duration.TotalMinutes))} min",
            };
            if (m.App.Length > 0) bits.Add(m.App);
            if (m.MicOnly) bits.Add("your mic only");
            return string.Join("  ·  ", bits);
        }
    }

    public string Attendees => Meeting.Attendees.Count > 0 ? "With " + string.Join(", ", Meeting.Attendees) : "";
    public bool HasAttendees => Attendees.Length > 0;
    public bool IsWriting => Meeting.Status == MeetingStatus.WritingNotes;
    public bool Failed => Meeting.Status == MeetingStatus.Failed;
    public string ErrorText => Meeting.Error ?? "Notes could not be written.";
    public string? Note => Meeting.Note;
    public bool HasNote => !string.IsNullOrEmpty(Meeting.Note);
    public bool HasOthers => !Meeting.MicOnly;

    [ObservableProperty] string _title;
    [ObservableProperty] string _youName;
    [ObservableProperty] string _othersName;
    [ObservableProperty] bool _showTranscript;

    public bool ShowNotes
    {
        get => !ShowTranscript;
        set => ShowTranscript = !value;
    }

    partial void OnShowTranscriptChanged(bool value) => OnPropertyChanged(nameof(ShowNotes));

    partial void OnYouNameChanged(string value) => RenameSpeaker(Speaker.You, value);

    partial void OnOthersNameChanged(string value) => RenameSpeaker(Speaker.Others, value);

    void RenameSpeaker(Speaker speaker, string name)
    {
        _services.Meetings.RenameSpeaker(Meeting.Id, speaker, name);
        Lines.Clear();
        foreach (var l in Meeting.Lines) Lines.Add(new TranscriptRow(l, Meeting));
    }

    [RelayCommand]
    void CommitTitle()
    {
        var t = Title.Trim();
        if (t.Length > 0 && t != Meeting.Title) _services.Meetings.Rename(Meeting.Id, t);
    }

    [RelayCommand]
    Task CopyMarkdown() => _services.Meetings.CopyMarkdownAsync(Meeting.Id);

    [RelayCommand]
    Task ExportMarkdown() => _services.Meetings.ExportMarkdownAsync(Meeting.Id);

    [RelayCommand]
    Task RewriteNotes() => _services.Meetings.RewriteNotesAsync(Meeting.Id);

    [RelayCommand]
    void AskDelete() => _showDialog(new ConfirmDialogViewModel(
        $"Delete \"{Meeting.Title}\"?",
        "The notes and transcript are removed from VoiceFlow. A copy already saved to your notes folder stays there.",
        "Delete",
        () => { _services.Meetings.Delete(Meeting.Id); _showDialog(null); },
        () => _showDialog(null)));
}

public sealed partial class ConfirmDialogViewModel : ObservableObject
{
    readonly Action _confirm;
    readonly Action _cancel;

    public ConfirmDialogViewModel(string title, string message, string confirmLabel, Action confirm, Action cancel)
    {
        Title = title;
        Message = message;
        ConfirmLabel = confirmLabel;
        _confirm = confirm;
        _cancel = cancel;
    }

    public string Title { get; }
    public string Message { get; }
    public string ConfirmLabel { get; }

    [RelayCommand] void Confirm() => _confirm();
    [RelayCommand] void Cancel() => _cancel();
}
