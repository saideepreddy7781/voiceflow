using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using VoiceFlow.Core;
using VoiceFlow.Core.Input;
using VoiceFlow.Desktop.ViewModels;
using VoiceFlow.Desktop.Views;
using VoiceFlow.Windows.Audio;
using VoiceFlow.Windows.Dictation;
using VoiceFlow.Windows.Input;
using VoiceFlow.Windows.Meetings;
using VoiceFlow.Windows.Native;
using static VoiceFlow.Windows.Native.NativeMethods;
using Page = VoiceFlow.Desktop.ViewModels.Page;

namespace VoiceFlow.Windows.Shell;

/// <summary>
/// The running app: a notification-area icon that stays all day and owns the hotkey, the Flow bar, dictation,
/// Notetaker and the main window. Everything here runs on the UI thread.
/// </summary>
internal sealed class WindowsApp
{
    readonly IClassicDesktopStyleApplicationLifetime _desktop;
    readonly MessageWindow _clipboardOwner = new();
    readonly AppModel _model;
    readonly FlowBarController _bar = new();
    readonly FlowBarHost _barHost;
    readonly TextInserter _inserter;
    readonly OutputMuter _muter;
    readonly DictationController _dictation;
    readonly Notetaker _notetaker;
    readonly HotkeyMonitor _hotkeys;
    readonly AppServices _services;
    readonly UpdateService _updates;
    readonly MeetingWatcher _meetingWatcher;
    readonly TrayIcon _tray;
    readonly NativeMenuItem _meetingItem;
    readonly DispatcherTimer _housekeeping = new() { Interval = TimeSpan.FromHours(1) };
    readonly WinEventDelegate _foregroundProc;   // kept alive for the WinEvent hook
    readonly IntPtr _foregroundHook;
    readonly ShellViewModel _shell;
    MainWindow? _main;
    OnboardingWindow? _onboarding;
    IntPtr _lastExternalWindow;
    bool _quit;
    System.Threading.Timer? _quitWatchdog;   // a field, so the GC can't collect it while quitting

    public static WindowsApp? Current { get; private set; }

    public WindowsApp(IClassicDesktopStyleApplicationLifetime desktop, bool background)
    {
        Current = this;
        _desktop = desktop;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        desktop.Exit += (_, _) => EmergencyCleanup();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => EmergencyCleanup();

        _model = new AppModel(AppPaths.DataFolder);
        Logger.DebugEnabled = _model.Preferences.DebugLogging;
        _muter = new OutputMuter(_model.DataFolder);
        _muter.RecoverAfterCrash();
        _inserter = new TextInserter(new ClipboardService(_clipboardOwner.Handle));
        _dictation = new DictationController(_model, _bar, _inserter, _muter);
        _notetaker = new Notetaker(_model, _bar);
        _hotkeys = new HotkeyMonitor(_model.Preferences.Hotkey, OnGesture, OnShortcut);

        _services = new AppServices(_model, _dictation, _notetaker, _bar, _inserter, topLevel: () => _main);
        _updates = (UpdateService)_services.Updates;
        _shell = new ShellViewModel(_services) { OnRunSetup = () => RunOnboarding(_main) };
        _barHost = new FlowBarHost(_bar, new FlowBarViewModel(_services));
        _meetingWatcher = new MeetingWatcher(_model, _dictation, _notetaker, _bar);

        _dictation.ResetHotkeySession = notify => _hotkeys.ResetSession(notify);
        _dictation.EnterHandsFreeGesture = _hotkeys.EnterHandsFree;
        _dictation.MeetingRecording = () => _notetaker.IsRecording;
        _dictation.OpenSettings = () => ShowMain(Page.Settings);
        _dictation.LastExternalWindow = () => _lastExternalWindow;
        _notetaker.OpenMeetingsPage = () => ShowMain(Page.Meetings);

        _bar.StartHandsFreeRequested += _dictation.StartHandsFreeFromBar;
        _bar.ConfirmRequested += () => { _dictation.Finish(); _hotkeys.ResetSession(notify: false); };
        _bar.CancelRequested += () => { _dictation.Cancel(); _hotkeys.ResetSession(notify: false); };
        _bar.StopMeetingRequested += () => _notetaker.Stop();
        _bar.OpenMeetingsRequested += () => ShowMain(Page.Meetings);

        _model.PreferencesChanged += OnPreferencesChanged;
        _notetaker.Changed += UpdateTray;

        _meetingItem = new NativeMenuItem("Start meeting notes");
        _meetingItem.Click += (_, _) => Guarded(ToggleMeeting);
        _tray = BuildTray();
        UpdateTray();

        // Only now: a change here raises PreferencesChanged, which updates the tray menu.
        // The registry is the truth for "Start with Windows" (the user can turn it off in Task Manager).
        // When it is on, rewrite it so it points at this copy of VoiceFlow.exe.
        var startup = StartupRegistration.IsEnabled;
        if (startup) TrySetStartup(true);
        if (startup != _model.Preferences.LaunchAtLogin) _model.UpdatePreferences(p => p.LaunchAtLogin = startup);

        // Remember the last window the user worked in, so "Paste last" from this menu goes back there.
        _foregroundProc = OnForegroundChanged;
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _foregroundProc, 0, 0,
                                          WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        _housekeeping.Tick += (_, _) => _model.ApplyRetention();
        _housekeeping.Start();
        _meetingWatcher.Start();
        _updates.Start();

        try
        {
            _hotkeys.Start();
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            Logger.Error("Hotkey could not start", e);
            _bar.Show("The hotkey could not start", "Restart VoiceFlow. If it keeps happening, check the log in Settings.", seconds: 10);
        }

        _bar.ShowWhenIdle = _model.Preferences.Onboarded && _model.Preferences.ShowBarWhenIdle;
        if (!_model.Preferences.Onboarded || !_model.HasKey(SecretKey.Groq)) Dispatcher.UIThread.Post(() => RunOnboarding(null));
        else if (!background) Dispatcher.UIThread.Post(() => ShowMain(null));
        Logger.Info($"VoiceFlow {_updates.CurrentVersion} started ({RuntimeInformation.ProcessArchitecture})");
    }

