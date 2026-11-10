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
/// Records a meeting: your microphone plus the meeting audio, transcribed while it happens.
///
/// Memory stays small for long meetings: audio is drained from both sources every second (the app calls
/// <see cref="Tick"/>) and only the chunk still being collected (at most 45 s) is kept. Each chunk is mixed
/// into one stream, so a meeting costs one upload per chunk, and Whisper's timed segments are labelled
/// You/Others by comparing the two sources over each segment.
/// </summary>
public sealed class MeetingRecorder
{
    public sealed record Settings(string ApiKey, string Model, string? Language, string? ParallelLanguage,
                                  IReadOnlyList<string> Languages, string? Prompt, string? MicDeviceId, bool RecordSystemAudio);

    public delegate Task<DetailedTranscript> TranscribeFunc(byte[] wav, Settings settings, CancellationToken cancel);

    readonly Func<Settings, IMeetingAudioSource> _makeMic;
    readonly Func<IMeetingAudioSource?> _makeSystem;
    readonly TranscribeFunc _transcribe;
    readonly Func<TimeSpan, Task> _delay;
    readonly object _gate = new();

    IMeetingAudioSource? _mic;
    IMeetingAudioSource? _system;
    Settings? _settings;
    CancellationTokenSource _cancel = new();

    // Audio not yet sent, aligned sample-for-sample.
    readonly List<float> _pendingMic = new();
    readonly List<float> _pendingSystem = new();
    readonly List<float> _queuedMic = new();
    readonly List<float> _queuedSystem = new();
    /// <summary>Samples already sent, i.e. the start time of the pending chunk.</summary>
    int _sentSamples;
    /// <summary>Appends each chunk's lines in order, exactly once.</summary>
    Task _appendChain = Task.CompletedTask;
    bool _heardSpeech;
    Meeting? _meeting;

    public bool IsRecording { get; private set; }
    public string? LastError { get; private set; }
    /// <summary>Live input levels (0...1) for the Meetings page.</summary>
    public float MicLevel { get; private set; }
    public float SystemLevel { get; private set; }
    /// <summary>Seconds in a row the meeting audio has been digital silence.</summary>
    public int SystemSilentSeconds { get; private set; }
    /// <summary>Shorter gaps are normal: a device restarting, a driver catching up.</summary>
    const double MinimumReportedGap = 2;
    /// <summary>Called whenever new transcript lines arrive, with a copy of the meeting.</summary>
    public event Action<Meeting>? Updated;

    /// <param name="logError">Receives failures for the log. Never given transcript text.</param>
    public MeetingRecorder(Func<Settings, IMeetingAudioSource> makeMic, Func<IMeetingAudioSource?> makeSystem,
                           TranscribeFunc? transcribe = null, Func<TimeSpan, Task>? delay = null, Action<string>? logError = null)
    {
        _makeMic = makeMic;
        _makeSystem = makeSystem;
        _transcribe = transcribe ?? ((wav, s, ct) => new GroqTranscriber(s.ApiKey, s.Model)
            .TranscribeResolvingAsync(wav, s.Language, s.ParallelLanguage, s.Languages, s.Prompt, sequential: true, cancel: ct));
        _delay = delay ?? (t => Task.Delay(t));
        _logError = logError;
    }

    readonly Action<string>? _logError;

    public Meeting? Snapshot()
    {
        lock (_gate) return _meeting?.Copy();
    }

