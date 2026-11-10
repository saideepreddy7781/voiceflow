using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceFlow.Core;
using VoiceFlow.Core.Library;
using VoiceFlow.Desktop.Services;

namespace VoiceFlow.Desktop.ViewModels;

// Dictionary

public sealed partial class DictionaryRow : ObservableObject
{
    public DictionaryRow(DictionaryEntry entry, Action<DictionaryEntry> edit, Action<DictionaryEntry> delete)
    {
        Entry = entry;
        Term = entry.Term;
        Misspelling = entry.Misspelling;
        Learned = entry.Source == DictionarySource.AutoLearned;
        EditCommand = new RelayCommand(() => edit(entry));
        DeleteCommand = new RelayCommand(() => delete(entry));
    }

    public DictionaryEntry Entry { get; }
    public string Term { get; }
    public string? Misspelling { get; }
    public bool HasCorrection => !string.IsNullOrEmpty(Misspelling);
    public bool Learned { get; }
    public IRelayCommand EditCommand { get; }
    public IRelayCommand DeleteCommand { get; }
}

public sealed partial class DictionaryViewModel : ObservableObject
{
    readonly IAppServices _services;
    readonly Action<object?> _showDialog;

    public DictionaryViewModel(IAppServices services, Action<object?> showDialog)
    {
        _services = services;
        _showDialog = showDialog;
        services.Dictionary.Changed += (_, _) => Refresh();
        Refresh();
    }

    public ObservableCollection<DictionaryRow> Rows { get; } = [];

    [ObservableProperty] string _searchText = "";
    [ObservableProperty] bool _showAll = true;
    [ObservableProperty] bool _showYours;
    [ObservableProperty] bool _showLearned;
    [ObservableProperty] bool _isEmpty;
    [ObservableProperty] string _emptyText = "";
    [ObservableProperty] string? _notice;

    public bool HasNotice => !string.IsNullOrEmpty(Notice);

    partial void OnNoticeChanged(string? value) => OnPropertyChanged(nameof(HasNotice));
    partial void OnSearchTextChanged(string value) => Refresh();
    partial void OnShowAllChanged(bool value) { if (value) Refresh(); }
    partial void OnShowYoursChanged(bool value) { if (value) Refresh(); }
    partial void OnShowLearnedChanged(bool value) { if (value) Refresh(); }

    void Refresh()
    {
        var query = SearchText.Trim();
        var list = _services.Dictionary.Entries.Where(e =>
            (ShowAll || (ShowYours && e.Source == DictionarySource.Manual) || (ShowLearned && e.Source == DictionarySource.AutoLearned))
            && (query.Length == 0 || e.Term.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || (e.Misspelling?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false)));
        Rows.Clear();
        foreach (var e in list) Rows.Add(new DictionaryRow(e, Edit, AskDelete));
        IsEmpty = Rows.Count == 0;
        EmptyText = query.Length > 0 ? "No matches." : "No words yet. Add names and terms you use, or correct a dictation and VoiceFlow learns it.";
    }

    [RelayCommand]
    void Add() => _showDialog(new DictionaryEditViewModel(null, _services, () => _showDialog(null)));

    void Edit(DictionaryEntry entry) => _showDialog(new DictionaryEditViewModel(entry, _services, () => _showDialog(null)));

    void AskDelete(DictionaryEntry entry) => _showDialog(new ConfirmDialogViewModel(
        $"Delete \"{entry.Term}\"?", "VoiceFlow will no longer correct to this spelling.", "Delete",
        () => { _services.Dictionary.Delete(entry.Id); _showDialog(null); }, () => _showDialog(null)));

    [RelayCommand]
    async Task Import()
    {
        var added = await _services.Dictionary.ImportAsync();
        Notice = added == 1 ? "Imported 1 word." : $"Imported {added} words.";
    }
}

public sealed partial class DictionaryEditViewModel : ObservableObject
{
    readonly DictionaryEntry? _entry;
    readonly IAppServices _services;
    readonly Action _close;

