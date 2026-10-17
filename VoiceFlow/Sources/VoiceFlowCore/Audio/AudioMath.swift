import Foundation

/// Pure audio helpers on 16 kHz mono Float32 samples in -1...1.
public enum AudioMath {
    public static let sampleRate = 16_000.0
    /// 30 ms analysis frames.
    public static let frameLength = 480

    public static func rms(_ samples: ArraySlice<Float>) -> Float {
        guard !samples.isEmpty else { return 0 }
        var sum: Float = 0
        for s in samples { sum += s * s }
        return (sum / Float(samples.count)).squareRoot()
    }

    public static func dbfs(_ rms: Float) -> Float {
        rms <= 0 ? -120 : 20 * log10(rms)
    }

    /// Seconds of audio louder than `thresholdDB`, measured in 30 ms frames.
    public static func voicedSeconds(_ samples: [Float], thresholdDB: Float = -45) -> Double {
        var voiced = 0
        var i = 0
        while i < samples.count {
            let end = min(i + frameLength, samples.count)
            if dbfs(rms(samples[i..<end])) > thresholdDB { voiced += end - i }
            i = end
        }
        return Double(voiced) / sampleRate
    }

    /// True when a recording has too little speech to send. Whisper invents text such as
    /// "Thank you." for silence, so silent recordings never reach the API.
    public static func isSilent(_ samples: [Float], minimumVoicedSeconds: Double = 0.25) -> Bool {
        voicedSeconds(samples) < minimumVoicedSeconds
    }

    /// Whisper mode: raises quiet speech to a usable level. Peak-normalizes to `targetPeak`,
    /// with gain capped so background noise in a silent room is not blown up.
    public static func normalized(_ samples: [Float], targetPeak: Float = 0.7, maxGain: Float = 12) -> [Float] {
        let peak = samples.reduce(Float(0)) { max($0, abs($1)) }
        guard peak > 0 else { return samples }
        let gain = min(targetPeak / peak, maxGain)
        guard gain > 1.05 else { return samples }
        return samples.map { max(-1, min(1, $0 * gain)) }
    }

    /// 16-bit PCM WAV, mono, 16 kHz.
    public static func wav(_ samples: [Float]) -> Data {
        var data = Data(capacity: 44 + samples.count * 2)
        func u32(_ v: UInt32) { withUnsafeBytes(of: v.littleEndian) { data.append(contentsOf: $0) } }
        func u16(_ v: UInt16) { withUnsafeBytes(of: v.littleEndian) { data.append(contentsOf: $0) } }
        let byteCount = UInt32(samples.count * 2)
        data.append(contentsOf: Array("RIFF".utf8)); u32(36 + byteCount)
        data.append(contentsOf: Array("WAVE".utf8))
        data.append(contentsOf: Array("fmt ".utf8)); u32(16); u16(1); u16(1)
        u32(UInt32(sampleRate)); u32(UInt32(sampleRate) * 2); u16(2); u16(16)
        data.append(contentsOf: Array("data".utf8)); u32(byteCount)
        for s in samples {
            let clamped = max(-1, min(1, s))
            u16(UInt16(bitPattern: Int16(clamped * Float(Int16.max))))
        }
        return data
    }

    /// Level for the waveform display, 0...1, from the most recent `count` samples.
    public static func level(_ samples: ArraySlice<Float>) -> Float {
        let db = dbfs(rms(samples))
        return max(0, min(1, (db + 55) / 45))
    }
}

/// Known Whisper hallucinations on near-silent audio.
public enum HallucinationFilter {
    static let phrases: Set<String> = [
        "thank you", "thank you.", "thanks for watching", "thanks for watching!", "thank you for watching",
        "you", "bye", "bye.", "so", ".", "[blank_audio]", "(silence)", "subtitles by the amara.org community",
    ]

    /// True when the text is a known hallucination and the audio had very little speech.
    public static func isLikelyHallucination(_ text: String, voicedSeconds: Double) -> Bool {
        let t = text.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        return t.isEmpty || (voicedSeconds < 1.0 && phrases.contains(t))
    }
}
