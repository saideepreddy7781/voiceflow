import Foundation
import Testing
@testable import VoiceFlowCore

struct LanguageEvidenceTests {
    @Test func englishReadsAsEnglish() {
        #expect(LanguageEvidence.readsAsEnglish("I think we should check the report with the team before the call today") == true)
    }

    @Test func teluguSpelledBySoundFails() {
        #expect(LanguageEvidence.readsAsEnglish("Neme rebu aafisu ki vastaanu meetingu padi gantalaki undi ani cheppandi") == false)
    }

    @Test func tooShortSaysNothing() {
        #expect(LanguageEvidence.readsAsEnglish("Okay thanks") == nil)
        #expect(LanguageEvidence.readsAsHindi("मैं कल") == nil)
    }

    @Test func realHindiPassesAndDisguisedTeluguFails() {
        #expect(LanguageEvidence.readsAsHindi("मैं कल ओफिस आएंगा, मीटिंग 10 बजे है, रिपोर्ट तयार करके भेज दूगा") == true)
        #expect(LanguageEvidence.readsAsHindi("नेनु रेपू ओफीस की वस्तानू, मीटिंग पदी गंटलकी उन्दी प्राउजेक्ट रिपोर्ट रेडी चेसी") == false)
    }
}

struct ResolverListTests {
    private func t(_ text: String, _ lang: String?, _ conf: Double?) -> DetailedTranscript {
        DetailedTranscript(text: text, language: lang, segments: [], confidence: conf)
    }
    private func o(_ lang: String, _ text: String, _ conf: Double) -> LanguageOpinion {
        LanguageOpinion(language: lang, transcript: t(text, lang, conf))
    }
    private let hindi = "मैं कल ओफिस आएंगा, मीटिंग 10 बजे है, रिपोर्ट तयार करके भेज दूगा"
    private let disguised = "Neme rebu aafisu ki vastaanu meetingu padi gantalaki undi ani cheppandi"

    @Test func realHindiIsNotReplacedByForcedTelugu() {
        let plan = LanguagePlan(languages: ["en", "te"])
        let out = LanguageResolver.choose(auto: t(hindi, "hi", -0.074), opinions: [o("te", "మెకల ఉఫిస ఆంగా", -0.31)], plan: plan)
        #expect(out.language == "hi")
    }

    @Test func spanishOnTheCallIsLeftAlone() {
        let plan = LanguagePlan(languages: ["en", "te"], speaker: .anyone)
        #expect(LanguageResolver.opinionLanguages(after: t("Hola a todos, vamos a empezar la reunión de hoy", "es", -0.2), plan: plan).isEmpty)
    }

    @Test func spanishFromTheUserGetsAnOpinion() {
        let plan = LanguagePlan(languages: ["en", "te"])
        #expect(LanguageResolver.opinionLanguages(after: t("Hola a todos", "es", -0.2), plan: plan) == ["te"])
    }

    @Test func disguisedTeluguAsksAndWins() {
        let plan = LanguagePlan(languages: ["en", "te"], speaker: .anyone)
        let auto = t(disguised, "en", -0.22)
        #expect(LanguageResolver.opinionLanguages(after: auto, plan: plan) == ["te"])
        #expect(LanguageResolver.choose(auto: auto, opinions: [o("te", "నేను రేపు office కి వస్తాను", -0.4)], plan: plan).language == "te")
    }

    @Test func accentedEnglishSurvivesAFalseAlarm() {
        let plan = LanguagePlan(languages: ["en", "te"])
        let out = LanguageResolver.choose(auto: t(disguised, "en", -0.15), opinions: [o("te", "గరిగాాందాలిండు", -0.70)], plan: plan)
        #expect(out.language == "en")
    }

    @Test func manyLanguagesAreCappedAndRankedByConfusion() {
        let plan = LanguagePlan(languages: ["en", "es", "te", "hi", "ta"])
        let langs = LanguageResolver.opinionLanguages(after: t("नेनु रेपू ओफीस की वस्तानू", "hi", -0.5), plan: plan)
        #expect(langs.count == LanguageResolver.maxOpinions)
        #expect(!langs.contains("es"))
        #expect(!langs.contains("hi"))
    }

