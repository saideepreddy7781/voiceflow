using System.Text;
using VoiceFlow.Core;
using VoiceFlow.Core.Input;
using VoiceFlow.Windows.Audio;
using VoiceFlow.Windows.Dictation;
using VoiceFlow.Windows.Input;
using VoiceFlow.Windows.Meetings;
using VoiceFlow.Windows.Native;
using VoiceFlow.Windows.UI;
using static VoiceFlow.Windows.Native.NativeMethods;

namespace VoiceFlow.Windows;

/// <summary>A hidden window on the UI thread: clipboard owner and target for posting work from the key hook.</summary>
internal sealed class MessageWindow : Control
{
    public MessageWindow()
    {
        Visible = false;
        CreateHandle();
    }
}

/// <summary>
/// The app: a notification-area icon that runs all day. Owns the hotkey, the Flow bar, dictation, Notetaker and
/// the main window. Everything here runs on the UI thread.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    readonly MessageWindow _poster = new();
    readonly AppModel _model;
    readonly FlowBarController _bar = new();
    readonly TextInserter _inserter;
    readonly OutputMuter _muter;
    readonly DictationController _dictation;
    readonly Notetaker _notetaker;
    readonly HotkeyMonitor _hotkeys;
    readonly NotifyIcon _tray;
    readonly ToolStripMenuItem _meetingItem;
    readonly System.Windows.Forms.Timer _housekeeping = new() { Interval = 60 * 60 * 1000 };
    readonly WinEventDelegate _foregroundProc;   // kept alive for the WinEvent hook
    readonly IntPtr _foregroundHook;
    readonly Icon? _icon;
    MainWindow? _main;
    IntPtr _lastExternalWindow;

    public static TrayApp? Current { get; private set; }

    public TrayApp(bool background)
    {
        Current = this;
        _model = new AppModel(AppPaths.DataFolder);
        Logger.DebugEnabled = _model.Preferences.DebugLogging;
        _icon = LoadIcon();
        _muter = new OutputMuter(_model.DataFolder);
        _muter.RecoverAfterCrash();
        _inserter = new TextInserter(new ClipboardService(_poster.Handle));
        _dictation = new DictationController(_model, _bar, _inserter, _muter);
        _notetaker = new Notetaker(_model, _bar);
        _hotkeys = new HotkeyMonitor(_poster, _model.Preferences.Hotkey, OnGesture, OnShortcut);

        _dictation.ResetHotkeySession = notify => _hotkeys.ResetSession(notify);
        _dictation.EnterHandsFreeGesture = _hotkeys.EnterHandsFree;
        _dictation.MeetingRecording = () => _notetaker.IsRecording;
        _dictation.OpenSettings = () => ShowMain(PageId.Settings);
        _dictation.LastExternalWindow = () => _lastExternalWindow;
        _notetaker.OpenMeetingsPage = () => ShowMain(PageId.Meetings);

        _bar.StartHandsFree += _dictation.StartHandsFreeFromBar;
        _bar.Confirm += () => { _dictation.Finish(); _hotkeys.ResetSession(notify: false); };
        _bar.Cancel += () => { _dictation.Cancel(); _hotkeys.ResetSession(notify: false); };
        _bar.OpenMeetings += () => ShowMain(PageId.Meetings);

        _model.PreferencesChanged += OnPreferencesChanged;
        _notetaker.Changed += UpdateTray;

        // The registry is the truth for "Start with Windows" (the user can turn it off in Task Manager).
        // When it is on, rewrite it so it points at this copy of VoiceFlow.exe.
        var startup = StartupRegistration.IsEnabled;
        if (startup) TrySetStartup(true);
        if (startup != _model.Preferences.LaunchAtLogin) _model.UpdatePreferences(p => p.LaunchAtLogin = startup);

        _meetingItem = new ToolStripMenuItem("Start meeting notes", null, (_, _) => ToggleMeeting());
        var menu = new ContextMenuStrip { Font = Theme.Body, ShowImageMargin = false };
        menu.Items.Add(new ToolStripMenuItem("Open VoiceFlow", null, (_, _) => ShowMain(null)) { Font = Theme.Strong });
        menu.Items.Add(_meetingItem);
        menu.Items.Add(new ToolStripMenuItem("Paste last transcript", null, (_, _) => _dictation.PasteLast()));
        menu.Items.Add(new ToolStripMenuItem("Copy last transcript", null, (_, _) => _dictation.CopyLast()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Settings", null, (_, _) => ShowMain(PageId.Settings)));
        menu.Items.Add(new ToolStripMenuItem("Quit VoiceFlow", null, (_, _) => Quit()));
        menu.Opening += (_, _) => UpdateTray();
        _tray = new NotifyIcon { Icon = _icon ?? SystemIcons.Application, ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowMain(null); };
        UpdateTray();

        // Remember the last window the user worked in, so "Paste last" from this menu goes back there.
        _foregroundProc = OnForegroundChanged;
        _foregroundHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _foregroundProc, 0, 0,
                                          WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        _housekeeping.Tick += (_, _) => _model.ApplyRetention();
        _housekeeping.Start();

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
        if (!_model.Preferences.Onboarded || !_model.HasKey(SecretKey.Groq)) _poster.BeginInvoke(RunOnboarding);
        else if (!background) _poster.BeginInvoke(() => ShowMain(null));
        Logger.Info($"VoiceFlow {Application.ProductVersion} started ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");
    }

    static Icon? LoadIcon()
    {
        try
        {
            using var stream = typeof(TrayApp).Assembly.GetManifestResourceStream("VoiceFlow.ico");
            return stream == null ? null : new Icon(stream, SystemInformation.SmallIconSize);
        }
        catch (ArgumentException) { return null; }
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
    }

    // MARK: State

    void OnPreferencesChanged(Preferences old, Preferences now)
    {
        if (now.Hotkey != old.Hotkey) _hotkeys.SetHotkey(now.Hotkey);
        if (now.LaunchAtLogin != old.LaunchAtLogin) TrySetStartup(now.LaunchAtLogin);
        if (now.ShowBarWhenIdle != old.ShowBarWhenIdle || now.Onboarded != old.Onboarded)
            _bar.ShowWhenIdle = now.Onboarded && now.ShowBarWhenIdle;
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
        _meetingItem.Text = _notetaker.Current switch
        {
            Notetaker.State.Recording => "Stop meeting notes",
            Notetaker.State.WritingNotes => "Writing meeting notes…",
            _ => "Start meeting notes",
        };
        _meetingItem.Enabled = _notetaker.Current != Notetaker.State.WritingNotes;
        var text = _notetaker.IsRecording ? "VoiceFlow: taking meeting notes" : $"VoiceFlow: hold {HotkeyInfo.DisplayName(_model.Preferences.Hotkey)} to dictate";
        _tray.Text = text.Length > 63 ? text[..63] : text;
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

    MainWindow Main => _main ??= new MainWindow(_model, _notetaker, _inserter, _icon);

    public void ShowMain(PageId? page) => Main.Present(page);

    /// <summary>A second launch asked this copy to show itself.</summary>
    public void OnSecondInstance() => _poster.BeginInvoke(() => ShowMain(null));

    void RunOnboarding()
    {
        using var form = new OnboardingForm(_model, _icon);
        form.ShowDialog();
        if (_model.HasKey(SecretKey.Groq))
        {
            _model.UpdatePreferences(p => p.Onboarded = true);
            _bar.Show($"Hold {HotkeyInfo.DisplayName(_model.Preferences.Hotkey)} and speak", "Release to type. VoiceFlow stays in the notification area.", seconds: 6);
        }
        else ShowMain(PageId.Settings);
    }

    /// <summary>Called on any exit path: never leave the PC muted or the microphone on.</summary>
    public void EmergencyCleanup()
    {
        try { _muter.Restore(); } catch (Exception e) { Logger.Error("Cleanup failed", e); }
    }

    void Quit()
    {
        Logger.Info("Quitting");
        _notetaker.Dispose();
        _dictation.Dispose();
        _hotkeys.Dispose();
        if (_foregroundHook != IntPtr.Zero) UnhookWinEvent(_foregroundHook);
        _housekeeping.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        if (_main != null)
        {
            _main.HideOnClose = false;
            _main.Close();
        }
        _bar.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _poster.Dispose();
        }
        base.Dispose(disposing);
    }
}
