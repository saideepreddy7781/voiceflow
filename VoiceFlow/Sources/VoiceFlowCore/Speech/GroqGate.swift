import Foundation

/// Who is asking. A dictation during a meeting must never wait behind meeting chunks.
public enum GroqPriority: Sendable, Equatable {
    /// Dictation and cleanup: someone is waiting for this text.
    case interactive
    /// Meeting chunks, transcript rewriting, notes.
    case background
}

public enum GroqCost: Sendable, Equatable {
    case transcription(model: String, audioSeconds: Double)
    case chat(model: String, estimatedTokens: Int)

    public var model: String {
        switch self {
        case .transcription(let model, _), .chat(let model, _): model
        }
    }
    /// Groq bills every transcription as at least 10 seconds of audio.
    public var billedAudioSeconds: Double {
        if case .transcription(_, let seconds) = self { return max(Groq.minimumBilledSeconds, seconds) }
        return 0
    }
    public var tokens: Int {
        if case .chat(_, let tokens) = self { return tokens }
        return 0
    }
}

/// Free-tier limits for one model. These are defaults; `RateBudget` adjusts to Groq's response headers.
public struct RateLimits: Sendable, Equatable {
    public var requestsPerMinute: Int
    public var requestsPerDay: Int
    public var audioSecondsPerHour: Double?
    public var audioSecondsPerDay: Double?
    public var tokensPerMinute: Int?
    public var tokensPerDay: Int?

    /// From console.groq.com/docs/rate-limits, read 2026-11-10 (base Developer-plan limits).
    public static let whisper = RateLimits(requestsPerMinute: 20, requestsPerDay: 2_000, audioSecondsPerHour: 7_200,
                                           audioSecondsPerDay: 28_800, tokensPerMinute: nil, tokensPerDay: nil)
    public static let chat = RateLimits(requestsPerMinute: 30, requestsPerDay: 1_000, audioSecondsPerHour: nil,
                                        audioSecondsPerDay: nil, tokensPerMinute: 8_000, tokensPerDay: 200_000)

    public static func defaults(forModel model: String) -> RateLimits {
        model.hasPrefix("whisper") ? .whisper : .chat
    }
}

public enum RateDecision: Sendable, Equatable {
    case go
    /// Try again after this many seconds.
    case wait(Double)
}

/// The rate-limit arithmetic for one model. A value type with no clock, so it can be tested directly.
public struct RateBudget: Sendable, Equatable {
    /// What background work leaves free, so dictation always has room even during a long meeting.
    public static let reservedRequestsPerMinute = 4
    public static let reservedAudioSecondsPerHour = 600.0
    public static let reservedTokensPerMinute = 1_500

    public var limits: RateLimits
    private var requests: [Date] = []
    private var audio: [(at: Date, seconds: Double)] = []
    private var tokens: [(at: Date, tokens: Int)] = []
    /// Requests left today according to the latest response headers, and when that count resets.
    private var serverRequestsLeft: Int?
    private var serverRequestsReset: Date?
    private var blockedUntil: Date?

    public init(limits: RateLimits) { self.limits = limits }

