using VoiceFlow.Core.Audio;
using VoiceFlow.Core.Speech;

namespace VoiceFlow.Core.Meetings;

/// <summary>Where a meeting's audio comes from: the microphone and system-audio loopback, or simulated sources in tests.</summary>
public interface IMeetingAudioSource : IDisposable
{
    void StartCapture();
    void StopCapture();
    /// <summary>Takes everything captured since the last call, as 16 kHz mono samples.</summary>
    float[] DrainSamples();
    /// <summary>Seconds of audio lost to device restarts since the last call. Sources that cannot tell report 0.</summary>
    double DrainGapSeconds() => 0;
}

/// <summary>
/// Records a meeting: your microphone and the call, transcribed separately while it happens.
///
/// Each side is cut into uploads of voiced audio only (<see cref="StreamChunker"/>), so "You" and "Others" are real
/// labels, overlapping talk stays apart, and a meeting costs about its own length in audio seconds. The two sides are
/// merged by time (<see cref="TranscriptMerge"/>). Audio stays in memory: at most the chunk being collected on each
/// side, plus failed chunks up to <see cref="ChunkHold.CapacitySeconds"/> to retry when the meeting ends.
/// </summary>
public sealed class MeetingRecorder
{
    public sealed record Settings(string ApiKey, string Model, IReadOnlyList<string> Languages, WhisperPrompts Prompts,
                                  string? MicDeviceId, bool RecordSystemAudio);

    /// <summary>One upload: which side it is, and the language to try first.</summary>
    public sealed record ChunkRequest(Settings Settings, LanguagePlan Plan, string? Hint);

    public delegate Task<DetailedTranscript> TranscribeFunc(byte[] wav, ChunkRequest request, CancellationToken cancel);

    /// <summary>Tries per chunk during the meeting, and again at the end.</summary>
    internal const int LiveAttempts = 2;
    internal const int FinalAttempts = 3;
    /// <summary>Longest Stop spends retrying failed chunks.</summary>
    internal const double FinalRetrySeconds = 180;
    /// <summary>Longest Stop spends retrying failed chunks in this recorder (shortened in tests).</summary>
    internal double FinalRetryLimit { get; set; } = FinalRetrySeconds;
    /// <summary>With less audio budget than this left in the hour, meetings skip second opinions.</summary>
    internal const double SecondOpinionHeadroom = 900;

    sealed class Side
    {
        public readonly StreamChunker Chunker = new();
        public readonly StreamLanguageTracker Tracker = new();
        public string? Language;
    }

    /// <summary>What came back from Groq for one chunk.</summary>
    sealed record Attempt(DetailedTranscript? Transcript, Exception? Error);

    readonly Func<Settings, IMeetingAudioSource> _makeMic;
    readonly Func<IMeetingAudioSource?> _makeSystem;
    readonly TranscribeFunc _transcribe;
    readonly Func<TimeSpan, Task> _delay;
    readonly Action<string>? _logError;
    /// <summary>Guards the meeting, the hold and the language trackers. Queues and chunkers belong to the Tick thread.</summary>
    readonly object _gate = new();

    IMeetingAudioSource? _mic;
    IMeetingAudioSource? _system;
    Settings? _settings;
    CancellationTokenSource _cancel = new();

    // Audio not yet handed to a chunker, so the two sides stay aligned.
    readonly List<float> _queuedMic = new();
    readonly List<float> _queuedSystem = new();
    /// <summary>Samples of the microphone already handed to its chunker.</summary>
    int _micFed;
    Side _micSide = new();
    Side _callSide = new();
    readonly List<Task> _uploads = [];
    readonly Dictionary<long, (TimeMap Map, double Seconds, Speaker Speaker)> _inFlight = [];
    long _nextUpload;
    ChunkHold _hold = new();
    double _lostSeconds;
    ProviderException? _dailyLimit;
    bool _heardSpeech;
    Meeting? _meeting;

    public bool IsRecording { get; private set; }
    public string? LastError { get; private set; }
    /// <summary>Live input levels (0...1) for the Meetings page.</summary>
    public float MicLevel { get; private set; }
    public float SystemLevel { get; private set; }
    /// <summary>Seconds in a row the meeting audio has been digital silence.</summary>
    public int SystemSilentSeconds { get; private set; }
    /// <summary>The language last heard on your microphone, for the live view.</summary>
    public string? MicLanguage { get { lock (_gate) return _micSide.Language; } }
    /// <summary>The language last heard on the call, for the live view.</summary>
    public string? CallLanguage { get { lock (_gate) return _callSide.Language; } }
    /// <summary>Shorter gaps are normal: a device restarting, a driver catching up.</summary>
    const double MinimumReportedGap = 2;
    /// <summary>Called whenever new transcript lines arrive, with a copy of the meeting.</summary>
    public event Action<Meeting>? Updated;
    /// <summary>Called once when Groq's daily limit runs out during a meeting, with a sentence for the user. Any thread.</summary>
    public event Action<string>? DailyLimitReached;

