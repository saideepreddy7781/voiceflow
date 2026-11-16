namespace VoiceFlow.Core.Meetings;

/// <summary>
/// Decides when a recorded call has ended, so Notetaker can stop by itself. Same rules as macOS.
///
/// A call is over when no other app has been recording from a microphone, and no meeting audio has played, for
/// <see cref="QuietSeconds"/>. It only applies after a call app was seen holding the microphone: an in-person meeting
/// recorded with VoiceFlow alone never stops by itself.
/// </summary>
public sealed class CallEndDetector
{
    public const double QuietSeconds = 60;
    /// <summary>How long the "call ended" notice stays up, with Keep recording, before Notetaker stops.</summary>
    public const double NoticeSeconds = 15;

    bool _sawCall;
    double? _releasedAt;

    /// <param name="t">Seconds since the recording started.</param>
    /// <param name="callAppRecording">Another app holds a microphone. Null when Windows can't say.</param>
    /// <param name="meetingAudioSilentSeconds">Seconds in a row the meeting audio has been silent, or null when it isn't recorded.</param>
    /// <returns>True once, when the call looks over.</returns>
    public bool Observe(double t, bool? callAppRecording, int? meetingAudioSilentSeconds)
    {
        if (callAppRecording is not { } recording) return false;
        if (recording)
        {
            _sawCall = true;
            _releasedAt = null;
            return false;
        }
        if (!_sawCall) return false;
        var released = _releasedAt ?? t;
        _releasedAt = released;
        if (t - released < QuietSeconds) return false;
        if (meetingAudioSilentSeconds is { } silent && silent < QuietSeconds) return false;
        _sawCall = false;
        _releasedAt = null;
        return true;
    }
}

/// <summary>One audio session on a capture endpoint: which process owns it and whether it is recording now.</summary>
public readonly record struct CaptureSession(uint ProcessId, bool Active);

public static class CaptureSessions
{
    /// <summary>
    /// Whether a process other than <paramref name="selfProcessId"/> is recording. Process 0 is the system's own
    /// session and never counts. Null when the sessions could not be read.
    /// </summary>
    public static bool? AnotherAppIsRecording(IEnumerable<CaptureSession>? sessions, uint selfProcessId) =>
        sessions?.Any(s => s.Active && s.ProcessId != 0 && s.ProcessId != selfProcessId);
}
