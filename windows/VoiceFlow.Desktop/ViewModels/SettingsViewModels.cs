using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceFlow.Core;
using VoiceFlow.Core.Meetings;
using VoiceFlow.Desktop.Services;

namespace VoiceFlow.Desktop.ViewModels;

public sealed partial class SettingsNav : ObservableObject
{
    readonly Action<SettingsNav> _select;

    public SettingsNav(string title, string icon, object section, Action<SettingsNav> select)
    {
        Title = title;
        IconKey = icon;
        Section = section;
        _select = select;
    }

    public string Title { get; }
    public string IconKey { get; }
    public object Section { get; }

    [ObservableProperty] bool _isSelected;

    partial void OnIsSelectedChanged(bool value) { if (value) _select(this); }
}

public sealed partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(IAppServices services, Action runSetup)
    {
        General = new GeneralSettings(services);
        Notetaker = new NotetakerSettings(services);
        System = new SystemSettings(services);
        Keys = new KeysSettings(services);
        Privacy = new PrivacySettings(services);
        Advanced = new AdvancedSettings(services, runSetup);
        About = new AboutSettings(services);
        Sections =
        [
            new SettingsNav("General", "IconOptions", General, Select),
            new SettingsNav("Notetaker", "IconPeopleAudience", Notetaker, Select),
            new SettingsNav("System", "IconWindow", System, Select),
            new SettingsNav("API keys", "IconKey", Keys, Select),
            new SettingsNav("Data & Privacy", "IconShieldLock", Privacy, Select),
            new SettingsNav("Advanced", "IconWrench", Advanced, Select),
            new SettingsNav("About", "IconInfo", About, Select),
        ];
        Select(Sections[0]);
        Sections[0].IsSelected = true;
    }

    public GeneralSettings General { get; }
    public NotetakerSettings Notetaker { get; }
    public SystemSettings System { get; }
    public KeysSettings Keys { get; }
    public PrivacySettings Privacy { get; }
    public AdvancedSettings Advanced { get; }
    public AboutSettings About { get; }
    public IReadOnlyList<SettingsNav> Sections { get; }

    [ObservableProperty] SettingsNav? _selectedSection;
    [ObservableProperty] object? _current;

    void Select(SettingsNav nav)
    {
        foreach (var n in Sections) if (n != nav) n.IsSelected = false;
        SelectedSection = nav;
        Current = nav.Section;
    }
}

/// <summary>A choice shown in a combo box. The non-generic base lets one XAML template show them all.</summary>
public record Choice(string Label);

public sealed record Choice<T>(string Label, T Value) : Choice(Label);

public sealed partial class LanguageItem : ObservableObject
{
    readonly Action _changed;

    public LanguageItem(string code, string name, bool selected, Action changed)
    {
        Code = code;
        Name = name;
        _isSelected = selected;
        _changed = changed;
    }

    public string Code { get; }
    public string Name { get; }

    [ObservableProperty] bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _changed();
}

public sealed partial class TeluguScriptOption : ObservableObject
{
    readonly Action<TeluguScript> _select;

    public TeluguScriptOption(TeluguScript script, bool selected, Action<TeluguScript> select)
    {
        Script = script;
        Name = EnumText.DisplayName(script);
        Example = EnumText.Example(script);
        _isSelected = selected;
        _select = select;
    }

    public TeluguScript Script { get; }
    public string Name { get; }
    public string Example { get; }

    [ObservableProperty] bool _isSelected;

    partial void OnIsSelectedChanged(bool value) { if (value) _select(Script); }
}

public sealed class ShortcutRow(string action, params string[] keys)
{
    public string Action { get; } = action;
    public IReadOnlyList<string> Keys { get; } = keys;
}

public sealed partial class GeneralSettings : ObservableObject
{
    readonly IAppServices _services;
    const int PopularLanguages = 11;

    public GeneralSettings(IAppServices services)
    {
        _services = services;
        Devices = services.Microphone.Devices;
        HotkeyChoices = Enum.GetValues<Hotkey>().Select(h => new Choice<Hotkey>(HotkeyInfo.DisplayName(h), h)).ToList();
        CleanupChoices = Enum.GetValues<CleanupLevel>().Select(l => new Choice<CleanupLevel>(EnumText.DisplayName(l), l)).ToList();
        var chosen = Prefs.Languages;
        foreach (var (code, name) in Languages.Options)
            AllLanguages.Add(new LanguageItem(code, name, chosen.Contains(code), SaveLanguages));
        ScriptOptions = Enum.GetValues<TeluguScript>()
            .Select(s => new TeluguScriptOption(s, s == Prefs.TeluguScript, SelectScript)).ToList();
        _testLevel = services.Microphone.TestLevel;
        _isTesting = services.Microphone.IsTesting;
        services.Microphone.TestLevelChanged += (_, _) =>
        {
            TestLevel = services.Microphone.TestLevel;
            IsTesting = services.Microphone.IsTesting;
        };
    }

