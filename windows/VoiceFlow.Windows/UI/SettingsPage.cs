using System.Diagnostics;
using VoiceFlow.Core;
using VoiceFlow.Core.Meetings;
using VoiceFlow.Windows.Audio;
using VoiceFlow.Windows.Dictation;

namespace VoiceFlow.Windows.UI;

/// <summary>Settings: keys, hotkey, microphone, languages, cleanup, history, startup, Notetaker.</summary>
internal sealed class SettingsPage : PageBase
{
    readonly AppModel _model;
    readonly FlowLayoutPanel _column;
    readonly Dictionary<SecretKey, (TextBox Box, Label Status)> _keyRows = new();
    readonly ComboBox _hotkey = Ui.Choice(220), _mic = Ui.Choice(360), _telugu = Ui.Choice(260), _cleanup = Ui.Choice(160),
        _retention = Ui.Choice(160), _notesLanguage = Ui.Choice(200);
    readonly Label _hotkeyHelp = Ui.Hint("", 700), _teluguExample = Ui.Hint("", 700), _cleanupHelp = Ui.Hint("", 700), _folder = Ui.Hint("", 600);
    readonly CheckedListBox _languages = new() { CheckOnClick = true, Height = 150, Width = 260, Font = Theme.Body, BorderStyle = BorderStyle.FixedSingle, MultiColumn = false };
    readonly Ui.LevelMeter _micLevel = new();
    readonly System.Windows.Forms.Timer _micTimer = new() { Interval = 50 };
    readonly Button _micTest;
    readonly Dictionary<string, CheckBox> _checks = new();
    AudioCapture? _testCapture;
    DateTime _testUntil;
    bool _loading;
    List<AudioDevice> _devices = [];

    static readonly (int Days, string Label)[] RetentionOptions =
        [(0, "Don't keep history"), (1, "1 day"), (7, "7 days"), (30, "30 days"), (90, "90 days"), (365, "1 year"), (-1, "Forever")];