    @Test func loopingOpinionNeverWins() {
        let plan = LanguagePlan(languages: ["en", "te"])
        let seg = DetailedTranscript.Segment(start: 0, end: 1, text: "ఓకే")
        var loop = t("ఓకే ఓకే ఓకే", "te", -0.01)
        loop.segments = [seg, seg, seg]
        let out = LanguageResolver.choose(auto: t(disguised, "en", -0.3), opinions: [LanguageOpinion(language: "te", transcript: loop)], plan: plan)
        #expect(out.language == "en")
    }

    @Test func trackerSticksRechecksAndLetsGo() {
        var tracker = StreamLanguageTracker()
        #expect(tracker.nextHint() == nil)
        tracker.observe(language: "te", viaOpinion: true, listed: ["en", "te"])
        #expect(tracker.sticky == nil)
        tracker.observe(language: "te", viaOpinion: true, listed: ["en", "te"])
        #expect(tracker.sticky == "te")
        let hints = (0..<5).map { _ in tracker.nextHint() }
        #expect(hints.filter { $0 == nil }.count == 1)
        tracker.observe(language: "en", viaOpinion: false, listed: ["en", "te"])
        #expect(tracker.sticky == nil)
    }
}

struct WhisperPromptTests {
    @Test func autoDetectRequestsAreNeverSeeded() {
        let prompts = WhisperPrompts(terms: ["Saideep"])
        #expect(prompts.text(forLanguage: nil) == "Saideep")
        #expect(WhisperPrompts().text(forLanguage: nil) == nil)
    }

    @Test func forcedRequestsAreSeededByWritingChoice() {
        #expect(WhisperPrompts(telugu: .mixed).text(forLanguage: "te")?.contains("office") == true)
        #expect(WhisperPrompts(telugu: .telugu).text(forLanguage: "te")?.contains("ఆఫీస్") == true)
        #expect(WhisperPrompts(hindi: .devanagari).text(forLanguage: "hi")?.contains("ऑफिस") == true)
        #expect(WhisperPrompts().text(forLanguage: "en") == nil)
    }

    @Test func staysInsideGroqsLimitAndKeepsTheSeedLast() throws {
        let terms = (0..<400).map { "Term\($0)" }
        let text = try #require(WhisperPrompts(terms: terms).text(forLanguage: "te"))
        #expect(text.count <= WhisperPrompt.maximumCharacters)
        #expect(WhisperPrompt.estimatedTokens(text) <= WhisperPrompt.maximumEstimatedTokens)
        #expect(text.hasSuffix(WhisperPrompt.teluguMixedSeed))
    }
}

struct LoopFilterTests {
    private func line(_ t: Double, _ text: String, _ who: Speaker = .others) -> TranscriptLine { TranscriptLine(time: t, speaker: who, text: text) }

    @Test func compressibleAndSilentSegmentsAreDropped() {
        #expect(WhisperSegmentFilter.shouldDrop(.init(start: 0, end: 1, text: "the same words the same words the same", compressionRatio: 3.1)))
        #expect(WhisperSegmentFilter.shouldDrop(.init(start: 0, end: 1, text: "Thanks", avgLogprob: -1.4, noSpeechProb: 0.9)))
        #expect(!WhisperSegmentFilter.shouldDrop(.init(start: 0, end: 1, text: "A normal sentence here.", avgLogprob: -0.2, noSpeechProb: 0.01, compressionRatio: 1.3)))
    }

    @Test func loopedSegmentsCollapseAndTextIsRebuilt() {
        let seg = DetailedTranscript.Segment(start: 0, end: 1, text: "Okay.")
        let other = DetailedTranscript.Segment(start: 4, end: 5, text: "Next point.")
        let out = WhisperSegmentFilter.filtered(DetailedTranscript(text: "x", language: "en", segments: [seg, seg, seg, seg, other]))
        #expect(out.segments.count == 3)
        #expect(out.text == "Okay. Okay. Next point.")
    }

