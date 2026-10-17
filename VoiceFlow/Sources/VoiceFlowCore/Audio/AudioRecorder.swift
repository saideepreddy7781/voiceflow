@preconcurrency import AVFoundation
import AudioToolbox
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

/// Captures the microphone as 16 kHz mono Float32.
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

    private let log = Logger(subsystem: "com.saideep.voiceflow", category: "audio")
    private var engine: AVAudioEngine?
    private let buffer = SampleBuffer()
    private var deviceUID: String?
    private var configObserver: NSObjectProtocol?
    public private(set) var isRecording = false
    /// How many times the engine was restarted after macOS changed the input (for diagnostics).
    public private(set) var restarts = 0

    public init() {}

    // MARK: Permission

    public static var microphoneStatus: AVAuthorizationStatus { AVCaptureDevice.authorizationStatus(for: .audio) }

    public static func requestMicrophone() async -> Bool {
        await AVCaptureDevice.requestAccess(for: .audio)
    }

    // MARK: Recording

    /// Starts capture. `deviceUID` nil means the system default input.
    public func start(deviceUID: String?) throws {
        guard !isRecording else { return }
        guard Self.microphoneStatus == .authorized else { throw RecorderError.microphoneDenied }
        buffer.reset()
        self.deviceUID = deviceUID
        restarts = 0
        try startEngine()
        isRecording = true
    }

    /// Builds and starts an engine. Also used to recover after a configuration change: AVAudioEngine stops
    /// itself when the input's format changes (AirPods entering call mode when Zoom opens the mic).
    private func startEngine() throws {
        let engine = AVAudioEngine()
        let input = engine.inputNode
        if let deviceUID, let device = AudioDevices.device(uid: deviceUID), let unit = input.audioUnit {
            var id = device.id
            AudioUnitSetProperty(unit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0,
                                 &id, UInt32(MemoryLayout<AudioDeviceID>.size))
        }
        let inputFormat = input.outputFormat(forBus: 0)
        guard inputFormat.sampleRate > 0, inputFormat.channelCount > 0 else { throw RecorderError.noInput }
        guard let target = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: AudioMath.sampleRate, channels: 1, interleaved: false),
              let converter = AVAudioConverter(from: inputFormat, to: target) else { throw RecorderError.converter }

        input.installTap(onBus: 0, bufferSize: 1024, format: inputFormat,
                         block: Self.makeTapBlock(converter: converter, target: target, inputFormat: inputFormat, sink: buffer))

        do {
            engine.prepare()
            try engine.start()
        } catch {
            input.removeTap(onBus: 0)
            throw RecorderError.engine(error.localizedDescription)
        }
        self.engine = engine
        configObserver = NotificationCenter.default.addObserver(forName: .AVAudioEngineConfigurationChange, object: engine,
                                                                queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.recoverFromConfigurationChange() }
        }
        log.info("Recording started (\(inputFormat.sampleRate, privacy: .public) Hz input)")
    }

    private func recoverFromConfigurationChange() {
        guard isRecording else { return }
        tearDownEngine()
        do {
            try startEngine()
            restarts += 1
            log.notice("Microphone changed; recording restarted")
        } catch {
            log.error("Microphone could not restart: \(String(describing: error), privacy: .public)")
        }
    }

    private func tearDownEngine() {
        if let configObserver { NotificationCenter.default.removeObserver(configObserver) }
        configObserver = nil
        engine?.inputNode.removeTap(onBus: 0)
        engine?.stop()
        engine = nil
    }

    /// The tap block runs on a CoreAudio thread, never the main thread. It is built in a nonisolated
    /// static function so Swift does not infer main-actor isolation for it; an isolated block traps
    /// at runtime the first time CoreAudio calls it.
    nonisolated static func makeTapBlock(converter: AVAudioConverter, target: AVAudioFormat,
                                         inputFormat: AVAudioFormat, sink: SampleBuffer) -> AVAudioNodeTapBlock {
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
        guard isRecording else { return [] }
        tearDownEngine()
        isRecording = false
        return buffer.snapshot()
    }

    /// Samples so far (for segmenting while recording).
    public func samples() -> [Float] { buffer.snapshot() }
    public func samples(from start: Int) -> [Float] { buffer.copy(from: start) }

    /// Takes everything recorded since the last call (meetings, where keeping an hour of audio would use too much memory).
    public func drain() -> [Float] { buffer.drain() }
    public var sampleCount: Int { buffer.count }

    /// 0...1 level of the last 50 ms, for the waveform.
    public func currentLevel() -> Float {
        AudioMath.level(buffer.tail(800)[...])
    }
}
