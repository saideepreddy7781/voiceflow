using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using VoiceFlow.Core.Audio;
using VoiceFlow.Core.Meetings;

namespace VoiceFlow.Windows.Audio;

/// <summary>
/// WASAPI access for a shared capture. Every COM object is created and used from the thread pool (the MTA),
/// because the health checks restart devices off the UI thread and audio objects can't move between apartments.
/// </summary>
internal abstract class WasapiEndpointSource : IMMNotificationClient, IDisposable
{
    readonly object _gate = new();
    MMDeviceEnumerator? _notifier;
    bool _registered;

    public event Action? DevicesChanged;

    /// <summary>False when Windows refused the registration (no audio service, for example).</summary>
    internal bool NotificationsRegistered => _notifier != null;

    internal static T Mta<T>(Func<T> work) => Task.Run(work).GetAwaiter().GetResult();
    internal static void Mta(Action work) => Task.Run(work).GetAwaiter().GetResult();

    /// <summary>Which default-device change this capture follows.</summary>
    protected abstract bool Follows(DataFlow flow, Role role);

    /// <summary>Registered on first use so the app doesn't touch the audio service until it needs a device.</summary>
    internal void EnsureNotifications()
    {
        lock (_gate)
        {
            if (_registered) return;
            _registered = true;
            try
            {
                Mta(() =>
                {
                    _notifier = new MMDeviceEnumerator();
                    _notifier.RegisterEndpointNotificationCallback(this);
                });
            }
            catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidCastException or InvalidOperationException)
            {
                // Recovery still works through the Lost event and the stall check.
                Logger.Warn($"Device change notifications are unavailable (0x{e.HResult:X8})");
                _notifier?.Dispose();
                _notifier = null;
            }
        }
    }

    // These arrive on Windows' notification thread, which must not do audio work.
    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) => DevicesChanged?.Invoke();
    void IMMNotificationClient.OnDeviceAdded(string deviceId) => DevicesChanged?.Invoke();
    void IMMNotificationClient.OnDeviceRemoved(string deviceId) => DevicesChanged?.Invoke();
    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (Follows(flow, role)) DevicesChanged?.Invoke();
    }
    void IMMNotificationClient.OnPropertyValueChanged(string deviceId, PropertyKey key) { }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_notifier == null) return;
            var notifier = _notifier;
            _notifier = null;
            try
            {
                Mta(() =>
                {
                    notifier.UnregisterEndpointNotificationCallback(this);
                    notifier.Dispose();
                });
            }
            catch (Exception e) when (e is COMException or InvalidCastException or InvalidOperationException) { Logger.Warn($"Could not unregister device notifications: {e.Message}"); }
        }
    }
}

/// <summary>The microphone endpoints.</summary>
internal sealed class WasapiMicrophoneSource : WasapiEndpointSource, IMicrophoneSource
{
    protected override bool Follows(DataFlow flow, Role role) => flow == DataFlow.Capture && role == Role.Console;

    public string? ResolveId(string? preferredId) => Mta(() =>
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!string.IsNullOrEmpty(preferredId))
        {
            try
            {
                using var device = enumerator.GetDevice(preferredId);
                if (device.State == DeviceState.Active) return device.ID;
            }
            catch (COMException) { }   // unplugged: fall back to the default
        }
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console)) return null;
        using var fallback = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
        return fallback.ID;
    });

    public IMicrophoneDevice Open(string? preferredId)
    {
        EnsureNotifications();
        return Mta(() =>
        {
            try { return new WasapiMicrophone(AudioDevices.Microphone(preferredId)); }
            catch (Exception e) when (e is not OutOfMemoryException) { throw AudioCapture.Translate(e, loopback: false); }
        });
    }

    /// <summary>The self-test uses this to check Windows accepts the callback object.</summary>
    internal static void CheckNotificationInterface()
    {
        var client = new WasapiMicrophoneSource();
        var pointer = Marshal.GetComInterfaceForObject(client, typeof(IMMNotificationClient));
        Marshal.Release(pointer);
    }
}