    @Test func runsCollapseAcrossChunksAndSpeakers() {
        let lines = [line(0, "Okay."), line(3, "Okay."), line(4, "Sure", .you), line(6, "Okay."), line(9, "okay"), line(40, "Okay.")]
        let out = TranscriptFilter.collapseRepeats(lines)
        #expect(out.map(\.time) == [0, 4, 40])
    }

    @Test func twoRepeatsAndSlowRepeatsStay() {
        #expect(TranscriptFilter.collapseRepeats([line(0, "Yes"), line(3, "Yes")]).count == 2)
        #expect(TranscriptFilter.collapseRepeats([line(0, "Yes"), line(30, "Yes"), line(60, "Yes")]).count == 3)
    }

    @Test func lostMarkersAreNeverCollapsed() {
        let m = TranscriptFilter.lostMarker
        #expect(TranscriptFilter.collapseRepeats([line(0, m), line(5, m), line(10, m)]).count == 3)
    }
}

struct StreamChunkerTests {
    static func speech(_ s: Double) -> [Float] { (0..<Int(s * 16_000)).map { 0.3 * sin(Float($0) * 0.17) } }
    static func silence(_ s: Double) -> [Float] { [Float](repeating: 0, count: Int(s * 16_000)) }

    @Test func silenceIsNeverUploaded() {
        var c = StreamChunker()
        #expect(c.feed(Self.silence(200)).isEmpty)
        #expect(c.finish() == nil)
    }

    @Test func onlyVoicedAudioIsUploadedAndMapsBack() throws {
        var c = StreamChunker()
        var chunks = c.feed(Self.silence(10) + Self.speech(12) + Self.silence(30) + Self.speech(12) + Self.silence(2))
        if let last = c.finish() { chunks.append(last) }
        #expect(chunks.count == 1)
        let chunk = try #require(chunks.first)
        #expect(Double(chunk.samples.count) / 16_000 < 26)
        #expect(abs(chunk.map.meetingTime(chunkSeconds: 0.3) - 10) < 0.4)
        let second = try #require(chunk.map.spans.last)
        #expect(abs(chunk.map.meetingTime(chunkSeconds: second.chunkStart + 0.3) - 52) < 0.4)
        #expect(chunk.map.spliceGaps(minimumGap: 2).count == 1)
    }

    @Test func continuousSpeechIsCutAt45Seconds() {
        var c = StreamChunker()
        var chunks = c.feed(Self.speech(100))
        if let last = c.finish() { chunks.append(last) }
        let seconds: [Double] = chunks.map { Double($0.samples.count) / 16_000 }
        let total: Double = seconds.reduce(0, +)
        #expect(seconds.allSatisfy { $0 <= 45.05 })
        #expect(abs(total - 100) < 0.1)
        #expect(abs(chunks[1].map.meetingTime(chunkSeconds: 0) - 45) < 0.1)
    }

    @Test func aQuietSideStillShowsUpAfterMaxWait() {
        var c = StreamChunker()
        let chunks = c.feed(Self.speech(3) + Self.silence(125))
        #expect(chunks.count == 1)
    }

    @Test func noisyRoomStaysSilentButVoiceGetsThrough() {
        var c = StreamChunker()
        // Steady hiss at about -50 dB is under the absolute floor; speech on top is voiced.
        let hiss: [Float] = (0..<(16_000 * 30)).map { _ in Float.random(in: -0.0045...0.0045) }
        #expect(c.feed(hiss).isEmpty)
        let chunks = c.feed(Self.speech(25) + Self.silence(1))
        #expect(chunks.count == 1)
    }

    @Test func splittingAtARemovedPauseUsesWordTimes() {
        let map = TimeMap(spans: [.init(chunkStart: 0, meetingStart: 100, seconds: 2), .init(chunkStart: 2.25, meetingStart: 160, seconds: 2)])
        let words: [DetailedTranscript.Word] = [.init(word: "before", start: 0.2, end: 0.8), .init(word: "pause", start: 1, end: 1.6), .init(word: "after", start: 2.4, end: 3)]
        let t = DetailedTranscript(text: "before pause after", language: "en", segments: [.init(start: 0.2, end: 3, text: "before pause after", words: words)])
        let lines = ChunkLines.lines(for: t, map: map, speaker: .you, chunkFallbackTime: 100)
        #expect(lines.map(\.text) == ["before pause", "after"])
        #expect(abs(lines[1].time - 160.15) < 0.01)
    }
}

