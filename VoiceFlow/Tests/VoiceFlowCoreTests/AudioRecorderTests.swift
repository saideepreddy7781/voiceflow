@preconcurrency import AVFoundation
import Foundation
import Testing
@testable import VoiceFlowCore

struct AudioRecorderTests {
    /// Regression: CoreAudio calls the tap block on its own thread. A main-actor-isolated block crashed
    /// VoiceFlow on 2026-10-17 (EXC_BREAKPOINT in dispatch_assert_queue). Calling it off the main
    /// thread must not trap, and must convert 48 kHz input to 16 kHz.
    @Test func tapBlockRunsOffTheMainThreadAndResamples() async throws {
        let input = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 48_000, channels: 1, interleaved: false)!
        let target = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 16_000, channels: 1, interleaved: false)!
        let converter = AVAudioConverter(from: input, to: target)!
        let sink = SampleBuffer()
        let block = AudioRecorder.makeTapBlock(converter: converter, target: target, inputFormat: input, sink: sink)

        let pcm = AVAudioPCMBuffer(pcmFormat: input, frameCapacity: 4_800)!
        pcm.frameLength = 4_800
        for i in 0..<4_800 { pcm.floatChannelData![0][i] = 0.3 * sin(Float(i) * 0.05) }

        await withCheckedContinuation { (done: CheckedContinuation<Void, Never>) in
            DispatchQueue.global(qos: .userInteractive).async {
                #expect(!Thread.isMainThread)
                for _ in 0..<5 { block(pcm, AVAudioTime(sampleTime: 0, atRate: 48_000)) }
                done.resume()
            }
        }
        // 5 × 100 ms at 48 kHz ≈ 8,000 samples at 16 kHz (the converter may hold a few back).
        #expect(sink.count > 7_000 && sink.count <= 8_000)
    }
}
