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
/// A stall gets restart attempts straight away. Digital silence gets a longer grace period and fewer
/// attempts, because the usual cause is a mute button on a headset.
public struct MicHealthMonitor: Sendable {
    public struct Decision: Equatable, Sendable {
        public var state: MicHealthState
        /// True when the caller should rebuild the microphone now.
        public var restart: Bool
    }

    /// Seconds with no samples before the first restart.
    static let stallSeconds = 4
    /// Seconds of exact zeros before the first restart.
    static let silenceSeconds = 10
    /// Restarts tried before the problem is reported to the user.
    static let stallAttempts = 2
    static let silenceAttempts = 1

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
        let attempts = stalled ? Self.stallAttempts : Self.silenceAttempts
        if restarts > attempts {
            state = stalled ? .noAudio : .silent
            nextRestartAt = badSeconds + 60
        } else {
            state = .restarting
            nextRestartAt = badSeconds + (stalled ? 5 * restarts : 20)
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
