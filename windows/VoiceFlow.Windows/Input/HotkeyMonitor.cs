using System.Diagnostics;
using Microsoft.Win32;
using VoiceFlow.Core;
using VoiceFlow.Core.Input;
using VoiceFlow.Windows.Native;
using static VoiceFlow.Windows.Native.NativeMethods;

namespace VoiceFlow.Windows.Input;

/// <summary>
/// The global hotkey: a low-level keyboard hook feeding <see cref="HotkeyProcessor"/>. The hook lives on its own
/// thread with its own message loop, so a busy UI thread (a large clipboard, saving history, drawing) never delays
/// keystrokes system-wide or makes Windows remove the hook after LowLevelHooksTimeout. The callback only runs the
/// (pure, fast) processor under a lock and returns; gesture actions are posted to the UI thread.
/// </summary>
internal sealed class HotkeyMonitor : IDisposable
{
    readonly Control _poster;
    readonly KeyboardHook _hook;
    readonly HotkeyProcessor _processor;
    readonly System.Windows.Forms.Timer _tickTimer = new();
    readonly System.Windows.Forms.Timer _refreshTimer = new() { Interval = 10 * 60 * 1000 };
    readonly Action<GestureAction> _onGesture;
    readonly Action<HotkeyShortcut> _onShortcut;
    /// <summary>Guards <see cref="_processor"/>, used by the hook thread and the UI thread.</summary>
    readonly object _gate = new();
    const uint WM_REINSTALL = WM_APP + 1;
    Thread? _thread;
    volatile uint _threadId;
    int _ownInjectedSeen;

    /// <summary>Counts our own injected events seen by the hook (the self-test checks this).</summary>
    public int OwnInjectedSeen => Volatile.Read(ref _ownInjectedSeen);

