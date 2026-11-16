@preconcurrency import AVFoundation
import AudioToolbox
import CoreAudio
import Foundation
import os

/// Which input the microphone is reading, and in what format. Compared before and after a Core Audio
/// notification to decide whether the capture has to be rebuilt.
struct InputRoute: Equatable, Sendable {
    var deviceID: AudioDeviceID
    var sampleRate: Double
    var channels: Int
    var alive: Bool
    /// True when no device was chosen, so the system default input is followed.
    var followsDefault: Bool
    var defaultInputID: AudioDeviceID

    /// A rebuild is needed when the device went away, its sample rate changed, or the default input moved while
    /// following it. A channel-count change is absorbed by the IO block, and anything else (a volume change,
    /// another app starting the device) leaves capture alone.
    static func needsRebuild(running: InputRoute, now: InputRoute) -> Bool {
        if !now.alive { return true }
        if running.followsDefault, now.defaultInputID != 0, now.defaultInputID != running.deviceID { return true }
        return now.sampleRate > 0 && now.sampleRate != running.sampleRate
    }
}

/// The real engine: a Core Audio IO proc on the input device, converted to 16 kHz mono.
///
/// It reads the device directly instead of through AVAudioEngine. While a call app (Chrome running Google Meet,
/// Zoom, FaceTime) holds the microphone with Apple's voice processing, an AVAudioEngine input in another app
/// receives exact zeros for as long as the call lasts; a device IO proc keeps receiving the microphone.
@MainActor
final class LiveMicrophoneEngine: MicrophoneEngine {
    var onConfigurationChange: (@MainActor () -> Void)?

    private let log = Logger(subsystem: "com.saideep.voiceflow", category: "audio")
    private let queue = DispatchQueue(label: "com.saideep.voiceflow.microphone", qos: .userInteractive)
    private var device = AudioDeviceID(kAudioObjectUnknown)
    private var procID: AudioDeviceIOProcID?
    private var route: InputRoute?
    private var listeners: [(AudioObjectID, AudioObjectPropertyAddress, AudioObjectPropertyListenerBlock)] = []
    private var configurationChanges = 0
    private var configurationChangesIgnored = 0
    private var pendingCheck = false

