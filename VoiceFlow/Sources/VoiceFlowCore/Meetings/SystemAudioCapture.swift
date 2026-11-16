@preconcurrency import AVFoundation
import AudioToolbox
import CoreAudio
import Foundation
import os

/// Records what the Mac is playing (the other people in a meeting) with a Core Audio process tap,
/// converted to 16 kHz mono. Needs macOS 14.2+ and the "System Audio Recording" permission, which macOS
/// asks for the first time capture starts.
/// What the system-audio tap is doing, for diagnostics. Numbers only.
public struct SystemAudioDiagnostics: Sendable, Codable, Equatable {
    public var running = false
    public var tapID: UInt32 = 0
    public var aggregateID: UInt32 = 0
    public var tapSampleRate = 0.0
    public var tapChannels = 0
    /// Times the tap was rebuilt because the output device changed.
    public var rebuilds = 0
    public var callbacks = 0
    public var samplesDelivered = 0
    public var lastError: String?

    public init() {}
}

@available(macOS 14.2, *)
public final class SystemAudioCapture: @unchecked Sendable {
    public enum CaptureError: Error, CustomStringConvertible {
        case tap(OSStatus)
        case aggregate(OSStatus)
        case format
        case start(OSStatus)

        public var description: String {
            switch self {
            case .tap(let s): "Could not tap system audio (\(s)). Allow VoiceFlow under System Settings → Privacy & Security → Screen & System Audio Recording."
            case .aggregate(let s): "Could not set up system audio capture (\(s))."
            case .format: "System audio has a format VoiceFlow can't convert."
            case .start(let s): "System audio capture could not start (\(s))."
            }
        }
    }