struct EchoTests {
    private func line(_ t: Double, _ text: String, _ who: Speaker) -> TranscriptLine { TranscriptLine(time: t, speaker: who, text: text) }

    @Test func callHeardByTheMicIsRemoved() {
        let out = TranscriptMerge.merge(existing: [line(10, "the payment service is ready for the next release", .others)],
                                        incoming: [line(10.8, "payment service is ready for the next release", .you)])
        #expect(out.map(\.speaker) == [.others])
    }

    @Test func realAnswersStay() {
        let out = TranscriptMerge.merge(existing: [line(10, "the payment service is ready for the next release", .others)],
                                        incoming: [line(13, "great, let's ship it on Friday then", .you)])
        #expect(out.count == 2)
    }

    @Test func shortRepliesAreNeverEchoes() {
        let out = TranscriptMerge.merge(existing: [line(10, "Okay.", .others)], incoming: [line(10.2, "Okay.", .you)])
        #expect(out.count == 2)
    }

    @Test func farApartLinesAreNotEchoes() {
        let text = "the payment service is ready for the next release"
        let out = TranscriptMerge.merge(existing: [line(10, text, .others)], incoming: [line(60, text, .you)])
        #expect(out.count == 2)
    }

    @Test func mergeIsOrderedByTime() {
        let out = TranscriptMerge.merge(existing: [line(5, "one two", .others), line(20, "five six", .others)], incoming: [line(10, "three four", .you)])
        #expect(out.map(\.time) == [5, 10, 20])
    }
}

struct RateBudgetTests {
    let t0 = Date(timeIntervalSince1970: 1_000_000)
    let whisper = GroqCost.transcription(model: "whisper-large-v3-turbo", audioSeconds: 30)

    @Test func backgroundLeavesRoomForDictation() {
        var b = RateBudget(limits: .whisper)
        for _ in 0..<16 { b.record(whisper, at: t0) }
        guard case .wait(let s) = b.decision(for: whisper, priority: .background, now: t0.addingTimeInterval(1)) else { Issue.record("should wait"); return }
        #expect(s > 50 && s <= 60)
        #expect(b.decision(for: whisper, priority: .interactive, now: t0.addingTimeInterval(1)) == .go)
    }

    @Test func shortUploadsAreBilledTenSeconds() {
        #expect(GroqCost.transcription(model: "w", audioSeconds: 2).billedAudioSeconds == 10)
        #expect(whisper.billedAudioSeconds == 30)
    }

    @Test func hourlyAudioBudgetHolds() {
        var b = RateBudget(limits: .whisper)
        for i in 0..<220 { b.record(whisper, at: t0.addingTimeInterval(Double(i) * 10)) }   // 6,600 s billed
        let now = t0.addingTimeInterval(2_210)
        guard case .wait = b.decision(for: whisper, priority: .background, now: now) else { Issue.record("should wait"); return }
        #expect(b.decision(for: whisper, priority: .interactive, now: now) == .go)
    }

    @Test func retryAfterBlocksBackgroundOnly() {
        var b = RateBudget(limits: .whisper)
        b.applyRetryAfter(30, now: t0)
        guard case .wait(let s) = b.decision(for: whisper, priority: .background, now: t0) else { Issue.record("should wait"); return }
        #expect(abs(s - 30) < 0.01)
        guard case .wait(let i) = b.decision(for: whisper, priority: .interactive, now: t0) else { Issue.record("brief wait"); return }
        #expect(i <= 2)
    }

    @Test func headersOverrideDefaults() {
        var b = RateBudget(limits: .chat)
        b.apply(headers: ["x-ratelimit-limit-requests": "500", "x-ratelimit-limit-tokens": "6000"], now: t0)
        #expect(b.limits.requestsPerDay == 500)
        #expect(b.limits.tokensPerMinute == 6_000)
    }

    @Test func durationsParse() {
        #expect(RateBudget.parseDuration("43.2s") == 43.2)
        #expect(RateBudget.parseDuration("2m59.5s") == 179.5)
        #expect(RateBudget.parseDuration("1h2m3s") == 3_723)
    }