    public SettingsPage(AppModel model)
    {
        _model = model;
        _column = Ui.Column();
        _column.Padding = new Padding(0, 0, 0, 40);

        // API keys
        Add(Ui.Section("Groq API key"));
        Add(Ui.Hint("VoiceFlow sends your audio to Groq for transcription and cleanup, using your own key. It is stored in Windows Credential Manager, never in a file.", 700));
        Add(KeyRow(SecretKey.Groq));
        var link = new LinkLabel { Text = "Get a free Groq key at console.groq.com/keys", AutoSize = true, Font = Theme.Small, LinkColor = Theme.Black, ActiveLinkColor = Theme.Muted };
        link.LinkClicked += (_, _) => DictationController.OpenUri(Groq.KeysUrl);
        Add(link);
        Add(Ui.Section("Gemini API key (optional)"));
        Add(Ui.Hint("Stored and checked only; dictation uses Groq.", 700));
        Add(KeyRow(SecretKey.Gemini));

        // Hotkey
        Add(Ui.Section("Hotkey"));
        foreach (var h in Enum.GetValues<Hotkey>()) _hotkey.Items.Add(HotkeyInfo.DisplayName(h));
        _hotkey.SelectedIndexChanged += (_, _) => Change(p => p.Hotkey = (Hotkey)_hotkey.SelectedIndex);
        Add(_hotkey);
        Add(_hotkeyHelp);

        // Microphone
        Add(Ui.Section("Microphone"));
        _mic.SelectedIndexChanged += (_, _) =>
        {
            var id = _mic.SelectedIndex <= 0 ? "" : _devices[_mic.SelectedIndex - 1].Id;
            Change(p => p.InputDeviceId = id);
        };
        _micTest = Ui.Secondary("Test", (_, _) => ToggleMicTest());
        Add(Ui.Row(_mic, _micTest, _micLevel));
        Add(Check("whisper", "Whisper mode: boost quiet speech", p => p.WhisperMode, (p, v) => p.WhisperMode = v));
        Add(Check("mute", "Mute other audio while dictating", p => p.MuteAudioWhileDictating, (p, v) => p.MuteAudioWhileDictating = v));
        Add(Check("sounds", "Play start and error sounds", p => p.PlaySounds, (p, v) => p.PlaySounds = v));
        _micTimer.Tick += (_, _) => TickMicTest();

        // Languages
        Add(Ui.Section("My languages"));
        Add(Ui.Hint("Pick every language you speak. With one, Whisper is told the language; with several, VoiceFlow compares auto-detect with your first non-English language and keeps the better result.", 700));
        foreach (var (code, name) in Languages.Options) _languages.Items.Add($"{name} ({code})");
        // ItemCheck fires before the check state changes, so the new state is read on the next message.
        // Reload checks items before the page (or the main window) has a handle, and BeginInvoke would
        // throw then; Reload's own changes are not user edits anyway.
        _languages.ItemCheck += (_, e) =>
        {
            if (_loading || !IsHandleCreated) return;
            BeginInvoke(() =>
            {
                if (_loading) return;
                var chosen = Languages.Options.Where((_, i) => _languages.GetItemChecked(i)).Select(o => o.Code).ToList();
                Change(p => p.Languages = chosen);
            });
        };
        Add(_languages);
        Add(Ui.Text("Telugu is written as"));
        foreach (var t in Enum.GetValues<TeluguScript>()) _telugu.Items.Add(EnumText.DisplayName(t));
        _telugu.SelectedIndexChanged += (_, _) => Change(p => p.TeluguScript = (TeluguScript)_telugu.SelectedIndex);
        Add(_telugu);
        Add(_teluguExample);

        // Cleanup
        Add(Ui.Section("AI cleanup"));
        foreach (var l in Enum.GetValues<CleanupLevel>()) _cleanup.Items.Add(EnumText.DisplayName(l));
        _cleanup.SelectedIndexChanged += (_, _) => Change(p => p.CleanupLevel = (CleanupLevel)_cleanup.SelectedIndex);
        Add(_cleanup);
        Add(_cleanupHelp);
        Add(Check("command", "Command Mode (edit selected text by voice)", p => p.CommandModeEnabled, (p, v) => p.CommandModeEnabled = v));

        // History and system
        Add(Ui.Section("History"));
        foreach (var (_, label) in RetentionOptions) _retention.Items.Add(label);
        _retention.SelectedIndexChanged += (_, _) => Change(p => p.HistoryRetentionDays = RetentionOptions[_retention.SelectedIndex].Days);
        Add(_retention);
        Add(Ui.Hint("Dictations older than this are deleted from this PC.", 700));

        Add(Ui.Section("Windows"));
        Add(Check("startup", "Start with Windows", p => p.LaunchAtLogin, (p, v) => p.LaunchAtLogin = v));
        Add(Check("bar", "Show the Flow bar when idle (click it to dictate hands-free)", p => p.ShowBarWhenIdle, (p, v) => p.ShowBarWhenIdle = v));
        Add(Check("debug", "Debug logging (may include dictated text; off by default)", p => p.DebugLogging, (p, v) => p.DebugLogging = v));

        // Notetaker
        Add(Ui.Section("Meeting notes"));
        Add(Check("system", "Record the other people (what your PC plays)", p => p.MeetingsSystemAudio, (p, v) => p.MeetingsSystemAudio = v));
        Add(Check("consent", "Remind me to tell people I'm taking notes", p => p.MeetingsConsentReminder, (p, v) => p.MeetingsConsentReminder = v));
        foreach (var l in Enum.GetValues<NotesLanguage>()) _notesLanguage.Items.Add(l.DisplayName());
        _notesLanguage.SelectedIndexChanged += (_, _) => Change(p => p.MeetingsNotesLanguage = (NotesLanguage)_notesLanguage.SelectedIndex);
        Add(Ui.Row(new Label { Text = "Notes language", AutoSize = true, Font = Theme.Body, Margin = new Padding(0, 8, 8, 0) }, _notesLanguage));
        Add(Ui.Row(Ui.Secondary("Choose notes folder…", (_, _) => ChooseFolder()),
                   Ui.Secondary("Don't save to a folder", (_, _) => Change(p => p.MeetingsFolder = ""))));
        Add(_folder);

        // Data
        Add(Ui.Section("Your data"));
        Add(Ui.Row(Ui.Secondary("Open data folder", (_, _) => OpenFolder(_model.DataFolder)),
                   Ui.Secondary("Open log folder", (_, _) => OpenFolder(AppPaths.LogFolder))));
        Add(Ui.Hint($"VoiceFlow {Application.ProductVersion.Split('+')[0]} · MIT License · Copyright (c) 2026 Saideep Reddy", 700));

        var scroller = new Panel { AutoScroll = true };
        scroller.Controls.Add(_column);
        Controls.Add(Ui.Page("Settings", null, scroller));
        _model.KeysChanged += () => { if (IsHandleCreated) BeginInvoke(ReloadKeys); };
    }

    void Add(Control c) => _column.Controls.Add(c);

    void Change(Action<Preferences> change)
    {
        if (!_loading) _model.UpdatePreferences(change);
    }

    CheckBox Check(string id, string text, Func<Preferences, bool> get, Action<Preferences, bool> set)
    {
        var box = Ui.Check(text, get(_model.Preferences), v => Change(p => set(p, v)));
        box.Tag = get;
        _checks[id] = box;
        return box;
    }

