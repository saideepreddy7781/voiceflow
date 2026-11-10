using System.Diagnostics;
using Avalonia.Threading;
using VoiceFlow.Core;
using VoiceFlow.Core.AI;
using VoiceFlow.Core.Audio;
using VoiceFlow.Core.Diagnostics;
using VoiceFlow.Core.Input;
using VoiceFlow.Core.Library;
using VoiceFlow.Core.Speech;
using VoiceFlow.Core.Text;
using VoiceFlow.Windows.Audio;
using VoiceFlow.Windows.Native;
using VoiceFlow.Desktop.Services;
using VoiceFlow.Windows.Shell;

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
    readonly DispatcherTimer _levelTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly DispatcherTimer _segmentTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    readonly SharedMicrophone _microphone;
    readonly DeliveryLog _deliveries;

    MicSubscription? _recorder;
    bool _recording;
    /// <summary>The microphone failed during this session, so missing audio is its fault and not silence.</summary>
    bool _micFailed;
    SegmentCutter _cutter = new();
    List<Task<string>> _segments = [];
    DateTime _startedAt;
    SessionKind _kind;
    TypingContext _context = TypingContext.None;
    Task<FieldState>? _fieldTask;
    Task<string>? _beforeCursorTask;
    /// <summary>Set once the target is known to be a password field: nothing is uploaded.</summary>
    bool _blockedField;
    IReadOnlyList<string> _whisperTerms = [];
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

    /// <param name="microphone">The shared capture. Null uses the app's one (tests and the self-test pass their own).</param>
    public DictationController(AppModel model, FlowBarController bar, TextInserter inserter, OutputMuter muter, SharedMicrophone? microphone = null)
    {
        _model = model;
        _bar = bar;
        _inserter = inserter;
        _muter = muter;
        _microphone = microphone ?? MicrophoneHub.Shared;
        _deliveries = AppDiagnostics.LogFor(model);
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
            Note(DeliveryReason.Blocked, DeliveryDetail.ExcludedApp, kind: kind);
            Fail("VoiceFlow doesn't listen in password managers.");
            return;
        }
        MicSubscription recorder;
        try
        {
            recorder = _microphone.Subscribe("dictation", prefs.InputDeviceId.Length == 0 ? null : prefs.InputDeviceId);
        }
        catch (Exception e) when (e is AudioCaptureException or InvalidOperationException)
        {
            Note(DeliveryReason.MicFailed, DeliveryDetail.StartFailed, kind: kind);
            var privacy = e.Message.StartsWith("Microphone access is off", StringComparison.Ordinal);
            Fail(e.Message, privacy ? new ToastAction("Open privacy settings", () => OpenUri("ms-settings:privacy-microphone")) : null);
            return;
        }
        recorder.Failed += OnRecorderFailed;
        _recorder = recorder;
        _recording = true;
        _micFailed = false;
        _session++;
        _kind = kind;
        _warnedLong = false;
        _startedAt = DateTime.UtcNow;
        _cutter = new SegmentCutter();
        _segments = [];
        _blockedField = false;
        var screenNames = _context.IsTerminal ? [] : NameExtractor.Extract([_context.WindowTitle]);
        _whisperTerms = _model.DictionaryTerms.Concat(screenNames).ToList();
        Sounds.Start(prefs.PlaySounds);
        if (prefs.MuteAudioWhileDictating && !MeetingRecording())
        {
            // After the start cue has played, so the cue itself is not muted.
            var session = _session;
            _ = DelayThen(250, () => { if (_recording && _session == session) _muter.Mute(); });
        }
        _bar.Set(kind == SessionKind.Command ? FlowPhase.Command : FlowPhase.Listening);

        // Check the field while the user talks: a password field stops the session before any audio is sent.
        var started = _session;
        _fieldTask = FocusInfo.FocusedFieldStateAsync(_context.Window, timeoutMs: 1500);
        _ = _fieldTask.ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (t.Result != FieldState.Password || started != _session) return;
            _blockedField = true;
            _segments = [];
            if (_recording)
            {
                StopCapture();
                ResetHotkeySession(false);
                _bar.Show("VoiceFlow doesn't listen in password fields", seconds: 3);
                _bar.Set(FlowPhase.Idle);   // after the toast, so the window stays up and doesn't flicker
                Note(DeliveryReason.Blocked, DeliveryDetail.PasswordField, kind: kind);
            }
        }), TaskContinuationOptions.OnlyOnRanToCompletion);
        // Read early, while the caret is still where the dictation started. Password managers and terminals are left unread.
        _beforeCursorTask = _context.IsTerminal || _context.IsExcluded ? Task.FromResult("") : FocusInfo.BeforeCursorAsync();

        _levelTimer.Start();
        _segmentTimer.Start();
        Logger.Info($"Session started: {kind} in {_context.ProcessName}");
    }

    public void BecomeCommand()
    {
        if (!_recording) return;
        _kind = SessionKind.Command;
        _bar.Set(FlowPhase.Command);
    }

    public void Lock()
    {
        if (!_recording) return;
        if (_kind == SessionKind.Dictation) _bar.Set(FlowPhase.HandsFree);
    }

    public void Cancel()
    {
        if (!_recording) return;
        var seconds = (DateTime.UtcNow - _startedAt).TotalSeconds;
        StopCapture();
        _segments = [];
        _bar.Set(FlowPhase.Idle);
        Note(DeliveryReason.Cancelled, audioSeconds: seconds);
        Logger.Info("Session cancelled");
    }

    public void Finish()
    {
        if (!_recording) return;
        var held = (DateTime.UtcNow - _startedAt).TotalSeconds;
        var micFailed = _micFailed;
        var samples = StopCapture();
        var audioSeconds = samples.Length / AudioMath.SampleRate;
        var voiced = AudioMath.VoicedSeconds(samples);
        var verdict = AudioMath.Verdict(samples, held);
        // A device that failed mid-session explains missing audio, however short or quiet the result is.
        if (micFailed && verdict is CaptureVerdict.TooShort or CaptureVerdict.NoSpeech) verdict = CaptureVerdict.NoAudio;
        if (verdict != CaptureVerdict.Speech)
        {
            _segments = [];
            switch (verdict)
            {
                case CaptureVerdict.TooShort:
                    // Too short to be speech: drop it quietly.
                    _bar.Set(FlowPhase.Idle);
                    Note(DeliveryReason.TooShort, audioSeconds: audioSeconds);
                    break;
                case CaptureVerdict.NoAudio or CaptureVerdict.DigitalSilence:
                    Sounds.Error(_model.Preferences.PlaySounds);
                    _bar.Show("Your microphone sent no audio", "Check the input in Settings.", new ToastAction("Open Settings", OpenSettings), 6);
                    Note(DeliveryReason.MicFailed, micFailed ? DeliveryDetail.DeviceLost : verdict == CaptureVerdict.DigitalSilence ? DeliveryDetail.DeadSignal : DeliveryDetail.Stalled, audioSeconds);
                    break;
                default:
                    _bar.Show("No speech heard", audioSeconds > 1 ? "Check your microphone in Settings." : "", seconds: 1.5);
                    Note(DeliveryReason.NoSpeech, DeliveryDetail.Silent, audioSeconds);
                    break;
            }
            // The microphone is off, so the bar can't keep saying it's listening. Set after the toast so the window stays up.
            _bar.Set(FlowPhase.Idle);
            return;
        }
        var kind = _kind;
        var context = _context;
        _bar.Set(FlowPhase.Processing);
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
                Note(DeliveryReason.Blocked, DeliveryDetail.PasswordField, audioSeconds, kind: kind);
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
                Note(DeliveryReason.NoSpeech, DeliveryDetail.Hallucination, audioSeconds, kind: kind);
                _bar.Show("No speech heard", seconds: 1.5);
                return;
            }
            Logger.Info($"Transcribed {audioSeconds:0.0} s of audio into {TextUtil.WhitespaceWords(transcript).Length} words in {began.ElapsedMilliseconds} ms");
            var beforeCursor = _beforeCursorTask != null ? await _beforeCursorTask : "";
            if (kind == SessionKind.Dictation) await DeliverDictationAsync(transcript, context, beforeCursor, audioSeconds, began);
            else await RunCommandAsync(transcript, context, audioSeconds, began);
        }
        catch (ProviderException e)
        {
            Logger.Error($"Transcription failed ({e.Kind})");
            Sounds.Error(_model.Preferences.PlaySounds);
            _bar.Show(e.Kind == ProviderErrorKind.DailyLimit ? "Groq's daily limit ran out" : "Transcription failed", e.Message, e.IsBadKey || e.Kind == ProviderErrorKind.MissingKey ? new ToastAction("Open Settings", OpenSettings) : null, 6);
            _model.Record(new HistoryEntry(kind == SessionKind.Command ? HistoryKind.Command : HistoryKind.Dictation, context.AppName, "", "",
                                           audioSeconds, 0, "failed") { Reason = DeliveryReason.TranscriptionFailed });
            Note(DeliveryReason.TranscriptionFailed, DeliveryDetail.Provider, audioSeconds, (int)began.ElapsedMilliseconds, kind);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Logger.Error("Dictation failed", e);
            Sounds.Error(_model.Preferences.PlaySounds);
            _bar.Show("Something went wrong", e.Message, seconds: 6);
            Note(DeliveryReason.TranscriptionFailed, DeliveryDetail.Unexpected, audioSeconds, (int)began.ElapsedMilliseconds, kind);
        }
        finally
        {
            _processing = null;
            if (_bar.Phase == FlowPhase.Processing) _bar.Set(FlowPhase.Idle);
        }
    }

    // MARK: Delivery

    async Task DeliverDictationAsync(string transcript, TypingContext context, string beforeCursor, double audioSeconds, Stopwatch began)
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
                var files = await MentionedFilesAsync(context, transcript);
                var request = new CleanupRequest(transcript, style, context.AppName, names, beforeCursor, files, AppClassifier.TagsFiles(context.ProcessName),
                                                 prefs.CleanupLevel, prefs.SpeaksTelugu ? prefs.TeluguScript : null, prefs.SpeaksHindi ? prefs.HindiScript : null);
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
        var result = await _inserter.DeliverAsync(final, context);
        var latency = (int)began.ElapsedMilliseconds;
        if (note != null) Logger.Info($"Cleanup skipped: {note}");
        Note(result.Reason, result.Detail, audioSeconds, latency);
        if (!ReportInsert(result, final, context)) return;
        var entry = new HistoryEntry(HistoryKind.Dictation, context.AppName, transcript, final, audioSeconds, latency,
                                     result.Result is InsertResult.Pasted or InsertResult.PastedUnverified or InsertResult.PasteNotLanded ? "pasted" : "copied")
        {
            Reason = result.Reason,
            Usage = usage,
            WordsCorrected = HistoryEntry.WordsChanged(transcript, final),
            DictionaryFixes = dictionaryFixes,
        };
        _model.Record(entry);
        Logger.Info($"Dictation delivered in {latency} ms via {result.Reason.WireName()}");
        if (result.Result is InsertResult.Pasted or InsertResult.PastedUnverified) ScheduleAutoLearn(final, result.FieldBefore, context);
    }

    /// <summary>Spoken file names in a code editor, for @file tags. Limited to a short wait so it never delays the paste.</summary>
    static async Task<IReadOnlyList<string>> MentionedFilesAsync(TypingContext context, string transcript)
    {
        if (!AppClassifier.IsCode(context.ProcessName) || WorkspaceFiles.StorageFolder(context.ProcessName) is not { } editor) return [];
        var title = context.WindowTitle;
        var work = Task.Run<IReadOnlyList<string>>(() =>
        {
            try
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), editor, "User", "globalStorage", "storage.json");
                if (!File.Exists(path) || new FileInfo(path).Length > 8_000_000) return [];
                var folder = WorkspaceFiles.ProjectFolder(File.ReadAllText(path), title, Directory.Exists);
                return folder == null ? [] : WorkspaceFiles.Mentioned(transcript, WorkspaceFiles.Files(folder));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        });
        var finished = await Task.WhenAny(work, Task.Delay(800));
        return finished == work ? work.Result : [];
    }

    /// <summary>Re-reads the field 15 seconds after a dictation: a word you corrected may belong in the dictionary. Same guards as macOS.</summary>
    void ScheduleAutoLearn(string inserted, string? fieldBefore, TypingContext context)
    {
        if (!_model.Preferences.AutoLearnWords || context.IsExcluded || context.IsTerminal) return;
        _ = AutoLearnAsync(inserted, fieldBefore ?? "");
    }

    async Task AutoLearnAsync(string inserted, string fieldBefore)
    {
        var field = await FocusInfo.CaptureFieldAsync();
        if (field == null) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            var after = await FocusInfo.ReadHeldFieldAsync(field);
            if (after == null || _bar.Phase != FlowPhase.Idle) return;
            var known = _model.DictionaryTerms.Select(t => t.ToLowerInvariant()).ToHashSet();
            var terms = AutoLearner.LearnedTerms(inserted, fieldBefore, after, known);
            if (terms.Count == 0) return;
            foreach (var term in terms) _model.AddDictionaryEntry(term, null, DictionarySource.AutoLearned);
            _bar.Show($"Added to dictionary: {string.Join(", ", terms)}",
                      action: new ToastAction("Undo", () => terms.ForEach(_model.RemoveDictionaryTerm)), seconds: 6);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { Logger.Warn($"Auto-learn stopped: {e.GetType().Name}"); }
        finally { field.Dispose(); }
    }

    /// <summary>Records how a session ended. Codes and numbers only.</summary>
    void Note(DeliveryReason reason, string? detail = null, double audioSeconds = 0, int latencyMs = 0, SessionKind? kind = null, DeliverySession? session = null)
    {
        var which = session ?? ((kind ?? _kind) == SessionKind.Command ? DeliverySession.Command : DeliverySession.Dictation);
        _deliveries.Record(new DeliveryLogEntry(which, reason, detail, (int)(audioSeconds * 1000), latencyMs));
    }

    /// <summary>Tells the user how an insertion ended. Returns false when nothing should be recorded.</summary>
    bool ReportInsert(InsertOutcome outcome, string text, TypingContext context)
    {
        var preview = TextUtil.Prefix(text, 140);
        switch (outcome.Result)
        {
            case InsertResult.Pasted:
                _bar.Set(FlowPhase.Idle);
                return true;
            case InsertResult.PastedUnverified:
                // Apps that can't be read back still got the paste; the bar stays out of the way.
                _bar.Set(FlowPhase.Idle);
                return true;
            case InsertResult.PasteNotLanded:
                OfferPaste("Didn't reach that field", preview, 6);
                return true;
            case InsertResult.FocusChanged:
                OfferPaste("You switched apps, so it was copied instead", preview, 5);
                return true;
            case InsertResult.CopiedOnly when outcome.Detail == DeliveryDetail.Elevated:
                // A Paste button can't work here: Windows drops synthetic keys sent to an administrator window.
                _bar.Show("This app runs as administrator. Copied instead; press Ctrl+V.", preview, seconds: 6);
                return true;
            case InsertResult.CopiedOnly when outcome.Detail == DeliveryDetail.AppNotResponding:
                _bar.Show("The app isn't responding. Copied to the clipboard.", preview, seconds: 6);
                return true;
            case InsertResult.CopiedOnly:
                OfferPaste("No text field. Copied to clipboard", preview, 5);
                return true;
            case InsertResult.Blocked:
                _bar.Show("Not typed: password field or password manager", seconds: 3);
                return false;
            default:
                Sounds.Error(_model.Preferences.PlaySounds);
                _bar.Show(outcome.Detail == DeliveryDetail.InputRefused ? "Windows refused the paste keys" : "Couldn't paste: the clipboard is busy",
                          "Use Paste last transcript from the tray menu.", seconds: 6);
                return true;
        }
    }

    /// <summary>One click to type the text into whatever has focus now, with every protection still applied.</summary>
    void OfferPaste(string message, string preview, double seconds) => _bar.Show(message, preview, new ToastAction("Paste", PasteLast), seconds);

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
            Note(DeliveryReason.Blocked, context.IsExcluded ? DeliveryDetail.ExcludedApp : DeliveryDetail.PasswordField, audioSeconds, kind: SessionKind.Command);
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
            InsertOutcome? delivered = null;
            switch (result.Action)
            {
                case CommandAction.Replace:
                    if (selected.Length > 0) delivered = await _inserter.DeliverAsync(result.Text, context);
                    else if (fullText.Length > 0) delivered = await _inserter.DeliverAsync(result.Text, context, replaceAll: true);
                    else
                    {
                        _bar.Show("Select some text first", "Command Mode edits the selected text or the current field.", seconds: 4);
                        outcome = "failed";
                    }
                    break;
                case CommandAction.Insert:
                    delivered = await _inserter.DeliverAsync(result.Text, context);
                    break;
                case CommandAction.Search:
                    OpenUri(CommandPrompt.SearchUrl(result.Text).AbsoluteUri);
                    _bar.Set(FlowPhase.Idle);
                    outcome = "search";
                    break;
                case CommandAction.Answer:
                    var answer = result.Text;
                    _bar.Show("Answer", answer, new ToastAction("Copy", () => _ = _inserter.CopyAsync(answer)), 12);
                    outcome = "answer";
                    break;
            }
            if (delivered is { } ended)
            {
                outcome = CommandInserted(ended, result.Text, context);
                Note(ended.Reason, ended.Detail, audioSeconds, latency, SessionKind.Command);
            }
            LastResult = result.Text;
            _model.Record(new HistoryEntry(HistoryKind.Command, context.AppName, instruction, result.Text, audioSeconds, latency, outcome)
            {
                Reason = delivered?.Reason ?? DeliveryReason.Unknown,
            });
        }
        catch (ProviderException e)
        {
            Sounds.Error(_model.Preferences.PlaySounds);
            _bar.Show("Command failed", e.Message, seconds: 6);
            Note(DeliveryReason.TranscriptionFailed, DeliveryDetail.CommandModel, audioSeconds, (int)began.ElapsedMilliseconds, SessionKind.Command);
        }
    }

    /// <summary>Reports a Command Mode insertion and returns the history outcome.</summary>
    string CommandInserted(InsertOutcome outcome, string text, TypingContext context)
    {
        if (outcome.Result == InsertResult.Pasted)
        {
            _bar.Show("Done", action: new ToastAction("Undo", () => _ = UndoAsync(context.Window)), seconds: 6);
            return "inserted";
        }
        if (outcome.Result == InsertResult.PastedUnverified)
        {
            _bar.Show("Done", action: new ToastAction("Undo", () => _ = UndoAsync(context.Window)), seconds: 6);
            return "pasted";
        }
        return ReportInsert(outcome, text, context) ? "copied" : "failed";
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
        await PasteTextAsync(text);
    }

    /// <summary>Types text into the focused app, or the last app the user worked in when VoiceFlow itself has focus.</summary>
    public async Task PasteTextAsync(string text)
    {
        var target = NativeMethods.GetForegroundWindow();
        if (target == IntPtr.Zero || FocusInfo.IsOwnWindow(target))
        {
            target = LastExternalWindow();
            if (!await TextInserter.RefocusAsync(target))
            {
                await _inserter.CopyAsync(text);
                _bar.Show("Copied last transcript", "Click where it should go and press Ctrl+V.", seconds: 3);
                Note(DeliveryReason.CopiedNoField, DeliveryDetail.NoWindow, session: DeliverySession.PasteLast);
                return;
            }
        }
        var context = FocusInfo.Describe(target);
        if (context.IsExcluded)
        {
            _bar.Show("VoiceFlow doesn't type into password managers", seconds: 3);
            Note(DeliveryReason.Blocked, DeliveryDetail.ExcludedApp, session: DeliverySession.PasteLast);
            return;
        }
        var outcome = await _inserter.DeliverAsync(text, context);
        Note(outcome.Reason, outcome.Detail, session: DeliverySession.PasteLast);
        ReportInsert(outcome, text, context);
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
            recorder.Failed -= OnRecorderFailed;
            samples = recorder.Stop();
        }
        _muter.Restore();
        return samples;
    }

    /// <summary>The shared microphone gave up restarting. Raised off the UI thread.</summary>
    void OnRecorderFailed()
    {
        void Handle()
        {
            if (!_recording) return;
            // The microphone went away mid-sentence: keep what was said.
            _micFailed = true;
            _bar.Show("The microphone stopped", "It was unplugged or taken by another app.", seconds: 5);
            Finish();
            ResetHotkeySession(false);
        }
        Dispatcher.UIThread.Post(Handle);
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
        var prompts = new WhisperPrompts(_whisperTerms, prefs.TeluguScript, prefs.HindiScript);
        var key = _model.GetKey();
        var task = Task.Run(async () =>
        {
            if (AudioMath.IsSilent(audio)) return "";
            if (key == null) throw ProviderException.MissingKey();
            var transcriber = new GroqTranscriber(key, prefs.TranscriptionModel);
            var result = await transcriber.TranscribeResolvingAsync(AudioMath.Wav(audio), prefs.LanguagePlanFor(), prompts,
                                                                    priority: GroqPriority.Interactive).ConfigureAwait(false);
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
        _levelTimer.Stop();
        _segmentTimer.Stop();
    }
}
