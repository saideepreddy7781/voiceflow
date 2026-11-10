import Foundation

/// Turns Whisper's result for one chunk into meeting-clock transcript lines.
public enum ChunkLines {
    /// A removed silence this long splits a Whisper segment, so words on either side get their real times.
    public static let splitAtGap = 2.0

    public static func lines(for transcript: DetailedTranscript, map: TimeMap, speaker: Speaker, chunkFallbackTime: Double) -> [TranscriptLine] {
        let language = transcript.language
        if transcript.segments.isEmpty {
            let text = transcript.text
            return text.isEmpty ? [] : [TranscriptLine(time: chunkFallbackTime, speaker: speaker, text: text, language: language)]
        }
        let gaps = map.spliceGaps(minimumGap: splitAtGap)
        var out: [TranscriptLine] = []
        for segment in transcript.segments where !segment.text.isEmpty {
            let groups = gaps.isEmpty || segment.words.isEmpty ? [segment.words] : splitWords(segment.words, gaps: gaps)
            if groups.count > 1 {
                for words in groups where !words.isEmpty {
                    out.append(TranscriptLine(time: map.meetingTime(chunkSeconds: words[0].start), speaker: speaker,
                                              text: words.map(\.word).joined(separator: " "),
                                              end: map.meetingTime(chunkSeconds: words[words.count - 1].end), language: language))
                }
            } else {
                out.append(TranscriptLine(time: map.meetingTime(chunkSeconds: segment.start), speaker: speaker, text: segment.text,
                                          end: map.meetingTime(chunkSeconds: segment.end), language: language))
            }
        }
        return out
    }

    private static func splitWords(_ words: [DetailedTranscript.Word], gaps: [Double]) -> [[DetailedTranscript.Word]] {
        var groups: [[DetailedTranscript.Word]] = [[]]
        var pending = gaps.makeIterator()
        var next = pending.next()
        for word in words {
            while let g = next, word.start >= g {
                if !groups[groups.count - 1].isEmpty { groups.append([]) }
                next = pending.next()
            }
            groups[groups.count - 1].append(word)
        }
        return groups
    }
}

/// Combines lines from the two sides of a call into one time-ordered transcript.
public enum TranscriptMerge {
    /// A mic line is an echo of the call when most of its words were said on the call around the same time:
    /// the call playing through speakers and picked up by the microphone.
    static let echoWindow = 4.0
    static let echoShare = 0.6
    static let shortEchoWindow = 2.0
    static let shortEchoShare = 0.9
    static let minimumEchoWords = 3
    static let longLineWords = 6

    public static func merge(existing: [TranscriptLine], incoming: [TranscriptLine]) -> [TranscriptLine] {
        let combined = (existing.enumerated().map { (0, $0.offset, $0.element) } + incoming.enumerated().map { (1, $0.offset, $0.element) })
            .sorted { a, b in a.2.time != b.2.time ? a.2.time < b.2.time : (a.0, a.1) < (b.0, b.1) }
            .map(\.2)
        return TranscriptFilter.collapseRepeats(removeEchoes(combined))
    }

    /// Removes mic lines that are really the call playing back into the microphone.
    public static func removeEchoes(_ lines: [TranscriptLine]) -> [TranscriptLine] {
        let others = lines.filter { $0.speaker == .others }
        guard !others.isEmpty, lines.contains(where: { $0.speaker == .you }) else { return lines }
        return lines.filter { line in
            guard line.speaker == .you else { return true }
            let words = tokens(line.text)
            guard words.count >= minimumEchoWords else { return true }
            let long = words.count >= longLineWords
            let window = long ? echoWindow : shortEchoWindow
            let pool = others.filter { abs($0.time - line.time) <= window || overlaps($0, line) }
                .reduce(into: Set<String>()) { $0.formUnion(tokens($1.text)) }
            guard !pool.isEmpty else { return true }
            let share = Double(words.filter(pool.contains).count) / Double(words.count)
            return share < (long ? echoShare : shortEchoShare)
        }
    }

    private static func overlaps(_ a: TranscriptLine, _ b: TranscriptLine) -> Bool {
        guard let ae = a.end, let be = b.end else { return false }
        return a.time < be && b.time < ae
    }

    static func tokens(_ text: String) -> [String] {
        text.lowercased().split { !$0.isLetter && !$0.isNumber }.map(String.init)
    }
}
