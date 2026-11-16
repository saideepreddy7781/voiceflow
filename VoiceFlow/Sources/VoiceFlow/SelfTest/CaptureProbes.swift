@preconcurrency import AVFoundation
import AudioToolbox
import CoreAudio
import Foundation

/// Counts what a probe received: buffers, frames, and how many buffers were exact zeros.
final class ProbeCounts: @unchecked Sendable {
    private let lock = NSLock()
    private var buffers = 0
    private var frames = 0
    private var zeroBuffers = 0
    private var sumSquares = 0.0
    private var peak: Float = 0

    func add(_ data: UnsafePointer<Float>, count: Int, stride: Int = 1) {
        var zero = true
        var sum = 0.0
        var top: Float = 0
        var i = 0
        while i < count * stride {
            let v = data[i]
            if abs(v) >= 0.000_001 { zero = false }
            sum += Double(v * v)
            top = max(top, abs(v))
            i += stride
        }
        lock.lock()
        buffers += 1
        frames += count
        if zero { zeroBuffers += 1 }
        sumSquares += sum
        peak = max(peak, top)
        lock.unlock()
    }

    var result: ProbeResult {
        lock.lock(); defer { lock.unlock() }
        let rms = frames > 0 ? (sumSquares / Double(frames)).squareRoot() : 0
        return ProbeResult(buffers: buffers, frames: frames, zeroBuffers: zeroBuffers,
                           db: rms > 0 ? (20 * log10(rms)).rounded() : -120, peakDB: peak > 0 ? (20 * log10(Double(peak))).rounded() : -120)
    }
}

struct ProbeResult: Codable {
    var method = ""
    var buffers = 0
    var frames = 0
    var zeroBuffers = 0
    var db = -120.0
    var peakDB = -120.0
    var error: String?

    init(buffers: Int, frames: Int, zeroBuffers: Int, db: Double, peakDB: Double) {
        self.buffers = buffers
        self.frames = frames
        self.zeroBuffers = zeroBuffers
        self.db = db
        self.peakDB = peakDB
    }
}

/// The audio unit call apps use for echo cancellation (WebRTC in Chrome, Zoom, FaceTime): input on, silence out.
final class VoiceProcessingUnit: @unchecked Sendable {
    private var unit: AudioUnit?
    let counts = ProbeCounts()
    private var scratch = [Float](repeating: 0, count: 4096)

