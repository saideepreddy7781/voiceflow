import AppKit
import Foundation

/// Notices a meeting: a meeting app is running and something is using the microphone.
public enum MeetingDetector {
    static let apps: [String: String] = [
        "us.zoom.xos": "Zoom", "com.microsoft.teams2": "Microsoft Teams", "com.microsoft.teams": "Microsoft Teams",
        "com.cisco.webexmeetingsapp": "Webex", "Cisco-Systems.Spark": "Webex", "com.apple.FaceTime": "FaceTime",
        "com.tinyspeck.slackmacgap": "Slack huddle", "com.hnc.Discord": "Discord", "net.whatsapp.WhatsApp": "WhatsApp call",
        "com.skype.skype": "Skype", "com.gotomeeting.GoToMeeting": "GoTo Meeting",
    ]

    /// Meeting web apps recognized from the front browser window's title.
    static let webTitles: [(String, String)] = [
        ("Meet - ", "Google Meet"), ("Google Meet", "Google Meet"), ("Zoom Meeting", "Zoom"),
        ("Microsoft Teams", "Microsoft Teams"), ("Webex", "Webex"), ("Jitsi", "Jitsi"), ("Whereby", "Whereby"),
    ]

    /// The meeting app's name, or nil when no meeting seems to be on.
    /// `browserTitle` is the frontmost browser window's title, when known.
    public static func detect(runningBundleIDs: [String], micInUse: Bool, browserTitle: String?) -> String? {
        guard micInUse else { return nil }
        if let title = browserTitle, let hit = webTitles.first(where: { title.contains($0.0) }) { return hit.1 }
        // Prefer dedicated meeting apps over chat apps that can also call.
        let priority = ["us.zoom.xos", "com.microsoft.teams2", "com.microsoft.teams", "com.cisco.webexmeetingsapp",
                        "Cisco-Systems.Spark", "com.apple.FaceTime", "com.gotomeeting.GoToMeeting", "com.skype.skype"]
        if let id = priority.first(where: runningBundleIDs.contains) { return apps[id] }
        return nil
    }

    @MainActor
    public static func current() -> String? {
        let running = NSWorkspace.shared.runningApplications.compactMap(\.bundleIdentifier)
        var title: String?
        if let front = NSWorkspace.shared.frontmostApplication, let id = front.bundleIdentifier,
           AppClassifier.browsers.contains(id) || id.hasPrefix("com.google.Chrome") {
            title = ContextReader.frontWindowTitle()
        }
        return detect(runningBundleIDs: running, micInUse: AudioDevices.defaultInputInUse(), browserTitle: title)
    }
}
