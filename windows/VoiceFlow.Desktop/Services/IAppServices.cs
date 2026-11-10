using VoiceFlow.Core;
using VoiceFlow.Core.Library;
using VoiceFlow.Core.Meetings;

namespace VoiceFlow.Desktop.Services;

// Everything the real app provides lives behind these interfaces. VoiceFlow.Windows implements them.
//
// Events and level pushes may arrive on any thread; implementations must raise them on the UI thread
// (Avalonia.Threading.Dispatcher.UIThread.Post).

/// <summary>The app's services, grouped so a view model takes only what it needs.</summary>
public interface IAppServices
{
    ISetupService Setup { get; }
    IHistoryService History { get; }
    IMeetingsService Meetings { get; }
    IDictionaryService Dictionary { get; }
    ISnippetsService Snippets { get; }
    IScratchpadService Scratchpad { get; }
    IPreferencesService Preferences { get; }
    IMicrophoneService Microphone { get; }
    IKeyService Keys { get; }
    IDictationService Dictation { get; }
    IShellService Shell { get; }
    IUpdateService Updates { get; }
    IDiagnosticsService Diagnostics { get; }
}

public sealed record SetupProblem(string Title, string Detail);

/// <summary>What is still missing before dictation can work (no key, mic blocked and so on).</summary>
public interface ISetupService
{
    IReadOnlyList<SetupProblem> Problems { get; }
    event EventHandler? Changed;
}

public interface IHistoryService
{
    /// <summary>Newest first.</summary>
    IReadOnlyList<HistoryEntry> Entries { get; }
    event EventHandler? Changed;
    void Delete(Guid id);
    void ToggleFlag(Guid id);
    /// <summary>Removes every entry. The caller asks first.</summary>
    void ClearAll();
    /// <summary>Types the text into the app that was last focused.</summary>
    Task PasteAsync(string text);
}

public enum NotetakerState { Idle, Recording, WritingNotes }

public enum MicHealth
{
    /// <summary>Signal present and at a usable level.</summary>
    Good,
    /// <summary>No signal for several seconds.</summary>
    Silent,
    /// <summary>Signal is present but very low.</summary>
    Quiet,
}

public interface IMeetingsService
{
    /// <summary>Newest first; excludes the meeting being recorded.</summary>
    IReadOnlyList<Meeting> Meetings { get; }
    Meeting? Live { get; }
    /// <summary>Time recorded so far in the live meeting.</summary>
    TimeSpan LiveElapsed { get; }
    NotetakerState State { get; }
    /// <summary>Live microphone level, 0 to 1.</summary>
    double MicLevel { get; }
    /// <summary>Live meeting-audio level, 0 to 1.</summary>
    double SystemLevel { get; }
    /// <summary>The language last heard on your microphone, as a Whisper code.</summary>
    string? MicLanguage { get; }
    /// <summary>The language last heard on the call, as a Whisper code.</summary>
    string? CallLanguage { get; }
    MicHealth MicHealth { get; }
    string? NotesFolder { get; }
    /// <summary>Raised when the list, the live meeting or the state changes.</summary>
    event EventHandler? Changed;
    /// <summary>Raised about 20 times a second while recording.</summary>
    event EventHandler? LevelsChanged;
    Task StartAsync();
    Task StopAsync();
    void Rename(Guid id, string title);
    void RenameSpeaker(Guid id, Speaker speaker, string name);
    void ToggleActionItem(Guid id, Guid itemId);
    void Delete(Guid id);
    Task RewriteNotesAsync(Guid id);
    /// <summary>Copies the Markdown to the clipboard.</summary>
    Task CopyMarkdownAsync(Guid id);
    /// <summary>Asks where to save, then writes the Markdown file.</summary>
    Task ExportMarkdownAsync(Guid id);
}

public interface IDictionaryService
{
    IReadOnlyList<DictionaryEntry> Entries { get; }
    event EventHandler? Changed;
    void Add(string term, string? misspelling);
    void Update(Guid id, string term, string? misspelling);
    void Delete(Guid id);
    /// <summary>Imports from a file the user picks; returns how many entries were added.</summary>
    Task<int> ImportAsync();
}

