import Foundation

/// Decides where to cut a growing recording into upload segments.
///
/// Groq transcribes files and bills each request as at least 10 s, so a segment is cut only once it
/// is at least `minimumSeconds` long and a pause of `pauseSeconds` appears, or when it reaches
/// `maximumSeconds`. Segments overlap by `overlapSeconds` so no word is split.
public struct SegmentCutter: Sendable {
    public var minimumSeconds = 10.0
    public var maximumSeconds = 30.0
    public var pauseSeconds = 0.4
    public var overlapSeconds = 0.2
    public var silenceDB: Float = -45

    /// Start of the segment that is still open, in samples.
    public private(set) var openStart = 0

    public init() {}

    private var rate: Int { Int(AudioMath.sampleRate) }

    /// Looks at the samples recorded so far. Returns the range of a finished segment, if one should be cut now.
    public mutating func nextCut(in samples: [Float]) -> Range<Int>? {
        nextCut(inTail: samples, startingAt: 0)
    }

    /// The same, given only the recording from `tailStart` on (at or before `openStart`), so long recordings
    /// are not copied in full every tick. Ranges are still in whole-recording samples.
    public mutating func nextCut(inTail tail: [Float], startingAt tailStart: Int) -> Range<Int>? {
        precondition(tailStart <= openStart, "the tail must include the open segment")
        let samples = tail[(openStart - tailStart)...]
        let offset = openStart - samples.startIndex   // slice index + offset = whole-recording sample
        let length = samples.count
        let minimum = Int(minimumSeconds * Double(rate))
        guard length >= minimum else { return nil }

        let maximum = Int(maximumSeconds * Double(rate))
        let pauseFrames = Int(pauseSeconds * Double(rate)) / AudioMath.frameLength
        let frame = AudioMath.frameLength

        // Search for a pause from the minimum length onwards.
        var quiet = 0
        var i = samples.startIndex + minimum
        while i + frame <= samples.endIndex {
            if AudioMath.dbfs(AudioMath.rms(samples[i..<(i + frame)])) <= silenceDB {
                quiet += 1
                if quiet >= pauseFrames {
                    let cut = i + frame - (quiet * frame) / 2   // middle of the pause
                    return makeCut(at: cut + offset)
                }
            } else {
                quiet = 0
            }
            i += frame
        }
        if length >= maximum { return makeCut(at: openStart + maximum) }
        return nil
    }

    /// The final segment when recording stops.
    public mutating func finish(totalSamples: Int) -> Range<Int>? {
        guard totalSamples > openStart else { return nil }
        let range = openStart..<totalSamples
        openStart = totalSamples
        return range
    }

    private mutating func makeCut(at cut: Int) -> Range<Int> {
        let overlap = Int(overlapSeconds * Double(rate))
        let range = openStart..<cut
        openStart = max(0, cut - overlap)
        return range
    }

    /// Joins segment transcripts, dropping a word repeated across an overlap.
    public static func join(_ parts: [String]) -> String {
        var result = ""
        for part in parts.map({ $0.trimmingCharacters(in: .whitespacesAndNewlines) }) where !part.isEmpty {
            guard !result.isEmpty else { result = part; continue }
            let lastWord = result.split(separator: " ").last.map { normalize(String($0)) }
            var words = part.split(separator: " ").map(String.init)
            if let first = words.first, normalize(first) == lastWord { words.removeFirst() }
            result += " " + words.joined(separator: " ")
        }
        return result
    }

    private static func normalize(_ word: String) -> String {
        word.lowercased().trimmingCharacters(in: .punctuationCharacters)
    }
}