/// <summary>What the PC is playing: the default output, followed when it changes.</summary>
internal sealed class WasapiLoopbackSource : WasapiEndpointSource, IMicrophoneSource
{
    protected override bool Follows(DataFlow flow, Role role) => flow == DataFlow.Render && role == Role.Multimedia;

    public string? ResolveId(string? preferredId) => Mta(() =>
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return null;
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return device.ID;
    });

    public IMicrophoneDevice Open(string? preferredId)
    {
        EnsureNotifications();
        return Mta(() =>
        {
            try { return new WasapiMicrophone(AudioDevices.DefaultOutput(), loopback: true); }
            catch (Exception e) when (e is not OutOfMemoryException) { throw AudioCapture.Translate(e, loopback: true); }
        });
    }
}

/// <summary>
/// One WASAPI capture stream, microphone or loopback, converted to 16 kHz mono. Takes ownership of the device.
///
/// Windows ends a shared-mode stream when another app takes the device in exclusive mode, when the device's format
/// is changed in Sound settings, or when the device goes away. The audio session reports which of these happened,
/// and any of them raises <see cref="Lost"/>, so <see cref="SharedMicrophone"/> rebuilds the capture on the device's
/// new format, or says why it can't.
/// </summary>
internal sealed class WasapiMicrophone(MMDevice device, bool loopback = false) : IMicrophoneDevice, IAudioSessionEventsHandler
{
    WasapiCapture? _capture;
    AudioSessionControl? _session;
    readonly CaptureConverter _converter = new(oneChannel: !loopback);
    CaptureFormat _format;
    volatile bool _stopping;
    int _lostRaised;

    public string Id { get; } = device.ID;
    public string Name { get; } = device.FriendlyName;

    public event Action<float[]>? Samples;
    public event Action<Exception?>? Lost;