    public DictionaryEditViewModel(DictionaryEntry? entry, IAppServices services, Action close)
    {
        _entry = entry;
        _services = services;
        _close = close;
        _term = entry?.Term ?? "";
        _misspelling = entry?.Misspelling ?? "";
        _isCorrection = entry?.Misspelling != null;
    }

    public string Heading => _entry == null ? "Add a word" : "Edit word";

    [ObservableProperty] string _term;
    [ObservableProperty] string _misspelling;
    [ObservableProperty] bool _isCorrection;
    [ObservableProperty] string? _error;

    public bool HasError => !string.IsNullOrEmpty(Error);

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    [RelayCommand]
    void Save()
    {
        var miss = IsCorrection ? Misspelling.Trim() : null;
        var others = _services.Dictionary.Entries.Where(e => e.Id != _entry?.Id);
        Error = DictionaryEntry.Validate(Term, miss, others);
        if (Error != null) return;
        if (_entry == null) _services.Dictionary.Add(Term.Trim(), miss);
        else _services.Dictionary.Update(_entry.Id, Term.Trim(), miss);
        _close();
    }

    [RelayCommand]
    void Cancel() => _close();
}

// Snippets

public sealed partial class SnippetRow : ObservableObject
{
    public SnippetRow(Snippet snippet, Action<Snippet> edit, Action<Snippet> delete)
    {
        Trigger = snippet.Trigger;
        Expansion = snippet.Expansion;
        EditCommand = new RelayCommand(() => edit(snippet));
        DeleteCommand = new RelayCommand(() => delete(snippet));
    }

    public string Trigger { get; }
    public string Expansion { get; }
    public IRelayCommand EditCommand { get; }
    public IRelayCommand DeleteCommand { get; }
}

public sealed partial class SnippetsViewModel : ObservableObject
{
    readonly IAppServices _services;
    readonly Action<object?> _showDialog;

    public SnippetsViewModel(IAppServices services, Action<object?> showDialog)
    {
        _services = services;
        _showDialog = showDialog;
        services.Snippets.Changed += (_, _) => Refresh();
        Refresh();
    }

    public ObservableCollection<SnippetRow> Rows { get; } = [];

    [ObservableProperty] bool _isEmpty;
    [ObservableProperty] string? _notice;

    public bool HasNotice => !string.IsNullOrEmpty(Notice);

    partial void OnNoticeChanged(string? value) => OnPropertyChanged(nameof(HasNotice));

    void Refresh()
    {
        Rows.Clear();
        foreach (var s in _services.Snippets.Snippets) Rows.Add(new SnippetRow(s, Edit, AskDelete));
        IsEmpty = Rows.Count == 0;
    }

    [RelayCommand]
    void Add() => _showDialog(new SnippetEditViewModel(null, _services, () => _showDialog(null)));

    void Edit(Snippet s) => _showDialog(new SnippetEditViewModel(s, _services, () => _showDialog(null)));

    void AskDelete(Snippet s) => _showDialog(new ConfirmDialogViewModel(
        $"Delete \"{s.Trigger}\"?", "This can't be undone.", "Delete",
        () => { _services.Snippets.Delete(s.Id); _showDialog(null); }, () => _showDialog(null)));

    [RelayCommand]
    async Task Import()
    {
        var added = await _services.Snippets.ImportAsync();
        Notice = added == 1 ? "Imported 1 snippet." : $"Imported {added} snippets.";
    }
}

public sealed partial class SnippetEditViewModel : ObservableObject
{
    readonly Snippet? _snippet;
    readonly IAppServices _services;
    readonly Action _close;

    public SnippetEditViewModel(Snippet? snippet, IAppServices services, Action close)
    {
        _snippet = snippet;
        _services = services;
        _close = close;
        _trigger = snippet?.Trigger ?? "";
        _expansion = snippet?.Expansion ?? "";
    }

    public string Heading => _snippet == null ? "Add a snippet" : "Edit snippet";

    [ObservableProperty] string _trigger;
    [ObservableProperty] string _expansion;
    [ObservableProperty] string? _error;

