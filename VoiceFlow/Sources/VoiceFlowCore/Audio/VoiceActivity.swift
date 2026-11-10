import Foundation

/// Decides whether a 30 ms frame holds a voice, by loudness against a slowly tracked noise floor.
/// Pure and clock-free, on 16 kHz samples like the rest of `AudioMath`.
public struct VoiceGate: Sendable {
    /// Below this is always silence, so digital silence and very quiet rooms can't be called speech.
    public static let absoluteFloorDB: Float = -45
    /// A frame is voiced when it is this far above the noise floor.
    public static let marginDB: Float = 12
    /// A frame above this is voiced whatever the noise floor, so a loud room can't mute a voice.
    public static let ceilingDB: Float = -35
    static let historyFrames = 500          // 15 s
    static let refreshEveryFrames = 33      // about 1 s

    private var history: [Float] = []
    private var cursor = 0
    private var sinceRefresh = 0
    private var threshold: Float = VoiceGate.absoluteFloorDB

    public init() {}

    public mutating func isVoiced(frameDB db: Float) -> Bool {
        if history.count < Self.historyFrames { history.append(db) } else {
            history[cursor] = db
            cursor = (cursor + 1) % Self.historyFrames
        }
        sinceRefresh += 1
        if sinceRefresh >= Self.refreshEveryFrames {
            sinceRefresh = 0
            let floor = history.sorted()[history.count / 10]
            threshold = max(Self.absoluteFloorDB, min(Self.ceilingDB, floor + Self.marginDB))
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
