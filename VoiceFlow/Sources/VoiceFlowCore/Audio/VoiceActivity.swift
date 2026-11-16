import Foundation

/// The one rule for "is this speech", shared by dictation and meetings: a 30 ms frame is voiced when it is
/// `marginDB` above the noise floor of the capture it came from.
///
/// A fixed level does not work for every microphone. While a call app uses voice processing, the built-in
/// microphone becomes a raw array about 10 dB quieter, and a fixed -45 dBFS floor called real speech silence.
/// The floor is the 10th percentile of the frames heard, leaving out exact zeros, so digital silence and a
/// microphone that gates to zero between words never drag it down.
public enum SpeechRule {
    /// Never speech below this, whatever the floor: digital silence and the quietest rooms stay silent.
    public static let absoluteMinimumDB: Float = -70
    /// A frame is voiced when it is this far above the noise floor.
    public static let marginDB: Float = 10
    /// A frame above this is voiced whatever the floor, so a loud room can't mute a voice.
    public static let ceilingDB: Float = -35
    /// Exact zeros are not a noise floor.
    static let zeroDB: Float = -119

    /// The voiced threshold for a capture with this noise floor (nil: nothing but zeros heard yet).
    public static func threshold(noiseFloorDB: Float?) -> Float {
        guard let floor = noiseFloorDB else { return absoluteMinimumDB }
        return max(absoluteMinimumDB, min(ceilingDB, floor + marginDB))
    }

    /// The 10th percentile of the frame levels, leaving out exact zeros.
    public static func noiseFloor(frameDBs: [Float]) -> Float? {
        let heard = frameDBs.filter { $0 > zeroDB }.sorted()
        guard !heard.isEmpty else { return nil }
        return heard[heard.count / 10]
    }

    /// Frame levels in dBFS, 30 ms each.
    public static func frameDBs(_ samples: [Float]) -> [Float] {
        stride(from: 0, to: samples.count, by: AudioMath.frameLength).map { i in
            AudioMath.dbfs(AudioMath.rms(samples[i..<min(i + AudioMath.frameLength, samples.count)]))
        }
    }

    /// A whole recording measured against its own floor.
    public struct Analysis: Sendable, Equatable {
        public var noiseFloorDB: Float?
        public var thresholdDB: Float
        public var voicedSeconds: Double
        /// RMS level of the voiced frames (-120 when there are none).
        public var speechDB: Float
    }

    public static func analyze(_ samples: [Float]) -> Analysis {
        let dbs = frameDBs(samples)
        let floor = noiseFloor(frameDBs: dbs)
        let threshold = threshold(noiseFloorDB: floor)
        var voiced = 0
        var energy: Double = 0
        for (k, db) in dbs.enumerated() where db > threshold {
            let start = k * AudioMath.frameLength
            let end = min(start + AudioMath.frameLength, samples.count)
            voiced += end - start
            for s in samples[start..<end] { energy += Double(s * s) }
        }
        let speechDB = voiced > 0 ? AudioMath.dbfs(Float((energy / Double(voiced)).squareRoot())) : -120
        return Analysis(noiseFloorDB: floor, thresholdDB: threshold, voicedSeconds: Double(voiced) / AudioMath.sampleRate, speechDB: speechDB)
    }

    /// Level the voiced frames are raised to before upload.
    public static let uploadSpeechDB: Float = -24
    /// Most gain added before upload. 24 dB covers a voice-processing call (about 10 dB down) with room to spare,
    /// and the voiced check has already kept room noise from getting this far.
    public static let maximumGainDB: Float = 24

    /// Gain in dB to bring quiet speech up to `uploadSpeechDB`. Speech already at that level or louder is untouched.
    public static func uploadGainDB(_ analysis: Analysis, maximum: Float = maximumGainDB) -> Float {
        guard analysis.voicedSeconds > 0 else { return 0 }
        return max(0, min(maximum, uploadSpeechDB - analysis.speechDB))
    }

    /// The audio to upload: quiet speech raised, then clipped to -1...1 (a few clipped clicks do not hurt Whisper).
    public static func prepareForUpload(_ samples: [Float], maximumGainDB maximum: Float = maximumGainDB) -> [Float] {
        let gainDB = uploadGainDB(analyze(samples), maximum: maximum)
        guard gainDB > 0.5 else { return samples }
        let gain = Float(pow(10, Double(gainDB) / 20))
        return samples.map { max(-1, min(1, $0 * gain)) }
    }
}