    /// Whether a request of this cost may start at `now`.
    public func decision(for cost: GroqCost, priority: GroqPriority, now: Date) -> RateDecision {
        var wait = 0.0
        if let blockedUntil, blockedUntil > now {
            if priority == .background { return .wait(blockedUntil.timeIntervalSince(now)) }
            wait = max(wait, min(blockedUntil.timeIntervalSince(now), 2))
        }
        let background = priority == .background
        let rpmReserve = background ? Self.reservedRequestsPerMinute : 0
        wait = max(wait, windowWait(events: requests.map { ($0, 1.0) }, window: 60, limit: Double(limits.requestsPerMinute - rpmReserve), cost: 1, now: now))
        let rpdReserve = background ? 150 : 0
        wait = max(wait, windowWait(events: requests.map { ($0, 1.0) }, window: 86_400, limit: Double(limits.requestsPerDay - rpdReserve), cost: 1, now: now))
        if let left = serverRequestsLeft, let reset = serverRequestsReset, reset > now, left <= rpdReserve {
            wait = max(wait, reset.timeIntervalSince(now))
        }
        let billed = cost.billedAudioSeconds
        if billed > 0 {
            if let hourly = limits.audioSecondsPerHour {
                let reserve = background ? Self.reservedAudioSecondsPerHour : 0
                wait = max(wait, windowWait(events: audio.map { ($0.at, $0.seconds) }, window: 3_600, limit: hourly - reserve, cost: billed, now: now))
            }
            if let daily = limits.audioSecondsPerDay {
                let reserve = background ? 1_800.0 : 0
                wait = max(wait, windowWait(events: audio.map { ($0.at, $0.seconds) }, window: 86_400, limit: daily - reserve, cost: billed, now: now))
            }
        }
        if cost.tokens > 0 {
            if let perMinute = limits.tokensPerMinute {
                let reserve = background ? Self.reservedTokensPerMinute : 0
                wait = max(wait, windowWait(events: tokens.map { ($0.at, Double($0.tokens)) }, window: 60,
                                            limit: Double(perMinute - reserve), cost: Double(cost.tokens), now: now))
            }
            if let perDay = limits.tokensPerDay {
                wait = max(wait, windowWait(events: tokens.map { ($0.at, Double($0.tokens)) }, window: 86_400,
                                            limit: Double(perDay), cost: Double(cost.tokens), now: now))
            }
        }
        return wait > 0 ? .wait(wait) : .go
    }

    /// Records that a request started.
    public mutating func record(_ cost: GroqCost, at now: Date) {
        requests.append(now)
        if cost.billedAudioSeconds > 0 { audio.append((now, cost.billedAudioSeconds)) }
        if cost.tokens > 0 { tokens.append((now, cost.tokens)) }
        prune(now: now)
    }

    /// Reads Groq's rate-limit response headers, which beat any local estimate.
    public mutating func apply(headers: [String: String], now: Date) {
        func header(_ name: String) -> String? {
            headers.first { $0.key.caseInsensitiveCompare(name) == .orderedSame }?.value
        }
        if let limit = header("x-ratelimit-limit-requests").flatMap(Int.init), limit > 0 { limits.requestsPerDay = limit }
        if let limit = header("x-ratelimit-limit-tokens").flatMap(Int.init), limit > 0, limits.tokensPerMinute != nil { limits.tokensPerMinute = limit }
        if let left = header("x-ratelimit-remaining-requests").flatMap(Int.init) {
            serverRequestsLeft = left
            if let reset = header("x-ratelimit-reset-requests").flatMap(Self.parseDuration) {
                serverRequestsReset = now.addingTimeInterval(reset)
            } else {
                serverRequestsReset = nil
            }
        }
    }

    /// Records a 429: background work waits this long.
    public mutating func applyRetryAfter(_ seconds: Double?, now: Date) {
        blockedUntil = now.addingTimeInterval(max(seconds ?? 5, 1))
    }

    /// "43.2s", "2m59.56s", "1h2m3s".
    static func parseDuration(_ text: String) -> Double? {
        var total = 0.0
        var number = ""
        for ch in text {
            if ch.isNumber || ch == "." { number.append(ch); continue }
            guard let value = Double(number) else { return nil }
            number = ""
            switch ch {
            case "h": total += value * 3_600
            case "m": total += value * 60
            case "s": total += value
            default: return nil
            }
        }
        return number.isEmpty ? total : nil
    }

    /// Seconds until the events inside `window` leave room for `cost` under `limit`.
    private func windowWait(events: [(Date, Double)], window: Double, limit: Double, cost: Double, now: Date) -> Double {
        guard limit > 0 else { return window }
        let live = events.filter { now.timeIntervalSince($0.0) < window }.sorted { $0.0 < $1.0 }
        var used = live.reduce(0) { $0 + $1.1 }
        guard used + cost > limit else { return 0 }
        for (at, amount) in live {
            used -= amount
            if used + cost <= limit { return at.addingTimeInterval(window).timeIntervalSince(now) }
        }
        return window
    }

    private mutating func prune(now: Date) {
        let cut = now.addingTimeInterval(-86_400)
        if let first = requests.first, first < cut { requests.removeAll { $0 < cut } }
        if let first = audio.first, first.at < cut { audio.removeAll { $0.at < cut } }
        if let first = tokens.first, first.at < cut { tokens.removeAll { $0.at < cut } }
    }

