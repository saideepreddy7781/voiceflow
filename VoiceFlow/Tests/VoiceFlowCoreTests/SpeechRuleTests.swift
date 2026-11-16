import Foundation
import Testing
@testable import VoiceFlowCore

/// Speech-like test audio: syllables of a voiced harmonic sound with gaps and word pauses, over a noise floor.
enum Voice {
    /// Deterministic white noise at `db` dBFS RMS.
    static func noise(_ seconds: Double, db: Float, seed: UInt64 = 7) -> [Float] {
        var state = seed
        let amplitude = Float(pow(10, Double(db) / 20) * 3.0.squareRoot())   // uniform -a...a has RMS a/√3
        return (0..<Int(seconds * 16_000)).map { _ in
            state = state &* 6364136223846793005 &+ 1442695040888963407
            return (Float(state >> 40) / Float(1 << 24) * 2 - 1) * amplitude
        }
    }

    /// Speech at about `db` dBFS RMS while voiced, over a room at `roomDB`.
    static func speech(_ seconds: Double, db: Float = -26, roomDB: Float = -62) -> [Float] {
        let rate = 16_000
        var voice = [Float](repeating: 0, count: Int(seconds * Double(rate)))
        let syllable = Int(0.18 * Double(rate)), gap = Int(0.07 * Double(rate)), wordPause = Int(0.25 * Double(rate))
        var i = Int(0.3 * Double(rate))
        var n = 0
        while i + syllable < voice.count {
            let f0 = Float(120 + (n % 5) * 12)
            for k in 0..<syllable {
                let t = Float(k) / Float(rate)
                let envelope = sin(Float.pi * Float(k) / Float(syllable))
                var v: Float = 0
                for h in 1...6 { v += sin(2 * .pi * f0 * Float(h) * t) / Float(h) }
                voice[i + k] = v * envelope
            }
            n += 1
            i += syllable + (n % 3 == 0 ? wordPause : gap)
        }
        let voicedRMS = AudioMath.rms(voice.filter { $0 != 0 }[...])
        let scale = Float(pow(10, Double(db) / 20)) / voicedRMS
        let room = noise(seconds, db: roomDB)
        return zip(voice, room).map { $0 * scale + $1 }
    }

    static func attenuated(_ samples: [Float], by db: Float) -> [Float] {
        let g = Float(pow(10, Double(-db) / 20))
        return samples.map { $0 * g }
    }
}

struct SpeechRuleTests {
    /// The old fixed rule, for the before-and-after comparison.
    private func oldVoicedSeconds(_ samples: [Float]) -> Double { AudioMath.voicedSeconds(samples, thresholdDB: -45) }

    @Test func normalSpeechIsSpeechAndIsNotRaised() {
        let s = Voice.speech(3)
        let a = SpeechRule.analyze(s)
        #expect(a.voicedSeconds > 1.2)
        #expect(AudioMath.captureVerdict(s, heldSeconds: 3) == .speech)
        #expect(SpeechRule.uploadGainDB(a) < 3)
    }

    /// The built-in microphone under a call's voice processing: everything about 10 to 15 dB down, or more when the
    /// call also ducks the speakers in a test. The old -45 dBFS floor lost it; the floor-relative rule keeps it.
    @Test func speechTenToTwentyFiveDecibelsDownIsStillSpeechAndIsRaised() {
        let normal = Voice.speech(3)
        for drop: Float in [10, 15, 20, 25] {
            let quiet = Voice.attenuated(normal, by: drop)
            let a = SpeechRule.analyze(quiet)
            #expect(a.voicedSeconds > 1.2, "drop \(drop) dB")
            #expect(!AudioMath.isSilent(quiet), "drop \(drop) dB")
            #expect(AudioMath.captureVerdict(quiet, heldSeconds: 3) == .speech, "drop \(drop) dB")
            #expect(!HallucinationFilter.isLikelyHallucination("Thank you.", voicedSeconds: a.voicedSeconds))
            // Raised back to the upload level, or by the most gain allowed.
            let raised = SpeechRule.analyze(SpeechRule.prepareForUpload(quiet))
            let expected = min(SpeechRule.uploadSpeechDB, a.speechDB + SpeechRule.maximumGainDB)
            #expect(abs(raised.speechDB - expected) < 1.5, "drop \(drop) dB: \(raised.speechDB)")
        }
        // Before: 25 dB down (what the self-test measured under a call) the fixed floor called it silence.
        #expect(oldVoicedSeconds(Voice.attenuated(normal, by: 25)) < 0.25)
    }