    public HotkeyMonitor(Control poster, Hotkey hotkey, Action<GestureAction> onGesture, Action<HotkeyShortcut> onShortcut)
    {
        _poster = poster;
        _onGesture = onGesture;
        _onShortcut = onShortcut;
        _processor = new HotkeyProcessor(hotkey, new GestureRecognizer(), vk => (GetAsyncKeyState(vk) & 0x8000) != 0);
        _hook = new KeyboardHook(OnKey);
        _tickTimer.Tick += (_, _) =>
        {
            _tickTimer.Stop();
            IReadOnlyList<GestureAction> actions;
            lock (_gate) actions = _processor.Recognizer.Handle(GestureEvent.Tick(Now));
            Dispatch(actions);
        };
        // Windows can drop a hook silently (sleep, a stalled hook thread). While nothing is happening, reinstall it now and then.
        _refreshTimer.Tick += (_, _) =>
        {
            bool idle;
            lock (_gate) idle = !_processor.Recognizer.IsActive && !_processor.ChordActive;
            if (idle) Reinstall("periodic refresh");
        };
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    public bool IsRunning => _hook.IsInstalled;

    static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>Starts the hook thread and waits until the hook is installed. Throws if it could not be.</summary>
    public void Start()
    {
        if (_thread != null) return;
        Exception? error = null;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => HookThread(ready, e => error = e))
        {
            IsBackground = true,
            Name = "VoiceFlow keyboard hook",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(5))) error ??= new TimeoutException("The keyboard hook thread did not start.");
        if (error != null)
        {
            _thread = null;
            throw error as System.ComponentModel.Win32Exception ?? new System.ComponentModel.Win32Exception(0, error.Message);
        }
        _refreshTimer.Start();
        Logger.Info("Keyboard hook installed");
    }

    public void Stop()
    {
        _refreshTimer.Stop();
        if (_thread is { } thread)
        {
            PostThreadMessageW(_threadId, WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
            thread.Join(TimeSpan.FromSeconds(2));
            _thread = null;
        }
        ResetSession(notify: false);
    }

    void HookThread(ManualResetEventSlim ready, Action<Exception> fail)
    {
        _threadId = GetCurrentThreadId();
        PeekMessageW(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);   // creates this thread's message queue for PostThreadMessage
        try
        {
            _hook.Install();
        }
        catch (Exception e)
        {
            fail(e);
            ready.Set();
            return;
        }
        ready.Set();
        // GetMessage also delivers the hook callbacks on this thread. It returns 0 on WM_QUIT and -1 on error.
        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message != WM_REINSTALL) continue;
            try
            {
                lock (_gate) _processor.ResetKeys();
                _hook.Reinstall();
            }
            catch (Exception e)
            {
                Logger.Error("Keyboard hook could not be reinstalled", e);
            }
        }
        _hook.Uninstall();
    }

    public void SetHotkey(Hotkey hotkey)
    {
        lock (_gate) _processor.SetHotkey(hotkey);
        ResetSession();
    }

    /// <summary>
    /// Clears the current gesture. With <paramref name="notify"/>, the app also gets a Cancel; without it (the app
    /// already finished or cancelled the session itself) the reset is silent.
    /// </summary>
    public void ResetSession(bool notify = true)
    {
        _tickTimer.Stop();
        bool wasActive;
        lock (_gate)
        {
            wasActive = _processor.Recognizer.IsActive;
            if (wasActive) _processor.Recognizer.Reset();
        }
        if (wasActive && notify) _onGesture(GestureAction.Cancel);
    }

    /// <summary>Hands-free started from outside the keyboard (Flow bar click): the next press finishes, Esc cancels.</summary>
    public void EnterHandsFree()
    {
        _tickTimer.Stop();
        lock (_gate) _processor.Recognizer.EnterHandsFree();
    }

    bool OnKey(int vk, int scanCode, bool down, uint flags, IntPtr extraInfo)
    {
        var own = (flags & LLKHF_INJECTED) != 0 && extraInfo == InputSender.Marker;
        if (own) Interlocked.Increment(ref _ownInjectedSeen);
        HookResult result;
        lock (_gate) result = _processor.Handle(new KeyInput(vk, down, scanCode, own), Now);
        if (result.Actions.Count > 0) Dispatch(result.Actions);
        if (result.Shortcut is { } shortcut)
        {
            // V was swallowed, so Windows would see a bare Alt+Shift release (the input-language switch) and a bare
            // Alt (menu bar). A masked key while they are still down prevents both.
            InputSender.TapMaskKey();
            _poster.BeginInvoke(() => _onShortcut(shortcut));
        }
        switch (result.Verdict)
        {
            case HookVerdict.Swallow:
                return true;
            case HookVerdict.SwallowAndReleaseWithMask:
                InputSender.ReleaseWithMask(vk);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Called on the hook thread or the UI thread; everything it starts runs on the UI thread.</summary>
    void Dispatch(IReadOnlyList<GestureAction> actions)
    {
        foreach (var action in actions)
        {
            var a = action;
            // Logging happens in the posted work: the hook callback does no I/O.
            _poster.BeginInvoke(() =>
            {
                Logger.Debug($"gesture {a}");
                _onGesture(a);
            });
        }
        if (_poster.InvokeRequired) _poster.BeginInvoke(ScheduleTick);
        else ScheduleTick();
    }

    void ScheduleTick()
    {
        _tickTimer.Stop();
        double? next;
        lock (_gate) next = _processor.Recognizer.Deadline;
        if (next is not { } deadline) return;
        _tickTimer.Interval = Math.Max(1, (int)Math.Ceiling((deadline - Now) * 1000) + 5);
        _tickTimer.Start();
    }

    void Reinstall(string reason)
    {
        // The hook belongs to its thread, so the reinstall happens there.
        if (_thread != null && PostThreadMessageW(_threadId, WM_REINSTALL, UIntPtr.Zero, IntPtr.Zero))
            Logger.Debug($"Keyboard hook reinstall requested ({reason})");
        else
            Logger.Error($"Keyboard hook reinstall could not be requested ({reason})");
    }

    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
            _poster.BeginInvoke(() => { ResetSession(); Reinstall("session unlocked"); });
        else if (e.Reason == SessionSwitchReason.SessionLock)
            _poster.BeginInvoke(() => ResetSession());
    }

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _poster.BeginInvoke(() => { ResetSession(); Reinstall("resumed"); });
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        Stop();
        _tickTimer.Dispose();
        _refreshTimer.Dispose();
    }
}
