import Foundation

/// Removes what Whisper invents on hard audio, using the numbers it reports for each segment.
public enum WhisperSegmentFilter {
    /// A segment this compressible (its text repeats itself) is a loop. OpenAI's Whisper uses 2.4 as well.
    public static let compressionRatioLimit = 2.4
    public static let noSpeechLimit = 0.6
    public static let noSpeechLogprob = -1.0
    /// Three identical segments in a row is a loop; people repeating themselves don't do it with Whisper's timing.
    public static let loopRun = 3

    /// Whether a single segment is noise or a loop.
    public static func shouldDrop(_ s: DetailedTranscript.Segment) -> Bool {
        if let ratio = s.compressionRatio, ratio > compressionRatioLimit, s.text.count >= 20 { return true }
        if let noSpeech = s.noSpeechProb, let logprob = s.avgLogprob, noSpeech > noSpeechLimit, logprob < noSpeechLogprob { return true }
        return false
    }

    /// Looks like a loop across segments: identical text three or more times running.
    public static func isLooping(_ segments: [DetailedTranscript.Segment]) -> Bool {
        var run = 1
        for i in segments.indices.dropFirst() {
            run = normalize(segments[i].text) == normalize(segments[i - 1].text) && !normalize(segments[i].text).isEmpty ? run + 1 : 1
            if run >= loopRun { return true }
        }
        return segments.contains { ($0.compressionRatio ?? 0) > compressionRatioLimit && $0.text.count >= 20 }
    }

    /// Returns the transcript without dropped segments or repeats of a looped segment. Text is rebuilt only if
    /// something was removed, so normal transcripts keep Whisper's exact spacing.
    public static func filtered(_ t: DetailedTranscript) -> DetailedTranscript {
        var kept: [DetailedTranscript.Segment] = []
        var runText = ""
        var run = 0
        for segment in t.segments where !shouldDrop(segment) {
            let n = normalize(segment.text)
            run = n == runText && !n.isEmpty ? run + 1 : 1
            runText = n
            if run >= loopRun { continue }
            kept.append(segment)
        }
        guard kept.count != t.segments.count else { return t }
        var out = t
        out.segments = kept
        out.text = kept.map(\.text).filter { !$0.isEmpty }.joined(separator: " ")
        return out
    }

    static func normalize(_ text: String) -> String {
        text.lowercased().filter { $0.isLetter || $0.isNumber }
    }
}

/// Transcript-level cleanup that also works on meetings already saved.
public enum TranscriptFilter {
    public static let lostMarker = "[Part of the meeting could not be transcribed]"
    /// A run this long from one speaker, gaps within `runGap`, keeps only its first line. Whisper loops on hard
    /// audio came out as 3-15 identical lines 1-9 s apart in real meetings.
    public static let runLength = 3
    public static let runGap = 20.0

    public static func collapseRepeats(_ lines: [TranscriptLine]) -> [TranscriptLine] {
        var keep = [Bool](repeating: true, count: lines.count)
        // Runs are per speaker: the other person can talk between looped lines.
        var indices: [Speaker: [Int]] = [:]
        for (i, line) in lines.enumerated() { indices[line.speaker, default: []].append(i) }
        for idx in indices.values {
            var i = 0
            while i < idx.count {
                let n = WhisperSegmentFilter.normalize(lines[idx[i]].text)
                var j = i
                if !n.isEmpty && lines[idx[i]].text != lostMarker {
                    while j + 1 < idx.count, WhisperSegmentFilter.normalize(lines[idx[j + 1]].text) == n,
                          lines[idx[j + 1]].time - lines[idx[j]].time <= runGap { j += 1 }
                }
                if j - i + 1 >= runLength {
                    for k in idx[(i + 1)...j] { keep[k] = false }
                }
                i = j + 1
            }
        }
        return lines.indices.filter { keep[$0] }.map { lines[$0] }
    }
}
