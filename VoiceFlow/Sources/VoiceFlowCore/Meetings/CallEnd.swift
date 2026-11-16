import Foundation

/// Decides when a recorded call has ended, so Notetaker can stop by itself.
///
/// A call is over when no other app has been recording from the microphone, and no meeting audio has played, for
/// `quietSeconds`. It only applies after a call app was seen holding the microphone: an in-person meeting recorded
/// with VoiceFlow alone never stops by itself.
public struct CallEndDetector: Sendable {
    public static let quietSeconds = 60.0

    private var sawCall = false
    private var releasedAt: Double?

    public init() {}

    /// `t`: seconds since the recording started. `callAppRecording`: another app holds the microphone (nil when
    /// macOS can't say). `meetingAudioSilentSeconds`: seconds in a row the meeting audio has been silent, or nil when
    /// it isn't recorded. Returns true once, when the call looks over.
    public mutating func observe(at t: Double, callAppRecording: Bool?, meetingAudioSilentSeconds: Int?) -> Bool {
        guard let callAppRecording else { return false }
        if callAppRecording {
            sawCall = true
            releasedAt = nil
            return false
        }
        guard sawCall else { return false }
        let released = releasedAt ?? t
        releasedAt = released
        guard t - released >= Self.quietSeconds else { return false }
        if let silent = meetingAudioSilentSeconds, Double(silent) < Self.quietSeconds { return false }
        sawCall = false
        releasedAt = nil
        return true
    }
}

/// The "System Audio Recording" permission, read without asking for it. macOS has no public call for this, so it
/// asks the privacy database directly and says `.unknown` if that is unavailable.
public enum SystemAudioPermission: String, Sendable, Codable {
    case granted, denied, unknown

    public static func current() -> SystemAudioPermission {
        typealias Preflight = @convention(c) (CFString, CFDictionary?) -> Int
        guard let handle = dlopen("/System/Library/PrivateFrameworks/TCC.framework/Versions/A/TCC", RTLD_NOW),
              let symbol = dlsym(handle, "TCCAccessPreflight") else { return .unknown }
        let preflight = unsafeBitCast(symbol, to: Preflight.self)
        switch preflight("kTCCServiceAudioCapture" as CFString, nil) {
        case 0: return .granted
        case 1: return .denied
        default: return .unknown
        }
    }
}
