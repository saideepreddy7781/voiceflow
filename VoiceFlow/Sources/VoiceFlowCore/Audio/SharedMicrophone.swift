@preconcurrency import AVFoundation
import AudioToolbox
import Foundation
import os

/// Receives 16 kHz mono samples on the audio thread.
protocol SampleSink: AnyObject, Sendable {
    func append(_ new: [Float])
}

extension SampleBuffer: SampleSink {}

/// Copies every block from the audio thread into each subscriber's own buffer. A subscriber joining or
/// leaving only edits this list, so the audio thread and the other subscribers never notice.
final class MicFanOut: SampleSink, @unchecked Sendable {
    private let lock = OSAllocatedUnfairLock()
    private var buffers: [SampleBuffer] = []
    private var blocks = 0
    private var samples = 0

    func add(_ buffer: SampleBuffer) { lock.withLock { buffers.append(buffer) } }
    func remove(_ buffer: SampleBuffer) { lock.withLock { buffers.removeAll { $0 === buffer } } }

    func append(_ new: [Float]) {
        let targets = lock.withLock {
            blocks += 1
            samples += new.count
            return buffers
        }
        for target in targets { target.append(new) }
    }

    /// Blocks and samples delivered by the engine since launch.
    var delivered: (blocks: Int, samples: Int) { lock.withLock { (blocks, samples) } }
}

/// What the microphone engine is doing, for diagnostics. Numbers only.
public struct MicEngineDiagnostics: Sendable, Codable, Equatable {
    public var state = "stopped"
    public var subscribers = 0
    public var restartCount = 0
    public var engineRunning = false
    public var inputSampleRate = 0.0
    public var inputChannels = 0
    /// The Core Audio device the input node is reading.
    public var inputDeviceID: UInt32 = 0
    public var configurationChanges = 0
    public var configurationChangesIgnored = 0
    public var blocksDelivered = 0
    public var samplesDelivered = 0
    public var lastError: String?

    public init() {}
}

/// The capture hardware behind `SharedMicrophone`. The real one wraps AVAudioEngine; tests use a fake.
@MainActor
protocol MicrophoneEngine: AnyObject {
    /// Called when the input changed underneath a running engine and it needs rebuilding.
    var onConfigurationChange: (@MainActor () -> Void)? { get set }
    /// Starts capture. Delivers 16 kHz mono samples to `sink` from the audio thread.
    func start(deviceUID: String?, sink: any SampleSink) throws
    func stop()
    /// Fills in what only the engine knows.
    func describe(into diagnostics: inout MicEngineDiagnostics)
}

extension MicrophoneEngine {
    func describe(into diagnostics: inout MicEngineDiagnostics) {}
}

/// One subscriber's view of the microphone: its own samples, independent of every other subscriber.
@MainActor
public final class MicSubscription {
    let buffer = SampleBuffer()
    fileprivate init() {}
}

/// The only owner of the microphone. Dictation and Notetaker each subscribe; the engine runs while
/// at least one subscriber exists.
///
/// Subscribing and unsubscribing never touch the running engine, so a dictation in the middle of a meeting
/// cannot disturb the meeting's audio. Recovery from input changes (AirPods entering call mode, a device
/// disappearing) is handled here, and all subscribers keep their buffers across it.
@MainActor
public final class SharedMicrophone {
    public enum State: Equatable, Sendable {
        case stopped, running, recovering, failed
    }

    public static let shared = SharedMicrophone(engine: LiveMicrophoneEngine())

    public private(set) var state: State = .stopped
    /// Successful engine rebuilds since the app started.
    public private(set) var restartCount = 0
    public private(set) var lastError: String?

    private let log = Logger(subsystem: "com.saideep.voiceflow", category: "audio")
    private let engine: MicrophoneEngine
    private let fanOut = MicFanOut()
    private var subscriptions: [MicSubscription] = []
    private var deviceUID: String?
    private let sleep: @Sendable (Double) async -> Void
    private let retryDelays: [Double]
    var recoveryTask: Task<Void, Never>?

    /// `retryDelays`: seconds before each rebuild attempt. The first entry also absorbs the burst of
    /// notifications macOS sends while an input switches.
    init(engine: MicrophoneEngine, retryDelays: [Double] = [0.15, 0.3, 0.6, 1.2, 2.5, 5],
         sleep: @escaping @Sendable (Double) async -> Void = { try? await Task.sleep(for: .seconds($0)) }) {
        self.engine = engine
        self.retryDelays = retryDelays
        self.sleep = sleep
        engine.onConfigurationChange = { [weak self] in self?.scheduleRecovery(reason: "input changed") }
    }

    /// Adds a subscriber, starting the engine if it is the first. A second subscriber always shares the
    /// device already in use: one device for everything, so joining never reconfigures the input.
    public func subscribe(deviceUID: String?) throws -> MicSubscription {
        let subscription = MicSubscription()
        fanOut.add(subscription.buffer)
        do {
            if subscriptions.isEmpty {
                self.deviceUID = deviceUID
                try startEngine()
            } else if state == .failed {
                // Recovery gave up earlier, so a new request gets one more chance.
                engine.stop()
                try startEngine()
            }
        } catch {
            fanOut.remove(subscription.buffer)
            throw error
        }
        subscriptions.append(subscription)
        return subscription
    }

    /// Removes a subscriber. The engine keeps running unless this was the last one.
    public func unsubscribe(_ subscription: MicSubscription) {
        fanOut.remove(subscription.buffer)
        subscriptions.removeAll { $0 === subscription }
        guard subscriptions.isEmpty else { return }
        recoveryTask?.cancel()
        recoveryTask = nil
        engine.stop()
        state = .stopped
        lastError = nil
    }

    public var diagnostics: MicEngineDiagnostics {
        var d = MicEngineDiagnostics()
        d.state = "\(state)"
        d.subscribers = subscriptions.count
        d.restartCount = restartCount
        d.lastError = lastError
        let delivered = fanOut.delivered
        d.blocksDelivered = delivered.blocks
        d.samplesDelivered = delivered.samples
        engine.describe(into: &d)
        return d
    }

    /// Rebuilds the engine for a stalled microphone. Subscribers stay attached.
    public func restart() {
        guard !subscriptions.isEmpty, state != .recovering else { return }
        scheduleRecovery(reason: "stalled")
    }

    private func startEngine() throws {
        do {
            try engine.start(deviceUID: deviceUID, sink: fanOut)
            state = .running
            lastError = nil
        } catch {
            state = .failed
            lastError = "\(error)"
            throw error
        }
    }

    private func scheduleRecovery(reason: String) {
        guard !subscriptions.isEmpty else { return }
        recoveryTask?.cancel()
        state = .recovering
        log.notice("Rebuilding microphone engine: \(reason, privacy: .public)")
        let delays = retryDelays
        let sleep = self.sleep
        recoveryTask = Task { [weak self] in
            for delay in delays {
                await sleep(delay)
                guard !Task.isCancelled, let self, !self.subscriptions.isEmpty else { return }
                self.engine.stop()
                do {
                    try self.startEngine()
                    self.restartCount += 1
                    self.log.notice("Microphone engine rebuilt")
                    return
                } catch {
                    self.state = .recovering
                    self.log.error("Microphone rebuild failed: \(String(describing: error), privacy: .public)")
                }
            }
            self?.state = .failed
        }
    }
}