/// Decides whether a 30 ms frame holds a voice while a stream is still arriving: `SpeechRule` against a noise
/// floor tracked over the last 15 seconds. Pure and clock-free, on 16 kHz samples like the rest of `AudioMath`.
public struct VoiceGate: Sendable {
    static let historyFrames = 500          // 15 s
    static let refreshEveryFrames = 33      // about 1 s

    private var history: [Float] = []
    private var cursor = 0
    private var sinceRefresh = 0
    private var threshold: Float = SpeechRule.absoluteMinimumDB

    public init() {}

    public mutating func isVoiced(frameDB db: Float) -> Bool {
        if db > SpeechRule.zeroDB {
            if history.count < Self.historyFrames { history.append(db) } else {
                history[cursor] = db
                cursor = (cursor + 1) % Self.historyFrames
            }
        }
        sinceRefresh += 1
        // Every frame until a second has been heard, so the start of a stream is judged against its own floor.
        if sinceRefresh >= Self.refreshEveryFrames || history.count < Self.refreshEveryFrames {
            sinceRefresh = 0
            threshold = SpeechRule.threshold(noiseFloorDB: SpeechRule.noiseFloor(frameDBs: history))
        }
        return db > threshold
    }
}

/// Maps a position in the uploaded audio back to the meeting clock. The upload has only the voiced parts,
/// with short silences spliced between them.
public struct TimeMap: Sendable, Equatable {
    public struct Span: Sendable, Equatable {
        public var chunkStart: Double
        public var meetingStart: Double
        public var seconds: Double

        public init(chunkStart: Double, meetingStart: Double, seconds: Double) {
            self.chunkStart = chunkStart
            self.meetingStart = meetingStart
            self.seconds = seconds
        }
    }

    public var spans: [Span]

    public init(spans: [Span]) { self.spans = spans }

    /// Meeting time for a position in the upload. A position inside a splice maps to the start of the next span.
    public func meetingTime(chunkSeconds t: Double) -> Double {
        guard let first = spans.first else { return t }
        guard t > first.chunkStart else { return first.meetingStart }
        for (i, span) in spans.enumerated().reversed() where t >= span.chunkStart {
            if t > span.chunkStart + span.seconds, i + 1 < spans.count { return spans[i + 1].meetingStart }
            return span.meetingStart + min(t - span.chunkStart, span.seconds)
        }
        return first.meetingStart
    }

    /// Meeting time for the end of a line. A position inside a splice is the end of the speech before it.
    public func meetingEndTime(chunkSeconds t: Double) -> Double {
        guard let first = spans.first else { return t }
        guard t > first.chunkStart else { return first.meetingStart }
        for span in spans.reversed() where t >= span.chunkStart {
            return span.meetingStart + min(t - span.chunkStart, span.seconds)
        }
        return first.meetingStart
    }

    /// Upload positions where the original meeting had at least `minimumGap` seconds of silence removed.
    public func spliceGaps(minimumGap: Double) -> [Double] {
        var out: [Double] = []
        for (a, b) in zip(spans, spans.dropFirst()) where b.meetingStart - (a.meetingStart + a.seconds) >= minimumGap {
            out.append(b.chunkStart)
        }
        return out
    }
}

/// One piece of a meeting ready to upload.
public struct AudioChunk: Sendable {
    public var samples: [Float]
    public var map: TimeMap
    public var voicedSeconds: Double
}

/// Cuts one side of a meeting into uploads made of voiced audio only.
///
/// Silence is left out (a splice of `gapSeconds` marks each removed pause) so a side's hour of audio costs only
/// its speech. Chunks are cut at a pause once they hold `minimumVoiced` seconds of speech, or forced at
/// `maximumVoiced`, and flushed at a pause after `maxWait` seconds so a quiet side still shows up in the transcript.
public struct StreamChunker: Sendable {
    public var minimumVoiced = 20.0
    public var maximumVoiced = 45.0
    public var pauseToCut = 0.6
    public var maxWait = 120.0
    public var gapSeconds = 0.25
    /// Chunks with less speech than this are not sent. Whisper invents text for noise.
    public static let minimumSendableVoiced = 0.4
    static let prerollFrames = 7            // 210 ms kept before speech starts
    static let hangoverFrames = 10          // 300 ms kept after speech stops