    public bool HasError => !string.IsNullOrEmpty(Error);

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    [RelayCommand]
    void Save()
    {
        Error = SnippetMatcher.Validate(Trigger, Expansion, _services.Snippets.Snippets, _snippet?.Id);
        if (Error != null) return;
        if (_snippet == null) _services.Snippets.Add(Trigger.Trim(), Expansion);
        else _services.Snippets.Update(_snippet.Id, Trigger.Trim(), Expansion);
        _close();
    }

    [RelayCommand]
    void Cancel() => _close();
}

// Style

public sealed partial class StyleOption : ObservableObject
{
    readonly Action<WritingStyle> _select;

    public StyleOption(WritingStyle style, StyleCategory category, bool selected, Action<WritingStyle> select)
    {
        Style = style;
        Name = style.DisplayName();
        Summary = style.Summary();
        Example = style.Example(category);
        _isSelected = selected;
        _select = select;
    }

    public WritingStyle Style { get; }
    public string Name { get; }
    public string Summary { get; }
    public string Example { get; }

    [ObservableProperty] bool _isSelected;

    partial void OnIsSelectedChanged(bool value) { if (value) _select(Style); }
}

public sealed partial class CategoryTab : ObservableObject
{
    readonly Action<StyleCategory> _select;

    public CategoryTab(StyleCategory category, bool selected, Action<StyleCategory> select)
    {
        Category = category;
        Name = category.DisplayName();
        _isSelected = selected;
        _select = select;
    }

    public StyleCategory Category { get; }
    public string Name { get; }

    [ObservableProperty] bool _isSelected;

    partial void OnIsSelectedChanged(bool value) { if (value) _select(Category); }
}

public sealed partial class CleanupOption : ObservableObject
{
    readonly Action<CleanupLevel> _select;

    public CleanupOption(CleanupLevel level, bool selected, Action<CleanupLevel> select)
    {
        Level = level;
        Name = EnumText.DisplayName(level);
        Summary = EnumText.Summary(level);
        (Said, Typed) = level switch
        {
            CleanupLevel.None => ("um can we move the review to tuesday no actually wednesday", "um can we move the review to tuesday no actually wednesday"),
            CleanupLevel.Light => ("um can we move the review to tuesday no actually wednesday", "Can we move the review to Wednesday?"),
            _ => ("so basically i think we should probably just move the review to wednesday", "Let's move the review to Wednesday."),
        };
        IsDefault = level == CleanupLevel.Light;
        _isSelected = selected;
        _select = select;
    }

    public CleanupLevel Level { get; }
    public string Name { get; }
    public string Summary { get; }
    public string Said { get; }
    public string Typed { get; }
    public bool IsDefault { get; }

    [ObservableProperty] bool _isSelected;

    partial void OnIsSelectedChanged(bool value) { if (value) _select(Level); }
}

public sealed partial class StyleViewModel : ObservableObject
{
    readonly IAppServices _services;

    public StyleViewModel(IAppServices services)
    {
        _services = services;
        Tabs = Enum.GetValues<StyleCategory>().Select(c => new CategoryTab(c, c == StyleCategory.Personal, SelectCategory)).ToList();
        var level = Prefs.CleanupLevel;
        CleanupOptions = Enum.GetValues<CleanupLevel>().Select(l => new CleanupOption(l, l == level, SelectCleanup)).ToList();
        LoadOptions(StyleCategory.Personal);
    }

    Preferences Prefs => _services.Preferences.Current;

    public IReadOnlyList<CategoryTab> Tabs { get; }
    public IReadOnlyList<CleanupOption> CleanupOptions { get; }
    public ObservableCollection<StyleOption> Options { get; } = [];

    [ObservableProperty] string _hint = "";

    StyleCategory _category;

    void SelectCategory(StyleCategory category)
    {
        foreach (var t in Tabs) if (t.Category != category) t.IsSelected = false;
        LoadOptions(category);
    }

    void LoadOptions(StyleCategory category)
    {
        _category = category;
        Hint = category.AppsHint();
        Options.Clear();
        var chosen = Prefs.StyleFor(category);
        foreach (var style in category.Options())
            Options.Add(new StyleOption(style, category, style == chosen, Choose));
    }

    void Choose(WritingStyle style)
    {
        foreach (var o in Options) if (o.Style != style) o.IsSelected = false;
        _services.Preferences.Update(p => p.Styles[_category] = style);
    }

