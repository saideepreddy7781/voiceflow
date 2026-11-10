using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceFlow.Core.Meetings;
using VoiceFlow.Desktop.Services;

namespace VoiceFlow.Desktop.ViewModels;

/// <summary>State of the Flow bar, the small pill at the bottom of the screen.</summary>
public sealed partial class FlowBarViewModel : ObservableObject
{
    readonly IDictationService _dictation;
    readonly DispatcherTimer? _clock;

    public FlowBarViewModel(IAppServices services, bool runClock = true)
    {
        _dictation = services.Dictation;
        _dictation.Changed += (_, _) => Refresh();
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

    void Refresh() => OnPropertyChanged(string.Empty);

    [RelayCommand] void StartHandsFree() => _dictation.StartHandsFree();
    [RelayCommand] void Cancel() => _dictation.Cancel();
    [RelayCommand] void Confirm() => _dictation.Confirm();
    [RelayCommand] void StopMeeting() => _dictation.StopMeeting();
    [RelayCommand] void RunToastAction() => Toast?.Action?.Invoke();
    [RelayCommand] void DismissToast() => _dictation.DismissToast();
}