    TrayIcon BuildTray()
    {
        var menu = new NativeMenu
        {
            Item("Open VoiceFlow", () => ShowMain(null)),
            _meetingItem,
            Item("Paste last transcript", () => _dictation.PasteLast()),
            Item("Copy last transcript", () => _dictation.CopyLast()),
            new NativeMenuItemSeparator(),
            Item("Settings", () => ShowMain(Page.Settings)),
            Item("Quit VoiceFlow", Quit),
        };
        var tray = new TrayIcon { Icon = LoadIcon(), Menu = menu, IsVisible = true };
        tray.Clicked += (_, _) => Guarded(() => ShowMain(null));
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { tray });
        return tray;
    }

    static NativeMenuItem Item(string title, Action action)
    {
        var item = new NativeMenuItem(title);
        item.Click += (_, _) => Guarded(action);
        return item;
    }

    /// <summary>Menu clicks arrive inside a window message, and an exception escaping one ends the process.</summary>
    static void Guarded(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is not OutOfMemoryException) { Logger.Error("A menu command failed", e); }
    }

    internal static WindowIcon? LoadIcon()
    {
        using var stream = typeof(WindowsApp).Assembly.GetManifestResourceStream("VoiceFlow.ico");
        return stream == null ? null : new WindowIcon(stream);
    }

    // MARK: Hotkey

    void OnGesture(GestureAction action)
    {
        var prefs = _model.Preferences;
        switch (action.Kind)
        {
            case GestureActionKind.Begin:
                _dictation.Begin(action.Session == SessionKind.Command && !prefs.CommandModeEnabled ? SessionKind.Dictation : action.Session);
                break;
            case GestureActionKind.BecomeCommand:
                if (prefs.CommandModeEnabled) _dictation.BecomeCommand();
                break;
            case GestureActionKind.Lock:
                _dictation.Lock();
                break;
            case GestureActionKind.Finish:
                _dictation.Finish();
                break;
            case GestureActionKind.Cancel:
                _dictation.Cancel();
                break;
        }
    }

    void OnShortcut(HotkeyShortcut shortcut)
    {
        if (shortcut == HotkeyShortcut.PasteLast) _dictation.PasteLast();
        else if (shortcut == HotkeyShortcut.Scratchpad) ToggleScratchpad();
    }

    /// <summary>Alt+Shift+S: shows the Scratchpad, or hides it when it is already in front. Same as macOS.</summary>
    void ToggleScratchpad()
    {
        if (_main is { IsActive: true } main && _shell.Current is ScratchpadViewModel) main.Hide();
        else ShowMain(Page.Scratchpad);
    }

    // MARK: State

    void OnPreferencesChanged(Preferences old, Preferences now)
    {
        if (now.Hotkey != old.Hotkey) _hotkeys.SetHotkey(now.Hotkey);
        if (now.LaunchAtLogin != old.LaunchAtLogin) TrySetStartup(now.LaunchAtLogin);
        if (now.ShowBarWhenIdle != old.ShowBarWhenIdle || now.Onboarded != old.Onboarded)
            _bar.ShowWhenIdle = now.Onboarded && now.ShowBarWhenIdle;
        UpdateTray();
    }

    void TrySetStartup(bool enabled)
    {
        try { StartupRegistration.Set(enabled); }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Logger.Error("Start with Windows could not be changed", e);
            _bar.Show("Start with Windows could not be changed", e.Message, seconds: 6);
        }
    }

    void UpdateTray()
    {
        _meetingItem.Header = _notetaker.Current switch
        {
            Notetaker.State.Recording => "Stop meeting notes",
            Notetaker.State.WritingNotes => "Writing meeting notes…",
            _ => "Start meeting notes",
        };
        _meetingItem.IsEnabled = _notetaker.Current != Notetaker.State.WritingNotes;
        _tray.ToolTipText = _notetaker.IsRecording ? "VoiceFlow: taking meeting notes" : $"VoiceFlow: hold {HotkeyInfo.DisplayName(_model.Preferences.Hotkey)} to dictate";
    }

    void ToggleMeeting()
    {
        if (_notetaker.IsRecording) _notetaker.Stop();
        else _notetaker.Start();
        UpdateTray();
    }

    void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero) return;
        var cls = new StringBuilder(64);
        GetClassNameW(hwnd, cls, cls.Capacity);
        // The taskbar and the notification-area overflow take focus when the tray icon is clicked.
        if (cls.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland") return;
        _lastExternalWindow = hwnd;
    }

    // MARK: Windows

    MainWindow Main
    {
        get
        {
            if (_main != null) return _main;
            _main = new MainWindow { DataContext = _shell, HideOnClose = true };
            // The microphone test has no window of its own: closing this one must not leave the mic open.
            _main.Closing += (_, _) => { if (_services.Microphone.IsTesting) _services.Microphone.StopTest(); };
            return _main;
        }
    }

    public void ShowMain(Page? page)
    {
        var window = Main;
        if (page is { } p) _shell.Navigate(p);
        _shell.Refresh();
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>A second launch asked this copy to show itself.</summary>
    public void OnSecondInstance() => ShowMain(null);

    void RunOnboarding(Window? owner)
    {
        if (_onboarding != null)
        {
            _onboarding.Activate();
            return;
        }
        var firstRun = !_model.Preferences.Onboarded;
        var window = new OnboardingWindow();
        window.DataContext = new OnboardingViewModel(_services, () => Dispatcher.UIThread.Post(window.Close));
        window.Closed += (_, _) =>
        {
            _onboarding = null;
            if (_services.Microphone.IsTesting) _services.Microphone.StopTest();
            if (_quit) return;   // quitting closes this window too: nothing new may open
            if (!_model.HasKey(SecretKey.Groq))
            {
                ShowMain(Page.Settings);
                return;
            }
            // Closing the window with a key saved counts as finished, so setup does not come back on the next launch.
            _model.UpdatePreferences(p => p.Onboarded = true);
            if (firstRun) _bar.Show($"Hold {HotkeyInfo.DisplayName(_model.Preferences.Hotkey)} and speak", "Release to type. VoiceFlow stays in the notification area.", seconds: 6);
        };
        _onboarding = window;
        if (owner is { IsVisible: true })
        {
            window.ShowDialog(owner);
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Show();
        }
    }

    /// <summary>Called on any exit path: never leave the PC muted or the microphone on.</summary>
    public void EmergencyCleanup()
    {
        try { _muter.Restore(); } catch (Exception e) { Logger.Error("Cleanup failed", e); }
    }

    void Quit()
    {
        if (_quit) return;
        _quit = true;
        Logger.Info("Quitting");
        // A stuck audio driver can block the steps below. Quitting never waits on one for longer than this.
        _quitWatchdog = new System.Threading.Timer(_ => Environment.Exit(0), null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
        // Each step is on its own: one failing must not leave the mic on or the process running with no tray icon.
        Guarded(((ScratchpadService)_services.Scratchpad).Flush);
        Guarded(_meetingWatcher.Dispose);
        Guarded(_updates.Stop);
        Guarded(_notetaker.Dispose);
        Guarded(_dictation.Dispose);
        Guarded(_hotkeys.Dispose);
        if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
        _housekeeping.Stop();
        Guarded(() =>
        {
            _tray.IsVisible = false;
            _tray.Dispose();
        });
        Guarded(() =>
        {
            if (_main == null) return;
            _main.HideOnClose = false;
            _main.Close();
        });
        Guarded(_barHost.Dispose);
        Guarded(_bar.Dispose);
        Guarded(_clipboardOwner.Dispose);
        _desktop.Shutdown();
    }
}