    Preferences Prefs => _services.Preferences.Current;

    public IReadOnlyList<MicrophoneDevice> Devices { get; }
    public IReadOnlyList<Choice<Hotkey>> HotkeyChoices { get; }
    public IReadOnlyList<Choice<CleanupLevel>> CleanupChoices { get; }
    public IReadOnlyList<TeluguScriptOption> ScriptOptions { get; }
    public List<LanguageItem> AllLanguages { get; } = [];
    public IEnumerable<LanguageItem> VisibleLanguages => ShowAllLanguages ? AllLanguages : AllLanguages.Take(PopularLanguages);

    [ObservableProperty] bool _showAllLanguages;
    [ObservableProperty] double _testLevel;
    [ObservableProperty] bool _isTesting;

    partial void OnShowAllLanguagesChanged(bool value) => OnPropertyChanged(nameof(VisibleLanguages));

    public string LanguagesHint
    {
        get
        {
            var picked = AllLanguages.Where(l => l.IsSelected).ToList();
            return picked.Count == 1
                ? $"VoiceFlow listens for {picked[0].Name}."
                : "VoiceFlow detects the language automatically. Choosing just one is most accurate.";
        }
    }

    public bool SpeaksTelugu => Prefs.SpeaksTelugu;

    public Choice<Hotkey> SelectedHotkey
    {
        get => HotkeyChoices.First(c => c.Value == Prefs.Hotkey);
        set { _services.Preferences.Update(p => p.Hotkey = value.Value); OnPropertyChanged(); OnPropertyChanged(nameof(Shortcuts)); }
    }

    public IEnumerable<ShortcutRow> Shortcuts
    {
        get
        {
            var key = HotkeyInfo.DisplayName(Prefs.Hotkey);
            yield return new ShortcutRow("Hands-free", HotkeyInfo.HandsFree(Prefs.Hotkey), "Double-tap");
            yield return new ShortcutRow("Command Mode", $"{key} + {HotkeyInfo.CommandModifier(Prefs.Hotkey)}");
            yield return new ShortcutRow("Paste last transcript", "Alt + Shift + V");
            yield return new ShortcutRow("Cancel", "Esc");
        }
    }

    public MicrophoneDevice? SelectedDevice
    {
        get => Devices.FirstOrDefault(d => d.Id == Prefs.InputDeviceId) ?? Devices.FirstOrDefault();
        set { if (value != null) _services.Preferences.Update(p => p.InputDeviceId = value.Id); OnPropertyChanged(); }
    }

    public Choice<CleanupLevel> SelectedCleanup
    {
        get => CleanupChoices.First(c => c.Value == Prefs.CleanupLevel);
        set { _services.Preferences.Update(p => p.CleanupLevel = value.Value); OnPropertyChanged(); OnPropertyChanged(nameof(CleanupHint)); }
    }

    public string CleanupHint => EnumText.Summary(Prefs.CleanupLevel);

    void SaveLanguages()
    {
        _services.Preferences.Update(p => p.Languages = AllLanguages.Where(l => l.IsSelected).Select(l => l.Code).ToList());
        OnPropertyChanged(nameof(LanguagesHint));
        OnPropertyChanged(nameof(SpeaksTelugu));
    }

    void SelectScript(TeluguScript script)
    {
        foreach (var o in ScriptOptions) if (o.Script != script) o.IsSelected = false;
        _services.Preferences.Update(p => p.TeluguScript = script);
    }

    [RelayCommand]
    void ToggleTest()
    {
        if (_services.Microphone.IsTesting) _services.Microphone.StopTest();
        else _services.Microphone.StartTest(Prefs.InputDeviceId);
    }
}

public sealed partial class NotetakerSettings : ObservableObject
{
    readonly IAppServices _services;

    public NotetakerSettings(IAppServices services)
    {
        _services = services;
        LanguageChoices = Enum.GetValues<NotesLanguage>().Select(l => new Choice<NotesLanguage>(l.DisplayName(), l)).ToList();
    }

    Preferences Prefs => _services.Preferences.Current;

    public IReadOnlyList<Choice<NotesLanguage>> LanguageChoices { get; }