    /// <param name="logError">Receives failures for the log. Never given transcript text.</param>
    public MeetingRecorder(Func<Settings, IMeetingAudioSource> makeMic, Func<IMeetingAudioSource?> makeSystem,
                           TranscribeFunc? transcribe = null, Func<TimeSpan, Task>? delay = null, Action<string>? logError = null)
    {
        _makeMic = makeMic;
        _makeSystem = makeSystem;
        _transcribe = transcribe ?? DefaultTranscribe;
        _delay = delay ?? (t => Task.Delay(t));
        _logError = logError;
    }

    static Task<DetailedTranscript> DefaultTranscribe(byte[] wav, ChunkRequest request, CancellationToken cancel)
    {
        var settings = request.Settings;
        var plan = request.Plan;
        // Short on audio budget: one upload per chunk, and auto-detect has the last word.
        if (GroqGate.Shared.TranscriptionHeadroom(settings.Model) < SecondOpinionHeadroom) plan = plan with { Languages = [] };
        return new GroqTranscriber(settings.ApiKey, settings.Model)
            .TranscribeResolvingAsync(wav, plan, settings.Prompts, ResolveMode.SequentialWith(request.Hint), GroqPriority.Background, wordTimestamps: true, cancel);
    }

    public Meeting? Snapshot()
    {
        lock (_gate) return _meeting?.Copy();
    }

    public void Start(string title, string app, IReadOnlyList<string> attendees, Settings settings)
    {
        if (IsRecording) return;
        var mic = _makeMic(settings);
        try
        {
            mic.StartCapture();
        }
        catch
        {
            mic.Dispose();
            throw;
        }
        _mic = mic;
        var m = new Meeting(title, app: app) { Attendees = attendees.ToList() };
        LastError = null;
        if (settings.RecordSystemAudio && _makeSystem() is { } capture)
        {
            try
            {
                capture.StartCapture();
                _system = capture;
            }
            catch (Exception e)
            {
                capture.Dispose();
                LastError = e.Message;
                m.MicOnly = true;
                _logError?.Invoke($"System audio unavailable: {e.GetType().Name}: {e.Message}");
            }
        }
        else m.MicOnly = true;
        lock (_gate)
        {
            _meeting = m;
            _micSide = new Side();
            _callSide = new Side();
            _hold = new ChunkHold();
            _uploads.Clear();
            _inFlight.Clear();
            _lostSeconds = 0;
            _dailyLimit = null;
        }
        _settings = settings;
        _cancel = new CancellationTokenSource();
        IsRecording = true;
        _heardSpeech = false;
        SystemSilentSeconds = 0;
        _queuedMic.Clear();
        _queuedSystem.Clear();
        _micFed = 0;
    }

    /// <summary>Stops recording and returns the meeting once every chunk is transcribed.</summary>
    /// <param name="retryFailed">False skips the second try for failed chunks, which are marked at once.</param>
    public async Task<Meeting?> StopAsync(bool retryFailed = true)
    {
        if (!IsRecording) return Snapshot();
        // The capture has to stop however the last tick goes.
        try { Tick(final: true); }
        catch (Exception e) when (e is not OutOfMemoryException) { _logError?.Invoke($"The last meeting tick failed: {e.GetType().Name}"); }
        StopSources();
        IsRecording = false;
        MicLevel = 0;
        SystemLevel = 0;
        Task[] running;
        lock (_gate)
        {
            if (_meeting != null) _meeting.End = DateTimeOffset.Now;
            running = _uploads.ToArray();
            _uploads.Clear();
        }
        await Task.WhenAll(running).ConfigureAwait(false);
        await RetryHeldAsync(retryFailed).ConfigureAwait(false);
        lock (_gate)
        {
            WriteGapNote();
            AddGapLines();
            if (_meeting is { Lines.Count: 0 })
            {
                _meeting.Error = _heardSpeech
                    ? "Speech was recorded but could not be transcribed. Check your Groq key and internet connection, then try again."
                    : "No speech was picked up. Check that your microphone works (Settings → Microphone) and that the call's audio plays on this PC.";
            }
        }
        return Snapshot();
    }

