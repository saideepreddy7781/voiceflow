using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Win32;
using VoiceFlow.Core;
using VoiceFlow.Core.Audio;
using VoiceFlow.Core.Library;
using VoiceFlow.Core.Meetings;
using VoiceFlow.Core.Updates;
using VoiceFlow.Desktop.Services;
using VoiceFlow.Windows.Audio;
using VoiceFlow.Windows.Dictation;
using VoiceFlow.Windows.Meetings;

namespace VoiceFlow.Windows.Shell;

/// <summary>The real services behind the Avalonia pages. Everything here runs on the UI thread.</summary>
internal sealed class AppServices : IAppServices
{
    public AppServices(AppModel model, DictationController dictation, Notetaker notetaker, FlowBarController bar, TextInserter inserter,
                       SharedMicrophone? microphone = null, Func<TopLevel?>? topLevel = null)
    {
        var shell = new ShellService(model, inserter, topLevel ?? (() => null));
        ShellServices = shell;
        Setup = new SetupService(model);
        History = new HistoryService(model, dictation);
        Meetings = new MeetingsService(model, notetaker, inserter, shell);
        Dictionary = new DictionaryService(model, shell);
        Snippets = new SnippetsService(model, shell);
        Scratchpad = new ScratchpadService(model);
        Preferences = new PreferencesService(model);
        Microphone = new MicrophoneService(microphone ?? MicrophoneHub.Shared);
        Keys = new KeyService(model);
        Dictation = bar;
        Updates = new UpdateService(model, shell);
        Diagnostics = new DiagnosticsService(model, microphone);
    }

    internal ShellService ShellServices { get; }
    public ISetupService Setup { get; }
    public IHistoryService History { get; }
    public IMeetingsService Meetings { get; }
    public IDictionaryService Dictionary { get; }
    public ISnippetsService Snippets { get; }
    public IScratchpadService Scratchpad { get; }
    public IPreferencesService Preferences { get; }
    public IMicrophoneService Microphone { get; }
    public IKeyService Keys { get; }
    public IDictationService Dictation { get; }
    public IShellService Shell => ShellServices;
    public IUpdateService Updates { get; }
    public IDiagnosticsService Diagnostics { get; }
}

internal sealed class SetupService : ISetupService
{
    readonly AppModel _model;

    public SetupService(AppModel model)
    {
        _model = model;
        model.KeysChanged += () => Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    public IReadOnlyList<SetupProblem> Problems
    {
        get
        {
            var problems = new List<SetupProblem>();
            if (!_model.HasKey(SecretKey.Groq)) problems.Add(new SetupProblem("Groq key missing.", "Add your free key to start dictating."));
            if (MicrophoneBlocked()) problems.Add(new SetupProblem("Microphone blocked.", "Allow microphone access in Windows Settings."));
            return problems;
        }
    }

    /// <summary>The Settings → Privacy switch for desktop apps, read from the registry so nothing needs to open the microphone.</summary>
    static bool MicrophoneBlocked()
    {
        const string ConsentKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ConsentKey);
            return key?.GetValue("Value") is string value && value.Equals("Deny", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }
}

internal sealed class HistoryService : IHistoryService
{
    readonly AppModel _model;
    readonly DictationController _dictation;

    public HistoryService(AppModel model, DictationController dictation)
    {
        _model = model;
        _dictation = dictation;
        model.LibraryChanged += () => Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<HistoryEntry> Entries => _model.History;
    public event EventHandler? Changed;
    public void Delete(Guid id) => _model.DeleteHistory([id]);
    public void ToggleFlag(Guid id) => _model.ToggleHistoryFlag(id);
    public void ClearAll() => _model.ClearHistory();
    public Task PasteAsync(string text) => _dictation.PasteTextAsync(text);
}

internal sealed class MeetingsService : IMeetingsService
{
    readonly AppModel _model;
    readonly Notetaker _notetaker;
    readonly TextInserter _inserter;
    readonly ShellService _shell;
    readonly DispatcherTimer _levels = new() { Interval = TimeSpan.FromMilliseconds(50) };