public interface ISnippetsService
{
    IReadOnlyList<Snippet> Snippets { get; }
    event EventHandler? Changed;
    void Add(string trigger, string expansion);
    void Update(Guid id, string trigger, string expansion);
    void Delete(Guid id);
    Task<int> ImportAsync();
}

public interface IScratchpadService
{
    /// <summary>Pinned first, then most recently updated.</summary>
    IReadOnlyList<Note> Notes { get; }
    event EventHandler? Changed;
    Note Create();
    void Update(Note note);
    void Delete(Guid id);
}

public interface IPreferencesService
{
    Preferences Current { get; }
    event EventHandler? Changed;
    void Update(Action<Preferences> change);
}

public sealed record MicrophoneDevice(string Id, string Name);

public interface IMicrophoneService
{
    IReadOnlyList<MicrophoneDevice> Devices { get; }
    bool IsTesting { get; }
    /// <summary>Latest test level, 0 to 1.</summary>
    double TestLevel { get; }
    event EventHandler? TestLevelChanged;
    /// <summary>Lists the microphones again (a headset may have been plugged in).</summary>
    void RefreshDevices();
    void StartTest(string deviceId);
    void StopTest();
}

public enum KeyTestState { Unknown, Checking, Ok, Invalid, Failed }

public interface IKeyService
{
    bool IsStored(SecretKey key);
    event EventHandler? Changed;
    void Save(SecretKey key, string value);
    void Remove(SecretKey key);
    /// <summary>Tests the stored key, or the given draft when not null.</summary>
    Task<KeyCheckResult> TestAsync(SecretKey key, string? draft);
}

public enum FlowPhase { Idle, Listening, HandsFree, Command, Processing }

/// <summary>Toast shown above the Flow bar. Action is null for a plain message.</summary>
public sealed record FlowToast(string Message, string Detail = "", string? ActionTitle = null, Action? Action = null);

/// <summary>Drives the Flow bar and receives its buttons.</summary>
public interface IDictationService
{
    FlowPhase Phase { get; }
    /// <summary>Recent input levels, loudest in the middle. Empty while idle.</summary>
    IReadOnlyList<float> Levels { get; }
    FlowToast? Toast { get; }
    /// <summary>Set while Notetaker records: the bar shows the recording chip.</summary>
    DateTimeOffset? MeetingSince { get; }
    /// <summary>The setting that keeps a resting bar on screen. Off unless the user turns it on.</summary>
    bool ShowWhenIdle { get; }
    event EventHandler? Changed;
    /// <summary>Raised on every level update; do not route through Changed.</summary>
    event EventHandler? LevelsChanged;
    void StartHandsFree();
    void Cancel();
    void Confirm();
    void StopMeeting();
    /// <summary>Opens the Meetings page (a click on the recording chip).</summary>
    void OpenMeetings();
    void DismissToast();
}

public interface IShellService
{
    void OpenUrl(string url);
    void OpenDataFolder();
    Task CopyToClipboardAsync(string text);
    /// <summary>Returns the folder the user chose, or null.</summary>
    Task<string?> PickFolderAsync();
    /// <summary>Returns the path the user chose, or null.</summary>
    Task<string?> PickSaveFileAsync(string suggestedName, string extension);
}

public enum UpdateState { Idle, Checking, UpToDate, Available, Failed }

public interface IUpdateService
{
    string CurrentVersion { get; }
    UpdateState State { get; }
    string? AvailableVersion { get; }
    event EventHandler? Changed;
    Task CheckAsync();
    /// <summary>Opens the release page. VoiceFlow never downloads an update by itself.</summary>
    void OpenRelease();
}

/// <summary>Builds the debug report. It must never contain anything the user said or typed.</summary>
public interface IDiagnosticsService
{
    string BuildReport();
}

/// <summary>Writes the debug report to the clipboard; the only call the Settings page needs.</summary>
public static class DiagnosticsExtensions
{
    public static Task CopyReportAsync(this IDiagnosticsService diagnostics, IShellService shell) =>
        shell.CopyToClipboardAsync(diagnostics.BuildReport());
}