    Control KeyRow(SecretKey key)
    {
        var box = Ui.Input(320, key == SecretKey.Groq ? "gsk_…" : "Paste key");
        box.UseSystemPasswordChar = true;
        var status = Ui.Hint("", 700);
        _keyRows[key] = (box, status);
        var save = Ui.Primary("Save", (_, _) =>
        {
            var error = _model.SaveKey(key, box.Text);
            box.Clear();
            status.Text = error ?? "Saved. Checking…";
            if (error == null) _ = TestAsync(key);
        });
        var test = Ui.Secondary("Test", (_, _) => _ = TestAsync(key));
        var remove = Ui.Secondary("Remove", (_, _) =>
        {
            if (MessageBox.Show(this, $"Remove the {key} key from this PC?", "VoiceFlow", MessageBoxButtons.OKCancel) == DialogResult.OK)
                _model.DeleteKey(key);
        });
        var column = Ui.Column();
        column.Controls.Add(Ui.Row(box, save, test, remove));
        column.Controls.Add(status);
        return column;
    }

    async Task TestAsync(SecretKey key)
    {
        _keyRows[key].Status.Text = "Checking…";
        var result = await _model.CheckKeyAsync(key);
        _keyRows[key].Status.Text = result.Summary;
    }

    void ReloadKeys()
    {
        foreach (var (key, (box, status)) in _keyRows)
        {
            var stored = _model.HasKey(key);
            box.PlaceholderText = stored ? "Saved (paste a new key to replace it)" : key == SecretKey.Groq ? "gsk_…" : "Paste key";
            status.Text = _model.KeyChecks.TryGetValue(key, out var check) ? check.Summary : stored ? "Saved in Windows Credential Manager." : "No key saved.";
        }
    }

    public override void Reload()
    {
        _loading = true;
        var p = _model.Preferences;
        _hotkey.SelectedIndex = (int)p.Hotkey;
        _hotkeyHelp.Text = $"Hold {HotkeyInfo.DisplayName(p.Hotkey)} to dictate and release to type. Tap it twice, or press {HotkeyInfo.HandsFree(p.Hotkey)}, " +
                           $"for hands-free (press the hotkey again to finish, Esc to cancel). Add {HotkeyInfo.CommandModifier(p.Hotkey)} for Command Mode: " +
                           "select text, then say what to do with it. Alt+Shift+V pastes the last result.";
        _devices = AudioDevices.Inputs();
        _mic.Items.Clear();
        _mic.Items.Add("Default microphone");
        foreach (var d in _devices) _mic.Items.Add(d.Name);
        var index = _devices.FindIndex(d => d.Id == p.InputDeviceId);
        _mic.SelectedIndex = index < 0 ? 0 : index + 1;
        for (int i = 0; i < Languages.Options.Length; i++) _languages.SetItemChecked(i, p.Languages.Contains(Languages.Options[i].Code));
        _telugu.SelectedIndex = (int)p.TeluguScript;
        _teluguExample.Text = p.SpeaksTelugu ? EnumText.Example(p.TeluguScript) : "Add Telugu to your languages to use this. " + EnumText.Example(p.TeluguScript);
        _cleanup.SelectedIndex = (int)p.CleanupLevel;
        _cleanupHelp.Text = EnumText.Summary(p.CleanupLevel);
        var retention = Array.FindIndex(RetentionOptions, o => o.Days == p.HistoryRetentionDays);
        _retention.SelectedIndex = retention < 0 ? 3 : retention;
        _notesLanguage.SelectedIndex = (int)p.MeetingsNotesLanguage;
        _folder.Text = p.MeetingsFolder.Length == 0 ? "Notes are kept in VoiceFlow only." : $"Notes are saved as Markdown in {p.MeetingsFolder}";
        foreach (var box in _checks.Values) box.Checked = ((Func<Preferences, bool>)box.Tag!)(p);
        ReloadKeys();
        _loading = false;
    }

    void ChooseFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Meeting notes will be saved here as Markdown.", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) Change(p => p.MeetingsFolder = dialog.SelectedPath);
    }

    static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException) { Logger.Warn(e.Message); }
    }

    void ToggleMicTest()
    {
        if (_testCapture != null) { StopMicTest(); return; }
        var id = _model.Preferences.InputDeviceId;
        var capture = new AudioCapture(id.Length == 0 ? null : id);
        try
        {
            capture.Start();
        }
        catch (AudioCaptureException e)
        {
            capture.Dispose();
            MessageBox.Show(this, e.Message, "Microphone", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _testCapture = capture;
        _testUntil = DateTime.UtcNow.AddSeconds(8);
        _micTest.Text = "Stop";
        _micTimer.Start();
    }

    void TickMicTest()
    {
        if (_testCapture == null) return;
        _micLevel.Level = _testCapture.CurrentLevel();
        _testCapture.Drain();
        if (DateTime.UtcNow > _testUntil || !_testCapture.IsRecording) StopMicTest();
    }

    void StopMicTest()
    {
        _micTimer.Stop();
        _testCapture?.Dispose();
        _testCapture = null;
        _micLevel.Level = 0;
        _micTest.Text = "Test";
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible) StopMicTest();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopMicTest();
            _micTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
