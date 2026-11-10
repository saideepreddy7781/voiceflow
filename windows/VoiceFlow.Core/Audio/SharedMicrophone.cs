namespace VoiceFlow.Core.Audio;

/// <summary>One open microphone. Samples are 16 kHz mono and arrive on the capture thread.</summary>
public interface IMicrophoneDevice
{
    string Id { get; }
    string Name { get; }
    event Action<float[]>? Samples;
    /// <summary>Raised on any thread when the device stops without <see cref="Stop"/> being called.</summary>
    event Action<Exception?>? Lost;
    void Start();
    void Stop();
}

/// <summary>Finds and opens microphones: WASAPI in the app, simulated devices in tests.</summary>
public interface IMicrophoneSource
{
    /// <summary>The device <see cref="Open"/> would use now, or null when there is no microphone.</summary>
    string? ResolveId(string? preferredId);
    /// <summary>Opens the preferred device, or the default one. The device is not started yet.</summary>
    IMicrophoneDevice Open(string? preferredId);
    /// <summary>Raised on any thread when devices were added or removed or the default changed.</summary>
    event Action? DevicesChanged;
}

public enum MicHealthStatus
{
    Idle,
    Healthy,
    /// <summary>The device delivers frames, but they are digital silence: muted in hardware or broken.</summary>
    DeadSignal,
    /// <summary>The device stopped delivering frames.</summary>
    Stalled,
    /// <summary>The device went away and a restart is under way.</summary>
    Recovering,
    /// <summary>Restarting keeps failing. Retries continue while anything is subscribed.</summary>
    Failed,
}

/// <param name="QuietRoom">Audio is arriving and is simply quiet. Healthy, nothing to report.</param>
public sealed record MicHealthSnapshot(MicHealthStatus Status, string DeviceName, bool QuietRoom, int RestartCount);

public sealed record MicDiagnostics(int Restarts, int DeviceSwitches, int Stalls, int DeadSignals, int FailedStarts, string CurrentDevice, string? LastFailure);

public sealed record SharedMicrophoneOptions
{
    public TimeSpan StallAfter { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan DeadAfter { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan QuietAfter { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>Wait before each retry of a failed start. The last value repeats.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15)];
    /// <summary>How often the health checks run. Null when the caller drives <see cref="SharedMicrophone.Poll"/>.</summary>
    public TimeSpan? PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// For speaker loopback. Windows delivers no frames while nothing is playing, so silence and missing frames say
    /// nothing about the device. Only a lost device or a change of default output restarts it.
    /// </summary>
    public static SharedMicrophoneOptions Loopback => new()
    {
        StallAfter = TimeSpan.MaxValue, DeadAfter = TimeSpan.MaxValue, QuietAfter = TimeSpan.MaxValue,
    };
}

/// <summary>
/// One microphone stream, shared by everything that wants to hear the user. The device starts with the first
/// subscriber and stops with the last, and each subscriber has its own buffer, so dictation can start and stop
/// in the middle of a meeting without touching the meeting's audio. Device loss, default-device changes and
/// stalls are handled here, once, for everyone.
/// </summary>
public sealed class SharedMicrophone : IDisposable
{
    /// <summary>Held while a device opens or closes. Never taken on the capture thread.</summary>
    readonly object _lifecycle = new();
    /// <summary>Held for fan-out and signal bookkeeping. Short, and taken after <see cref="_lifecycle"/>.</summary>
    readonly object _fanout = new();

    readonly IMicrophoneSource _source;
    readonly SharedMicrophoneOptions _options;
    readonly Func<TimeSpan> _clock;
    readonly Action<string>? _log;
    readonly List<MicSubscription> _subscribers = new();
    readonly Timer? _timer;

    IMicrophoneDevice? _device;
    string? _preferredId;
    string _deviceName = "";
    bool _disposed;

