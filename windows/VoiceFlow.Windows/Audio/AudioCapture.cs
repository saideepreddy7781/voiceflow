using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using VoiceFlow.Core.Audio;
using VoiceFlow.Core.Meetings;

namespace VoiceFlow.Windows.Audio;

internal sealed record AudioDevice(string Id, string Name);

internal static class AudioDevices
{
    /// <summary>Active capture endpoints. Empty (not an exception) when the audio service is unavailable.</summary>
    public static List<AudioDevice> Inputs() => List(DataFlow.Capture);

    public static List<AudioDevice> Outputs() => List(DataFlow.Render);

    static List<AudioDevice> List(DataFlow flow)
    {
        var result = new List<AudioDevice>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device) result.Add(new AudioDevice(device.ID, device.FriendlyName));
            }
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidCastException)
        {
            Logger.Warn($"Audio devices could not be listed (0x{e.HResult:X8})");
        }
        return result;
    }

    /// <summary>The chosen microphone, or the default one. The caller disposes it.</summary>
    public static MMDevice Microphone(string? id)
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!string.IsNullOrEmpty(id))
        {
            try
            {
                var device = enumerator.GetDevice(id);
                if (device.State == DeviceState.Active) return device;
                device.Dispose();
            }
            catch (COMException) { }   // unplugged since it was chosen: fall back to the default
        }
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console))
            throw new AudioCaptureException("No microphone was found. Plug one in or choose another in Settings.");
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
    }
}

internal sealed class AudioCaptureException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// WASAPI capture (shared mode) converted to 16 kHz mono floats. Used for the microphone and, with
/// <c>loopback</c>, for what the PC is playing (the other side of a call). Audio arrives on NAudio's capture
/// thread and goes into a thread-safe buffer.
/// </summary>
internal sealed class AudioCapture : IDisposable
{
    readonly SampleBuffer _buffer = new();
    readonly bool _loopback;
    readonly string? _deviceId;
    WasapiCapture? _capture;
    MMDevice? _device;
    StreamingResampler? _resampler;
    int _bits;
    bool _isFloat;

    public bool IsRecording { get; private set; }
    public string DeviceName { get; private set; } = "";

    /// <summary>Raised on the UI thread when Windows stops the stream (device unplugged, exclusive use).</summary>
    public event Action<Exception?>? Stopped;

    public AudioCapture(string? deviceId, bool loopback = false)
    {
        _deviceId = deviceId;
        _loopback = loopback;
    }

    public void Start()
    {
        if (IsRecording) return;
        _buffer.Reset();
        try
        {
            if (_loopback)
            {
                using var enumerator = new MMDeviceEnumerator();
                if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                    throw new AudioCaptureException("No speakers or headphones are active, so the meeting audio cannot be recorded.");
                _device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _capture = new WasapiLoopbackCapture(_device);
            }
            else
            {
                _device = AudioDevices.Microphone(_deviceId);
                _capture = new WasapiCapture(_device, useEventSync: true, audioBufferMillisecondsLength: 100);
            }
            DeviceName = _device.FriendlyName;
            var format = _capture.WaveFormat;
            _bits = format.BitsPerSample;
            _isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                       || (format is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
            _resampler = new StreamingResampler(format.SampleRate, format.Channels);
            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += OnStopped;
            _capture.StartRecording();
            IsRecording = true;
            Logger.Info($"{(_loopback ? "Loopback" : "Microphone")} capture started ({format.SampleRate} Hz, {format.Channels} ch, {_bits}-bit{(_isFloat ? " float" : "")})");
        }
        catch (AudioCaptureException)
        {
            Cleanup();
            throw;
        }
        // NAudio raises HRESULTs through Marshal.ThrowExceptionForHR, which turns E_ACCESSDENIED (the microphone
        // privacy switch) into UnauthorizedAccessException, not COMException.
        catch (Exception e) when (!_loopback && (e is UnauthorizedAccessException || (uint)e.HResult == 0x80070005))
        {
            Cleanup();
            throw new AudioCaptureException(
                "Microphone access is off. Turn on Settings → Privacy & security → Microphone → Let desktop apps access your microphone.", e);
        }
        // Other HRESULTs map to many exception types (COMException, ArgumentException, NotImplementedException,
        // TimeoutException…); any of them means the device could not start.
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Cleanup();
            throw new AudioCaptureException($"The {(_loopback ? "speaker audio" : "microphone")} could not start: {e.Message}", e);
        }
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        // Capture thread: convert and store, nothing else.
        try
        {
            if (e.BytesRecorded <= 0 || _resampler == null) return;
            var floats = PcmDecoder.ToFloat(e.Buffer.AsSpan(0, e.BytesRecorded), _bits, _isFloat);
            var mono = _resampler.Process(floats);
            if (mono.Length > 0) _buffer.Append(mono);
        }
        catch (Exception ex)
        {
            Logger.Error("Audio conversion failed", ex);
        }
    }

