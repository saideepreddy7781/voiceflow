using CommunityToolkit.Mvvm.ComponentModel;
using VoiceFlow.Desktop.Services;

namespace VoiceFlow.Desktop.ViewModels;

public sealed partial class ShellViewModel : ObservableObject
{
    readonly Dictionary<Page, object> _pages = [];
    readonly IAppServices _services;
    readonly Func<DateTimeOffset> _clock;
    bool _selecting;

    public ShellViewModel(IAppServices services, DateTimeOffset? now = null)
    {
        _services = services;
        _clock = now is { } fixedNow ? () => fixedNow : () => DateTimeOffset.Now;
        TopItems =
        [
            new NavItem(Page.Home, "Home", "IconHome", OnSelected),
            new NavItem(Page.Meetings, "Meetings", "IconPeopleAudience", OnSelected),
            new NavItem(Page.Insights, "Insights", "IconDataBarVertical", OnSelected),
            new NavItem(Page.Dictionary, "Dictionary", "IconBookOpen", OnSelected),
            new NavItem(Page.Snippets, "Snippets", "IconTextBulletListSquare", OnSelected),
            new NavItem(Page.Style, "Style", "IconTextFont", OnSelected),
            new NavItem(Page.Scratchpad, "Scratchpad", "IconNote", OnSelected),
        ];
        BottomItems =
        [
            new NavItem(Page.Settings, "Settings", "IconSettings", OnSelected),
            new NavItem(Page.Help, "Help", "IconQuestionCircle", OnSelected),
        ];
        Navigate(Page.Home);
    }

    public DateTimeOffset Now => _clock();
    public IReadOnlyList<NavItem> TopItems { get; }
    public IReadOnlyList<NavItem> BottomItems { get; }
    public IEnumerable<NavItem> AllItems => TopItems.Concat(BottomItems);

    [ObservableProperty] object? _current;
    [ObservableProperty] object? _dialog;

    public HomeViewModel Home => (HomeViewModel)Page_(Page.Home);
    public MeetingsViewModel Meetings => (MeetingsViewModel)Page_(Page.Meetings);
    public InsightsViewModel Insights => (InsightsViewModel)Page_(Page.Insights);
    public DictionaryViewModel Dictionary => (DictionaryViewModel)Page_(Page.Dictionary);
    public SnippetsViewModel Snippets => (SnippetsViewModel)Page_(Page.Snippets);
    public StyleViewModel Style => (StyleViewModel)Page_(Page.Style);
    public ScratchpadViewModel Scratchpad => (ScratchpadViewModel)Page_(Page.Scratchpad);
    public SettingsViewModel Settings => (SettingsViewModel)Page_(Page.Settings);

    object Page_(Page page)
    {
        if (!_pages.TryGetValue(page, out var vm))
        {
            vm = page switch
            {
                Page.Home => new HomeViewModel(_services, _clock, RunSetup),
                Page.Meetings => new MeetingsViewModel(_services, ShowDialog),
                Page.Insights => new InsightsViewModel(_services, _clock),
                Page.Dictionary => new DictionaryViewModel(_services, ShowDialog),
                Page.Snippets => new SnippetsViewModel(_services, ShowDialog),
                Page.Style => new StyleViewModel(_services),
                Page.Scratchpad => new ScratchpadViewModel(_services, ShowDialog),
                Page.Settings => new SettingsViewModel(_services, RunSetup),
                _ => new HelpViewModel(_services),
            };
            _pages[page] = vm;
        }
        return vm;
    }

    public void Navigate(Page page)
    {
        var item = AllItems.First(i => i.Page == page);
        if (item.IsSelected) { Current = Page_(page); return; }
        item.IsSelected = true;
    }

    void OnSelected(NavItem item)
    {
        if (_selecting) return;
        _selecting = true;
        foreach (var other in AllItems)
            if (other != item) other.IsSelected = false;
        _selecting = false;
        if (item.Page == Page.Settings) Settings.General.RefreshDevices();
        Current = Page_(item.Page);
    }

    /// <summary>Called when the window comes to the front: the day may have changed since the pages last updated.</summary>
    public void Refresh()
    {
        if (_pages.TryGetValue(Page.Home, out var home)) ((HomeViewModel)home).Reload();
        if (_pages.TryGetValue(Page.Insights, out var insights)) ((InsightsViewModel)insights).Reload();
    }

    /// <summary>Set by the app to open the onboarding window.</summary>
    public Action? OnRunSetup { get; set; }
    void RunSetup() => OnRunSetup?.Invoke();

    public void ShowDialog(object? dialog) => Dialog = dialog;
}