    public void Start() => WasapiMicrophoneSource.Mta(() =>
    {
        try
        {
            _capture = loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 100);
            _format = FormatOf(_capture.WaveFormat);
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += OnStopped;
            _capture.StartRecording();
            WatchSession();
            Logger.Info($"{(loopback ? "Loopback" : "Microphone")} started ({_format.SampleRate} Hz, {_format.Channels} ch, {_format.BitsPerSample}-bit{(_format.IsFloat ? " float" : "")})");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Release();
            throw AudioCapture.Translate(e, loopback);
        }
    });

    internal static CaptureFormat FormatOf(WaveFormat format) => new(
        format.SampleRate, format.Channels, format.BitsPerSample,
        format.Encoding == WaveFormatEncoding.IeeeFloat
        || (format is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT));

    /// <summary>Listens to this process's session on the device, which carries the reason a stream was ended.</summary>
    void WatchSession()
    {
        try
        {
            _session = device.AudioSessionManager.AudioSessionControl;
            _session.RegisterEventClient(this);
        }
        catch (Exception e) when (e is COMException or InvalidCastException or InvalidOperationException or NotImplementedException)
        {
            // Loss still arrives through RecordingStopped and the stall check.
            Logger.Warn($"Audio session events are unavailable (0x{e.HResult:X8})");
            _session = null;
        }
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        try
        {
            if (e.BytesRecorded <= 0) return;
            // The format is read for every buffer, so the converter follows any change.
            var format = sender is IWaveIn capture ? FormatOf(capture.WaveFormat) : _format;
            var mono = _converter.Convert(e.Buffer.AsSpan(0, e.BytesRecorded), format);
            if (mono.Length > 0) Samples?.Invoke(mono);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Logger.Error("Audio conversion failed", ex);
        }
    }

    void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (_stopping) return;
        if (e.Exception != null) Logger.Warn($"Windows stopped the {(loopback ? "loopback" : "microphone")}: {e.Exception.GetType().Name} (0x{e.Exception.HResult:X8})");
        RaiseLost(e.Exception == null ? null : AudioCapture.Translate(e.Exception, loopback));
    }

    void RaiseLost(Exception? error)
    {
        if (_stopping || Interlocked.Exchange(ref _lostRaised, 1) == 1) return;
        Lost?.Invoke(error);
    }

    // Session events arrive on a Windows thread; they only flag the loss.
    void IAudioSessionEventsHandler.OnSessionDisconnected(AudioSessionDisconnectReason reason)
    {
        Logger.Warn($"Windows ended the {(loopback ? "loopback" : "microphone")} session: {reason}");
        RaiseLost(AudioCapture.Disconnected(reason, loopback));
    }
    void IAudioSessionEventsHandler.OnVolumeChanged(float volume, bool isMuted) { }
    void IAudioSessionEventsHandler.OnDisplayNameChanged(string displayName) { }
    void IAudioSessionEventsHandler.OnIconPathChanged(string iconPath) { }
    void IAudioSessionEventsHandler.OnChannelVolumeChanged(uint channelCount, IntPtr newVolumes, uint channelIndex) { }
    void IAudioSessionEventsHandler.OnGroupingParamChanged(ref Guid groupingId) { }
    void IAudioSessionEventsHandler.OnStateChanged(AudioSessionState state) { }

    public void Stop() => WasapiMicrophoneSource.Mta(() =>
    {
        _stopping = true;
        Release();
    });

    void Release()
    {
        if (_session != null)
        {
            try
            {
                _session.UnRegisterEventClient(this);
                _session.Dispose();
            }
            catch (Exception e) when (e is COMException or InvalidCastException or InvalidOperationException) { Logger.Warn($"Stopping session events: {e.GetType().Name}"); }
            _session = null;
        }
        if (_capture != null)
        {
            _capture.DataAvailable -= OnData;
            _capture.RecordingStopped -= OnStopped;
            try
            {
                _capture.StopRecording();
                _capture.Dispose();
            }
            catch (Exception e) when (e is COMException or InvalidOperationException or UnauthorizedAccessException) { Logger.Warn($"Stopping the capture: {e.GetType().Name}"); }
            _capture = null;
        }
        device.Dispose();
    }
}

/// <summary>Where the app's one shared microphone lives.</summary>
internal static class MicrophoneHub
{
    static readonly Lazy<SharedMicrophone> Instance = new(() => new SharedMicrophone(new WasapiMicrophoneSource(), log: Logger.Info));

    public static SharedMicrophone Shared => Instance.Value;
}

/// <summary>The call's audio, with the same recovery as the microphone. Only meetings subscribe to it.</summary>
internal static class LoopbackHub
{
    static readonly Lazy<SharedMicrophone> Instance = new(() => new SharedMicrophone(new WasapiLoopbackSource(), SharedMicrophoneOptions.Loopback,
                                                                                     log: m => Logger.Info(m.Replace("Microphone", "Meeting audio", StringComparison.Ordinal))));

    public static SharedMicrophone Shared => Instance.Value;
}

/// <summary>One side of a meeting: a subscriber to a shared capture.</summary>
internal sealed class SharedMicMeetingSource(SharedMicrophone microphone, string? deviceId, string name = "meeting") : IMeetingAudioSource
{
    MicSubscription? _subscription;

    public void StartCapture() => _subscription = microphone.Subscribe(name, deviceId);
    public void StopCapture() => _subscription?.Dispose();
    public float[] DrainSamples() => _subscription?.Drain() ?? [];
    public double DrainGapSeconds() => _subscription?.DrainGapSeconds() ?? 0;
    // Too little audio since the last drain reads as silence and would make the meter flicker.
    public float? CurrentLevel() => _subscription is { SampleCount: >= 800 } s ? s.CurrentLevel() : null;
    public void Dispose() => _subscription?.Dispose();
}
