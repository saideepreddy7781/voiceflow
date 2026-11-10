using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceFlow.Core;
using VoiceFlow.Desktop.Services;

namespace VoiceFlow.Desktop.ViewModels;

public sealed partial class HelpViewModel : ObservableObject
{
    readonly IAppServices _services;

    public HelpViewModel(IAppServices services) => _services = services;

    string Key => HotkeyInfo.DisplayName(_services.Preferences.Current.Hotkey);

    public string Hotkey => Key;
    public string HandsFree => HotkeyInfo.HandsFree(_services.Preferences.Current.Hotkey);
    public string Command => $"{Key} + {HotkeyInfo.CommandModifier(_services.Preferences.Current.Hotkey)}";
    public string Version => $"Version {_services.Updates.CurrentVersion}";

    [RelayCommand]
    void OpenDocs() => _services.Shell.OpenUrl("https://github.com/saideepreddy7781/voiceflow#readme");

    [RelayCommand]
    void ReportIssue() => _services.Shell.OpenUrl("https://github.com/saideepreddy7781/voiceflow/issues");
}
