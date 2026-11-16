@preconcurrency import AVFoundation
import CoreAudio
import Foundation
import Testing
@testable import VoiceFlowCore

struct AudioRecorderTests {
    /// Runs the microphone IO block the way Core Audio does: off the main thread, on the device's buffers.
    private func feed(format: AVAudioFormat, buffers: [[Float]], blocks: Int = 5) async -> SampleBuffer {
        let target = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 16_000, channels: 1, interleaved: false)!
        let mono = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: format.sampleRate, channels: 1, interleaved: false)!
        let converter = AVAudioConverter(from: mono, to: target)!
        let sink = SampleBuffer()
        let block = LiveMicrophoneEngine.makeIOBlock(deviceRate: mono, target: target, converter: converter, sink: sink)
        let storage = buffers
        await withCheckedContinuation { (done: CheckedContinuation<Void, Never>) in
            DispatchQueue.global(qos: .userInteractive).async {
                #expect(!Thread.isMainThread)
                var copies = storage
                let list = AudioBufferList.allocate(maximumBuffers: copies.count)
                for i in copies.indices {
                    copies[i].withUnsafeMutableBufferPointer { p in
                        list[i] = AudioBuffer(mNumberChannels: format.isInterleaved ? format.channelCount : 1,
                                              mDataByteSize: UInt32(p.count * 4), mData: UnsafeMutableRawPointer(p.baseAddress))
                    }
                }
                var time = AudioTimeStamp()
                for _ in 0..<blocks { block(&time, list.unsafePointer, &time, list.unsafeMutablePointer, &time) }
                free(list.unsafeMutablePointer)
                done.resume()
            }
        }
        return sink
    }

    private func tone(_ frames: Int, channels: Int = 1) -> [Float] {
        (0..<(frames * channels)).map { 0.3 * sin(Float($0 / channels) * 0.05) }
    }

    /// Regression: Core Audio calls this block on its own thread. A main-actor-isolated block crashed
    /// VoiceFlow on 2026-10-17 (EXC_BREAKPOINT in dispatch_assert_queue).
    @Test func ioBlockRunsOffTheMainThreadAndResamples() async {
        let mono = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 48_000, channels: 1, interleaved: false)!
        let sink = await feed(format: mono, buffers: [tone(4_800)])
        // 5 × 100 ms at 48 kHz ≈ 8,000 samples at 16 kHz (the converter may hold a few back).
        #expect(sink.count > 7_000 && sink.count <= 8_000)
        #expect(!MicHealthMonitor.isDigitallySilent(sink.snapshot()))
    }

    @Test func interleavedStereoReadsTheFirstChannel() async {
        let stereo = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 48_000, channels: 2, interleaved: true)!
        // Left carries the tone, right is silent: the result must be the tone at full level.
        let left = tone(4_800)
        let frames = (0..<4_800).flatMap { [left[$0], Float(0)] }
        let sink = await feed(format: stereo, buffers: [frames])
        #expect(sink.count > 7_000 && sink.count <= 8_000)
        let level = AudioMath.dbfs(AudioMath.rms(sink.snapshot()[2_000...]))
        #expect(level > -15)
    }

    @Test func onlyTheFirstStreamOfAMultiStreamDeviceIsRead() async {
        let stereo = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 48_000, channels: 2, interleaved: true)!
        let sink = await feed(format: stereo, buffers: [tone(4_800, channels: 2), [Float](repeating: 0, count: 9_600)])
        #expect(sink.count > 7_000 && sink.count <= 8_000)
        #expect(!MicHealthMonitor.isDigitallySilent(sink.snapshot()))
    }

    @Test func nonInterleavedInputReadsTheFirstChannel() async {
        let planar = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 44_100, channels: 2, interleaved: false)!
        let sink = await feed(format: planar, buffers: [tone(4_410), tone(4_410)])
        #expect(sink.count > 7_000 && sink.count <= 8_000)
    }

    /// The built-in microphone becomes a 3-channel array while a call app uses voice processing, under a capture
    /// that started with 1 channel. Regression: that was no audio for the rest of the meeting.
    @Test func aChannelCountChangeUnderARunningCaptureKeepsAudioFlowing() async {
        let threeChannels = AVAudioFormat(streamDescription: [AudioStreamBasicDescription(
            mSampleRate: 48_000, mFormatID: kAudioFormatLinearPCM, mFormatFlags: kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked,
            mBytesPerPacket: 12, mFramesPerPacket: 1, mBytesPerFrame: 12, mChannelsPerFrame: 3, mBitsPerChannel: 32, mReserved: 0)],
            channelLayout: AVAudioChannelLayout(layoutTag: kAudioChannelLayoutTag_DiscreteInOrder | 3)!)!
        let sink = await feed(format: threeChannels, buffers: [tone(4_800, channels: 3)])
        #expect(sink.count > 7_000 && sink.count <= 8_000)
        #expect(!MicHealthMonitor.isDigitallySilent(sink.snapshot()))
    }
}