    @Test func dailyLimitTextIsRecognised() {
        #expect(GroqLimitMessage.dailyResource(in: "Rate limit reached on tokens per day (TPD): Limit 200000") == "tokens")
        #expect(GroqLimitMessage.dailyResource(in: "on seconds of audio per day (ASPD): Limit 28800") == "transcription")
        #expect(GroqLimitMessage.dailyResource(in: "on seconds of audio per hour (ASPH): Limit 7200") == nil)
    }
}

struct GroqGateTests {
    private func ok() -> (Data, HTTPURLResponse) {
        (Data(), HTTPURLResponse(url: URL(string: "https://x")!, statusCode: 200, httpVersion: nil, headerFields: nil)!)
    }

    @Test func dictationGoesAheadOfABlockedBackgroundQueue() async throws {
        let gate = GroqGate(sleep: { _ in try? await Task.sleep(for: .milliseconds(5)) })
        let cost = GroqCost.transcription(model: "whisper-large-v3-turbo", audioSeconds: 30)
        let response = ok()
        let result = response
        for _ in 0..<16 { _ = try await gate.perform(cost, priority: .background) { result } }
        let started = Date()
        _ = try await gate.perform(cost, priority: .interactive) { result }
        #expect(Date().timeIntervalSince(started) < 1)
    }

    @Test func backgroundGivesUpInsteadOfWaitingForever() async throws {
        let gate = GroqGate(sleep: { _ in })
        let cost = GroqCost.transcription(model: "whisper-large-v3-turbo", audioSeconds: 30)
        let result = ok()
        await #expect(throws: ProviderError.self) {
            for _ in 0..<40 { _ = try await gate.perform(cost, priority: .background) { result } }
        }
    }
}

struct ChunkHoldTests {
    private func chunk(_ s: Double) -> HeldChunk {
        HeldChunk(samples: [Float](repeating: 0.1, count: Int(s * 16_000)), map: TimeMap(spans: []), speaker: .others, voicedSeconds: s)
    }

    @Test func oldestIsEvictedWhenFull() {
        var hold = ChunkHold(capacity: 100)
        #expect(hold.add(chunk(60)).isEmpty)
        let evicted = hold.add(chunk(60))
        #expect(evicted.count == 1)
        #expect(hold.totalSeconds == 60)
        #expect(hold.takeAll().count == 1)
        #expect(hold.chunks.isEmpty)
    }

    @Test func samplesSurviveTheRoundTrip() {
        let held = HeldChunk(samples: [0.5, -0.5], map: TimeMap(spans: []), speaker: .you, voicedSeconds: 1)
        #expect(abs(held.floatSamples[0] - 0.5) < 0.001)
        #expect(abs(held.floatSamples[1] + 0.5) < 0.001)
    }
}

@MainActor
struct SeparateStreamsTests {
    typealias T = MeetingRecorderTests

    @Test func eachSideIsUploadedSeparatelyAndOverlapStaysApart() async throws {
        let mic = FakeSource(), system = FakeSource()
        let plans = PlanLog()
        let recorder = MeetingRecorder(mic: { _ in mic }, system: { system }) { _, request in
            await plans.add(request.plan.speaker)
            let who = request.plan.speaker == .user ? "mine words here now" : "their different sentence over"
            return DetailedTranscript(text: who, language: "en", segments: [.init(start: 0, end: 5, text: who)])
        }
        try recorder.start(title: "T", app: "", attendees: [], settings: T.settings, autoTick: false)
        mic.queue = T.speech(5); system.queue = T.speech(5)
        let meeting = await recorder.stop()
        #expect(Set(await plans.all) == [.user, .anyone])
        #expect(Set(meeting?.lines.map(\.speaker) ?? []) == [.you, .others])
    }

