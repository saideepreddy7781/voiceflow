@preconcurrency import AVFoundation
import Foundation
import os

/// Thread-safe sample store written from the audio thread and read from the main thread.
final class SampleBuffer: @unchecked Sendable {
    private let lock = OSAllocatedUnfairLock()
    private var samples: [Float] = []

    func append(_ new: [Float]) {
        lock.withLock { samples.append(contentsOf: new) }
    }

    func snapshot() -> [Float] { lock.withLock { samples } }
    var count: Int { lock.withLock { samples.count } }
    /// A real copy of the samples from `start` on. (Returning a slice would share storage, and the next append on
    /// the audio thread would then copy the whole recording.)
    func copy(from start: Int) -> [Float] { lock.withLock { Array(samples[min(start, samples.count)...]) } }
    func tail(_ n: Int) -> [Float] { lock.withLock { Array(samples.suffix(n)) } }
    func reset() { lock.withLock { samples.removeAll(keepingCapacity: true) } }

    /// Takes and removes everything buffered (long recordings stay small in memory).
    func drain() -> [Float] {
        lock.withLock {
            let out = samples
            samples.removeAll(keepingCapacity: true)
            return out
        }
    }
}

/// One-shot flag for the converter's input block, which runs synchronously on the audio thread.
final class Flag: @unchecked Sendable {
    var value = false
}

/// One subscriber's handle on the microphone: 16 kHz mono Float32 samples of its own.
/// The hardware is shared through `SharedMicrophone`, so any number of recorders can run together.
@MainActor
public final class AudioRecorder {
    public enum RecorderError: Error, CustomStringConvertible {
        case microphoneDenied
        case noInput
        case converter
        case engine(String)

        public var description: String {
            switch self {
            case .microphoneDenied: "Microphone access is off. Turn it on in System Settings → Privacy & Security → Microphone."
            case .noInput: "No microphone was found."
            case .converter: "Could not convert microphone audio to 16 kHz."
            case .engine(let message): "Microphone could not start: \(message)"
            }
        }
    }

    private let microphone: SharedMicrophone
    private var subscription: MicSubscription?
    private var restartsAtStart = 0

    public var isRecording: Bool { subscription != nil }
    /// How many times the engine was rebuilt during this recording (for diagnostics).
    public var restarts: Int { microphone.restartCount - restartsAtStart }

    public init(microphone: SharedMicrophone = .shared) {
        self.microphone = microphone
    }

    // MARK: Permission

    public static var microphoneStatus: AVAuthorizationStatus { AVCaptureDevice.authorizationStatus(for: .audio) }

    public static func requestMicrophone() async -> Bool {
        await AVCaptureDevice.requestAccess(for: .audio)
    }

    // MARK: Recording

    /// Starts capture. `deviceUID` nil means the system default input. While another recorder is running
    /// the device it opened is shared, whatever is passed here.
    public func start(deviceUID: String?) throws {
        guard !isRecording else { return }
        subscription = try microphone.subscribe(deviceUID: deviceUID)
        restartsAtStart = microphone.restartCount
    }

    /// Asks for the microphone to be rebuilt after it stalled. Other recorders stay attached.
    public func restartMicrophone() { microphone.restart() }

    /// The tap block runs on a CoreAudio thread, never the main thread. It is built in a nonisolated
    /// static function so Swift does not infer main-actor isolation for it; an isolated block traps
    /// at runtime the first time CoreAudio calls it.
    nonisolated static func makeTapBlock(converter: AVAudioConverter, target: AVAudioFormat,
                                         inputFormat: AVAudioFormat, sink: any SampleSink) -> AVAudioNodeTapBlock {
        { pcm, _ in
            let ratio = target.sampleRate / inputFormat.sampleRate
            let capacity = AVAudioFrameCount(Double(pcm.frameLength) * ratio) + 32
            guard let out = AVAudioPCMBuffer(pcmFormat: target, frameCapacity: capacity) else { return }
            let fed = Flag()
            var error: NSError?
            converter.convert(to: out, error: &error) { _, status in
                if fed.value { status.pointee = .noDataNow; return nil }
                fed.value = true
                status.pointee = .haveData
                return pcm
            }
            guard error == nil, let channel = out.floatChannelData?[0] else { return }
            sink.append(Array(UnsafeBufferPointer(start: channel, count: Int(out.frameLength))))
        }
    }

    /// Stops capture and returns everything recorded.
    @discardableResult
    public func stop() -> [Float] {
        guard let subscription else { return [] }
        let samples = subscription.buffer.snapshot()
        microphone.unsubscribe(subscription)
        self.subscription = nil
        return samples
    }

    /// Samples so far (for segmenting while recording).
    public func samples() -> [Float] { subscription?.buffer.snapshot() ?? [] }
    public func samples(from start: Int) -> [Float] { subscription?.buffer.copy(from: start) ?? [] }

    /// Takes everything recorded since the last call (meetings, where keeping an hour of audio would use too much memory).
    public func drain() -> [Float] { subscription?.buffer.drain() ?? [] }
    public var sampleCount: Int { subscription?.buffer.count ?? 0 }

    /// 0...1 level of the last 50 ms, for the waveform.
    public func currentLevel() -> Float {
        AudioMath.level((subscription?.buffer.tail(800) ?? [])[...])
    }
}
