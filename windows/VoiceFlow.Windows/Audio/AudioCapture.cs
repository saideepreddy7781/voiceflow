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

    /// <summary>The default speakers or headset, for loopback. The caller disposes it.</summary>
    public static MMDevice DefaultOutput()
    {
        using var enumerator = new MMDeviceEnumerator();
        if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            throw new AudioCaptureException("No speakers or headphones are active, so the meeting audio cannot be recorded.");
        return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    }
}

internal sealed class AudioCaptureException(string message, Exception? inner = null) : Exception(message, inner);

internal static class AudioCapture
{
    /// <summary>Turns a start failure into the message the user sees.</summary>
    internal static AudioCaptureException Translate(Exception e, bool loopback)
    {
        if (e is AudioCaptureException known) return known;
        // NAudio raises HRESULTs through Marshal.ThrowExceptionForHR, which turns E_ACCESSDENIED (the microphone
        // privacy switch) into UnauthorizedAccessException, not COMException.
        if (!loopback && (e is UnauthorizedAccessException || (uint)e.HResult == 0x80070005))
            return new AudioCaptureException(
                "Microphone access is off. Turn on Settings → Privacy & security → Microphone → Let desktop apps access your microphone.", e);
        // Other HRESULTs map to many exception types (COMException, ArgumentException, NotImplementedException,
        // TimeoutException…); any of them means the device could not start.
        return new AudioCaptureException($"The {(loopback ? "speaker audio" : "microphone")} could not start: {e.Message}", e);
    }
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