    @Test func failedChunkIsRetriedAtTheEndNotMarked() async throws {
        let mic = FakeSource()
        let tries = Counter()
        let recorder = MeetingRecorder(mic: { _ in mic }, system: { nil }) { _, _ in
            if await tries.next() <= MeetingRecorder.liveAttempts { throw ProviderError.network("offline") }
            return DetailedTranscript(text: "recovered", language: "en", segments: [])
        }
        try recorder.start(title: "T", app: "", attendees: [], settings: T.settings, autoTick: false)
        mic.queue = T.speech(5)
        let meeting = await recorder.stop()
        #expect(meeting?.lines.map(\.text) == ["recovered"])
        #expect(meeting?.note == nil)
    }

    @Test func dailyLimitIsToldPlainlyAndMarked() async throws {
        let mic = FakeSource()
        let recorder = MeetingRecorder(mic: { _ in mic }, system: { nil }) { _, _ in
            throw ProviderError.dailyLimit(resource: "transcription", retryAfter: 7_200)
        }
        try recorder.start(title: "T", app: "", attendees: [], settings: T.settings, autoTick: false)
        var told: String?
        recorder.onDailyLimit = { told = $0 }
        mic.queue = T.speech(5)
        let meeting = await recorder.stop()
        #expect(meeting?.lines.first?.text == TranscriptFilter.lostMarker)
        #expect(meeting?.note?.contains("daily limit") == true)
        #expect(told?.contains("daily limit") == true)
    }

    @Test func quittingMarksFailedChunksWithoutRetrying() async throws {
        let mic = FakeSource()
        let tries = Counter()
        let recorder = MeetingRecorder(mic: { _ in mic }, system: { nil }) { _, _ in
            _ = await tries.next()
            throw ProviderError.network("offline")
        }
        try recorder.start(title: "T", app: "", attendees: [], settings: T.settings, autoTick: false)
        mic.queue = T.speech(5)
        let meeting = await recorder.stop(retryFailed: false)
        #expect(meeting?.lines.first?.text == TranscriptFilter.lostMarker)
        #expect(await tries.next() == MeetingRecorder.liveAttempts + 1)
    }
}

actor PlanLog {
    private(set) var all: [LanguagePlan.Speaker] = []
    func add(_ s: LanguagePlan.Speaker) { all.append(s) }
}

struct ScriptWriterTests {
    @Test func onlyLinesThatNeedItAreSent() {
        #expect(TranscriptScriptWriter.needsRewrite("నేను రేపు office కి వస్తాను", telugu: .mixed, hindi: nil))
        #expect(!TranscriptScriptWriter.needsRewrite("నేను రేపు వస్తాను", telugu: .telugu, hindi: nil))
        #expect(TranscriptScriptWriter.needsRewrite("నేను office", telugu: .telugu, hindi: nil))
        #expect(!TranscriptScriptWriter.needsRewrite("Plain English line", telugu: .mixed, hindi: .mixed))
        #expect(!TranscriptScriptWriter.needsRewrite("मैं कल आऊँगा", telugu: .mixed, hindi: nil))
    }

    @Test func badRepliesAreRejected() {
        #expect(TranscriptScriptWriter.parse(#"{"lines":["a"]}"#, expected: ["x", "y"]) == nil)
        #expect(TranscriptScriptWriter.parse(#"{"lines":[""]}"#, expected: ["x"]) == nil)
        #expect(TranscriptScriptWriter.parse(#"{"lines":["one two three four five six seven eight nine"]}"#, expected: ["one two"]) == nil)
        #expect(TranscriptScriptWriter.parse("nope", expected: ["x"]) == nil)
        #expect(TranscriptScriptWriter.parse(#"{"lines":["ok then"]}"#, expected: ["okay then"]) == ["ok then"])
    }

    @Test func batchesFitOneRequest() {
        let line = TranscriptLine(time: 0, speaker: .others, text: String(repeating: "నేను రేపు ", count: 40))
        let batches = TranscriptScriptWriter.batches(Array(repeating: line, count: 10), telugu: .mixed, hindi: nil)
        #expect(batches.count > 1)
        #expect(batches.flatMap { $0 }.count == 10)
    }
}

struct MeetingFeatureTests {
    private func meeting() -> Meeting {
        var m = Meeting(title: "Sync")
        m.lines = [TranscriptLine(time: 0, speaker: .others, text: "The payment gateway is down"), TranscriptLine(time: 5, speaker: .you, text: "నేను చూస్తాను")]
        m.notes = MeetingNotes(summary: ["Budget moved"], actionItems: [ActionItem(task: "Call Ramesh")])
        return m
    }