    func start() throws {
        var desc = AudioComponentDescription(componentType: kAudioUnitType_Output, componentSubType: kAudioUnitSubType_VoiceProcessingIO,
                                             componentManufacturer: kAudioUnitManufacturer_Apple, componentFlags: 0, componentFlagsMask: 0)
        guard let component = AudioComponentFindNext(nil, &desc) else { throw ProbeError.status("find", -1) }
        var made: AudioUnit?
        try check("new", AudioComponentInstanceNew(component, &made))
        guard let unit = made else { throw ProbeError.status("new", -1) }
        self.unit = unit
        var one: UInt32 = 1
        try check("enable input", AudioUnitSetProperty(unit, kAudioOutputUnitProperty_EnableIO, kAudioUnitScope_Input, 1, &one, 4))
        var format = AudioStreamBasicDescription(mSampleRate: 48_000, mFormatID: kAudioFormatLinearPCM,
                                                 mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked | kAudioFormatFlagIsNonInterleaved,
                                                 mBytesPerPacket: 4, mFramesPerPacket: 1, mBytesPerFrame: 4, mChannelsPerFrame: 1,
                                                 mBitsPerChannel: 32, mReserved: 0)
        let size = UInt32(MemoryLayout<AudioStreamBasicDescription>.size)
        try check("input format", AudioUnitSetProperty(unit, kAudioUnitProperty_StreamFormat, kAudioUnitScope_Output, 1, &format, size))
        try check("output format", AudioUnitSetProperty(unit, kAudioUnitProperty_StreamFormat, kAudioUnitScope_Input, 0, &format, size))
        let me = Unmanaged.passUnretained(self).toOpaque()
        var input = AURenderCallbackStruct(inputProc: { refCon, flags, time, bus, frames, _ in
            let probe = Unmanaged<VoiceProcessingUnit>.fromOpaque(refCon).takeUnretainedValue()
            return probe.pull(flags: flags, time: time, bus: bus, frames: frames)
        }, inputProcRefCon: me)
        try check("input callback", AudioUnitSetProperty(unit, kAudioOutputUnitProperty_SetInputCallback, kAudioUnitScope_Global, 0,
                                                         &input, UInt32(MemoryLayout<AURenderCallbackStruct>.size)))
        var render = AURenderCallbackStruct(inputProc: { _, flags, _, _, _, data in
            // Play silence.
            if let data {
                for buffer in UnsafeMutableAudioBufferListPointer(data) {
                    if let p = buffer.mData { memset(p, 0, Int(buffer.mDataByteSize)) }
                }
            }
            flags.pointee.insert(.unitRenderAction_OutputIsSilence)
            return noErr
        }, inputProcRefCon: nil)
        try check("render callback", AudioUnitSetProperty(unit, kAudioUnitProperty_SetRenderCallback, kAudioUnitScope_Input, 0,
                                                          &render, UInt32(MemoryLayout<AURenderCallbackStruct>.size)))
        // Least ducking of other audio, so the speech the self-test plays stays loud enough for the microphone.
        var ducking = AUVoiceIOOtherAudioDuckingConfiguration(mEnableAdvancedDucking: false, mDuckingLevel: .min)
        AudioUnitSetProperty(unit, kAUVoiceIOProperty_OtherAudioDuckingConfiguration, kAudioUnitScope_Global, 0,
                             &ducking, UInt32(MemoryLayout<AUVoiceIOOtherAudioDuckingConfiguration>.size))
        try check("initialize", AudioUnitInitialize(unit))
        try check("start", AudioOutputUnitStart(unit))
    }

    private func pull(flags: UnsafeMutablePointer<AudioUnitRenderActionFlags>, time: UnsafePointer<AudioTimeStamp>,
                      bus: UInt32, frames: UInt32) -> OSStatus {
        guard let unit, Int(frames) <= scratch.count else { return noErr }
        return scratch.withUnsafeMutableBufferPointer { buffer in
            var list = AudioBufferList(mNumberBuffers: 1, mBuffers: AudioBuffer(mNumberChannels: 1, mDataByteSize: frames * 4,
                                                                               mData: UnsafeMutableRawPointer(buffer.baseAddress)))
            let status = AudioUnitRender(unit, flags, time, bus, frames, &list)
            if status == noErr, let base = buffer.baseAddress { counts.add(base, count: Int(frames)) }
            return status
        }
    }

    func stop() {
        guard let unit else { return }
        AudioOutputUnitStop(unit)
        AudioUnitUninitialize(unit)
        AudioComponentInstanceDispose(unit)
        self.unit = nil
    }

    private func check(_ what: String, _ status: OSStatus) throws {
        if status != noErr { throw ProbeError.status(what, status) }
    }
}

enum ProbeError: Error, CustomStringConvertible {
    case status(String, OSStatus)
    var description: String {
        switch self { case .status(let what, let s): "\(what) failed (\(s))" }
    }
}

/// Reads the default input device straight from the HAL with an IO proc, below AVAudioEngine.
final class HALInputProbe: @unchecked Sendable {
    let counts = ProbeCounts()
    /// One counter per channel of the first stream.
    let channelCounts = (0..<8).map { _ in ProbeCounts() }
    private(set) var channels = 0
    private var device = AudioDeviceID(0)
    private var proc: AudioDeviceIOProcID?
    private let queue = DispatchQueue(label: "selftest.hal")

