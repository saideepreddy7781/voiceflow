using System.Diagnostics;
using VoiceFlow.Core;
using VoiceFlow.Core.AI;
using VoiceFlow.Core.Audio;
using VoiceFlow.Core.Input;
using VoiceFlow.Core.Library;
using VoiceFlow.Core.Speech;
using VoiceFlow.Core.Text;
using VoiceFlow.Windows.Audio;
using VoiceFlow.Windows.Native;
using VoiceFlow.Windows.UI;

namespace VoiceFlow.Windows.Dictation;

/// <summary>
/// Runs one dictation or command from hotkey press to inserted text. Lives on the UI thread; network calls
/// are awaited, so continuations come back to the UI thread.
///
/// Fallback chain, so speech is never lost: cleaned text → raw transcript → clipboard + "Paste last".
/// </summary>
internal sealed class DictationController : IDisposable
{
    readonly AppModel _model;
    readonly FlowBarController _bar;
    readonly TextInserter _inserter;
    readonly OutputMuter _muter;
    readonly System.Windows.Forms.Timer _levelTimer = new() { Interval = 33 };
    readonly System.Windows.Forms.Timer _segmentTimer = new() { Interval = 1000 };

    AudioCapture? _recorder;
    bool _recording;
    SegmentCutter _cutter = new();
    List<Task<string>> _segments = [];
    DateTime _startedAt;
    SessionKind _kind;
    TypingContext _context = TypingContext.None;
    Task<FieldState>? _fieldTask;
    /// <summary>Set once the target is known to be a password field: nothing is uploaded.</summary>
    bool _blockedField;
    string? _whisperPrompt;
    Task? _processing;
    bool _warnedLong;
    int _session;

    /// <summary>Long sessions: a warning at 19 minutes, automatic finish (not discard) at 20.</summary>
    static readonly TimeSpan WarnAfter = TimeSpan.FromMinutes(19), StopAfter = TimeSpan.FromMinutes(20);

    public string? LastResult { get; private set; }

    /// <summary>Wired by the app: the gesture state to reset, Notetaker state, the Settings window.</summary>
    public Action<bool> ResetHotkeySession { get; set; } = _ => { };
    public Action EnterHandsFreeGesture { get; set; } = () => { };
    public Func<bool> MeetingRecording { get; set; } = () => false;
    public Action OpenSettings { get; set; } = () => { };
    /// <summary>The last window the user worked in outside VoiceFlow (for Paste last from the tray).</summary>
    public Func<IntPtr> LastExternalWindow { get; set; } = () => IntPtr.Zero;

    public DictationController(AppModel model, FlowBarController bar, TextInserter inserter, OutputMuter muter)
    {
        _model = model;
        _bar = bar;
        _inserter = inserter;
        _muter = muter;
        _levelTimer.Tick += (_, _) => TickLevel();
        _segmentTimer.Tick += (_, _) => TickSegments();
    }

    public bool IsRecording => _recording;
    public bool IsBusy => _recording || _processing != null;

    // MARK: Session control

