import Foundation

/// How the microphone is doing during a meeting.
public enum MicHealthState: String, Codable, Sendable {
    case healthy
    /// No audio arrived for a few seconds, so the microphone is being rebuilt.
    case restarting
    /// Audio arrives but it is exact zeros: a muted or disconnected input. A quiet room is never this clean.
    case silent
    /// No audio arrives at all, and restarting did not help.
    case noAudio
}

/// Decides when a meeting's microphone is dead. Fed once per second with what arrived during that second.
///
/// A problem gets one quick rebuild, and if that does not fix it the user is told within seconds, while the
/// meeting is still on and can be fixed. Rebuilds then continue quietly, so a reconnected microphone comes back
/// by itself.
public struct MicHealthMonitor: Sendable {
    public struct Decision: Equatable, Sendable {
        public var state: MicHealthState
        /// True when the caller should rebuild the microphone now.
        public var restart: Bool
    }

    /// Seconds with no samples before the first rebuild.
    static let stallSeconds = 2
    /// Seconds of exact zeros before the first rebuild. Longer than a stall: a headset mute button also sends zeros.
    static let silenceSeconds = 3
    /// Seconds after the first rebuild before the problem is reported.
    static let reportAfterRestart = 3
    /// Quiet retries once reported: the first soon, then twice a minute.
    static let firstRetry = 10
    static let laterRetry = 30

    public private(set) var state: MicHealthState = .healthy
    /// Seconds of the current episode.
    private var badSeconds = 0
    private var restarts = 0
    private var nextRestartAt = 0

    public init() {}

    public mutating func observe(sampleCount: Int, digitallySilent: Bool) -> Decision {
        if sampleCount > 0 && !digitallySilent {
            self = MicHealthMonitor()
            return Decision(state: .healthy, restart: false)
        }
        let stalled = sampleCount == 0
        badSeconds += 1
        if nextRestartAt == 0 { nextRestartAt = stalled ? Self.stallSeconds : Self.silenceSeconds }
        guard badSeconds >= nextRestartAt else { return Decision(state: state, restart: false) }

        restarts += 1
        switch restarts {
        case 1:
            state = .restarting
            nextRestartAt = badSeconds + Self.reportAfterRestart
        case 2:
            state = stalled ? .noAudio : .silent
            nextRestartAt = badSeconds + Self.firstRetry
        default:
            state = stalled ? .noAudio : .silent
            nextRestartAt = badSeconds + Self.laterRetry
        }
        return Decision(state: state, restart: true)
    }

    /// True when every sample is effectively zero. Real microphone noise sits far above this (-120 dBFS).
    public static func isDigitallySilent(_ samples: [Float]) -> Bool {
        samples.allSatisfy { abs($0) < 0.000_001 }
    }
}

/// What a finished dictation's audio says about the microphone.
public enum CaptureVerdict: Equatable, Sendable {
    /// Released almost immediately.
    case tooShort
    /// The key was held but almost no audio arrived: the microphone did not deliver.
    case noAudio
    /// Exact zeros throughout: muted or disconnected input.
    case digitalSilence
    /// A real input, but nobody spoke.
    case noSpeech
    case speech
}

public extension AudioMath {
    static func captureVerdict(_ samples: [Float], heldSeconds: Double) -> CaptureVerdict {
        let seconds = Double(samples.count) / sampleRate
        if seconds < 0.5 { return heldSeconds >= 1 ? .noAudio : .tooShort }
        if seconds >= 1, MicHealthMonitor.isDigitallySilent(samples) { return .digitalSilence }
        return isSilent(samples) ? .noSpeech : .speech
    }
}
