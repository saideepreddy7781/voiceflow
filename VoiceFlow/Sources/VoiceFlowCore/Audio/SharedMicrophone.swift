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

    func add(_ buffer: SampleBuffer) { lock.withLock { buffers.append(buffer) } }
    func remove(_ buffer: SampleBuffer) { lock.withLock { buffers.removeAll { $0 === buffer } } }

    func append(_ new: [Float]) {
        let targets = lock.withLock { buffers }
        for target in targets { target.append(new) }
    }
}

/// The capture hardware behind `SharedMicrophone`. The real one wraps AVAudioEngine; tests use a fake.
@MainActor
protocol MicrophoneEngine: AnyObject {
    /// Called when the input changed underneath a running engine and it needs rebuilding.
    var onConfigurationChange: (@MainActor () -> Void)? { get set }
    /// Starts capture. Delivers 16 kHz mono samples to `sink` from the audio thread.
    func start(deviceUID: String?, sink: any SampleSink) throws
    func stop()
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

    /// Rebuilds the engine for a stalled microphone. Subscribers stay attached.
    public func restart() {
        guard !subscriptions.isEmpty else { return }
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
                    self.log.error("Microphone rebuild failed: \(String(describing: error), privacy: .public)")
                }
            }
            self?.state = .failed
        }
    }
}

/// The real engine: one AVAudioEngine, rebuilt from scratch on every start.
@MainActor
final class LiveMicrophoneEngine: MicrophoneEngine {
    var onConfigurationChange: (@MainActor () -> Void)?

    private let log = Logger(subsystem: "com.saideep.voiceflow", category: "audio")
    private var engine: AVAudioEngine?
    private var observer: NSObjectProtocol?
    private var tapFormat: AVAudioFormat?

    func start(deviceUID: String?, sink: any SampleSink) throws {
        guard AudioRecorder.microphoneStatus == .authorized else { throw AudioRecorder.RecorderError.microphoneDenied }
        let engine = AVAudioEngine()
        let input = engine.inputNode
        if let deviceUID, let device = AudioDevices.device(uid: deviceUID), let unit = input.audioUnit {
            var id = device.id
            AudioUnitSetProperty(unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0,
                                 &id, UInt32(MemoryLayout<AudioDeviceID>.size))
        }
        let inputFormat = input.outputFormat(forBus: 0)
        guard inputFormat.sampleRate > 0, inputFormat.channelCount > 0 else { throw AudioRecorder.RecorderError.noInput }
        guard let target = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: AudioMath.sampleRate, channels: 1, interleaved: false),
              let converter = AVAudioConverter(from: inputFormat, to: target) else { throw AudioRecorder.RecorderError.converter }

        input.installTap(onBus: 0, bufferSize: 1024, format: inputFormat,
                         block: AudioRecorder.makeTapBlock(converter: converter, target: target, inputFormat: inputFormat, sink: sink))
        // Added before start so a change during startup is not missed.
        let observer = NotificationCenter.default.addObserver(forName: .AVAudioEngineConfigurationChange, object: engine,
                                                              queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.handleConfigurationChange() }
        }
        do {
            engine.prepare()
            try engine.start()
        } catch {
            NotificationCenter.default.removeObserver(observer)
            input.removeTap(onBus: 0)
            throw AudioRecorder.RecorderError.engine(error.localizedDescription)
        }
        self.engine = engine
        self.observer = observer
        tapFormat = inputFormat
        log.info("Recording started (\(inputFormat.sampleRate, privacy: .public) Hz input)")
    }

    /// macOS can post this for changes that did not affect a running engine (often as the engine starts).
    /// Rebuilding then would be the glitch this class exists to prevent.
    private func handleConfigurationChange() {
        guard let engine else { return }
        let format = engine.inputNode.outputFormat(forBus: 0)
        if engine.isRunning, let tapFormat, format.sampleRate == tapFormat.sampleRate, format.channelCount == tapFormat.channelCount {
            log.debug("Configuration change ignored: engine still running with the same format")
            return
        }
        onConfigurationChange?()
    }

    func stop() {
        if let observer { NotificationCenter.default.removeObserver(observer) }
        observer = nil
        engine?.inputNode.removeTap(onBus: 0)
        engine?.stop()
        engine = nil
        tapFormat = nil
    }
}
