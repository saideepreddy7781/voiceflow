
namespace VoiceFlow.Core.Meetings;

/// <summary>A visible top-level window: its process file name and title.</summary>
public readonly record struct WindowSnapshot(string ProcessName, string Title);

/// <summary>
/// Notices a meeting from the running apps, their window titles and whether something is using the microphone.
/// It only suggests Notetaker; recording always starts from the user's own click.
/// </summary>
public static class MeetingDetector
{
    /// <summary>Dedicated meeting apps, strongest first. Chat apps that can also call are left out, as on macOS.</summary>
    static readonly (string Process, string Name, string[] CallTitles)[] Apps =
    [
        ("zoom.exe", "Zoom", ["Zoom Meeting", "Zoom Webinar"]),
        ("ms-teams.exe", "Microsoft Teams", ["Meeting", "Call with"]),
        ("teams.exe", "Microsoft Teams", ["Meeting", "Call with"]),
        ("webex.exe", "Webex", ["Webex Meeting", "Meeting"]),
        ("ciscocollabhost.exe", "Webex", ["Webex Meeting", "Meeting"]),
        ("webexmta.exe", "Webex", ["Webex Meeting", "Meeting"]),
        ("atmgr.exe", "Webex", ["Webex Meeting", "Meeting"]),
        ("g2mcomm.exe", "GoTo Meeting", []),
        ("gotomeeting.exe", "GoTo Meeting", []),
        ("skype.exe", "Skype", []),
    ];

    /// <summary>Meeting web apps recognized from browser window titles. Same list as macOS.</summary>
    static readonly (string Title, string Name)[] WebTitles =
    [
        ("Meet - ", "Google Meet"), ("Google Meet", "Google Meet"), ("Zoom Meeting", "Zoom"),
        ("Microsoft Teams", "Microsoft Teams"), ("Webex", "Webex"), ("Jitsi", "Jitsi"), ("Whereby", "Whereby"),
    ];

    /// <summary>The meeting app's name, or null when no meeting seems to be on.</summary>
    /// <param name="windows">Visible top-level windows. Only process names and titles are read, and they are not stored.</param>
    /// <param name="micInUse">Whether any app has the microphone open. Null when Windows would not say.</param>
    public static string? Detect(IReadOnlyCollection<WindowSnapshot> windows, bool? micInUse)
    {
        if (micInUse == false) return null;
        foreach (var window in windows)
        {
            if (!AppClassifier.Browsers.Contains(AppClassifier.Normalize(window.ProcessName))) continue;
            foreach (var (title, name) in WebTitles)
                if (window.Title.Contains(title, StringComparison.OrdinalIgnoreCase)) return name;
        }
        foreach (var (process, name, callTitles) in Apps)
        {
            var own = windows.Where(w => AppClassifier.Normalize(w.ProcessName) == process).ToList();
            if (own.Count == 0) continue;
            // A running app with the microphone open is a call. If the microphone state is unknown, the title has to say so.
            if (micInUse == true) return name;
            if (own.Any(w => callTitles.Any(t => w.Title.Contains(t, StringComparison.OrdinalIgnoreCase)))) return name;
        }
        return null;
    }
}