    // Bumped on every device change, so callbacks from a device that was replaced are ignored.
    int _generation;
    Exception? _lostError;
    volatile bool _lost;
    volatile bool _devicesChanged;

    // Written under _fanout on the capture thread.
    TimeSpan _lastFrameAt;
    TimeSpan? _deadSince;
    TimeSpan? _quietSince;
    long _framesThisStart;
    long _liveFramesThisStart;
    TimeSpan? _gapStartedAt;

    MicHealthStatus _status = MicHealthStatus.Idle;
    int _failedAttempts;
    TimeSpan _nextAttemptAt;
    bool _deadEpisode;
    bool _failedRaised;

    int _restarts, _switches, _stalls, _deadSignals, _failedStarts;
    string? _lastFailure;

    /// <summary>Raised outside all locks, on the health-check thread.</summary>
    public event Action<MicHealthSnapshot>? HealthChanged;

    public SharedMicrophone(IMicrophoneSource source, SharedMicrophoneOptions? options = null, Func<TimeSpan>? clock = null, Action<string>? log = null)
    {
        _source = source;
        _options = options ?? new SharedMicrophoneOptions();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        _clock = clock ?? (() => stopwatch.Elapsed);
        _log = log;
        _source.DevicesChanged += NotifyDevicesChanged;
        if (_options.PollInterval != null) _timer = new Timer(_ => PollFromTimer());
    }

    public MicHealthSnapshot Health
    {
        get
        {
            lock (_lifecycle) lock (_fanout) return Snapshot(_clock());
        }
    }

    public MicDiagnostics Diagnostics
    {
        get
        {
            lock (_lifecycle) return new MicDiagnostics(_restarts, _switches, _stalls, _deadSignals, _failedStarts, _deviceName, _lastFailure);
        }
    }

    public int SubscriberCount
    {
        get { lock (_fanout) return _subscribers.Count; }
    }

    /// <summary>
    /// Adds a subscriber, starting the device if nothing else is listening. Throws what the device throws when
    /// it cannot start. A device that is already running keeps running on the device it has.
    /// </summary>
    public MicSubscription Subscribe(string name, string? preferredDeviceId = null)
    {
        var notify = new List<Action>();
        MicSubscription subscription;
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var first = SubscriberCount == 0;
            if (first) _preferredId = preferredDeviceId;
            if (_device == null)
            {
                var now = _clock();
                try
                {
                    StartDevice(now);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    NoteFailure(e);
                    if (first) ResetToIdle();
                    throw;
                }
                SetStatus(MicHealthStatus.Healthy, notify);
                _failedAttempts = 0;
                _failedRaised = false;
            }
            subscription = new MicSubscription(this, name, _clock());
            lock (_fanout) _subscribers.Add(subscription);
            if (first) _timer?.Change(_options.PollInterval!.Value, _options.PollInterval!.Value);
        }
        Raise(notify);
        return subscription;
    }

    /// <summary>Called by a subscription when it ends.</summary>
    internal void Remove(MicSubscription subscription)
    {
        var notify = new List<Action>();
        lock (_lifecycle)
        {
            bool last;
            lock (_fanout)
            {
                if (!_subscribers.Remove(subscription)) return;
                last = _subscribers.Count == 0;
            }
            if (!last) return;
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            StopDevice();
            ResetToIdle();
            SetStatus(MicHealthStatus.Idle, notify);
        }
        Raise(notify);
    }

    public void NotifyDevicesChanged() => _devicesChanged = true;

    void PollFromTimer()
    {
        // A slow device open must not stack timer callbacks behind it.
        if (!Monitor.TryEnter(_lifecycle)) return;
        var notify = new List<Action>();
        try { PollLocked(notify); }
        catch (Exception e) when (e is not OutOfMemoryException) { _log?.Invoke($"Microphone health check failed: {e.GetType().Name}"); }
        finally { Monitor.Exit(_lifecycle); }
        Raise(notify);
    }

