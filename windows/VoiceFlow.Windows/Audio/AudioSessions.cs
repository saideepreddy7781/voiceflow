using System.Runtime.InteropServices;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using VoiceFlow.Core.Meetings;

namespace VoiceFlow.Windows.Audio;

/// <summary>
/// Which apps are recording, from the audio sessions on every active capture endpoint (IAudioSessionManager2 and
/// IAudioSessionEnumerator, through NAudio). A session is active while one of its streams runs, so this follows a
/// call app taking and letting go of the microphone within a poll. Only process IDs and states are read.
/// </summary>
internal static class AudioSessions
{
    /// <summary>Sessions on every active capture endpoint. Null when the audio service could not be asked.</summary>
    public static List<CaptureSession>? CaptureSessions() => Read(DataFlow.Capture, out _);

    /// <summary>
    /// The same enumeration on endpoints of <paramref name="flow"/>, with the number of endpoints, for the self-test.
    /// </summary>
    internal static List<CaptureSession>? Read(DataFlow flow, out int endpoints)
    {
        var count = 0;
        try
        {
            var sessions = WasapiEndpointSource.Mta(() =>
            {
                var list = new List<CaptureSession>();
                using var enumerator = new MMDeviceEnumerator();
                foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
                {
                    using (device)
                    {
                        count++;
                        var manager = device.AudioSessionManager;
                        try
                        {
                            var all = manager.Sessions;
                            for (int i = 0; i < all.Count; i++)
                            {
                                using var session = all[i];
                                list.Add(new CaptureSession(session.GetProcessID, session.State == AudioSessionState.AudioSessionStateActive));
                            }
                        }
                        finally { manager.Dispose(); }
                    }
                }
                return list;
            });
            endpoints = count;
            return sessions;
        }
        catch (Exception e) when (e is COMException or UnauthorizedAccessException or InvalidCastException or InvalidOperationException or NotImplementedException)
        {
            Logger.Warn($"Audio sessions could not be read (0x{e.HResult:X8})");
            endpoints = count;
            return null;
        }
    }

    /// <summary>
    /// Whether another app is recording from any microphone: a call app holding it. VoiceFlow's own capture does not
    /// count. Falls back to the privacy indicator's registry data when the sessions can't be read. Null when neither works.
    /// </summary>
    public static bool? AnotherAppIsRecording() =>
        global::VoiceFlow.Core.Meetings.CaptureSessions.AnotherAppIsRecording(CaptureSessions(), (uint)Environment.ProcessId) ?? RegistryInUse();

    /// <summary>
    /// The registry data behind the microphone indicator in the taskbar. Weaker than the sessions: it lags, and an app
    /// that crashed while recording can stay "in use" until it runs again.
    /// </summary>
    internal static bool? RegistryInUse()
    {
        const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Root);
            if (key == null) return null;
            var self = AppPaths.ExePath.Replace('\\', '#');
            using var nonPackaged = key.OpenSubKey("NonPackaged");
            return AnyInUse(key, null) || (nonPackaged != null && AnyInUse(nonPackaged, self));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    static bool AnyInUse(RegistryKey parent, string? self)
    {
        foreach (var name in parent.GetSubKeyNames())
        {
            if (name == "NonPackaged" || string.Equals(name, self, StringComparison.OrdinalIgnoreCase)) continue;
            using var app = parent.OpenSubKey(name);
            if (app?.GetValue("LastUsedTimeStop") is long stop && stop == 0 && app.GetValue("LastUsedTimeStart") is long start && start != 0) return true;
        }
        return false;
    }
}
