using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using VoiceFlow.Core.Audio;
using VoiceFlow.Core.Meetings;

namespace VoiceFlow.Windows.Audio;

/// <summary>
/// WASAPI access for the shared microphone. Every COM object is created and used from the thread pool (the MTA),
/// because the health checks restart devices off the UI thread and audio objects can't move between apartments.
/// </summary>
internal sealed class WasapiMicrophoneSource : IMicrophoneSource, IMMNotificationClient, IDisposable
{
    readonly object _gate = new();
    MMDeviceEnumerator? _notifier;
    bool _registered;

    public event Action? DevicesChanged;

    /// <summary>False when Windows refused the registration (no audio service, for example).</summary>
    internal bool NotificationsRegistered => _notifier != null;

    internal static T Mta<T>(Func<T> work) => Task.Run(work).GetAwaiter().GetResult();
    internal static void Mta(Action work) => Task.Run(work).GetAwaiter().GetResult();

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

    /// <summary>Registered on first use so the app doesn't touch the audio service until it needs a microphone.</summary>
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
        if (flow == DataFlow.Capture && role == Role.Console) DevicesChanged?.Invoke();
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

/// <summary>One WASAPI capture stream converted to 16 kHz mono. Takes ownership of the device.</summary>
internal sealed class WasapiMicrophone(MMDevice device) : IMicrophoneDevice
{
    WasapiCapture? _capture;
    StreamingResampler? _resampler;
    int _bits;
    bool _isFloat;
    volatile bool _stopping;

    public string Id { get; } = device.ID;
    public string Name { get; } = device.FriendlyName;

    public event Action<float[]>? Samples;
    public event Action<Exception?>? Lost;

    public void Start() => WasapiMicrophoneSource.Mta(() =>
    {
        try
        {
            _capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 100);
            var format = _capture.WaveFormat;
            _bits = format.BitsPerSample;
            _isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                       || (format is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
            _resampler = new StreamingResampler(format.SampleRate, format.Channels);
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += OnStopped;
            _capture.StartRecording();
            Logger.Info($"Microphone started ({format.SampleRate} Hz, {format.Channels} ch, {_bits}-bit{(_isFloat ? " float" : "")})");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Release();
            throw AudioCapture.Translate(e, loopback: false);
        }
    });

    void OnData(object? sender, WaveInEventArgs e)
    {
        try
        {
            if (e.BytesRecorded <= 0 || _resampler == null) return;
            var floats = PcmDecoder.ToFloat(e.Buffer.AsSpan(0, e.BytesRecorded), _bits, _isFloat);
            var mono = _resampler.Process(floats);
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
        if (e.Exception != null) Logger.Warn($"Windows stopped the microphone: {e.Exception.GetType().Name}");
        Lost?.Invoke(e.Exception);
    }

    public void Stop() => WasapiMicrophoneSource.Mta(() =>
    {
        _stopping = true;
        Release();
    });

    void Release()
    {
        if (_capture != null)
        {
            _capture.DataAvailable -= OnData;
            _capture.RecordingStopped -= OnStopped;
            try
            {
                _capture.StopRecording();
                _capture.Dispose();
            }
            catch (Exception e) when (e is COMException or InvalidOperationException or UnauthorizedAccessException) { Logger.Warn($"Stopping the microphone: {e.GetType().Name}"); }
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

/// <summary>Your microphone in a meeting: one subscriber to the shared capture.</summary>
internal sealed class SharedMicMeetingSource(SharedMicrophone microphone, string? deviceId) : IMeetingAudioSource
{
    MicSubscription? _subscription;

    public void StartCapture() => _subscription = microphone.Subscribe("meeting", deviceId);
    public void StopCapture() => _subscription?.Dispose();
    public float[] DrainSamples() => _subscription?.Drain() ?? [];
    public double DrainGapSeconds() => _subscription?.DrainGapSeconds() ?? 0;
    public void Dispose() => _subscription?.Dispose();
}