    public bool SystemAudio
    {
        get => Prefs.MeetingsSystemAudio;
        set { _services.Preferences.Update(p => p.MeetingsSystemAudio = value); OnPropertyChanged(); }
    }

    public bool ConsentReminder
    {
        get => Prefs.MeetingsConsentReminder;
        set { _services.Preferences.Update(p => p.MeetingsConsentReminder = value); OnPropertyChanged(); }
    }

    public Choice<NotesLanguage> SelectedLanguage
    {
        get => LanguageChoices.First(c => c.Value == Prefs.MeetingsNotesLanguage);
        set { _services.Preferences.Update(p => p.MeetingsNotesLanguage = value.Value); OnPropertyChanged(); }
    }

    public string FolderText => Prefs.MeetingsFolder.Length > 0 ? Prefs.MeetingsFolder : "Notes stay in VoiceFlow only.";
    public bool HasFolder => Prefs.MeetingsFolder.Length > 0;

    [RelayCommand]
    async Task ChooseFolder()
    {
        var path = await _services.Shell.PickFolderAsync();
        if (path == null) return;
        _services.Preferences.Update(p => p.MeetingsFolder = path);
        OnPropertyChanged(nameof(FolderText));
        OnPropertyChanged(nameof(HasFolder));
    }

    [RelayCommand]
    void StopSyncing()
    {
        _services.Preferences.Update(p => p.MeetingsFolder = "");
        OnPropertyChanged(nameof(FolderText));
        OnPropertyChanged(nameof(HasFolder));
    }
}

public sealed partial class SystemSettings : ObservableObject
{
    readonly IAppServices _services;

    public SystemSettings(IAppServices services) => _services = services;

    Preferences Prefs => _services.Preferences.Current;

    public bool LaunchAtLogin
    {
        get => Prefs.LaunchAtLogin;
        set { _services.Preferences.Update(p => p.LaunchAtLogin = value); OnPropertyChanged(); }
    }

    public bool ShowBarWhenIdle
    {
        get => Prefs.ShowBarWhenIdle;
        set { _services.Preferences.Update(p => p.ShowBarWhenIdle = value); OnPropertyChanged(); }
    }

    public bool PlaySounds
    {
        get => Prefs.PlaySounds;
        set { _services.Preferences.Update(p => p.PlaySounds = value); OnPropertyChanged(); }
    }

    public bool MuteAudio
    {
        get => Prefs.MuteAudioWhileDictating;
        set { _services.Preferences.Update(p => p.MuteAudioWhileDictating = value); OnPropertyChanged(); }
    }
}

public sealed partial class KeysSettings : ObservableObject
{
    readonly IAppServices _services;

    public KeysSettings(IAppServices services)
    {
        _services = services;
        Groq = new KeyField(SecretKey.Groq, "Groq", "console.groq.com/keys", Core.Groq.KeysUrl, services);
    }

    public KeyField Groq { get; }
}

/// <summary>The Groq key field. Shared by Settings and onboarding.</summary>
public sealed partial class KeyField : ObservableObject
{
    readonly SecretKey _key;
    readonly IAppServices _services;
    readonly string _url;

    public KeyField(SecretKey key, string provider, string getItAt, string url, IAppServices services)
    {
        _key = key;
        _services = services;
        _url = url;
        Provider = provider;
        GetItAt = getItAt;
        services.Keys.Changed += (_, _) => OnPropertyChanged(nameof(IsStored));
    }

    public string Provider { get; }
    public string GetItAt { get; }
    public bool IsStored => _services.Keys.IsStored(_key);
    public string Status => IsStored ? "Saved" : "Not set";
    public string Placeholder => IsStored ? "Paste a new key to replace it" : "Paste your key";

    [ObservableProperty] string _draft = "";
    [ObservableProperty] bool _checking;
    [ObservableProperty] string _result = "";
    [ObservableProperty] bool _resultOk;
    [ObservableProperty] bool _hasResult;

    [RelayCommand]
    void OpenKeysPage() => _services.Shell.OpenUrl(_url);

    [RelayCommand]
    async Task Save()
    {
        if (Draft.Trim().Length == 0) return;
        _services.Keys.Save(_key, Draft.Trim());
        var d = Draft;
        Draft = "";
        OnPropertyChanged(nameof(IsStored));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Placeholder));
        await RunTest(d);
    }

    [RelayCommand]
    Task Test() => RunTest(Draft.Trim().Length > 0 ? Draft.Trim() : null);

    async Task RunTest(string? draft)
    {
        Checking = true;
        HasResult = false;
        var result = await _services.Keys.TestAsync(_key, draft);
        Checking = false;
        Result = result.Summary;
        ResultOk = result.Status == KeyCheckStatus.Ok;
        HasResult = true;
    }

    [RelayCommand]
    void Remove()
    {
        _services.Keys.Remove(_key);
        OnPropertyChanged(nameof(IsStored));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Placeholder));
        HasResult = false;
    }
}