    func start(deviceUID: String?, sink: any SampleSink) throws {
        guard AudioRecorder.microphoneStatus == .authorized else { throw AudioRecorder.RecorderError.microphoneDenied }
        let chosen = deviceUID.flatMap { AudioDevices.device(uid: $0)?.id }
        guard let id = chosen ?? AudioDevices.defaultDevice(kAudioHardwarePropertyDefaultInputDevice) else {
            throw AudioRecorder.RecorderError.noInput
        }
        guard let asbd = Self.inputFormat(of: id), asbd.mSampleRate > 0, asbd.mChannelsPerFrame > 0 else {
            throw AudioRecorder.RecorderError.noInput
        }
        // IO procs receive the stream's virtual format, which is 32-bit float on every Mac input.
        guard asbd.mFormatID == kAudioFormatLinearPCM, asbd.mFormatFlags & kAudioFormatFlagIsFloat != 0, asbd.mBitsPerChannel == 32,
              let mono = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: asbd.mSampleRate, channels: 1, interleaved: false),
              let target = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: AudioMath.sampleRate, channels: 1, interleaved: false),
              let converter = AVAudioConverter(from: mono, to: target) else { throw AudioRecorder.RecorderError.converter }

        let block = Self.makeIOBlock(deviceRate: mono, target: target, converter: converter, sink: sink)
        var proc: AudioDeviceIOProcID?
        var status = AudioDeviceCreateIOProcIDWithBlock(&proc, id, queue, block)
        guard status == noErr, let proc else { throw AudioRecorder.RecorderError.engine("IO proc (\(status))") }
        device = id
        procID = proc
        route = currentRoute(followsDefault: chosen == nil)
        // Listening before the device starts, so a change during startup is not missed.
        addListeners()
        status = AudioDeviceStart(id, proc)
        guard status == noErr else {
            stop()
            throw AudioRecorder.RecorderError.engine("start (\(status))")
        }
        log.info("Recording started (\(asbd.mSampleRate, privacy: .public) Hz, \(asbd.mChannelsPerFrame) ch input)")
    }

    func stop() {
        removeListeners()
        if let procID, device != kAudioObjectUnknown {
            AudioDeviceStop(device, procID)
            AudioDeviceDestroyIOProcID(device, procID)
        }
        procID = nil
        device = AudioDeviceID(kAudioObjectUnknown)
        route = nil
    }

    func describe(into d: inout MicEngineDiagnostics) {
        d.configurationChanges = configurationChanges
        d.configurationChangesIgnored = configurationChangesIgnored
        guard let route else { return }
        d.engineRunning = procID != nil && Self.isRunning(device)
        d.inputSampleRate = route.sampleRate
        d.inputChannels = route.channels
        d.inputDeviceID = route.deviceID
    }

    // MARK: Changes

    private func currentRoute(followsDefault: Bool) -> InputRoute {
        let asbd = Self.inputFormat(of: device)
        return InputRoute(deviceID: device, sampleRate: asbd?.mSampleRate ?? 0, channels: Int(asbd?.mChannelsPerFrame ?? 0),
                          alive: Self.isAlive(device), followsDefault: followsDefault,
                          defaultInputID: AudioDevices.defaultDevice(kAudioHardwarePropertyDefaultInputDevice) ?? 0)
    }

    /// Core Audio sends bursts of notifications while a device switches. They are coalesced, then the route is compared.
    private func noteChange() {
        configurationChanges += 1
        guard !pendingCheck else { return }
        pendingCheck = true
        Task { @MainActor [weak self] in
            try? await Task.sleep(for: .milliseconds(100))
            guard let self else { return }
            self.pendingCheck = false
            guard let running = self.route else { return }
            let now = self.currentRoute(followsDefault: running.followsDefault)
            if InputRoute.needsRebuild(running: running, now: now) {
                self.log.notice("Input changed (device \(now.deviceID), \(now.sampleRate, privacy: .public) Hz); rebuilding")
                self.onConfigurationChange?()
            } else {
                self.configurationChangesIgnored += 1
            }
        }
    }

    private func addListeners() {
        let system = AudioObjectID(kAudioObjectSystemObject)
        listen(system, kAudioHardwarePropertyDefaultInputDevice, kAudioObjectPropertyScopeGlobal)
        listen(system, kAudioHardwarePropertyDevices, kAudioObjectPropertyScopeGlobal)
        listen(device, kAudioDevicePropertyDeviceIsAlive, kAudioObjectPropertyScopeGlobal)
        listen(device, kAudioDevicePropertyNominalSampleRate, kAudioObjectPropertyScopeGlobal)
        listen(device, kAudioDevicePropertyStreamFormat, kAudioDevicePropertyScopeInput)
        listen(device, kAudioDevicePropertyStreams, kAudioDevicePropertyScopeInput)
    }

    private func listen(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector, _ scope: AudioObjectPropertyScope) {
        var address = AudioObjectPropertyAddress(mSelector: selector, mScope: scope, mElement: kAudioObjectPropertyElementMain)
        let block: AudioObjectPropertyListenerBlock = { [weak self] _, _ in
            MainActor.assumeIsolated { self?.noteChange() }
        }
        if AudioObjectAddPropertyListenerBlock(object, &address, DispatchQueue.main, block) == noErr {
            listeners.append((object, address, block))
        }
    }

    private func removeListeners() {
        for (object, address, block) in listeners {
            var address = address
            AudioObjectRemovePropertyListenerBlock(object, &address, DispatchQueue.main, block)
        }
        listeners = []
    }

    // MARK: Core Audio

    /// The format the IO proc receives: the first input stream's virtual format.
    nonisolated static func inputFormat(of device: AudioDeviceID) -> AudioStreamBasicDescription? {
        guard device != kAudioObjectUnknown else { return nil }
        var address = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyStreams, mScope: kAudioDevicePropertyScopeInput,
                                                 mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(device, &address, 0, nil, &size) == noErr, size >= UInt32(MemoryLayout<AudioStreamID>.size) else { return nil }
        var streams = [AudioStreamID](repeating: 0, count: Int(size) / MemoryLayout<AudioStreamID>.size)
        guard AudioObjectGetPropertyData(device, &address, 0, nil, &size, &streams) == noErr, let first = streams.first else { return nil }
        var asbd = AudioStreamBasicDescription()
        size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
        address = AudioObjectPropertyAddress(mSelector: kAudioStreamPropertyVirtualFormat, mScope: kAudioObjectPropertyScopeGlobal,
                                             mElement: kAudioObjectPropertyElementMain)
        guard AudioObjectGetPropertyData(first, &address, 0, nil, &size, &asbd) == noErr else { return nil }
        return asbd
    }

    nonisolated static func isAlive(_ device: AudioDeviceID) -> Bool {
        uint32(device, kAudioDevicePropertyDeviceIsAlive) != 0
    }

    nonisolated static func isRunning(_ device: AudioDeviceID) -> Bool {
        uint32(device, kAudioDevicePropertyDeviceIsRunning) != 0
    }

    private nonisolated static func uint32(_ device: AudioDeviceID, _ selector: AudioObjectPropertySelector) -> UInt32 {
        guard device != kAudioObjectUnknown else { return 0 }
        var address = AudioObjectPropertyAddress(mSelector: selector, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var value = UInt32(0)
        var size = UInt32(MemoryLayout<UInt32>.size)
        return AudioObjectGetPropertyData(device, &address, 0, nil, &size, &value) == noErr ? value : 0
    }

    /// Built outside any actor: Core Audio calls it on its own real-time queue.
    ///
    /// Only the first channel of the first buffer is read. The channel count is taken from each buffer, because it
    /// changes under a running capture: the built-in microphone goes from 1 channel to 3 (the raw microphone array)
    /// when a call app turns on voice processing. A fixed format turned that into no audio at all, and averaging the
    /// three array channels cost 9 dB more than reading one, because they partly cancel.
    nonisolated static func makeIOBlock(deviceRate mono: AVAudioFormat, target: AVAudioFormat, converter: AVAudioConverter,
                                        sink: any SampleSink) -> AudioDeviceIOBlock {
        { _, inputData, _, _, _ in
            let list = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: inputData))
            guard list.count > 0, let data = list[0].mData?.assumingMemoryBound(to: Float.self) else { return }
            let channels = Int(max(1, list[0].mNumberChannels))
            let frames = Int(list[0].mDataByteSize) / 4 / channels
            guard frames > 0, let pcm = AVAudioPCMBuffer(pcmFormat: mono, frameCapacity: AVAudioFrameCount(frames)),
                  let out = pcm.floatChannelData?[0] else { return }
            pcm.frameLength = AVAudioFrameCount(frames)
            if channels == 1 {
                out.update(from: data, count: frames)
            } else {
                for f in 0..<frames { out[f] = data[f * channels] }
            }
            convert(pcm, target: target, converter: converter, sink: sink)
        }
    }

    nonisolated private static func convert(_ pcm: AVAudioPCMBuffer, target: AVAudioFormat, converter: AVAudioConverter,
                                            sink: any SampleSink) {
        let capacity = AVAudioFrameCount(Double(pcm.frameLength) * target.sampleRate / pcm.format.sampleRate) + 32
        guard let out = AVAudioPCMBuffer(pcmFormat: target, frameCapacity: capacity) else { return }
        let fed = Flag()
        var error: NSError?
        converter.convert(to: out, error: &error) { _, status in
            if fed.value { status.pointee = .noDataNow; return nil }
            fed.value = true
            status.pointee = .haveData
            return pcm
        }
        guard error == nil, let channel = out.floatChannelData?[0], out.frameLength > 0 else { return }
        sink.append(Array(UnsafeBufferPointer(start: channel, count: Int(out.frameLength))))
    }
}
