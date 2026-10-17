import Foundation

/// The system setting "Press 🌐 key to" (System Settings → Keyboard). Anything other than
/// Do Nothing makes the Fn key open the emoji picker, change input source or start Apple dictation
/// when VoiceFlow uses it.
public enum FnKeySetting: Int, Sendable {
    case doNothing = 0
    case changeInputSource = 1
    case showEmoji = 2
    case startDictation = 3

    public static var current: FnKeySetting? {
        let value = CFPreferencesCopyAppValue("AppleFnUsageType" as CFString, "com.apple.HIToolbox" as CFString)
        guard let number = value as? Int else { return nil }
        return FnKeySetting(rawValue: number)
    }

    public var displayName: String {
        switch self {
        case .doNothing: "Do Nothing"
        case .changeInputSource: "Change Input Source"
        case .showEmoji: "Show Emoji & Symbols"
        case .startDictation: "Start Dictation"
        }
    }

    /// Opens System Settings at the Keyboard pane.
    public static let settingsURL = URL(string: "x-apple.systempreferences:com.apple.Keyboard-Settings.extension")!
}