public sealed partial class PrivacySettings : ObservableObject
{
    readonly IAppServices _services;

    public PrivacySettings(IAppServices services)
    {
        _services = services;
        RetentionChoices =
        [
            new("Forever", -1), new("30 days", 30), new("7 days", 7), new("Never store my data", 0),
        ];
    }

    public IReadOnlyList<Choice<int>> RetentionChoices { get; }

    public Choice<int> SelectedRetention
    {
        get => RetentionChoices.FirstOrDefault(c => c.Value == _services.Preferences.Current.HistoryRetentionDays) ?? RetentionChoices[1];
        set { _services.Preferences.Update(p => p.HistoryRetentionDays = value.Value); OnPropertyChanged(); }
    }

    public bool DebugLogging
    {
        get => _services.Preferences.Current.DebugLogging;
        set { _services.Preferences.Update(p => p.DebugLogging = value); OnPropertyChanged(); }
    }

    [ObservableProperty] string? _notice;

    public bool HasNotice => !string.IsNullOrEmpty(Notice);

    partial void OnNoticeChanged(string? value) => OnPropertyChanged(nameof(HasNotice));

    [RelayCommand]
    void OpenDataFolder() => _services.Shell.OpenDataFolder();

    [RelayCommand]
    void OpenGroqDataControls() => _services.Shell.OpenUrl("https://console.groq.com/settings/data-controls");

    [RelayCommand]
    void OpenGroqDataPolicy() => _services.Shell.OpenUrl("https://console.groq.com/docs/your-data");

    [RelayCommand]
    async Task CopyDebugReport()
    {
        await _services.Diagnostics.CopyReportAsync(_services.Shell);
        Notice = "Debug report copied. It has no dictated text in it.";
    }
}

public sealed partial class AdvancedSettings : ObservableObject
{
    readonly IAppServices _services;
    readonly Action _runSetup;

    public AdvancedSettings(IAppServices services, Action runSetup)
    {
        _services = services;
        _runSetup = runSetup;
    }

    Preferences Prefs => _services.Preferences.Current;

    public IReadOnlyList<string> SpeechModels => Core.Groq.TranscriptionModels;
    public IReadOnlyList<string> TextModels => Core.Groq.TextModels;

    public bool CommandMode
    {
        get => Prefs.CommandModeEnabled;
        set { _services.Preferences.Update(p => p.CommandModeEnabled = value); OnPropertyChanged(); }
    }

    public bool WhisperMode
    {
        get => Prefs.WhisperMode;
        set { _services.Preferences.Update(p => p.WhisperMode = value); OnPropertyChanged(); }
    }

    public string SpeechModel
    {
        get => Prefs.TranscriptionModel;
        set { _services.Preferences.Update(p => p.TranscriptionModel = value); OnPropertyChanged(); }
    }

    public string CleanupModel
    {
        get => Prefs.CleanupModel;
        set { _services.Preferences.Update(p => p.CleanupModel = value); OnPropertyChanged(); }
    }

    public string CommandModel
    {
        get => Prefs.CommandModel;
        set { _services.Preferences.Update(p => p.CommandModel = value); OnPropertyChanged(); }
    }

    [RelayCommand]
    void RunSetup() => _runSetup();
}

public sealed partial class AboutSettings : ObservableObject
{
    readonly IAppServices _services;

    public AboutSettings(IAppServices services)
    {
        _services = services;
        services.Updates.Changed += (_, _) => OnPropertyChanged(nameof(UpdateText));
    }

    public string Version => $"Version {_services.Updates.CurrentVersion}";

    public string UpdateText => _services.Updates.State switch
    {
        UpdateState.Checking => "Checking for updates...",
        UpdateState.UpToDate => "You have the latest version.",
        UpdateState.Available => $"Version {_services.Updates.AvailableVersion} is available.",
        UpdateState.Failed => "Could not check for updates.",
        _ => "Updates are checked at launch.",
    };

    public bool UpdateAvailable => _services.Updates.State == UpdateState.Available;

    [RelayCommand]
    Task CheckUpdates() => _services.Updates.CheckAsync();

    [RelayCommand]
    Task Install() => _services.Updates.InstallAsync();

    [RelayCommand]
    void OpenRepo() => _services.Shell.OpenUrl("https://github.com/saideepreddy7781/voiceflow");
}
