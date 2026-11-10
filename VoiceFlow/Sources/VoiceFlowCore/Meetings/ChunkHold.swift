import Foundation

/// A chunk that failed, kept in memory to retry when the meeting ends. Never written to disk.
public struct HeldChunk: Sendable, Equatable {
    public var samples: [Int16]
    public var map: TimeMap
    public var speaker: Speaker
    public var voicedSeconds: Double

    public var seconds: Double { Double(samples.count) / AudioMath.sampleRate }

    public init(samples: [Float], map: TimeMap, speaker: Speaker, voicedSeconds: Double) {
        self.samples = samples.map { Int16(max(-1, min(1, $0)) * Float(Int16.max)) }
        self.map = map
        self.speaker = speaker
        self.voicedSeconds = voicedSeconds
    }

    public var floatSamples: [Float] { samples.map { Float($0) / Float(Int16.max) } }
}

/// Failed chunks waiting for a retry, bounded so a long outage can't use unlimited memory.
public struct ChunkHold: Sendable {
    /// About 38 MB as 16-bit samples.
    public static let capacitySeconds = 1_200.0

    public private(set) var chunks: [HeldChunk] = []
    public var capacity: Double

    public init(capacity: Double = ChunkHold.capacitySeconds) { self.capacity = capacity }

    public var totalSeconds: Double { chunks.reduce(0) { $0 + $1.seconds } }

    /// Keeps a chunk. Returns the oldest chunks pushed out to make room: those are lost.
    public mutating func add(_ chunk: HeldChunk) -> [HeldChunk] {
        chunks.append(chunk)
        var evicted: [HeldChunk] = []
        while totalSeconds > capacity, chunks.count > 1 { evicted.append(chunks.removeFirst()) }
        if totalSeconds > capacity { evicted.append(contentsOf: chunks); chunks = [] }
        return evicted
    }

    /// Hands over everything, oldest first.
    public mutating func takeAll() -> [HeldChunk] {
        defer { chunks = [] }
        return chunks
    }
}