    /// <summary>Meeting chunks: 20–45 s, cut at a pause, no overlap (pauses separate sentences).</summary>
    internal static SegmentCutter MakeCutter() => new()
    {
        MinimumSeconds = 20, MaximumSeconds = 45, PauseSeconds = 0.6, OverlapSeconds = 0,
    };

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
        lock (_gate) _meeting = m;
        _settings = settings;
        _cancel = new CancellationTokenSource();
        IsRecording = true;
        _heardSpeech = false;
        SystemSilentSeconds = 0;
        _pendingMic.Clear(); _pendingSystem.Clear(); _queuedMic.Clear(); _queuedSystem.Clear();
        _sentSamples = 0;
        _appendChain = Task.CompletedTask;
    }

    /// <summary>Stops recording and returns the meeting once every chunk is transcribed.</summary>
    public async Task<Meeting?> StopAsync()
    {
        if (!IsRecording) return Snapshot();
        Tick(final: true);
        StopSources();
        IsRecording = false;
        MicLevel = 0;
        SystemLevel = 0;
        lock (_gate) if (_meeting != null) _meeting.End = DateTimeOffset.Now;
        await _appendChain;
        lock (_gate)
        {
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

    /// <summary>Stops without waiting for uploads (app exit). Pending chunks are cancelled.</summary>
    public void Abort()
    {
        if (!IsRecording) return;
        _cancel.Cancel();
        StopSources();
        IsRecording = false;
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
        if (_system != null)
        {
            var newSystem = _system.DrainSamples();
            SystemLevel = AudioMath.Level(newSystem.AsSpan(Math.Max(0, newSystem.Length - 1_600)));
            SystemSilentSeconds = newSystem.All(v => v == 0) ? SystemSilentSeconds + 1 : 0;
            _queuedSystem.AddRange(newSystem);
            // Keep the two sources aligned. Whichever stalls (loopback delivers nothing while nothing plays,
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
            _pendingMic.AddRange(Take(_queuedMic, n));
            _pendingSystem.AddRange(Take(_queuedSystem, n));
        }
        else
        {
            var n = _queuedMic.Count;
            _pendingMic.AddRange(Take(_queuedMic, n));
            _pendingSystem.AddRange(new float[n]);
        }

        // Send every complete chunk (more than one if audio piled up), never more than 45 s per upload.
        while (true)
        {
            var mixed = SpeakerAttribution.Mix(_pendingMic, _pendingSystem);
            var cutter = MakeCutter();
            var range = cutter.NextCut(mixed);
            if (range == null && final && mixed.Length > 0) range = 0..mixed.Length;   // what's left at Stop (< 45 s)
            if (range is not { } r) break;
            int start = r.Start.Value, end = r.End.Value;
            Send(mixed[start..end], _pendingMic.GetRange(start, end - start).ToArray(), _pendingSystem.GetRange(start, end - start).ToArray());
            _pendingMic.RemoveRange(0, end);
            _pendingSystem.RemoveRange(0, end);
            _sentSamples += end;
            if (_pendingMic.Count == 0) break;
        }
    }

    /// <summary>Samples of microphone timeline so far: sent, pending and queued.</summary>
    int MicTimeline => _sentSamples + _pendingMic.Count + _queuedMic.Count;

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

    void Send(float[] mixed, float[] mic, float[] system)
    {
        if (_settings is not { } settings) return;
        var chunkStart = _sentSamples / AudioMath.SampleRate;
        if (AudioMath.IsSilent(mixed, minimumVoicedSeconds: 0.5)) return;
        _heardSpeech = true;
        var wav = AudioMath.Wav(AudioMath.Normalized(mixed));
        var cancel = _cancel.Token;
        var transcribe = _transcribe;
        var delay = _delay;
        var log = _logError;
        var chunk = Task.Run(async () =>
        {
            // Up to 4 tries: rate limits are waited out, other errors retried after a short pause.
            for (int attempt = 1; attempt <= 4; attempt++)
            {
                try
                {
                    var result = await transcribe(wav, settings, cancel).ConfigureAwait(false);
                    if (HallucinationFilter.IsLikelyHallucination(result.Text, AudioMath.VoicedSeconds(mixed))) return new List<TranscriptLine>();
                    return SpeakerAttribution.Lines(result, chunkStart, mic, system);
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    return new List<TranscriptLine>();
                }
                catch (Exception e)
                {
                    log?.Invoke($"Meeting chunk failed (try {attempt}): {e.GetType().Name}: {e.Message}");
                    if (e is ProviderException { Kind: ProviderErrorKind.MissingKey }) break;
                    if (attempt < 4)
                    {
                        var wait = e is ProviderException { Kind: ProviderErrorKind.RateLimited } p
                            ? Math.Min(60, (p.RetryAfter ?? 10) + 1)
                            : attempt * 2.0;
                        await delay(TimeSpan.FromSeconds(wait)).ConfigureAwait(false);
                    }
                }
            }
            return new List<TranscriptLine> { new(chunkStart, Speaker.Others, "[Part of the meeting could not be transcribed]") };
        });
        _appendChain = AppendAfter(_appendChain, chunk);
    }

    async Task AppendAfter(Task previous, Task<List<TranscriptLine>> chunk)
    {
        await previous;
        var lines = await chunk;
        if (lines.Count == 0) return;
        Meeting? copy;
        lock (_gate)
        {
            if (_meeting == null) return;
            _meeting.Lines.AddRange(lines);
            copy = _meeting.Copy();
        }
        Updated?.Invoke(copy);
    }
}