    private var gate = VoiceGate()
    private var leftover: [Float] = []
    /// Whole frames seen so far, in samples.
    private var consumed = 0
    private var inRegion = false
    private var hangover = 0
    private var silentFrames = 0
    private var preroll: [[Float]] = []
    private var chunk: [Float] = []
    private var spans: [TimeMap.Span] = []
    private var chunkFirstVoice: Int?
    private var voicedSamples = 0
    private var rate: Double { AudioMath.sampleRate }

    public init() {}

    /// Feeds newly captured samples. Returns any chunks ready to upload, oldest first.
    public mutating func feed(_ samples: [Float]) -> [AudioChunk] {
        var input = leftover
        input.append(contentsOf: samples)
        leftover = []
        var out: [AudioChunk] = []
        var i = 0
        let frameLength = AudioMath.frameLength
        while i + frameLength <= input.count {
            let frame = Array(input[i..<(i + frameLength)])
            i += frameLength
            if let chunk = processFrame(frame) { out.append(chunk) }
        }
        leftover = Array(input[i...])
        return out
    }

    /// The final chunk at Stop.
    public mutating func finish() -> AudioChunk? {
        if !leftover.isEmpty, inRegion, !chunk.isEmpty {
            appendToRegion(leftover)
            leftover = []
        }
        return cut()
    }

    private mutating func processFrame(_ frame: [Float]) -> AudioChunk? {
        let frameStart = consumed
        consumed += frame.count
        let voiced = gate.isVoiced(frameDB: AudioMath.dbfs(AudioMath.rms(frame[...])))
        if voiced {
            if !inRegion { beginRegion(at: frameStart) }
            ensureSpan(at: frameStart)
            inRegion = true
            hangover = Self.hangoverFrames
            silentFrames = 0
            appendToRegion(frame)
            voicedSamples += frame.count
            if chunkFirstVoice == nil { chunkFirstVoice = frameStart }
        } else if inRegion, hangover > 0 {
            hangover -= 1
            silentFrames += 1
            ensureSpan(at: frameStart)
            appendToRegion(frame)
        } else {
            inRegion = false
            silentFrames += 1
            pushPreroll(frame)
        }
        let voicedSeconds = Double(voicedSamples) / rate
        if voicedSeconds >= maximumVoiced { return cut() }
        let quiet = Double(silentFrames) * Double(AudioMath.frameLength) / rate
        if !inRegion, quiet >= pauseToCut, !chunk.isEmpty {
            if voicedSeconds >= minimumVoiced { return cut() }
            if let first = chunkFirstVoice, Double(consumed - first) / rate >= maxWait { return cut() }
        }
        return nil
    }

    private mutating func beginRegion(at frameStart: Int) {
        let meetingSamples = max(0, frameStart - preroll.reduce(0) { $0 + $1.count })
        if !chunk.isEmpty { chunk.append(contentsOf: [Float](repeating: 0, count: Int(gapSeconds * rate))) }
        spans.append(TimeMap.Span(chunkStart: Double(chunk.count) / rate, meetingStart: Double(meetingSamples) / rate, seconds: 0))
        for p in preroll { appendToRegion(p) }
        preroll.removeAll()
    }

    /// A forced cut can land in the middle of speech; the next chunk starts its map here.
    private mutating func ensureSpan(at frameStart: Int) {
        guard spans.isEmpty else { return }
        spans.append(TimeMap.Span(chunkStart: Double(chunk.count) / rate, meetingStart: Double(frameStart) / rate, seconds: 0))
    }

    private mutating func appendToRegion(_ frame: [Float]) {
        chunk.append(contentsOf: frame)
        if !spans.isEmpty {
            spans[spans.count - 1].seconds = Double(chunk.count) / rate - spans[spans.count - 1].chunkStart
        }
    }

    private mutating func pushPreroll(_ frame: [Float]) {
        preroll.append(frame)
        if preroll.count > Self.prerollFrames { preroll.removeFirst() }
    }

    private mutating func cut() -> AudioChunk? {
        defer {
            chunk = []
            spans = []
            voicedSamples = 0
            chunkFirstVoice = nil
            preroll = []
            silentFrames = 0
        }
        let seconds = Double(voicedSamples) / rate
        guard seconds >= Self.minimumSendableVoiced, !chunk.isEmpty else { return nil }
        return AudioChunk(samples: chunk, map: TimeMap(spans: spans), voicedSeconds: seconds)
    }
}