    /// Free-tier budget: a room with nobody talking is never uploaded, at any noise level.
    @Test func roomNoiseIsNeverSpeech() {
        for db: Float in [-80, -72, -60, -50, -45, -40, -36] {
            let room = Voice.noise(5, db: db, seed: UInt64(100 - Int(db)))
            #expect(AudioMath.isSilent(room), "room at \(db) dB")
            #expect(AudioMath.captureVerdict(room, heldSeconds: 5) == .noSpeech, "room at \(db) dB")
        }
        // Before: a noisy room above the fixed floor counted as speech and was uploaded.
        #expect(oldVoicedSeconds(Voice.noise(5, db: -40)) > 0.25)
    }

    @Test func digitalSilenceAndClicksStaySilent() {
        #expect(AudioMath.isSilent([Float](repeating: 0, count: 48_000)))
        var clicks = [Float](repeating: 0, count: 48_000)
        for k in stride(from: 1_000, to: 48_000, by: 8_000) { clicks[k] = 0.8 }
        #expect(AudioMath.isSilent(clicks))
        #expect(SpeechRule.analyze([Float](repeating: 0, count: 16_000)).noiseFloorDB == nil)
    }

    @Test func aMicrophoneThatGatesToZeroBetweenWordsStillCountsTheWords() {
        let gated = Voice.speech(3, db: -30, roomDB: -200).map { abs($0) < 0.000_01 ? 0 : $0 }
        #expect(!AudioMath.isSilent(gated))
    }

    @Test func thresholdFollowsTheFloorWithinLimits() {
        #expect(SpeechRule.threshold(noiseFloorDB: nil) == SpeechRule.absoluteMinimumDB)
        #expect(SpeechRule.threshold(noiseFloorDB: -100) == SpeechRule.absoluteMinimumDB)
        #expect(SpeechRule.threshold(noiseFloorDB: -60) == -50)
        #expect(SpeechRule.threshold(noiseFloorDB: -20) == SpeechRule.ceilingDB)
    }

    @Test func gainIsCappedAndSilenceIsNotRaised() {
        #expect(SpeechRule.prepareForUpload([Float](repeating: 0, count: 16_000)) == [Float](repeating: 0, count: 16_000))
        let faint = Voice.attenuated(Voice.speech(2), by: 40)
        let gain = SpeechRule.uploadGainDB(SpeechRule.analyze(faint))
        #expect(gain == SpeechRule.maximumGainDB)
    }
}

struct QuietMeetingChunkTests {
    @Test func quietCallSpeechIsChunkedAndRoomNoiseIsNot() {
        var chunker = StreamChunker()
        var chunks: [AudioChunk] = []
        // 60 s of speech 20 dB down, as heard through a voice-processing call.
        for _ in 0..<20 { chunks += chunker.feed(Voice.attenuated(Voice.speech(3), by: 20)) }
        if let last = chunker.finish() { chunks.append(last) }
        let voiced = chunks.reduce(0) { $0 + $1.voicedSeconds }
        #expect(voiced > 25)

        var quietRoom = StreamChunker()
        var noiseChunks: [AudioChunk] = []
        for k in 0..<60 { noiseChunks += quietRoom.feed(Voice.noise(5, db: -50, seed: UInt64(k + 1))) }
        if let last = quietRoom.finish() { noiseChunks.append(last) }
        #expect(noiseChunks.isEmpty)
    }

    @Test func speechAfterALongDigitalSilenceIsHeardAtOnce() {
        var gate = VoiceGate()
        var anyVoiced = false
        for _ in 0..<600 where gate.isVoiced(frameDB: -120) { anyVoiced = true }
        #expect(!anyVoiced)
        let heard = gate.isVoiced(frameDB: -30)
        #expect(heard)
    }
}
