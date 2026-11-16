using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Threading;
using VoiceFlow.Core.Meetings;
using VoiceFlow.Windows.Audio;
using VoiceFlow.Windows.Dictation;
using VoiceFlow.Windows.Meetings;
using VoiceFlow.Windows.Native;
using static VoiceFlow.Windows.Native.NativeMethods;

namespace VoiceFlow.Windows.Shell;

/// <summary>
/// Looks for a call every few seconds and offers Notetaker with a toast, once per call. It never starts recording.
/// Window titles are read only in memory and are never logged or stored.
/// </summary>
internal sealed class MeetingWatcher : IDisposable
{
    readonly AppModel _model;
    readonly DictationController _dictation;
    readonly Notetaker _notetaker;
    readonly FlowBarController _bar;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    bool _scanning;
    bool _offeredThisCall;

    public MeetingWatcher(AppModel model, DictationController dictation, Notetaker notetaker, FlowBarController bar)
    {
        _model = model;
        _dictation = dictation;
        _notetaker = notetaker;
        _bar = bar;
        _timer.Tick += async (_, _) => await ScanAsync();
    }

    public void Start() => _timer.Start();

    async Task ScanAsync()
    {
        if (_scanning || !_model.Preferences.MeetingsDetect || _notetaker.Current != Notetaker.State.Idle || _dictation.IsRecording) return;
        _scanning = true;
        try
        {
            var app = await Task.Run(() => MeetingDetector.Detect(VisibleWindows(), AudioSessions.AnotherAppIsRecording()));
            if (app == null)
            {
                _offeredThisCall = false;   // the call ended; offer again next time
                return;
            }
            if (_offeredThisCall || _notetaker.Current != Notetaker.State.Idle) return;
            _offeredThisCall = true;
            _bar.Show($"{app} call detected", "Take notes of this meeting?", new ToastAction("Take notes", () => _notetaker.Start(app)), 15);
        }
        finally
        {
            _scanning = false;
        }
    }

    /// <summary>Process file names and titles of visible top-level windows. Runs off the UI thread.</summary>
    static List<WindowSnapshot> VisibleWindows()
    {
        var windows = new List<WindowSnapshot>();
        var names = new Dictionary<uint, string>();
        var title = new StringBuilder(256);
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!names.TryGetValue(pid, out var name))
            {
                name = FocusInfo.ProcessFileName(pid);
                names[pid] = name;
            }
            if (name.Length == 0) return true;
            title.Clear();
            InternalGetWindowText(hwnd, title, title.Capacity);
            windows.Add(new WindowSnapshot(name, title.ToString()));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    public void Dispose() => _timer.Stop();
}