    /// <summary>Runs the health checks and any restart that is due.</summary>
    public void Poll()
    {
        var notify = new List<Action>();
        lock (_lifecycle) PollLocked(notify);
        Raise(notify);
    }

    void PollLocked(List<Action> notify)
    {
        if (_disposed || SubscriberCount == 0) return;
        var now = _clock();

        if (_lost)
        {
            var error = _lostError;
            _log?.Invoke($"Microphone lost: {error?.GetType().Name ?? "no error given"}");
            Restart(now, notify, MicHealthStatus.Recovering, switching: false);
            return;
        }

        if (_device == null)
        {
            // A plugged-in device should not have to wait out a long retry delay.
            if (_devicesChanged) _nextAttemptAt = now;
            _devicesChanged = false;
            if (now >= _nextAttemptAt) TryRestart(now, notify, switching: false);
            return;
        }

        if (_devicesChanged)
        {
            _devicesChanged = false;
            var target = _source.ResolveId(_preferredId);
            if (target == null || target != _device.Id)
            {
                _log?.Invoke("Microphone changed: following the current device");
                Restart(now, notify, MicHealthStatus.Recovering, switching: true);
                return;
            }
        }

        TimeSpan lastFrame;
        TimeSpan? deadSince;
        long frames, liveFrames;
        lock (_fanout)
        {
            lastFrame = _lastFrameAt;
            deadSince = _deadSince;
            frames = _framesThisStart;
            liveFrames = _liveFramesThisStart;
        }

        if (now - lastFrame >= _options.StallAfter)
        {
            if (now < _nextAttemptAt) return;
            _stalls++;
            _log?.Invoke("Microphone stalled: no audio arriving");
            SetStatus(MicHealthStatus.Stalled, notify);
            Restart(now, notify, MicHealthStatus.Stalled, switching: false, stalled: true);
            return;
        }

        if (deadSince is { } since && now - since >= _options.DeadAfter)
        {
            if (!_deadEpisode)
            {
                _deadEpisode = true;
                _deadSignals++;
                _log?.Invoke("Microphone delivers digital silence");
                SetStatus(MicHealthStatus.DeadSignal, notify);
                // One restart per episode, and only for a device that never produced sound since it started.
                // A live mic that went flat is muted or noise-gated, and restarting can't change that.
                if (liveFrames == 0) Restart(now, notify, MicHealthStatus.DeadSignal, switching: false);
            }
            return;
        }

        if (frames > 0 && deadSince == null)
        {
            _deadEpisode = false;
            _failedAttempts = 0;
            _failedRaised = false;
            SetStatus(MicHealthStatus.Healthy, notify);
        }
    }

    void Restart(TimeSpan now, List<Action> notify, MicHealthStatus during, bool switching, bool stalled = false)
    {
        lock (_fanout) _gapStartedAt ??= _lastFrameAt;
        StopDevice();
        _lost = false;
        _lostError = null;
        SetStatus(during == MicHealthStatus.DeadSignal ? MicHealthStatus.DeadSignal : MicHealthStatus.Recovering, notify);
        TryRestart(now, notify, switching, stalled);
    }

    void TryRestart(TimeSpan now, List<Action> notify, bool switching, bool stalled = false)
    {
        try
        {
            StartDevice(now);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            NoteFailure(e);
            RegisterFailure(now, notify);
            return;
        }
        if (switching) _switches++;
        else _restarts++;
        // A stalled device can restart cleanly and still deliver nothing, so it keeps counting as a failure
        // until frames arrive.
        if (stalled) RegisterFailure(now, notify, keepRunning: true);
    }

    void RegisterFailure(TimeSpan now, List<Action> notify, bool keepRunning = false)
    {
        _failedAttempts++;
        var delays = _options.RetryDelays;
        _nextAttemptAt = now + delays[Math.Min(_failedAttempts - 1, delays.Count - 1)];
        if (_failedAttempts < 2) return;
        SetStatus(MicHealthStatus.Failed, notify);
        if (_failedRaised) return;
        _failedRaised = true;
        if (keepRunning) _log?.Invoke("Microphone restarted but delivers no audio");
        MicSubscription[] subscribers;
        lock (_fanout) subscribers = _subscribers.ToArray();
        foreach (var subscriber in subscribers) notify.Add(subscriber.RaiseFailed);
    }