    /// <summary>Stops without waiting for uploads (app exit). Pending chunks are marked as lost.</summary>
    public void Abort()
    {
        if (!IsRecording) return;
        _cancel.Cancel();
        StopSources();
        IsRecording = false;
        lock (_gate)
        {
            foreach (var (map, seconds, speaker) in _inFlight.Values) MarkLost(map, seconds, speaker);
            _inFlight.Clear();
            foreach (var chunk in _hold.TakeAll()) MarkLost(chunk.Map, chunk.VoicedSeconds, chunk.Speaker);
            WriteGapNote();
        }
    }

    void StopSources()
    {
        foreach (var source in new[] { _mic, _system })
        {
            if (source == null) continue;
            try { source.StopCapture(); }
            catch (Exception e) { _logError?.Invoke($"Stopping a meeting source failed: {e.GetType().Name}"); }
            source.Dispose();
        }
        _mic = null;
        _system = null;
    }

    /// <summary>Call every second while recording (the app's UI timer), and once with final at Stop.</summary>
    public void Tick(bool final = false)
    {
        if (!IsRecording) return;
        var micGap = _mic?.DrainGapSeconds() ?? 0;
        var newMic = _mic?.DrainSamples() ?? [];
        // With system audio the lag check below pads the mic; a mic-only meeting has only this report to go on.
        if (_system == null && micGap > 0)
        {
            var missing = (int)(micGap * AudioMath.SampleRate);
            NoteMicGap(MicTimeline, missing);
            _queuedMic.AddRange(new float[missing]);
        }
        _queuedMic.AddRange(newMic);
        MicLevel = AudioMath.Level(newMic.AsSpan(Math.Max(0, newMic.Length - 1_600)));
        float[] micSamples, callSamples = [];
        if (_system != null)
        {
            var newSystem = _system.DrainSamples();
            SystemLevel = AudioMath.Level(newSystem.AsSpan(Math.Max(0, newSystem.Length - 1_600)));
            SystemSilentSeconds = newSystem.All(v => v == 0) ? SystemSilentSeconds + 1 : 0;
            _queuedSystem.AddRange(newSystem);
            // Keep the two sides on one clock. Whichever stalls (loopback delivers nothing while nothing plays,
            // a device restarting) is padded with silence.
            var twoSeconds = (int)AudioMath.SampleRate * 2;
            var lag = _queuedMic.Count - _queuedSystem.Count;
            if (lag > twoSeconds) _queuedSystem.AddRange(new float[lag]);
            if (-lag > twoSeconds)
            {
                NoteMicGap(MicTimeline, -lag);
                _queuedMic.AddRange(new float[-lag]);
            }
            var n = final ? Math.Max(_queuedMic.Count, _queuedSystem.Count) : Math.Min(_queuedMic.Count, _queuedSystem.Count);
            micSamples = Take(_queuedMic, n);
            callSamples = Take(_queuedSystem, n);
        }
        else micSamples = Take(_queuedMic, _queuedMic.Count);
        _micFed += micSamples.Length;

        foreach (var chunk in _micSide.Chunker.Feed(micSamples)) Send(chunk, Speaker.You);
        foreach (var chunk in _callSide.Chunker.Feed(callSamples)) Send(chunk, Speaker.Others);
        if (final)
        {
            if (_micSide.Chunker.Finish() is { } mine) Send(mine, Speaker.You);
            if (_callSide.Chunker.Finish() is { } theirs) Send(theirs, Speaker.Others);
        }
    }

    /// <summary>Samples of microphone timeline so far: fed to the chunker and still queued.</summary>
    int MicTimeline => _micFed + _queuedMic.Count;

    /// <summary>Records that your voice is missing here, so the meeting says so instead of padding silently.</summary>
    void NoteMicGap(int startSample, int samples)
    {
        var start = startSample / AudioMath.SampleRate;
        var seconds = samples / AudioMath.SampleRate;
        lock (_gate)
        {
            if (_meeting == null) return;
            var gaps = _meeting.MicGaps;
            // A stall that lasts minutes is padded a tick at a time. It is still one gap.
            if (gaps.Count > 0 && start <= gaps[^1].Time + gaps[^1].Seconds + 3)
                gaps[^1] = gaps[^1] with { Seconds = Math.Max(gaps[^1].Seconds, start + seconds - gaps[^1].Time) };
            else if (seconds >= MinimumReportedGap)
                gaps.Add(new MicGap(start, seconds));
        }
    }

