using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceFlow.Core;
using VoiceFlow.Core.Library;
using VoiceFlow.Desktop.Services;

namespace VoiceFlow.Desktop.ViewModels;

public sealed partial class HistoryRowViewModel : ObservableObject
{
    readonly IAppServices _services;
    readonly HistoryEntry _entry;
    readonly Action _refresh;

    public HistoryRowViewModel(HistoryEntry entry, IAppServices services, Action refresh)
    {
        _entry = entry;
        _services = services;
        _refresh = refresh;
        Time = entry.Date.ToLocalTime().ToString("h:mm tt", CultureInfo.CurrentCulture);
        Text = entry.Final.Length == 0 ? "(no text)" : entry.Final;
        App = entry.AppName;
        IsCommand = entry.Kind == HistoryKind.Command;
        Failed = entry.Outcome == "failed";
    }

    public string Time { get; }
    public string Text { get; }
    public string App { get; }
    public bool IsCommand { get; }
    public bool Failed { get; }
    public bool Flagged => _entry.Flagged == true;

    [RelayCommand]
    Task Copy() => _services.Shell.CopyToClipboardAsync(_entry.Final);

    [RelayCommand]
    Task Paste() => _services.History.PasteAsync(_entry.Final);

    [RelayCommand]
    void ToggleFlag()
    {
        _services.History.ToggleFlag(_entry.Id);
        OnPropertyChanged(nameof(Flagged));
    }

    [RelayCommand]
    Task CopyRaw() => _services.Shell.CopyToClipboardAsync(_entry.Raw);

    [RelayCommand]
    void Delete()
    {
        _services.History.Delete(_entry.Id);
        _refresh();
    }
}

public sealed record HistoryGroup(string Label, IReadOnlyList<HistoryRowViewModel> Rows);

public sealed partial class HomeViewModel : ObservableObject
{
    readonly IAppServices _services;
    readonly Func<DateTimeOffset> _clock;

    public HomeViewModel(IAppServices services, DateTimeOffset now, Action runSetup) : this(services, () => now, runSetup) { }

    public HomeViewModel(IAppServices services, Func<DateTimeOffset> clock, Action runSetup)
    {
        _services = services;
        _clock = clock;
        RunSetupCommand = new RelayCommand(runSetup);
        services.History.Changed += (_, _) => Refresh();
        services.Setup.Changed += (_, _) => RefreshSetup();
        services.Preferences.Changed += (_, _) => OnPropertyChanged(nameof(Hotkey));
        services.Updates.Changed += (_, _) => RefreshUpdate();
        RefreshSetup();
        Refresh();
        RefreshUpdate();
    }

    public IRelayCommand RunSetupCommand { get; }

    public string Greeting => _clock().ToLocalTime().Hour switch
    {
        < 12 => "Good morning",
        < 17 => "Good afternoon",
        _ => "Good evening",
    };

    public string Hotkey => HotkeyInfo.DisplayName(_services.Preferences.Current.Hotkey);

    public ObservableCollection<SetupProblem> SetupProblems { get; } = [];
    public bool ShowSetup => SetupProblems.Count > 0;

    public ObservableCollection<HistoryGroup> Groups { get; } = [];

    [ObservableProperty] string _wordsToday = "0";
    [ObservableProperty] string _dictationsToday = "0";
    [ObservableProperty] string _timeSavedToday = "0 min";
    [ObservableProperty] string _wordsThisWeek = "0";
    [ObservableProperty] bool _noHistory;
    [ObservableProperty] bool _noMatches;
    [ObservableProperty] string _searchText = "";
    [ObservableProperty] bool _hasUpdate;
    [ObservableProperty] string _updateNotice = "";

    partial void OnSearchTextChanged(string value) => RefreshList();

    /// <summary>Called when the window comes back: the day may have changed.</summary>
    public void Reload()
    {
        OnPropertyChanged(nameof(Greeting));
        RefreshSetup();
        Refresh();
    }

    void RefreshUpdate()
    {
        HasUpdate = _services.Updates.State == UpdateState.Available;
        UpdateNotice = HasUpdate ? $"VoiceFlow {_services.Updates.AvailableVersion} is available." : "";
    }

    [RelayCommand]
    void OpenUpdate() => _services.Updates.OpenRelease();

    void RefreshSetup()
    {
        SetupProblems.Clear();
        foreach (var p in _services.Setup.Problems) SetupProblems.Add(p);
        OnPropertyChanged(nameof(ShowSetup));
    }

    void Refresh()
    {
        var now = _clock();
        var today = now.Date;
        var entries = _services.History.Entries;
        var todayEntries = entries.Where(e => e.Date.Date == today && e.Outcome != "failed").ToList();
        var words = todayEntries.Sum(e => e.WordCount);
        var seconds = todayEntries.Sum(e => e.AudioSeconds);
        WordsToday = words.ToString("N0", CultureInfo.CurrentCulture);
        DictationsToday = todayEntries.Count.ToString("N0", CultureInfo.CurrentCulture);
        // Typing averages about 40 words a minute (the figure the Insights page uses).
        var saved = Math.Max(0, words / 40.0 - seconds / 60);
        TimeSavedToday = words == 0 ? "0 min" : saved < 1 ? "<1 min" : $"{Math.Round(saved)} min";
        WordsThisWeek = UsageStats.Compute(entries, now).WordsThisWeek.ToString("N0", CultureInfo.CurrentCulture);
        NoHistory = entries.Count == 0;
        RefreshList();
    }

    void RefreshList()
    {
        var query = SearchText.Trim();
        var filtered = _services.History.Entries.Where(e => query.Length == 0
            || e.Final.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || e.Raw.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || e.AppName.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        Groups.Clear();
        foreach (var group in GroupByDay(filtered, _clock(), this))
            Groups.Add(group);
        NoMatches = Groups.Count == 0 && !NoHistory;
    }

    static IEnumerable<HistoryGroup> GroupByDay(IEnumerable<HistoryEntry> entries, DateTimeOffset now, HomeViewModel vm)
    {
        foreach (var day in entries.GroupBy(e => e.Date.ToLocalTime().Date).OrderByDescending(g => g.Key).Take(30))
        {
            var label = day.Key == now.ToLocalTime().Date ? "Today"
                : day.Key == now.ToLocalTime().Date.AddDays(-1) ? "Yesterday"
                : day.Key.ToString("dddd, MMMM d", CultureInfo.CurrentCulture);
            yield return new HistoryGroup(label, day.Select(e => new HistoryRowViewModel(e, vm._services, vm.Refresh)).ToList());
        }
    }
}