    func start() throws {
        var address = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDefaultInputDevice, mScope: kAudioObjectPropertyScopeGlobal,
                                                 mElement: kAudioObjectPropertyElementMain)
        var size = UInt32(MemoryLayout<AudioDeviceID>.size)
        var status = AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &address, 0, nil, &size, &device)
        guard status == noErr else { throw ProbeError.status("default input", status) }
        let counts = self.counts
        let perChannel = channelCounts
        let seen = ChannelSeen()
        self.seen = seen
        status = AudioDeviceCreateIOProcIDWithBlock(&proc, device, queue) { _, input, _, _, _ in
            let list = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: input))
            guard let first = list.first, let p = first.mData?.assumingMemoryBound(to: Float.self) else { return }
            let channels = Int(max(1, first.mNumberChannels))
            seen.set(channels)
            counts.add(p, count: Int(first.mDataByteSize) / 4 / channels, stride: channels)
            for c in 0..<min(channels, perChannel.count) {
                perChannel[c].add(p + c, count: Int(first.mDataByteSize) / 4 / channels, stride: channels)
            }
        }
        guard status == noErr else { throw ProbeError.status("io proc", status) }
        status = AudioDeviceStart(device, proc)
        guard status == noErr else { throw ProbeError.status("start", status) }
    }

    private var seen: ChannelSeen?
    var channelResults: [ProbeResult] { channelCounts.prefix(seen?.value ?? 0).map(\.result) }

    func stop() {
        guard let proc else { return }
        AudioDeviceStop(device, proc)
        AudioDeviceDestroyIOProcID(device, proc)
        self.proc = nil
    }
}

/// AVAudioEngine with Apple's voice processing switched on for our own input.
@MainActor
final class VoiceProcessingEngineProbe {
    let counts = ProbeCounts()
    private let engine = AVAudioEngine()

    func start() throws {
        try engine.inputNode.setVoiceProcessingEnabled(true)
        let format = engine.inputNode.outputFormat(forBus: 0)
        engine.inputNode.installTap(onBus: 0, bufferSize: 1024, format: format, block: Self.block(counts))
        engine.prepare()
        try engine.start()
    }

    nonisolated static func block(_ counts: ProbeCounts) -> AVAudioNodeTapBlock {
        { buffer, _ in
            if let p = buffer.floatChannelData?[0] { counts.add(p, count: Int(buffer.frameLength)) }
        }
    }

    func stop() {
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
    }
}

/// AVCaptureSession with an audio data output, the camera-and-mic capture stack.
final class CaptureSessionProbe: NSObject, AVCaptureAudioDataOutputSampleBufferDelegate, @unchecked Sendable {
    let counts = ProbeCounts()
    private let session = AVCaptureSession()
    private let queue = DispatchQueue(label: "selftest.capture")

    func start() throws {
        guard let device = AVCaptureDevice.default(for: .audio) else { throw ProbeError.status("device", -1) }
        let input = try AVCaptureDeviceInput(device: device)
        let output = AVCaptureAudioDataOutput()
        output.audioSettings = [AVFormatIDKey: kAudioFormatLinearPCM, AVLinearPCMIsFloatKey: true, AVLinearPCMBitDepthKey: 32,
                                AVLinearPCMIsNonInterleaved: false, AVNumberOfChannelsKey: 1]
        output.setSampleBufferDelegate(self, queue: queue)
        session.beginConfiguration()
        guard session.canAddInput(input), session.canAddOutput(output) else { throw ProbeError.status("configure", -1) }
        session.addInput(input)
        session.addOutput(output)
        session.commitConfiguration()
        session.startRunning()
    }

    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        guard let block = CMSampleBufferGetDataBuffer(sampleBuffer) else { return }
        var length = 0
        var pointer: UnsafeMutablePointer<CChar>?
        guard CMBlockBufferGetDataPointer(block, atOffset: 0, lengthAtOffsetOut: nil, totalLengthOut: &length, dataPointerOut: &pointer) == noErr,
              let pointer else { return }
        pointer.withMemoryRebound(to: Float.self, capacity: length / 4) { counts.add($0, count: length / 4) }
    }

    func stop() { session.stopRunning() }
}

final class ChannelSeen: @unchecked Sendable {
    private let lock = NSLock()
    private var n = 0
    func set(_ v: Int) { lock.lock(); n = max(n, v); lock.unlock() }
    var value: Int { lock.lock(); defer { lock.unlock() }; return n }
}
