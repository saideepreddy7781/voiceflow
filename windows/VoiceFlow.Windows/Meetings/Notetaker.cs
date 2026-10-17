using VoiceFlow.Core;
using VoiceFlow.Core.AI;
using VoiceFlow.Core.Meetings;
using VoiceFlow.Windows.Audio;
using VoiceFlow.Windows.UI;

namespace VoiceFlow.Windows.Meetings;

/// <summary>
/// Meeting notes: records your microphone and what the PC plays (WASAPI loopback), transcribes it while it
/// happens, writes notes when it ends, and saves them as Markdown to your notes folder if you chose one.
/// </summary>
internal sealed class Notetaker : IDisposable
{
    public enum State { Idle, Recording, WritingNotes }

    readonly AppModel _model;
    readonly FlowBarController _bar;
    readonly MeetingRecorder _recorder;
    readonly JsonFile<List<Meeting>> _store;
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    bool _warnedSilentSystem;

    public List<Meeting> Meetings { get; private set; }
    public State Current { get; private set; } = State.Idle;
    /// <summary>The meeting being recorded, with its live transcript.</summary>
    public Meeting? Live { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public string? LastError { get; private set; }
    public Guid? LastStoppedId { get; private set; }

    public bool IsRecording => Current == State.Recording;
    public float MicLevel => _recorder.MicLevel;
    public float SystemLevel => _recorder.SystemLevel;

    public event Action? Changed;
    public Action OpenMeetingsPage { get; set; } = () => { };
    public Func<bool> DictationBusy { get; set; } = () => false;

    public Notetaker(AppModel model, FlowBarController bar)
    {
        _model = model;
        _bar = bar;
        _store = new JsonFile<List<Meeting>>("meetings.json", model.DataFolder);
        Meetings = (_store.Load() ?? []).Select(m =>
        {
            if (m.Status is not (MeetingStatus.Recording or MeetingStatus.WritingNotes)) return m;
            m.Status = MeetingStatus.Failed;
            m.End ??= m.Lines.Count > 0 ? m.Start.AddSeconds(m.Lines[^1].Time + 5) : m.Start;
            m.Error = "VoiceFlow quit before the notes were finished. Press Try again.";
            return m;
        }).ToList();
        _recorder = new MeetingRecorder(
            s => new CaptureMeetingSource(s.MicDeviceId, loopback: false),
            () => new CaptureMeetingSource(null, loopback: true),
            logError: message => Logger.Error(message));
        // Updates are raised from the upload chain; state is only touched on the UI thread.
        var ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Notetaker must be created on the UI thread.");
        _recorder.Updated += meeting => ui.Post(_ => OnUpdate(meeting), null);
        _tick.Tick += (_, _) => Tick();
    }

    void Save()
    {
        try { _store.Save(Meetings); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Logger.Error("Meetings could not be saved", e); }
        Changed?.Invoke();
    }

    // MARK: Recording

    public void Start()
    {
        if (Current != State.Idle) return;
        if (_model.GetKey() is not { } key)
        {
            _bar.Show("Add your Groq key to take meeting notes.", seconds: 4);
            return;
        }
        var prefs = _model.Preferences;
        var settings = new MeetingRecorder.Settings(
            key, prefs.TranscriptionModel, prefs.WhisperLanguage, prefs.ParallelLanguage, prefs.Languages,
            NameExtractor.WhisperPrompt(_model.DictionaryTerms, []),
            prefs.InputDeviceId.Length == 0 ? null : prefs.InputDeviceId, prefs.MeetingsSystemAudio);
        try
        {
            _recorder.Start("Meeting", "", [], settings);
        }
        catch (Exception e) when (e is AudioCaptureException or InvalidOperationException)
        {
            _bar.Show("Notetaker could not start", e.Message, seconds: 6);
            return;
        }
        Live = _recorder.Snapshot();
        StartedAt = DateTimeOffset.Now;
        _warnedSilentSystem = false;
        Current = State.Recording;
        _bar.MeetingSince = StartedAt;
        LastError = _recorder.LastError;
        _tick.Start();
        if (LastError != null) _bar.Show("Recording your microphone only", LastError, seconds: 8);
        else if (prefs.MeetingsConsentReminder) _bar.Show("Notetaker is on", "Let everyone on the call know you're taking notes.", seconds: 6);
        Logger.Info("Notetaker started");
        Changed?.Invoke();
    }

    void Tick()
    {
        if (Current != State.Recording) return;
        _recorder.Tick();
        if (!_warnedSilentSystem && _recorder.SystemSilentSeconds >= 90 && Live?.MicOnly == false)
        {
            _warnedSilentSystem = true;
            _bar.Show("Not hearing the meeting audio",
                      "VoiceFlow records what plays on your default speakers or headset. Check that the call plays there. Your microphone is still recorded.",
                      seconds: 12);
        }
        Changed?.Invoke();
    }

    void OnUpdate(Meeting meeting)
    {
        if (Current != State.Recording) return;
        Live = meeting;
        // Keep a copy on disk, so a crash loses at most the last chunk.
        var snapshot = meeting.Copy();
        snapshot.Status = MeetingStatus.Recording;
        var i = Meetings.FindIndex(m => m.Id == meeting.Id);
        if (i >= 0) Meetings[i] = snapshot; else Meetings.Insert(0, snapshot);
        Save();
    }

    public void Stop()
    {
        if (Current != State.Recording) return;
        _ = FinishAsync();
    }

    async Task FinishAsync()
    {
        Current = State.WritingNotes;
        _bar.MeetingSince = null;
        _tick.Stop();
        Changed?.Invoke();
        var meeting = await _recorder.StopAsync();
        Live = null;
        Current = State.Idle;
        if (meeting == null) { Changed?.Invoke(); return; }
        meeting.Status = MeetingStatus.WritingNotes;
        Meetings.RemoveAll(m => m.Id == meeting.Id);   // the live snapshot
        Meetings.Insert(0, meeting);
        LastStoppedId = meeting.Id;
        Save();
        await WriteNotesAsync(meeting.Id);
    }

    /// <summary>App exit during a meeting: keep what was transcribed, mark notes as unfinished.</summary>
    public void Abort()
    {
        if (Current != State.Recording) return;
        _tick.Stop();
        var meeting = _recorder.Snapshot();
        _recorder.Abort();
        Current = State.Idle;
        if (meeting == null) return;
        meeting.Status = MeetingStatus.Failed;
        meeting.End = DateTimeOffset.Now;
        meeting.Error = "VoiceFlow quit before the notes were written. Press Try again.";
        Meetings.RemoveAll(m => m.Id == meeting.Id);
        Meetings.Insert(0, meeting);
        Save();
    }

    /// <summary>Writes (or rewrites) the notes for a meeting, then saves them to the notes folder.</summary>
    public async Task WriteNotesAsync(Guid id)
    {
        var meeting = Meetings.FirstOrDefault(m => m.Id == id);
        if (meeting == null) return;
        if (meeting.Lines.Count == 0)
        {
            meeting.Status = MeetingStatus.Failed;
            meeting.Error ??= "No speech was picked up. Check that your microphone works (Settings → Microphone) and that the call's audio plays on this PC.";
            Save();
            _bar.Show("No speech was recorded", meeting.Error, seconds: 8);
            return;
        }
        meeting.Status = MeetingStatus.WritingNotes;
        meeting.Error = null;
        Save();
        if (_model.GetKey() is not { } key)
        {
            meeting.Status = MeetingStatus.Failed;
            meeting.Error = "No Groq key available. Add it in Settings, then press Try again.";
            Save();
            return;
        }
        try
        {
            var notes = await new MeetingNotesWriter(new GroqChat(key), _model.Preferences.CommandModel)
                .WriteAsync(meeting, _model.Preferences.MeetingsNotesLanguage);
            meeting.Notes = notes;
            if ((meeting.Title == "Meeting" || meeting.Title.EndsWith("meeting", StringComparison.Ordinal)) && notes.Title.Length > 0)
                meeting.Title = notes.Title;
            meeting.Status = MeetingStatus.Done;
            SaveToFolder(meeting);
            Save();
            _bar.Show("Meeting notes are ready", meeting.Title, new ToastAction("Open", OpenMeetingsPage), 8);
        }
        catch (ProviderException e)
        {
            meeting.Status = MeetingStatus.Failed;
            meeting.Error = e.Message;
            SaveToFolder(meeting);
            Save();
        }
    }

    // MARK: Editing

    public void Rename(Guid id, string title)
    {
        var meeting = Meetings.FirstOrDefault(m => m.Id == id);
        if (meeting == null || title.Trim().Length == 0) return;
        meeting.Title = title.Trim();
        SaveToFolder(meeting);
        Save();
    }

    public void Delete(Guid id)
    {
        Meetings.RemoveAll(m => m.Id == id);
        Save();
    }

    // MARK: Notes folder

    public void SaveToFolder(Meeting meeting)
    {
        var folder = _model.Preferences.MeetingsFolder;
        if (folder.Length == 0 || meeting.Status == MeetingStatus.Recording) return;
        var name = MeetingMarkdown.FileName(meeting);
        try
        {
            Directory.CreateDirectory(folder);
            // meetings.json is plain data on disk: only ever delete a bare file name inside the notes folder.
            if (meeting.SyncedFileName is { } previous && previous != name && Path.GetFileName(previous) == previous)
            {
                var old = Path.Combine(folder, previous);
                if (File.Exists(old)) File.Delete(old);
            }
            AtomicFile.WriteAllText(Path.Combine(folder, name), MeetingMarkdown.Render(meeting));
            meeting.SyncedFileName = name;
            LastError = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            LastError = $"Could not save notes to {folder}: {e.Message}";
            Logger.Error("Notes could not be saved to the folder", e);
        }
    }

    public void Dispose()
    {
        Abort();
        _tick.Dispose();
    }
}