    void NoteFailure(Exception e)
    {
        _failedStarts++;
        _lastFailure = e.GetType().Name;
        _log?.Invoke($"Microphone could not start: {e.GetType().Name}");
    }

    /// <summary>Opens and starts a device. On failure <see cref="_device"/> stays null and the error propagates.</summary>
    void StartDevice(TimeSpan now)
    {
        var generation = Interlocked.Increment(ref _generation);
        var device = _source.Open(_preferredId);
        device.Samples += frames => OnFrames(generation, frames);
        device.Lost += error => OnLost(generation, error);
        device.Start();
        _device = device;
        _deviceName = device.Name;
        lock (_fanout)
        {
            _lastFrameAt = now;
            _deadSince = null;
            _quietSince = null;
            _framesThisStart = 0;
            _liveFramesThisStart = 0;
        }
    }

    void StopDevice()
    {
        var device = _device;
        _device = null;
        Interlocked.Increment(ref _generation);
        if (device == null) return;
        try { device.Stop(); }
        catch (Exception e) when (e is not OutOfMemoryException) { _log?.Invoke($"Microphone did not stop cleanly: {e.GetType().Name}"); }
    }

    void ResetToIdle()
    {
        _lost = false;
        _lostError = null;
        _devicesChanged = false;
        _failedAttempts = 0;
        _failedRaised = false;
        _deadEpisode = false;
        _nextAttemptAt = TimeSpan.Zero;
        lock (_fanout)
        {
            _deadSince = null;
            _quietSince = null;
            _gapStartedAt = null;
            _framesThisStart = 0;
            _liveFramesThisStart = 0;
        }
    }

    /// <summary>Capture thread: must stay short, and must not touch the device lifecycle.</summary>
    void OnFrames(int generation, float[] frames)
    {
        if (frames.Length == 0) return;
        lock (_fanout)
        {
            if (generation != Volatile.Read(ref _generation)) return;
            var now = _clock();
            if (_gapStartedAt is { } gapFrom)
            {
                _gapStartedAt = null;
                var gap = now - gapFrom;
                foreach (var subscriber in _subscribers) subscriber.AddGap(gap, now);
            }
            foreach (var subscriber in _subscribers) subscriber.Append(frames);
            _lastFrameAt = now;
            _framesThisStart += frames.Length;
            var dead = AudioMath.IsDigitalSilence(frames);
            _deadSince = dead ? _deadSince ?? now : null;
            if (!dead) _liveFramesThisStart += frames.Length;
            var quiet = !dead && AudioMath.Dbfs(AudioMath.Rms(frames)) < -45;
            _quietSince = quiet ? _quietSince ?? now : null;
        }
    }

    void OnLost(int generation, Exception? error)
    {
        if (generation != Volatile.Read(ref _generation)) return;
        _lostError = error;
        _lost = true;
    }

    void SetStatus(MicHealthStatus status, List<Action> notify)
    {
        if (_status == status) return;
        _status = status;
        MicHealthSnapshot snapshot;
        lock (_fanout) snapshot = Snapshot(_clock());
        notify.Add(() => HealthChanged?.Invoke(snapshot));
    }

    /// <summary>Call with both locks held.</summary>
    MicHealthSnapshot Snapshot(TimeSpan now)
    {
        var quiet = _status == MicHealthStatus.Healthy && _quietSince is { } since && now - since >= _options.QuietAfter;
        return new MicHealthSnapshot(_status, _deviceName, quiet, _restarts + _switches);
    }