    /// <summary>
    /// Marks each gap in the transcript. Call with the lock held, after every chunk has landed. A meeting with no
    /// real lines stays empty, so it reports "no speech" instead of writing notes about a marker.
    /// </summary>
    void AddGapLines()
    {
        if (_meeting is not { Lines.Count: > 0 } m) return;
        var marks = m.MicGaps.Where(g => g.Seconds >= MinimumReportedGap)
            .Select(g => new TranscriptLine(g.Time, Speaker.You, $"[Your microphone was not recording for {Math.Round(g.Seconds)} seconds]"))
            .ToList();
        if (marks.Count == 0) return;
        m.Lines = m.Lines.Concat(marks).OrderBy(l => l.Time).ToList();
    }

    static float[] Take(List<float> list, int n)
    {
        var count = Math.Min(n, list.Count);
        var head = new float[n];
        list.CopyTo(0, head, 0, count);   // the rest stays zero (padding)
        list.RemoveRange(0, count);
        return head;
    }

    ChunkRequest RequestFor(Speaker speaker, Settings settings)
    {
        string? hint;
        lock (_gate) hint = (speaker == Speaker.You ? _micSide : _callSide).Tracker.NextHint();
        return new ChunkRequest(settings, new LanguagePlan(settings.Languages, speaker == Speaker.You ? PlanSpeaker.User : PlanSpeaker.Anyone), hint);
    }

    void Send(AudioChunk chunk, Speaker speaker)
    {
        if (_settings is not { } settings) return;
        _heardSpeech = true;
        var request = RequestFor(speaker, settings);
        var wav = AudioMath.Wav(AudioMath.Normalized(chunk.Samples));
        var cancel = _cancel.Token;
        long id;
        lock (_gate)
        {
            id = _nextUpload++;
            _inFlight[id] = (chunk.Map, chunk.VoicedSeconds, speaker);
        }
        var upload = Task.Run(async () =>
        {
            var attempt = await AttemptAsync(wav, request, LiveAttempts, cancel).ConfigureAwait(false);
            Finish(attempt, id, chunk, speaker, request);
        });
        lock (_gate) _uploads.Add(upload);
    }

    /// <summary>
    /// Rate limits are waited out (<see cref="GroqGate"/> has already waited what it can); other errors retried after a
    /// short pause. Past <paramref name="deadline"/> no retry starts, and no pause runs beyond it.
    /// </summary>
    async Task<Attempt> AttemptAsync(byte[] wav, ChunkRequest request, int tries, CancellationToken cancel, DateTime? deadline = null)
    {
        Exception last = ProviderException.BadResponse();
        for (int attempt = 1; attempt <= tries; attempt++)
        {
            if (attempt > 1 && deadline is { } end && DateTime.UtcNow >= end) break;
            try
            {
                return new Attempt(await _transcribe(wav, request, cancel).ConfigureAwait(false), null);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                return new Attempt(null, new OperationCanceledException());
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                last = e;
                _logError?.Invoke($"Meeting chunk failed (try {attempt}): {Describe(e)}");
                if (e is ProviderException { Kind: ProviderErrorKind.MissingKey or ProviderErrorKind.DailyLimit }) break;
                if (attempt < tries)
                {
                    var wait = e is ProviderException { Kind: ProviderErrorKind.RateLimited } p
                        ? Math.Min(60, (p.RetryAfter ?? 10) + 1)
                        : attempt * 2.0;
                    if (deadline is { } limit) wait = Math.Min(wait, Math.Max(0, (limit - DateTime.UtcNow).TotalSeconds));
                    if (wait > 0) await _delay(TimeSpan.FromSeconds(wait)).ConfigureAwait(false);
                }
            }
        }
        return new Attempt(null, last);
    }

    /// <summary>Error bodies can echo a request, so the log gets the kind and status only.</summary>
    static string Describe(Exception e) => e is ProviderException p ? $"{p.Kind} {p.StatusCode}" : e.GetType().Name;

    void Finish(Attempt attempt, long id, AudioChunk chunk, Speaker speaker, ChunkRequest request)
    {
        // Abort has already marked this one as lost.
        if (attempt.Error is OperationCanceledException) return;
        var notify = new List<Action>();
        lock (_gate)
        {
            _inFlight.Remove(id);
            if (attempt.Transcript is { } transcript)
            {
                Accept(transcript, chunk.Map, chunk.VoicedSeconds, speaker, request, notify);
            }
            else
            {
                NoteDailyLimit(attempt.Error, notify);
                if (attempt.Error is ProviderException { Kind: ProviderErrorKind.MissingKey })
                {
                    MarkLost(chunk.Map, chunk.VoicedSeconds, speaker, notify);
                }
                else
                {
                    foreach (var lost in _hold.Add(new HeldChunk(chunk.Samples, chunk.Map, speaker, chunk.VoicedSeconds)))
                        MarkLost(lost.Map, lost.VoicedSeconds, lost.Speaker, notify);
                }
            }
        }
        foreach (var action in notify) action();
    }