    void OnStopped(object? sender, StoppedEventArgs e)
    {
        var wasRecording = IsRecording;
        IsRecording = false;
        if (e.Exception != null) Logger.Error("Audio capture stopped by Windows", e.Exception);
        if (wasRecording) Stopped?.Invoke(e.Exception);
    }

    /// <summary>Stops capture and returns everything recorded.</summary>
    public float[] Stop()
    {
        if (_capture != null)
        {
            IsRecording = false;
            try { _capture.StopRecording(); }
            catch (Exception e) when (e is COMException or InvalidOperationException or UnauthorizedAccessException) { Logger.Warn($"StopRecording: {e.Message}"); }
        }
        Cleanup();
        return _buffer.Snapshot();
    }

    public float[] Samples() => _buffer.Snapshot();
    public float[] SamplesFrom(int start) => _buffer.CopyFrom(start);
    public float[] Drain() => _buffer.Drain();
    public int SampleCount => _buffer.Count;

    /// <summary>0...1 level of the last 50 ms, for the waveform.</summary>
    public float CurrentLevel() => AudioMath.Level(_buffer.Tail(800));

    void Cleanup()
    {
        if (_capture != null)
        {
            _capture.DataAvailable -= OnData;
            _capture.RecordingStopped -= OnStopped;
            _capture.Dispose();
            _capture = null;
        }
        _device?.Dispose();
        _device = null;
    }

    public void Dispose()
    {
        if (IsRecording) Stop();
        else Cleanup();
    }
}

/// <summary>The microphone or the speaker loopback as a meeting source.</summary>
internal sealed class CaptureMeetingSource(string? deviceId, bool loopback) : IMeetingAudioSource
{
    readonly AudioCapture _capture = new(deviceId, loopback);

    public void StartCapture() => _capture.Start();
    public void StopCapture() => _capture.Stop();
    public float[] DrainSamples() => _capture.Drain();
    public void Dispose() => _capture.Dispose();
}

/// <summary>
/// Mutes speaker output during a dictation and restores it afterwards. Leaves output alone if it was already
/// muted, and a marker file makes sure a crash cannot leave the PC silent.
/// </summary>
internal sealed class OutputMuter(string dataFolder)
{
    string Marker => Path.Combine(dataFolder, "output-muted.flag");
    bool _muted;

    public void Mute()
    {
        if (_muted) return;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return;
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (device.AudioEndpointVolume.Mute) return;
            File.WriteAllText(Marker, device.ID);
            device.AudioEndpointVolume.Mute = true;
            _muted = true;
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Could not mute output: {e.Message}");
        }
    }

    public void Restore()
    {
        if (!_muted) return;
        _muted = false;
        Unmute();
    }

    /// <summary>Call at launch: unmutes output if the previous run quit while it was muted.</summary>
    public void RecoverAfterCrash()
    {
        if (File.Exists(Marker)) Unmute();
    }

    void Unmute()
    {
        try
        {
            var id = File.Exists(Marker) ? File.ReadAllText(Marker).Trim() : "";
            using var enumerator = new MMDeviceEnumerator();
            MMDevice? device = null;
            try { device = id.Length > 0 ? enumerator.GetDevice(id) : null; }
            catch (COMException) { }
            device ??= enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : null;
            using (device)
            {
                if (device != null) device.AudioEndpointVolume.Mute = false;
            }
            File.Delete(Marker);
        }
        catch (Exception e) when (e is COMException or IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Could not restore output: {e.Message}");
        }
    }
}