    @Test func searchCoversTitleNotesAndTranscript() {
        let m = meeting()
        #expect(MeetingSearch.matches(m, query: "sync"))
        #expect(MeetingSearch.matches(m, query: "RAMESH"))
        #expect(MeetingSearch.matches(m, query: "చూస్తాను"))
        #expect(!MeetingSearch.matches(m, query: "invoice"))
        #expect(MeetingSearch.matches(m, query: "  "))
        #expect(MeetingSearch.snippet(m, query: "gateway")?.contains("gateway") == true)
        #expect(MeetingSearch.snippet(m, query: "sync") == nil)
    }

    @Test func renamingOthersReachesTheMarkdownAndYouStays() {
        var m = meeting()
        m.othersLabel = "Priya"
        let md = MeetingMarkdown.render(m)
        #expect(md.contains("**Priya** [0:00]"))
        #expect(md.contains("**You** [0:05]"))
        #expect(m.transcriptText.contains("Priya:"))
        m.othersLabel = "   "
        #expect(m.label(for: .others) == "Others")
    }

    @Test func ticksSurviveRewritingNotes() {
        var old = MeetingNotes(actionItems: [ActionItem(task: "Send deck"), ActionItem(task: "Book room")])
        old.actionItems[0].done = true
        let new = MeetingNotes(actionItems: [ActionItem(task: "send deck"), ActionItem(task: "New task")]).carryingOverDone(from: old)
        #expect(new.actionItems.map(\.done) == [true, false])
    }

    @Test func oldMeetingsStillDecode() throws {
        let json = #"[{"id":"7E57D004-2B97-4E7A-B45F-5387367791CD","title":"Old","start":0,"app":"","attendees":[],"lines":[{"time":1,"speaker":"you","text":"hi"}],"status":"done","micOnly":false}]"#
        let meetings = try JSONDecoder().decode([Meeting].self, from: Data(json.utf8))
        #expect(meetings[0].lines[0].end == nil)
        #expect(meetings[0].othersLabel == nil)
        #expect(meetings[0].label(for: .others) == "Others")
    }

    @Test func vagueTitlesAreReplaced() {
        #expect(NotesTitle.isVague("Unclear Discussion Session"))
        #expect(NotesTitle.isVague("General Meeting"))
        #expect(!NotesTitle.isVague("Payment gateway outage"))
        let notes = MeetingNotes(summary: ["Payment gateway outage hit checkout on Tuesday morning again"])
        #expect(NotesTitle.resolved("Unclear Discussion Session", notes: notes, fallback: "Zoom meeting") == "Payment gateway outage hit checkout on")
        #expect(NotesTitle.resolved("Unclear", notes: MeetingNotes(), fallback: "Zoom meeting") == "Zoom meeting")
    }

    @Test func notesFollowTheWritingChoice() {
        var m = Meeting(title: "T")
        m.lines = [TranscriptLine(time: 0, speaker: .others, text: "నేను రేపు వస్తాను", language: "te")]
        #expect(MeetingNotesWriter.languageRule(.meeting, meeting: m, telugu: .roman, hindi: .mixed).contains("English letters"))
        #expect(MeetingNotesWriter.languageRule(.english, meeting: m, telugu: .roman, hindi: .mixed).contains("in English"))
    }

    @Test func hindiChoiceRoundTripsAndOldPreferencesMigrate() throws {
        var prefs = Preferences()
        prefs.hindiScript = .hinglish
        let back = try JSONDecoder().decode(Preferences.self, from: JSONEncoder().encode(prefs))
        #expect(back.hindiScript == .hinglish)
        let old = try JSONDecoder().decode(Preferences.self, from: Data(#"{"languages":["en","te"],"teluguScript":"roman"}"#.utf8))
        #expect(old.languages == ["en", "te"])
        #expect(old.hindiScript == .mixed)
        #expect(CleanupPrompt.userMessage(transcript: "x", style: "casual", app: nil, names: [], hindi: "hinglish").contains("<hindi>hinglish</hindi>"))
    }
}