    public void Begin(SessionKind kind)
    {
        if (_recording) return;
        if (_processing != null)
        {
            _bar.Show("Still finishing the last dictation…", seconds: 1.5);
            ResetHotkeySession(false);
            return;
        }
        if (!_model.HasKey(SecretKey.Groq))
        {
            Fail("Add your Groq key to start dictating.", new ToastAction("Open Settings", OpenSettings));
            return;
        }
        var prefs = _model.Preferences;
        _context = FocusInfo.Capture();
        if (_context.IsExcluded)
        {
            Fail("VoiceFlow doesn't listen in password managers.");
            return;
        }
        var recorder = new AudioCapture(prefs.InputDeviceId.Length == 0 ? null : prefs.InputDeviceId);
        try
        {
            recorder.Start();
        }
        catch (AudioCaptureException e)
        {
            recorder.Dispose();
            var privacy = e.Message.StartsWith("Microphone access is off", StringComparison.Ordinal);
            Fail(e.Message, privacy ? new ToastAction("Open privacy settings", () => OpenUri("ms-settings:privacy-microphone")) : null);
            return;
        }
        recorder.Stopped += OnRecorderStopped;
        _recorder = recorder;
        _recording = true;
        _session++;
        _kind = kind;
        _warnedLong = false;
        _startedAt = DateTime.UtcNow;
        _cutter = new SegmentCutter();
        _segments = [];
        _blockedField = false;
        var screenNames = _context.IsTerminal ? [] : NameExtractor.Extract([_context.WindowTitle]);
        _whisperPrompt = NameExtractor.WhisperPrompt(_model.DictionaryTerms, screenNames);
        Sounds.Start(prefs.PlaySounds);
        if (prefs.MuteAudioWhileDictating && !MeetingRecording())
        {
            // After the start cue has played, so the cue itself is not muted.
            var session = _session;
            _ = DelayThen(250, () => { if (_recording && _session == session) _muter.Mute(); });
        }
        _bar.Set(kind == SessionKind.Command ? BarPhase.Command : BarPhase.Listening);

        // Check the field while the user talks: a password field stops the session before any audio is sent.
        var started = _session;
        _fieldTask = FocusInfo.FocusedFieldStateAsync(_context.Window, timeoutMs: 1500);
        _ = _fieldTask.ContinueWith(t =>
        {
            if (t.Result != FieldState.Password || started != _session) return;
            _blockedField = true;
            _segments = [];
            if (_recording)
            {
                StopCapture();
                ResetHotkeySession(false);
                _bar.Show("VoiceFlow doesn't listen in password fields", seconds: 3);
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());

        _levelTimer.Start();
        _segmentTimer.Start();
        Logger.Info($"Session started: {kind} in {_context.ProcessName}");
    }

    public void BecomeCommand()
    {
        if (!_recording) return;
        _kind = SessionKind.Command;
        _bar.Set(BarPhase.Command);
    }

    public void Lock()
    {
        if (!_recording) return;
        if (_kind == SessionKind.Dictation) _bar.Set(BarPhase.HandsFree);
    }

    public void Cancel()
    {
        if (!_recording) return;
        StopCapture();
        _segments = [];
        _bar.Set(BarPhase.Idle);
        Logger.Info("Session cancelled");
    }

    public void Finish()
    {
        if (!_recording) return;
        var samples = StopCapture();
        var audioSeconds = samples.Length / AudioMath.SampleRate;
        var voiced = AudioMath.VoicedSeconds(samples);
        if (audioSeconds < 0.5)
        {
            // Too short to be speech: drop it quietly.
            _segments = [];
            _bar.Set(BarPhase.Idle);
            return;
        }
        if (AudioMath.IsSilent(samples))
        {
            _segments = [];
            _bar.Show("No speech heard", audioSeconds > 1 ? "Check your microphone in Settings." : "", seconds: 1.5);
            return;
        }
        var kind = _kind;
        var context = _context;
        _bar.Set(BarPhase.Processing);
        var finalRange = _cutter.Finish(samples.Length);
        // ProcessAsync can finish synchronously (password field, all segments already back and empty). Its
        // finally block then clears _processing before this assignment, which would leave the app "still
        // finishing" forever, so a completed task is never stored.
        var processing = ProcessAsync(samples, finalRange, kind, context, audioSeconds, voiced);
        if (!processing.IsCompleted) _processing = processing;
    }

    async Task ProcessAsync(float[] samples, Range? finalRange, SessionKind kind, TypingContext context, double audioSeconds, double voiced)
    {
        var began = Stopwatch.StartNew();
        try
        {
            // The field must be known before the last audio leaves the PC.
            var field = _fieldTask != null ? await _fieldTask : FieldState.Unknown;
            if (field == FieldState.Password || _blockedField)
            {
                _segments = [];
                _bar.Show("VoiceFlow doesn't type into password fields", seconds: 3);
                return;
            }
            if (finalRange is { } r) StartSegment(samples[r]);
            var tasks = _segments;
            _segments = [];
            var parts = new List<string>();
            foreach (var task in tasks) parts.Add(await task);
            var transcript = SegmentCutter.Join(parts);
            if (HallucinationFilter.IsLikelyHallucination(transcript, voiced))
            {
                _bar.Show("No speech heard", seconds: 1.5);
                return;
            }
            Logger.Info($"Transcribed {audioSeconds:0.0} s of audio into {TextUtil.WhitespaceWords(transcript).Length} words in {began.ElapsedMilliseconds} ms");
            Logger.Debug($"Transcript: {transcript}");
            if (kind == SessionKind.Dictation) await DeliverDictationAsync(transcript, context, audioSeconds, began);
            else await RunCommandAsync(transcript, context, audioSeconds, began);
        }
        catch (ProviderException e)
        {
            Logger.Error($"Transcription failed ({e.Kind})");
            Sounds.Error(_model.Preferences.PlaySounds);
            _bar.Show("Transcription failed", e.Message, e.IsBadKey || e.Kind == ProviderErrorKind.MissingKey ? new ToastAction("Open Settings", OpenSettings) : null, 6);
            _model.Record(new HistoryEntry(kind == SessionKind.Command ? HistoryKind.Command : HistoryKind.Dictation, context.AppName, "", "",
                                           audioSeconds, 0, "failed"));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Logger.Error("Dictation failed", e);
            Sounds.Error(_model.Preferences.PlaySounds);
            _bar.Show("Something went wrong", e.Message, seconds: 6);
        }
        finally
        {
            _processing = null;
            if (_bar.Phase == BarPhase.Processing) _bar.Set(BarPhase.Idle);
        }
    }

    // MARK: Delivery

    async Task DeliverDictationAsync(string transcript, TypingContext context, double audioSeconds, Stopwatch began)
    {
        var prefs = _model.Preferences;
        string final;
        string? note = null;
        int dictionaryFixes;
        var usage = AppClassifier.Usage(context.ProcessName, context.WindowTitle);

        if (SnippetMatcher.ExactMatch(transcript, _model.Snippets) is { } snippet)
        {
            final = snippet.Expansion;
            dictionaryFixes = 1;
        }
        else
        {
            var cleaned = transcript;
            if (prefs.CleanupLevel != CleanupLevel.None && _model.GetKey() is { } key)
            {
                // Terminal titles can be anyone's output (a remote host, a running command), so none of it is sent.
                var screenNames = context.IsTerminal ? [] : NameExtractor.Extract([context.WindowTitle], 20);
                var names = _model.DictionaryTerms.Concat(screenNames).Take(40).ToList();
                var style = AppClassifier.Style(context.ProcessName, context.WindowTitle, prefs);
                var request = new CleanupRequest(transcript, style, context.AppName, names, "", [], AppClassifier.TagsFiles(context.ProcessName),
                                                 prefs.CleanupLevel, prefs.SpeaksTelugu ? prefs.TeluguScript : null);
                var outcome = await new TextCleaner(new GroqChat(key), prefs.CleanupModel).CleanAsync(request);
                cleaned = outcome.Text;
                note = outcome.Note;
            }
            var corrected = DictionaryCorrector.Apply(cleaned, _model.Dictionary);
            var expanded = SnippetMatcher.ExpandInlineCounting(corrected.Text, _model.Snippets);
            final = expanded.Text;
            dictionaryFixes = corrected.Fixes + expanded.Count;
        }

        LastResult = final;
        var result = await _inserter.InsertAsync(final, context);
        var latency = (int)began.ElapsedMilliseconds;
        if (note != null) Logger.Info($"Cleanup skipped: {note}");
        if (!ReportInsert(result, final, context)) return;
        var entry = new HistoryEntry(HistoryKind.Dictation, context.AppName, transcript, final, audioSeconds, latency,
                                     result == InsertResult.Pasted ? "pasted" : "copied")
        {
            Usage = usage,
            WordsCorrected = HistoryEntry.WordsChanged(transcript, final),
            DictionaryFixes = dictionaryFixes,
        };
        _model.Record(entry);
        Logger.Info($"Dictation delivered in {latency} ms via {result}");
    }

    /// <summary>Tells the user how an insertion ended. Returns false when nothing should be recorded.</summary>
    bool ReportInsert(InsertResult result, string text, TypingContext context)
    {
        var preview = TextUtil.Prefix(text, 140);
        switch (result)
        {
            case InsertResult.Pasted:
                _bar.Set(BarPhase.Idle);
                return true;
            case InsertResult.FocusChanged:
                _bar.Show("You switched windows, so it was copied instead", preview, seconds: 5);
                return true;
            case InsertResult.CopiedOnly:
                _bar.Show(context.Elevated ? "This app runs as administrator. Copied instead; press Ctrl+V."
                                           : "The app isn't responding. Copied to the clipboard.", preview, seconds: 6);
                return true;
            case InsertResult.Blocked:
                _bar.Show("Not typed: password field or password manager", seconds: 3);
                return false;
            default:
                Sounds.Error(_model.Preferences.PlaySounds);
                _bar.Show("Couldn't paste: the clipboard is busy", "Use Paste last transcript from the tray menu.", seconds: 6);
                return true;
        }
    }

    async Task RunCommandAsync(string instruction, TypingContext context, double audioSeconds, Stopwatch began)
    {
        if (_model.GetKey() is not { } key)
        {
            _bar.Show("Add your Groq key to use Command Mode.", seconds: 4);
            return;
        }
        if (context.IsExcluded || await FocusInfo.FocusedFieldStateAsync(context.Window) == FieldState.Password)
        {
            _bar.Show("VoiceFlow doesn't type into password fields", seconds: 3);
            return;
        }
        // Terminal scrollback is untrusted (anyone's output), so it is never sent.
        var fullText = context.IsTerminal ? "" : await FocusInfo.FocusedFieldValueAsync();
        var selected = NativeMethods.GetForegroundWindow() == context.Window ? await _inserter.CopySelectionAsync() ?? "" : "";
        var runner = new CommandRunner(new GroqChat(key), _model.Preferences.CommandModel);
        try
        {
            var result = await runner.RunAsync(context.AppName, fullText, selected, instruction);
            var latency = (int)began.ElapsedMilliseconds;
            var outcome = "inserted";
            switch (result.Action)
            {
                case CommandAction.Replace:
                    if (selected.Length > 0) outcome = CommandInserted(await _inserter.InsertAsync(result.Text, context), result.Text, context);
                    else if (fullText.Length > 0) outcome = CommandInserted(await _inserter.ReplaceAllAsync(result.Text, context), result.Text, context);
                    else
                    {
                        _bar.Show("Select some text first", "Command Mode edits the selected text or the current field.", seconds: 4);
                        outcome = "failed";
                    }
                    break;
                case CommandAction.Insert:
                    outcome = CommandInserted(await _inserter.InsertAsync(result.Text, context), result.Text, context);
                    break;
                case CommandAction.Search:
                    OpenUri(CommandPrompt.SearchUrl(result.Text).AbsoluteUri);
                    _bar.Set(BarPhase.Idle);
                    outcome = "search";
                    break;
                case CommandAction.Answer:
                    var answer = result.Text;
                    _bar.Show("Answer", answer, new ToastAction("Copy", () => _ = _inserter.CopyAsync(answer)), 12);
                    outcome = "answer";
                    break;
            }
            LastResult = result.Text;
            _model.Record(new HistoryEntry(HistoryKind.Command, context.AppName, instruction, result.Text, audioSeconds, latency, outcome));
        }
        catch (ProviderException e)
        {
            Sounds.Error(_model.Preferences.PlaySounds);
            _bar.Show("Command failed", e.Message, seconds: 6);
        }
    }

    /// <summary>Reports a Command Mode insertion and returns the history outcome.</summary>
    string CommandInserted(InsertResult result, string text, TypingContext context)
    {
        if (result == InsertResult.Pasted)
        {
            _bar.Show("Done", action: new ToastAction("Undo", () => _ = UndoAsync(context.Window)), seconds: 6);
            return "inserted";
        }
        return ReportInsert(result, text, context) ? "copied" : "failed";
    }

    static async Task UndoAsync(IntPtr window)
    {
        if (await TextInserter.RefocusAsync(window)) InputSender.CtrlZ();
    }

    /// <summary>Called when the app quits: never leave the PC muted or the microphone running.</summary>
    public void Shutdown()
    {
        if (_recording) StopCapture();
        _muter.Restore();
    }

    // MARK: Shortcuts

    public async void PasteLast()
    {
        var text = LastResult ?? _model.LastResult;
        if (text == null)
        {
            _bar.Show("Nothing to paste yet", seconds: 1.5);
            return;
        }
        var target = NativeMethods.GetForegroundWindow();
        if (target == IntPtr.Zero || FocusInfo.IsOwnWindow(target))
        {
            target = LastExternalWindow();
            if (!await TextInserter.RefocusAsync(target))
            {
                await _inserter.CopyAsync(text);
                _bar.Show("Copied last transcript", "Click where it should go and press Ctrl+V.", seconds: 3);
                return;
            }
        }
        var context = FocusInfo.Describe(target);
        if (context.IsExcluded)
        {
            _bar.Show("VoiceFlow doesn't type into password managers", seconds: 3);
            return;
        }
        ReportInsert(await _inserter.InsertAsync(text, context), text, context);
    }

    public async void CopyLast()
    {
        var text = LastResult ?? _model.LastResult;
        if (text == null)
        {
            _bar.Show("Nothing to copy yet", seconds: 1.5);
            return;
        }
        await _inserter.CopyAsync(text);
        _bar.Show("Copied last transcript", seconds: 1.5);
    }

    /// <summary>Clicking the idle Flow bar starts a hands-free dictation.</summary>
    public void StartHandsFreeFromBar()
    {
        Begin(SessionKind.Dictation);
        if (!_recording) return;
        Lock();
        EnterHandsFreeGesture();
    }

    // MARK: Capture internals

    float[] StopCapture()
    {
        _levelTimer.Stop();
        _segmentTimer.Stop();
        _recording = false;
        var recorder = _recorder;
        _recorder = null;
        float[] samples = [];
        if (recorder != null)
        {
            recorder.Stopped -= OnRecorderStopped;
            samples = recorder.Stop();
            recorder.Dispose();
        }
        _muter.Restore();
        return samples;
    }

    void OnRecorderStopped(Exception? error)
    {
        if (!_recording) return;
        // The microphone went away mid-sentence: keep what was said.
        _bar.Show("The microphone stopped", error?.Message ?? "It was unplugged or taken by another app.", seconds: 5);
        Finish();
        ResetHotkeySession(false);
    }

    void TickLevel()
    {
        if (_recorder == null) return;
        _bar.PushLevel(_recorder.CurrentLevel());
        var elapsed = DateTime.UtcNow - _startedAt;
        if (elapsed > StopAfter)
        {
            Finish();
            ResetHotkeySession(false);
        }
        else if (elapsed > WarnAfter && !_warnedLong)
        {
            _warnedLong = true;
            Sounds.Error(_model.Preferences.PlaySounds);
            _bar.Show("One minute left", "Dictations finish automatically after 20 minutes.", seconds: 4);
        }
    }

    /// <summary>Cuts and uploads finished segments while the user is still talking.</summary>
    void TickSegments()
    {
        if (_recorder == null) return;
        // Only the open segment is copied, not the whole (up to 20-minute) recording.
        int start = _cutter.OpenStart;
        var tail = _recorder.SamplesFrom(start);
        if (_cutter.NextCut(tail, start) is { } range) StartSegment(tail[(range.Start.Value - start)..(range.End.Value - start)]);
    }

    void StartSegment(float[] samples)
    {
        if (_blockedField) return;
        var prefs = _model.Preferences;
        var audio = prefs.WhisperMode ? AudioMath.Normalized(samples) : samples;
        var prompt = _whisperPrompt;
        var key = _model.GetKey();
        var task = Task.Run(async () =>
        {
            if (AudioMath.IsSilent(audio)) return "";
            if (key == null) throw ProviderException.MissingKey();
            var transcriber = new GroqTranscriber(key, prefs.TranscriptionModel);
            var result = await transcriber.TranscribeResolvingAsync(AudioMath.Wav(audio), prefs.WhisperLanguage, prefs.ParallelLanguage,
                                                                    prefs.Languages, prompt).ConfigureAwait(false);
            return result.Text;
        });
        // A cancelled session drops its tasks; observe failures so they are not reported as unobserved.
        _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        _segments.Add(task);
    }

    void Fail(string message, ToastAction? action = null)
    {
        Sounds.Error(_model.Preferences.PlaySounds);
        _bar.Show(message, action: action, seconds: 5);
        ResetHotkeySession(false);
    }

    static async Task DelayThen(int ms, Action action)
    {
        await Task.Delay(ms);
        action();
    }

    public static void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Not the whole URI: a Command Mode search carries the user's spoken words in the query string.
            Logger.Warn($"Could not open a {(Uri.TryCreate(uri, UriKind.Absolute, out var u) ? u.Scheme : "")} link: {e.Message}");
        }
    }

    public void Dispose()
    {
        Shutdown();
        _levelTimer.Dispose();
        _segmentTimer.Dispose();
    }
}
