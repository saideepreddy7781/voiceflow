using Avalonia.Threading;
using VoiceFlow.Desktop.Services;

namespace VoiceFlow.Windows.Shell;

internal sealed record ToastAction(string Title, Action Run);

/// <summary>
/// State of the Flow bar: phase, levels and toasts. It has no window; <see cref="FlowBarHost"/> shows it. Everything
/// here runs on the UI thread.
/// </summary>
internal sealed class FlowBarController : IDictationService, IDisposable
{
    const int BarCount = 11;
    const float MinimumLevel = 0.08f;

    readonly float[] _history = new float[40];
    readonly DispatcherTimer _toastTimer = new();
    IReadOnlyList<float> _levels = [];
    bool _showWhenIdle;
    DateTimeOffset? _meetingSince;

    public FlowBarController()
    {
        _toastTimer.Tick += (_, _) => DismissToast();
    }

    public FlowPhase Phase { get; private set; } = FlowPhase.Idle;
    public IReadOnlyList<float> Levels => _levels;
    public FlowToast? Toast { get; private set; }
    public DateTimeOffset? MeetingSince
    {
        get => _meetingSince;
        set
        {
            _meetingSince = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool ShowWhenIdle
    {
        get => _showWhenIdle;
        set
        {
            _showWhenIdle = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The bar is on screen only for these: dictating, a toast, a meeting chip, or the user's "always show" setting.</summary>
    public bool ShouldShow => Phase != FlowPhase.Idle || Toast != null || _meetingSince != null || _showWhenIdle;

    public event EventHandler? Changed;
    public event EventHandler? LevelsChanged;
    public event Action? StartHandsFreeRequested;
    public event Action? ConfirmRequested;
    public event Action? CancelRequested;
    public event Action? StopMeetingRequested;
    public event Action? OpenMeetingsRequested;

    public void Set(FlowPhase phase)
    {
        Phase = phase;
        if (phase is FlowPhase.Listening or FlowPhase.HandsFree or FlowPhase.Command)
        {
            Array.Clear(_history);
            _levels = Enumerable.Repeat(MinimumLevel, BarCount).ToArray();
        }
        else if (phase == FlowPhase.Idle) _levels = [];
        Changed?.Invoke(this, EventArgs.Empty);
        LevelsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void PushLevel(float level)
    {
        Array.Copy(_history, 1, _history, 0, _history.Length - 1);
        _history[^1] = level;
        // Symmetric bars: loudest in the middle, recent levels spread outwards. A new array, so the binding sees a new value.
        var levels = new float[BarCount];
        var mid = BarCount / 2;
        for (int i = 0; i < BarCount; i++)
        {
            var distance = Math.Abs(i - mid);
            var sample = _history[_history.Length - 1 - distance * 2];
            var falloff = 1 - distance / (float)(mid + 2);
            levels[i] = Math.Max(MinimumLevel, sample * falloff);
        }
        _levels = levels;
        LevelsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Shows a message above the bar for <paramref name="seconds"/>. A processing bar returns to rest, since the
    /// toast reports how that ended; a recording bar keeps recording.
    /// </summary>
    public void Show(string message, string detail = "", ToastAction? action = null, double seconds = 4)
    {
        if (Phase == FlowPhase.Processing) Phase = FlowPhase.Idle;
        var run = action == null ? (Action?)null : () =>
        {
            action.Run();
            DismissToast();
        };
        Toast = new FlowToast(message, detail, action?.Title, run);
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(500, seconds * 1000));
        _toastTimer.Start();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void DismissToast()
    {
        _toastTimer.Stop();
        if (Toast == null) return;
        Toast = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void StartHandsFree() => StartHandsFreeRequested?.Invoke();
    public void Confirm() => ConfirmRequested?.Invoke();
    public void Cancel() => CancelRequested?.Invoke();
    public void StopMeeting() => StopMeetingRequested?.Invoke();
    public void OpenMeetings() => OpenMeetingsRequested?.Invoke();

    public void Dispose() => _toastTimer.Stop();
}