    public static func == (a: RateBudget, b: RateBudget) -> Bool {
        a.limits == b.limits && a.requests == b.requests && a.audio.map(\.seconds) == b.audio.map(\.seconds)
            && a.tokens.map(\.tokens) == b.tokens.map(\.tokens) && a.serverRequestsLeft == b.serverRequestsLeft
    }
}

/// The single queue every Groq request goes through. Background requests run in order and leave room for
/// interactive ones; interactive requests jump the line and only ever wait a moment.
public actor GroqGate {
    public static let shared = GroqGate()

    /// Longest a background request waits for room before giving up. The caller holds the chunk for later.
    public static let maxBackgroundWait = 90.0
    /// Longest an interactive request waits. Past that it goes out anyway and Groq has the last word.
    public static let maxInteractiveWait = 2.0

    private var budgets: [String: RateBudget] = [:]
    private let now: @Sendable () -> Date
    private let sleep: @Sendable (Double) async -> Void
    private var backgroundTail: Task<Void, Never>?

    public init(now: @escaping @Sendable () -> Date = { Date() },
                sleep: @escaping @Sendable (Double) async -> Void = { try? await Task.sleep(for: .seconds($0)) }) {
        self.now = now
        self.sleep = sleep
    }

    /// Runs one Groq request when the budget allows it.
    public func perform(_ cost: GroqCost, priority: GroqPriority,
                        _ body: @escaping @Sendable () async throws -> (Data, HTTPURLResponse)) async throws -> Data {
        try await admit(cost, priority: priority)
        do {
            let (data, response) = try await body()
            let headers = response.allHeaderFields.reduce(into: [String: String]()) { out, item in
                if let k = item.key as? String { out[k] = "\(item.value)" }
            }
            budgets[cost.model, default: RateBudget(limits: .defaults(forModel: cost.model))].apply(headers: headers, now: now())
            return data
        } catch let error as ProviderError {
            let retryAfter: Double?
            switch error {
            case .rateLimited(let after): retryAfter = after
            case .dailyLimit(_, let after): retryAfter = after ?? 3_600
            default: throw error
            }
            budgets[cost.model, default: RateBudget(limits: .defaults(forModel: cost.model))].applyRetryAfter(retryAfter, now: now())
            throw error
        }
    }

    private func admit(_ cost: GroqCost, priority: GroqPriority) async throws {
        if priority == .interactive {
            if case .wait(let seconds) = decision(for: cost, priority: priority) { await sleep(min(seconds, Self.maxInteractiveWait)) }
            record(cost)
            return
        }
        // One background request is admitted at a time, so requests go out in the order they were queued.
        let previous = backgroundTail
        let turn = Task<Result<Void, Error>, Never> {
            await previous?.value
            do {
                try await self.waitForRoom(cost)
                return .success(())
            } catch {
                return .failure(error)
            }
        }
        backgroundTail = Task { _ = await turn.value }
        try await turn.value.get()
    }

    private func waitForRoom(_ cost: GroqCost) async throws {
        var waited = 0.0
        while true {
            switch decision(for: cost, priority: .background) {
            case .go:
                record(cost)
                return
            case .wait(let seconds):
                if waited + seconds > Self.maxBackgroundWait { throw ProviderError.rateLimited(retryAfter: seconds) }
                let step = min(seconds, 2)
                await sleep(step)
                waited += step
            }
        }
    }

    private func decision(for cost: GroqCost, priority: GroqPriority) -> RateDecision {
        budgets[cost.model, default: RateBudget(limits: .defaults(forModel: cost.model))].decision(for: cost, priority: priority, now: now())
    }

    private func record(_ cost: GroqCost) {
        budgets[cost.model, default: RateBudget(limits: .defaults(forModel: cost.model))].record(cost, at: now())
    }

    /// How much audio budget is left this hour, by the app's own count. Meetings use it to drop second opinions.
    public func transcriptionHeadroom(model: String) -> Double {
        let budget = budgets[model, default: RateBudget(limits: .defaults(forModel: model))]
        return budget.remainingAudioThisHour(now: now())
    }
}

extension RateBudget {
    func remainingAudioThisHour(now: Date) -> Double {
        guard let hourly = limits.audioSecondsPerHour else { return .infinity }
        let used = audio.filter { now.timeIntervalSince($0.at) < 3_600 }.reduce(0) { $0 + $1.seconds }
        return hourly - used
    }
}