    public MeetingsService(AppModel model, Notetaker notetaker, TextInserter inserter, ShellService shell)
    {
        _model = model;
        _notetaker = notetaker;
        _inserter = inserter;
        _shell = shell;
        _levels.Tick += (_, _) => LevelsChanged?.Invoke(this, EventArgs.Empty);
        notetaker.Changed += () =>
        {
            if (notetaker.IsRecording) _levels.Start();
            else _levels.Stop();
            Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    public IReadOnlyList<Meeting> Meetings => _notetaker.Meetings.Where(m => m.Id != _notetaker.Live?.Id).ToList();
    public Meeting? Live => _notetaker.Live;
    public TimeSpan LiveElapsed => _notetaker.StartedAt is { } s ? DateTimeOffset.Now - s : TimeSpan.Zero;

    public NotetakerState State => _notetaker.Current switch
    {
        Notetaker.State.Recording => NotetakerState.Recording,
        Notetaker.State.WritingNotes => NotetakerState.WritingNotes,
        _ => NotetakerState.Idle,
    };

    public double MicLevel => _notetaker.MicLevel;
    public double SystemLevel => _notetaker.SystemLevel;
    public string? MicLanguage => _notetaker.MicLanguage;
    public string? CallLanguage => _notetaker.CallLanguage;

    public MicHealth MicHealth => _notetaker.MicHealth switch
    {
        { Status: MicHealthStatus.DeadSignal or MicHealthStatus.Failed } => MicHealth.Silent,
        { QuietRoom: true } => MicHealth.Quiet,
        _ => MicHealth.Good,
    };

    public string? NotesFolder => _model.Preferences.MeetingsFolder.Length > 0 ? _model.Preferences.MeetingsFolder : null;
    public event EventHandler? Changed;
    public event EventHandler? LevelsChanged;

    public Task StartAsync()
    {
        _notetaker.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _notetaker.Stop();
        return Task.CompletedTask;
    }

    public void Rename(Guid id, string title) => _notetaker.Rename(id, title);
    public void RenameSpeaker(Guid id, Speaker speaker, string name) => _notetaker.RenameSpeaker(id, speaker, name);
    public void ToggleActionItem(Guid id, Guid itemId) => _notetaker.ToggleActionItem(id, itemId);
    public void Delete(Guid id) => _notetaker.Delete(id);
    public Task RewriteNotesAsync(Guid id) => _notetaker.WriteNotesAsync(id);

    Meeting? Find(Guid id) => _notetaker.Meetings.FirstOrDefault(m => m.Id == id);

    public async Task CopyMarkdownAsync(Guid id)
    {
        if (Find(id) is { } meeting) await _inserter.CopyAsync(MeetingMarkdown.Render(meeting));
    }

    public async Task ExportMarkdownAsync(Guid id)
    {
        if (Find(id) is not { } meeting) return;
        var path = await _shell.PickSaveFileAsync(MeetingMarkdown.FileName(meeting), "md");
        if (path == null) return;
        try { AtomicFile.WriteAllText(path, MeetingMarkdown.Render(meeting)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Logger.Error("Notes could not be exported", e); }
    }
}

internal sealed class DictionaryService : IDictionaryService
{
    readonly AppModel _model;
    readonly ShellService _shell;

    public DictionaryService(AppModel model, ShellService shell)
    {
        _model = model;
        _shell = shell;
        model.LibraryChanged += () => Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<DictionaryEntry> Entries => _model.Dictionary;
    public event EventHandler? Changed;
    public void Add(string term, string? misspelling) => _model.AddDictionaryEntry(term, misspelling);
    public void Update(Guid id, string term, string? misspelling) => _model.UpdateDictionaryEntry(id, term, misspelling);
    public void Delete(Guid id) => _model.RemoveDictionaryEntries([id]);

    public async Task<int> ImportAsync()
    {
        if (await _shell.ReadPickedFileAsync() is not { } text) return 0;
        return LibraryImport.DictionaryWords(text).Count(word => _model.AddDictionaryEntry(word) == null);
    }
}

internal sealed class SnippetsService : ISnippetsService
{
    readonly AppModel _model;
    readonly ShellService _shell;

    public SnippetsService(AppModel model, ShellService shell)
    {
        _model = model;
        _shell = shell;
        model.LibraryChanged += () => Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<Snippet> Snippets => _model.Snippets;
    public event EventHandler? Changed;
    public void Add(string trigger, string expansion) => _model.SaveSnippet(trigger, expansion);
    public void Update(Guid id, string trigger, string expansion) => _model.SaveSnippet(trigger, expansion, id);
    public void Delete(Guid id) => _model.RemoveSnippets([id]);

    public async Task<int> ImportAsync()
    {
        if (await _shell.ReadPickedFileAsync() is not { } text) return 0;
        return LibraryImport.Snippets(text).Count(s => _model.SaveSnippet(s.Trigger, s.Expansion) == null);
    }
}

internal sealed class ScratchpadService : IScratchpadService
{
    readonly AppModel _model;
    /// <summary>Typing saves after a short pause; every keystroke writing the file would be wasteful.</summary>
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public ScratchpadService(AppModel model)
    {
        _model = model;
        _saveTimer.Tick += (_, _) => Flush();
        model.NotesChanged += () => Changed?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<Note> Notes => _model.Notes.OrderByDescending(n => n.Pinned).ThenByDescending(n => n.Updated).ToList();
    public event EventHandler? Changed;
    public Note Create() => _model.CreateNote();
    public void Delete(Guid id) => _model.DeleteNote(id);

    public void Update(Note note)
    {
        note.Updated = DateTimeOffset.Now;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Writes pending edits. Also called when the app quits.</summary>
    public void Flush()
    {
        _saveTimer.Stop();
        _model.SaveNotes();
    }
}

internal sealed class PreferencesService : IPreferencesService
{
    readonly AppModel _model;

    public PreferencesService(AppModel model)
    {
        _model = model;
        model.PreferencesChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public Preferences Current => _model.Preferences;
    public event EventHandler? Changed;
    public void Update(Action<Preferences> change) => _model.UpdatePreferences(change);
}

internal sealed class MicrophoneService : IMicrophoneService
{
    readonly SharedMicrophone _microphone;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    IReadOnlyList<MicrophoneDevice> _devices = [];
    MicSubscription? _test;

    public MicrophoneService(SharedMicrophone microphone)
    {
        _microphone = microphone;
        _timer.Tick += (_, _) => PushLevel();
        RefreshDevices();
    }

    public IReadOnlyList<MicrophoneDevice> Devices => _devices;
    public bool IsTesting => _test != null;
    public double TestLevel { get; private set; }
    public event EventHandler? TestLevelChanged;

    public void RefreshDevices()
    {
        // Listed on the thread pool: audio COM objects stay in the MTA.
        var inputs = WasapiMicrophoneSource.Mta(AudioDevices.Inputs);
        _devices = [new MicrophoneDevice("", "System default"), .. inputs.Select(d => new MicrophoneDevice(d.Id, d.Name))];
    }

    public void StartTest(string deviceId)
    {
        if (_test != null) StopTest();
        try
        {
            _test = _microphone.Subscribe("microphone test", deviceId.Length == 0 ? null : deviceId);
        }
        catch (Exception e) when (e is AudioCaptureException or InvalidOperationException)
        {
            Logger.Warn($"Microphone test could not start: {e.GetType().Name}");
            TestLevel = 0;
            TestLevelChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        _timer.Start();
        TestLevelChanged?.Invoke(this, EventArgs.Empty);
    }

    public void StopTest()
    {
        _timer.Stop();
        var test = _test;
        _test = null;
        test?.Dispose();
        TestLevel = 0;
        TestLevelChanged?.Invoke(this, EventArgs.Empty);
    }

    void PushLevel()
    {
        if (_test is not { } test) return;
        TestLevel = test.CurrentLevel();
        TestLevelChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class KeyService : IKeyService
{
    readonly AppModel _model;

    public KeyService(AppModel model)
    {
        _model = model;
        model.KeysChanged += () => Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool IsStored(SecretKey key) => _model.HasKey(key);
    public event EventHandler? Changed;
    public void Save(SecretKey key, string value) => _model.SaveKey(key, value);
    public void Remove(SecretKey key) => _model.DeleteKey(key);

    public Task<KeyCheckResult> TestAsync(SecretKey key, string? draft) =>
        draft != null ? new KeyTester().CheckAsync(key, draft) : _model.CheckKeyAsync(key);
}

internal sealed class DiagnosticsService(AppModel model, SharedMicrophone? microphone) : IDiagnosticsService
{
    public string BuildReport() => AppDiagnostics.BuildDebugReport(model, microphone);
}

/// <summary>Links, the clipboard and file pickers. Pickers open over the main window.</summary>
internal sealed class ShellService(AppModel model, TextInserter inserter, Func<TopLevel?> topLevel) : IShellService
{
    const long MaxImportBytes = 2_000_000;

    public void OpenUrl(string url) => DictationController.OpenUri(url);

    public void OpenDataFolder()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{model.DataFolder}\"") { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Logger.Warn($"Could not open the data folder: {e.GetType().Name}"); }
    }

    public async Task CopyToClipboardAsync(string text) => await inserter.CopyAsync(text);

    public async Task<string?> PickFolderAsync()
    {
        if (topLevel()?.StorageProvider is not { } storage) return null;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFileAsync(string suggestedName, string extension)
    {
        if (topLevel()?.StorageProvider is not { } storage) return null;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    /// <summary>Asks for a text file and reads it. Null when cancelled, unreadable or too large.</summary>
    public async Task<string?> ReadPickedFileAsync()
    {
        if (topLevel()?.StorageProvider is not { } storage) return null;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Text files") { Patterns = ["*.txt", "*.csv"] }, FilePickerFileTypes.All],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return null;
        try
        {
            if (new FileInfo(path).Length > MaxImportBytes) return null;
            return await File.ReadAllTextAsync(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Could not read the import file: {e.GetType().Name}");
            return null;
        }
    }
}

/// <summary>Checks GitHub for a newer release at launch and once a day. It only reports; it never downloads.</summary>
internal sealed class UpdateService : IUpdateService
{
    readonly AppModel _model;
    readonly ShellService _shell;
    readonly UpdateChecker _checker = new();
    readonly DispatcherTimer _daily = new() { Interval = TimeSpan.FromHours(24) };
    readonly DispatcherTimer _firstRun = new() { Interval = TimeSpan.FromSeconds(8) };
    string _releaseUrl = UpdateChecker.ReleasesUrl;

    public UpdateService(AppModel model, ShellService shell)
    {
        _model = model;
        _shell = shell;
        var version = typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        CurrentVersion = AppVersion.Display(version);
        _daily.Tick += (_, _) => CheckIfEnabled();
        _firstRun.Tick += (_, _) => { _firstRun.Stop(); CheckIfEnabled(); };
    }

    public string CurrentVersion { get; }
    public UpdateState State { get; private set; } = UpdateState.Idle;
    public string? AvailableVersion { get; private set; }
    public event EventHandler? Changed;

    /// <summary>Starts the schedule: shortly after launch, then every 24 hours.</summary>
    public void Start()
    {
        _firstRun.Start();
        _daily.Start();
    }

    public void Stop()
    {
        _firstRun.Stop();
        _daily.Stop();
    }

    void CheckIfEnabled()
    {
        if (_model.Preferences.CheckForUpdates) _ = CheckAsync();
    }

    public async Task CheckAsync()
    {
        if (State == UpdateState.Checking) return;
        State = UpdateState.Checking;
        Changed?.Invoke(this, EventArgs.Empty);
        var result = await _checker.CheckAsync(CurrentVersion);
        switch (result.Status)
        {
            case UpdateCheckStatus.Available:
                State = UpdateState.Available;
                AvailableVersion = result.Release!.Version;
                _releaseUrl = result.Release.Url;
                break;
            case UpdateCheckStatus.UpToDate:
                State = UpdateState.UpToDate;
                AvailableVersion = null;
                break;
            default:
                State = UpdateState.Failed;
                break;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void OpenRelease() => _shell.OpenUrl(_releaseUrl);
}
