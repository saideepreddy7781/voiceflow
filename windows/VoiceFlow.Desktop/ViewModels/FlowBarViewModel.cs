using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceFlow.Core.Meetings;
using VoiceFlow.Desktop.Services;

namespace VoiceFlow.Desktop.ViewModels;

/// <summary>State of the Flow bar, the small capsule on screen while you dictate.</summary>
public sealed partial class FlowBarViewModel : ObservableObject
{
    readonly IDictationService _dictation;
    readonly IPreferencesService _preferences;
    readonly DispatcherTimer? _clock;
    bool _showMenu;

    public FlowBarViewModel(IAppServices services, bool runClock = true)
    {
        _dictation = services.Dictation;
        _preferences = services.Preferences;
        _dictation.Changed += (_, _) => Refresh();
        _preferences.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(MenuHint));
            OnPropertyChanged(nameof(LockTitle));
            ResetPositionCommand.NotifyCanExecuteChanged();
        };
        _dictation.LevelsChanged += (_, _) => OnPropertyChanged(nameof(Levels));
        if (runClock)
        {
            _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clock.Tick += (_, _) => OnPropertyChanged(nameof(MeetingClock));
            _clock.Start();
        }
        Refresh();
    }

    public FlowPhase Phase => _dictation.Phase;
    public IReadOnlyList<float> Levels => _dictation.Levels;

    public bool ShowPill => Phase != FlowPhase.Idle;
    public bool ShowIdlePill => Phase == FlowPhase.Idle && _dictation.MeetingSince == null && _dictation.ShowWhenIdle;
    public bool ShowHandsFree => Phase is FlowPhase.HandsFree or FlowPhase.Command;
    public bool IsCommand => Phase == FlowPhase.Command;
    public bool Frozen => Phase == FlowPhase.Processing;

    public bool ShowMeetingChip => Phase == FlowPhase.Idle && _dictation.MeetingSince != null;
    public string MeetingClock => _dictation.MeetingSince is { } s ? Meeting.Clock((DateTimeOffset.Now - s).TotalSeconds) : "0:00";

    public FlowToast? Toast => _dictation.Toast;
    public bool ShowToast => Toast != null;
    public string ToastMessage => Toast?.Message ?? "";
    public string ToastDetail => Toast?.Detail ?? "";
    public bool HasToastDetail => ToastDetail.Length > 0;
    public string? ToastActionTitle => Toast?.ActionTitle;
    public bool HasToastAction => Toast?.Action != null;

    /// <summary>The small menu a right-click on the bar opens.</summary>
    public bool ShowMenu
    {
        get => _showMenu;
        set
        {
            if (SetProperty(ref _showMenu, value)) ResetPositionCommand.NotifyCanExecuteChanged();
        }
    }

    bool Locked => _preferences.Current.FlowBarLocked;
    public string MenuHint => Locked ? "Position locked" : "Drag to move";
    public string LockTitle => Locked ? "Unlock" : "Lock";
    bool CanResetPosition => _preferences.Current.FlowBarPositions.Count > 0;

    void Refresh() => OnPropertyChanged(string.Empty);

    [RelayCommand] void StartHandsFree() => _dictation.StartHandsFree();
    [RelayCommand] void Cancel() => _dictation.Cancel();
    [RelayCommand] void Confirm() => _dictation.Confirm();
    [RelayCommand] void StopMeeting() => _dictation.StopMeeting();
    [RelayCommand] void OpenMeetings() => _dictation.OpenMeetings();
    [RelayCommand] void RunToastAction() => Toast?.Action?.Invoke();
    [RelayCommand] void DismissToast() => _dictation.DismissToast();

    [RelayCommand] void ToggleMenu() => ShowMenu = !ShowMenu;

    /// <summary>Back to the bottom centre, for every arrangement of displays.</summary>
    [RelayCommand(CanExecute = nameof(CanResetPosition))]
    void ResetPosition()
    {
        _preferences.Update(p => p.FlowBarPositions = []);
        ShowMenu = false;
    }

    [RelayCommand]
    void ToggleLock()
    {
        _preferences.Update(p => p.FlowBarLocked = !p.FlowBarLocked);
        ShowMenu = false;
    }
}