    private let log = Logger(subsystem: "com.saideep.voiceflow", category: "system-audio")
    private var tapID = AudioObjectID(kAudioObjectUnknown)
    private var aggregateID = AudioObjectID(kAudioObjectUnknown)
    private var procID: AudioDeviceIOProcID?
    private let queue = DispatchQueue(label: "com.saideep.voiceflow.system-audio", qos: .userInitiated)
    let buffer = SampleBuffer()
    public private(set) var isRunning = false
    private let counter = CallbackCounter()
    private var info = SystemAudioDiagnostics()
    private var outputListener: AudioObjectPropertyListenerBlock?
    private var outputAddress = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultOutputDevice,
                                                           mScope: kAudioObjectPropertyScopeGlobal,
                                                           mElement: kAudioObjectPropertyElementMain)

    public init() {}

    public func start() throws {
        guard !isRunning else { return }
        do { try startTap() } catch {
            info.lastError = "\(error)"
            throw error
        }
        // Headphones connected or disconnected mid-meeting: rebuild the tap on the new output.
        let listener: AudioObjectPropertyListenerBlock = { [weak self] _, _ in
            guard let self, self.isRunning else { return }
            self.teardownTap()
            self.info.rebuilds += 1
            do { try self.startTap() } catch {
                self.info.lastError = "\(error)"
                self.log.error("System audio could not follow the new output device")
            }
        }
        outputListener = listener
        AudioObjectAddPropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &outputAddress, queue, listener)
    }

    private func startTap() throws {
        let description = CATapDescription(stereoGlobalTapButExcludeProcesses: [])
        description.uuid = UUID()
        description.name = "VoiceFlow Notetaker"
        description.isPrivate = true
        description.muteBehavior = .unmuted

        var tap = AudioObjectID(kAudioObjectUnknown)
        var status = AudioHardwareCreateProcessTap(description, &tap)
        guard status == noErr else { throw CaptureError.tap(status) }
        tapID = tap

        guard let outputID = AudioDevices.defaultOutputDevice(), let outputUID = AudioDevices.uid(of: outputID) else {
            stop(); throw CaptureError.aggregate(-1)
        }
        let aggregate: [String: Any] = [
            kAudioAggregateDeviceNameKey: "VoiceFlow Notetaker",
            kAudioAggregateDeviceUIDKey: UUID().uuidString,
            kAudioAggregateDeviceMainSubDeviceKey: outputUID,
            kAudioAggregateDeviceIsPrivateKey: true,
            kAudioAggregateDeviceIsStackedKey: false,
            kAudioAggregateDeviceTapAutoStartKey: true,
            kAudioAggregateDeviceSubDeviceListKey: [[kAudioSubDeviceUIDKey: outputUID]],
            kAudioAggregateDeviceTapListKey: [[kAudioSubTapDriftCompensationKey: true,
                                               kAudioSubTapUIDKey: description.uuid.uuidString]],
        ]
        var agg = AudioObjectID(kAudioObjectUnknown)
        status = AudioHardwareCreateAggregateDevice(aggregate as CFDictionary, &agg)
        guard status == noErr else { stop(); throw CaptureError.aggregate(status) }
        aggregateID = agg

        var asbd = AudioStreamBasicDescription()
        var size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
        var address = AudioObjectPropertyAddress(mSelector: kAudioTapPropertyFormat, mScope: kAudioObjectPropertyScopeGlobal,
                                                 mElement: kAudioObjectPropertyElementMain)
        status = AudioObjectGetPropertyData(tapID, &address, 0, nil, &size, &asbd)
        guard status == noErr, let format = AVAudioFormat(streamDescription: &asbd),
              let target = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: AudioMath.sampleRate, channels: 1, interleaved: false),
              let converter = AVAudioConverter(from: format, to: target) else { stop(); throw CaptureError.format }

        let block = Self.makeIOBlock(format: format, target: target, converter: converter, sink: buffer, counter: counter)
        status = AudioDeviceCreateIOProcIDWithBlock(&procID, aggregateID, queue, block)
        guard status == noErr else { stop(); throw CaptureError.start(status) }
        status = AudioDeviceStart(aggregateID, procID)
        guard status == noErr else { stop(); throw CaptureError.start(status) }
        isRunning = true
        info.tapSampleRate = format.sampleRate
        info.tapChannels = Int(format.channelCount)
        log.info("System audio capture started (\(format.sampleRate, privacy: .public) Hz, \(format.channelCount) ch)")
    }

    /// Built outside any actor: Core Audio calls it on its own real-time queue.
    nonisolated static func makeIOBlock(format: AVAudioFormat, target: AVAudioFormat, converter: AVAudioConverter,
                                        sink: SampleBuffer, counter: CallbackCounter) -> AudioDeviceIOBlock {
        { _, inputData, _, _, _ in
            counter.callback()
            guard let pcm = AVAudioPCMBuffer(pcmFormat: format, bufferListNoCopy: inputData, deallocator: nil),
                  pcm.frameLength > 0 else { return }
            let capacity = AVAudioFrameCount(Double(pcm.frameLength) * target.sampleRate / format.sampleRate) + 32
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
            counter.add(samples: Int(out.frameLength))
            sink.append(Array(UnsafeBufferPointer(start: channel, count: Int(out.frameLength))))
        }
    }

    public var diagnostics: SystemAudioDiagnostics {
        var d = info
        d.running = isRunning
        d.tapID = tapID
        d.aggregateID = aggregateID
        let counts = counter.counts
        d.callbacks = counts.callbacks
        d.samplesDelivered = counts.samples
        return d
    }

    /// Takes everything recorded since the last call.
    public func drain() -> [Float] { buffer.drain() }

    public func stop() {
        isRunning = false
        if let outputListener {
            AudioObjectRemovePropertyListenerBlock(AudioObjectID(kAudioObjectSystemObject), &outputAddress, queue, outputListener)
        }
        outputListener = nil
        teardownTap()
    }

    private func teardownTap() {
        if aggregateID != kAudioObjectUnknown {
            if let procID {
                AudioDeviceStop(aggregateID, procID)
                AudioDeviceDestroyIOProcID(aggregateID, procID)
            }
            AudioHardwareDestroyAggregateDevice(aggregateID)
        }
        if tapID != kAudioObjectUnknown { AudioHardwareDestroyProcessTap(tapID) }
        procID = nil
        aggregateID = kAudioObjectUnknown
        tapID = kAudioObjectUnknown
    }

    deinit { stop() }
}

/// Counts Core Audio callbacks from the real-time queue.
final class CallbackCounter: @unchecked Sendable {
    private let lock = OSAllocatedUnfairLock()
    private var callbacks = 0
    private var samples = 0
    func callback() { lock.withLock { callbacks += 1 } }
    func add(samples n: Int) { lock.withLock { samples += n } }
    var counts: (callbacks: Int, samples: Int) { lock.withLock { (callbacks, samples) } }
}