    void SelectCleanup(CleanupLevel level)
    {
        foreach (var o in CleanupOptions) if (o.Level != level) o.IsSelected = false;
        _services.Preferences.Update(p => p.CleanupLevel = level);
    }
}

// Scratchpad

public sealed partial class NoteRow : ObservableObject
{
    public NoteRow(Note note, Action<NoteRow> select, Action<NoteRow> pin, Action<NoteRow> delete)
    {
        Note = note;
        Title = note.DisplayTitle;
        Updated = Relative(note.Updated);
        Pinned = note.Pinned;
        SelectCommand = new RelayCommand(() => select(this));
        PinCommand = new RelayCommand(() => pin(this));
        DeleteCommand = new RelayCommand(() => delete(this));
    }

    public Note Note { get; }
    public string Title { get; }
    public string Updated { get; }
    public bool Pinned { get; }
    public IRelayCommand SelectCommand { get; }
    public IRelayCommand PinCommand { get; }
    public IRelayCommand DeleteCommand { get; }

    static string Relative(DateTimeOffset date)
    {
        var d = DateTimeOffset.Now - date;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} hr ago";
        return (int)d.TotalDays == 1 ? "yesterday" : $"{(int)d.TotalDays} days ago";
    }
}

public sealed partial class ScratchpadViewModel : ObservableObject
{
    readonly IAppServices _services;
    readonly Action<object?> _showDialog;

    public ScratchpadViewModel(IAppServices services, Action<object?> showDialog)
    {
        _services = services;
        _showDialog = showDialog;
        Refresh();
        SelectedRow = Rows.FirstOrDefault();
    }

    public ObservableCollection<NoteRow> Rows { get; } = [];

    [ObservableProperty] string _searchText = "";
    [ObservableProperty] NoteRow? _selectedRow;
    [ObservableProperty] bool _isEmpty;
    [ObservableProperty] string _title = "";
    [ObservableProperty] string _body = "";

    bool _loading;

    public bool HasNote => SelectedRow != null;

    partial void OnSearchTextChanged(string value) => Refresh();

    partial void OnSelectedRowChanged(NoteRow? value)
    {
        OnPropertyChanged(nameof(HasNote));
        _loading = true;
        Title = value?.Note.Title ?? "";
        Body = value?.Note.Body ?? "";
        _loading = false;
    }

    partial void OnTitleChanged(string value) => Save();

    partial void OnBodyChanged(string value) => Save();

    void Save()
    {
        if (_loading || SelectedRow is not { } row) return;
        row.Note.Title = Title;
        row.Note.Body = Body;
        _services.Scratchpad.Update(row.Note);
    }

    void Refresh()
    {
        var keep = SelectedRow?.Note.Id;
        var query = SearchText.Trim();
        var notes = _services.Scratchpad.Notes.Where(n => query.Length == 0
            || n.DisplayTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || n.Body.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        Rows.Clear();
        foreach (var n in notes) Rows.Add(new NoteRow(n, r => SelectedRow = r, TogglePin, AskDelete));
        IsEmpty = _services.Scratchpad.Notes.Count == 0;
        SelectedRow = Rows.FirstOrDefault(r => r.Note.Id == keep);
    }

    void TogglePin(NoteRow row)
    {
        row.Note.Pinned = !row.Note.Pinned;
        _services.Scratchpad.Update(row.Note);
        Refresh();
    }

    void AskDelete(NoteRow row) => _showDialog(new ConfirmDialogViewModel(
        $"Delete \"{row.Title}\"?", "This can't be undone.", "Delete",
        () =>
        {
            _services.Scratchpad.Delete(row.Note.Id);
            _showDialog(null);
            Refresh();
            SelectedRow ??= Rows.FirstOrDefault();
        },
        () => _showDialog(null)));

    [RelayCommand]
    void NewNote()
    {
        var n = _services.Scratchpad.Create();
        Refresh();
        SelectedRow = Rows.FirstOrDefault(r => r.Note.Id == n.Id);
    }

    [RelayCommand]
    Task CopyNote() => _services.Shell.CopyToClipboardAsync(Body);
}