    /// <summary>Call with the lock held.</summary>
    void Accept(DetailedTranscript transcript, TimeMap map, double voicedSeconds, Speaker speaker, ChunkRequest request, List<Action> notify)
    {
        var side = speaker == Speaker.You ? _micSide : _callSide;
        side.Language = transcript.Language ?? side.Language;
        // A forced language produced this if it isn't what auto-detect would be trusted to say on its own.
        // A hint that lost to auto-detect (the stream moved to English) doesn't count, so the sticky language lets go.
        var viaOpinion = (request.Hint != null && transcript.Language == request.Hint)
                         || (transcript.Language is { } l && l != "en" && request.Plan.Languages.Contains(l));
        side.Tracker.Observe(transcript.Language, viaOpinion, request.Plan.Languages);
        if (HallucinationFilter.IsLikelyHallucination(transcript.Text, voicedSeconds)) return;
        Append(ChunkLines.Lines(transcript, map, speaker, map.MeetingTime(0)), notify);
    }

    /// <summary>Call with the lock held.</summary>
    void Append(List<TranscriptLine> lines, List<Action> notify)
    {
        if (lines.Count == 0 || _meeting == null) return;
        _meeting.Lines = TranscriptMerge.Merge(_meeting.Lines, lines);
        var copy = _meeting.Copy();
        notify.Add(() => Updated?.Invoke(copy));
    }

    /// <summary>Call with the lock held.</summary>
    void MarkLost(TimeMap map, double seconds, Speaker speaker, List<Action>? notify = null)
    {
        _lostSeconds += seconds;
        Append([new TranscriptLine(map.MeetingTime(0), speaker, TranscriptFilter.LostMarker)], notify ?? []);
    }

    /// <summary>Call with the lock held.</summary>
    void NoteDailyLimit(Exception? error, List<Action> notify)
    {
        if (error is not ProviderException { Kind: ProviderErrorKind.DailyLimit } e || _dailyLimit != null) return;
        _dailyLimit = e;
        notify.Add(() => DailyLimitReached?.Invoke($"{e.Message} The rest of this meeting can't be transcribed until then."));
    }

    /// <summary>Second chance for chunks that failed. The marker appears only for what is still lost after this.</summary>
    async Task RetryHeldAsync(bool enabled)
    {
        List<HeldChunk> waiting;
        lock (_gate) waiting = _hold.TakeAll();
        if (waiting.Count == 0 || _settings is not { } settings) return;
        var deadline = DateTime.UtcNow.AddSeconds(FinalRetryLimit);
        foreach (var chunk in waiting)
        {
            var notify = new List<Action>();
            bool giveUp;
            lock (_gate) giveUp = !enabled || _dailyLimit != null || DateTime.UtcNow >= deadline;
            if (giveUp)
            {
                lock (_gate) MarkLost(chunk.Map, chunk.VoicedSeconds, chunk.Speaker, notify);
                foreach (var action in notify) action();
                continue;
            }
            var request = RequestFor(chunk.Speaker, settings);
            var wav = AudioMath.Wav(AudioMath.Normalized(chunk.FloatSamples));
            var result = await AttemptAsync(wav, request, FinalAttempts, CancellationToken.None, deadline).ConfigureAwait(false);
            lock (_gate)
            {
                if (result.Transcript is { } transcript)
                {
                    Accept(transcript, chunk.Map, chunk.VoicedSeconds, chunk.Speaker, request, notify);
                }
                else
                {
                    NoteDailyLimit(result.Error, notify);
                    MarkLost(chunk.Map, chunk.VoicedSeconds, chunk.Speaker, notify);
                }
            }
            foreach (var action in notify) action();
        }
    }

    /// <summary>Call with the lock held.</summary>
    void WriteGapNote()
    {
        if (_lostSeconds <= 0 || _meeting == null) return;
        var minutes = Math.Max(1, (int)Math.Round(_lostSeconds / 60, MidpointRounding.AwayFromZero));
        _meeting.Note = _dailyLimit is { } limit
            ? $"{limit.Message} About {minutes} min of speech could not be transcribed."
            : $"About {minutes} min of speech could not be transcribed after retrying.";
    }
}