    void Raise(List<Action> notify)
    {
        foreach (var action in notify)
        {
            try { action(); }
            catch (Exception e) when (e is not OutOfMemoryException) { _log?.Invoke($"Microphone listener failed: {e.GetType().Name}"); }
        }
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;
            _source.DevicesChanged -= NotifyDevicesChanged;
            _timer?.Dispose();
            StopDevice();
            ResetToIdle();
            lock (_fanout) _subscribers.Clear();
        }
    }
}

/// <summary>One listener on the shared microphone, with its own buffer.</summary>
public sealed class MicSubscription : IDisposable
{
    readonly SharedMicrophone _microphone;
    readonly SampleBuffer _buffer = new();
    readonly TimeSpan _startedAt;
    readonly object _gapLock = new();
    double _gapSeconds;
    bool _ended;

    public string Name { get; }
    public int SampleCount => _buffer.Count;

    /// <summary>Raised on the health-check thread when the microphone cannot be restarted.</summary>
    public event Action? Failed;

    internal MicSubscription(SharedMicrophone microphone, string name, TimeSpan startedAt)
    {
        _microphone = microphone;
        Name = name;
        _startedAt = startedAt;
    }

    internal void Append(float[] frames) => _buffer.Append(frames);

    internal void AddGap(TimeSpan gap, TimeSpan now)
    {
        // A subscriber that joined during the gap only missed the part after it joined.
        var missed = Math.Min(gap.TotalSeconds, (now - _startedAt).TotalSeconds);
        if (missed <= 0) return;
        lock (_gapLock) _gapSeconds += missed;
    }

    internal void RaiseFailed() => Failed?.Invoke();

    public float[] Samples() => _buffer.Snapshot();
    public float[] SamplesFrom(int start) => _buffer.CopyFrom(start);
    public float[] Drain() => _buffer.Drain();

    /// <summary>0...1 level of the last 50 ms, for the waveform.</summary>
    public float CurrentLevel() => AudioMath.Level(_buffer.Tail(800));

    /// <summary>Seconds this subscriber missed to device restarts since the last call.</summary>
    public double DrainGapSeconds()
    {
        lock (_gapLock)
        {
            var seconds = _gapSeconds;
            _gapSeconds = 0;
            return seconds;
        }
    }

    /// <summary>Ends this subscription and returns everything it collected.</summary>
    public float[] Stop()
    {
        End();
        return _buffer.Snapshot();
    }

    public void Dispose() => End();

    void End()
    {
        if (_ended) return;
        _ended = true;
        _microphone.Remove(this);
    }
}

/// <summary>A microphone with no hardware behind it, for tests and <c>--self-test</c>.</summary>
public sealed class SimulatedMicrophoneSource : IMicrophoneSource
{
    public sealed class Device(string id, string name, SimulatedMicrophoneSource source) : IMicrophoneDevice
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public bool Running { get; private set; }
        public event Action<float[]>? Samples;
        public event Action<Exception?>? Lost;

        public void Start()
        {
            if (source.StartError is { } error) throw error;
            Running = true;
        }

        public void Stop() => Running = false;

        public void Emit(float[] frames) => Samples?.Invoke(frames);

        public void SimulateLoss(Exception? error = null)
        {
            Running = false;
            Lost?.Invoke(error);
        }
    }

    public string? CurrentId { get; set; } = "mic-1";
    public string CurrentName { get; set; } = "Simulated microphone";
    public Exception? StartError { get; set; }
    public List<Device> Opened { get; } = new();
    public Device? Latest => Opened.Count == 0 ? null : Opened[^1];

    public event Action? DevicesChanged;

    public string? ResolveId(string? preferredId) => CurrentId;

    public IMicrophoneDevice Open(string? preferredId)
    {
        var device = new Device(CurrentId ?? throw new InvalidOperationException("no microphone"), CurrentName, this);
        Opened.Add(device);
        return device;
    }

    public void RaiseDevicesChanged() => DevicesChanged?.Invoke();
}
