import Foundation
import Testing
@testable import VoiceFlowCore

struct AudioMathTests {
    private func tone(seconds: Double, amplitude: Float = 0.3) -> [Float] {
        (0..<Int(seconds * 16_000)).map { amplitude * sin(Float($0) * 2 * .pi * 220 / 16_000) }
    }

    @Test func wavHeaderIsMono16kHz16Bit() {
        let wav = AudioMath.wav([0, 0.5, -0.5, 1])
        #expect(wav.count == 44 + 8)
        #expect(String(decoding: wav[0..<4], as: UTF8.self) == "RIFF")
        #expect(String(decoding: wav[8..<16], as: UTF8.self) == "WAVEfmt ")
        let u16 = { (o: Int) in UInt16(wav[o]) | UInt16(wav[o + 1]) << 8 }
        let u32 = { (o: Int) in UInt32(wav[o]) | UInt32(wav[o + 1]) << 8 | UInt32(wav[o + 2]) << 16 | UInt32(wav[o + 3]) << 24 }
        #expect(u16(20) == 1)        // PCM
        #expect(u16(22) == 1)        // mono
        #expect(u32(24) == 16_000)   // sample rate
        #expect(u16(34) == 16)       // bits
        #expect(u32(40) == 8)        // data bytes
    }

    @Test func silenceIsDetected() {
        #expect(AudioMath.isSilent([Float](repeating: 0, count: 48_000)))
        #expect(AudioMath.isSilent([Float](repeating: 0.001, count: 48_000)))   // room hum, -60 dB
        #expect(!AudioMath.isSilent(tone(seconds: 1)))
    }

    @Test func whisperModeBoostsQuietSpeechButNotSilenceOrLoudSpeech() {
        let quiet = tone(seconds: 0.5, amplitude: 0.05)
        let boosted = AudioMath.normalized(quiet)
        #expect(boosted.map(abs).max()! >= 0.59)   // 0.05 × 12 (gain cap)
        let loud = tone(seconds: 0.5, amplitude: 0.8)
        #expect(AudioMath.normalized(loud) == loud)
        #expect(AudioMath.normalized([0, 0, 0]) == [0, 0, 0])
        let faint = tone(seconds: 0.5, amplitude: 0.001)                       // gain capped at 12×
        #expect(AudioMath.normalized(faint).map(abs).max()! < 0.02)
    }

    @Test func hallucinationsOnSilenceAreDropped() {
        #expect(HallucinationFilter.isLikelyHallucination("Thank you.", voicedSeconds: 0.3))
        #expect(!HallucinationFilter.isLikelyHallucination("Thank you.", voicedSeconds: 2))
        #expect(!HallucinationFilter.isLikelyHallucination("Send the report", voicedSeconds: 0.5))
        #expect(HallucinationFilter.isLikelyHallucination("  ", voicedSeconds: 3))
    }
}

struct SegmentCutterTests {
    private func speech(_ seconds: Double) -> [Float] {
        (0..<Int(seconds * 16_000)).map { 0.3 * sin(Float($0) * 0.17) }
    }
    private func pause(_ seconds: Double) -> [Float] { [Float](repeating: 0, count: Int(seconds * 16_000)) }

    @Test func tailCutsMatchWholeRecordingCuts() {
        let audio = speech(6) + pause(1) + speech(6) + pause(0.6) + speech(12) + pause(0.5) + speech(20) + pause(1) + speech(35)
        var whole = SegmentCutter()
        var tailed = SegmentCutter()
        for end in stride(from: 16_000, through: audio.count, by: 16_000) {
            let expected = whole.nextCut(in: Array(audio[..<end]))
            let start = tailed.openStart
            let actual = tailed.nextCut(inTail: Array(audio[start..<end]), startingAt: start)
            #expect(expected == actual)
            #expect(whole.openStart == tailed.openStart)
        }
        #expect(whole.openStart > 30 * 16_000)   // several cuts happened
    }

    @Test func noCutBeforeTenSeconds() {
        var c = SegmentCutter()
        #expect(c.nextCut(in: speech(4) + pause(1) + speech(4)) == nil)
    }

    @Test func cutsAtFirstPauseAfterTenSeconds() {
        var c = SegmentCutter()
        let audio = speech(6) + pause(1) + speech(6) + pause(0.6) + speech(3)
        let cut = c.nextCut(in: audio)
        #expect(cut != nil)
        let seconds = Double(cut!.upperBound) / 16_000
        #expect(seconds > 13 && seconds < 13.6)     // inside the second pause (13.0–13.6 s), not the first
        #expect(c.openStart == cut!.upperBound - 3_200) // 200 ms overlap
    }

    @Test func forcesCutAtThirtySeconds() {
        var c = SegmentCutter()
        let cut = c.nextCut(in: speech(31))
        #expect(cut == 0..<(30 * 16_000))
    }

    @Test func finishReturnsTheRest() {
        var c = SegmentCutter()
        #expect(c.finish(totalSamples: 1_000) == 0..<1_000)
        #expect(c.finish(totalSamples: 1_000) == nil)
    }

    @Test func joinDropsWordRepeatedAcrossOverlap() {
        #expect(SegmentCutter.join(["We should ship on", "on Friday."]) == "We should ship on Friday.")
        #expect(SegmentCutter.join(["Hello there.", "", "General Kenobi."]) == "Hello there. General Kenobi.")
    }
}

struct ContextTests {
    @Test func namesFromScreenText() {
        let names = NameExtractor.extract(from: ["Hi Priya, Ramesh shared the LearnX deck. The deck from Ramesh is in @saideep's folder. Thanks"])
        #expect(names.first == "Ramesh")
        #expect(names.contains("Priya"))
        #expect(names.contains("LearnX"))
        #expect(names.contains("@saideep's") || names.contains("@saideep"))
        #expect(!names.contains("The"))
        #expect(!names.contains("Thanks"))
    }

    @Test func whisperPromptFitsTheLimitAndDedupes() {
        let p = NameExtractor.whisperPrompt(dictionary: ["Saideep", "LearnX"], names: ["saideep", "Priya"])
        #expect(p == "Saideep, LearnX, Priya")
        let long = NameExtractor.whisperPrompt(dictionary: (0..<200).map { "Term\($0)" }, names: [])!
        #expect(long.count <= 650)
        #expect(NameExtractor.whisperPrompt(dictionary: [], names: []) == nil)
    }

    @Test func spokenFileNamesMatchWorkspaceFiles() {
        let files = ["Sources/Auth/AuthController.swift", "src/user_service.py", "README.md", "src/app.ts", "web/LoginForm.tsx"]
        #expect(WorkspaceFiles.mentioned(in: "open the auth controller and fix it", files: files) == ["AuthController.swift"])
        #expect(WorkspaceFiles.mentioned(in: "check user service for the bug", files: files) == ["user_service.py"])
        #expect(WorkspaceFiles.mentioned(in: "the loginform needs a label", files: files) == ["LoginForm.tsx"])
        #expect(WorkspaceFiles.mentioned(in: "update the app", files: files).isEmpty)  // too short to be specific
    }

    @Test func appClassification() {
        #expect(AppClassifier.usage(bundleID: "com.apple.mail", windowTitle: "") == .email)
        #expect(AppClassifier.usage(bundleID: "com.tinyspeck.slackmacgap", windowTitle: "") == .work)
        #expect(AppClassifier.usage(bundleID: "net.whatsapp.WhatsApp", windowTitle: "") == .personal)
        #expect(AppClassifier.usage(bundleID: "com.google.Chrome", windowTitle: "Inbox (3) - me@gmail.com - Gmail") == .email)
        #expect(AppClassifier.usage(bundleID: "com.apple.Safari", windowTitle: "WhatsApp") == .personal)
        #expect(AppClassifier.usage(bundleID: "com.apple.Safari", windowTitle: "ChatGPT") == .aiPrompts)
        #expect(AppClassifier.usage(bundleID: "com.unknown.app", windowTitle: "Gmail") == .other)
        #expect(UsageCategory.aiPrompts.styleCategory == .other)
        #expect(AppClassifier.isCode(bundleID: "com.apple.Terminal"))
        #expect(AppClassifier.isCode(bundleID: "com.jetbrains.intellij"))
        #expect(AppClassifier.style(bundleID: "com.microsoft.VSCode", windowTitle: "", preferences: Preferences()) == .code)
        #expect(AppClassifier.style(bundleID: "com.apple.mail", windowTitle: "", preferences: Preferences()) == .formal)
        #expect(AppClassifier.tagsFiles(bundleID: "com.todesktop.230313mzl4w4u92"))
        #expect(!AppClassifier.tagsFiles(bundleID: "com.microsoft.VSCode"))
    }
}

struct LibraryTests {
    @Test func snippetExactAndInline() {
        let snippets = [Snippet(trigger: "my address", expansion: "12 Park Road, Hyderabad"),
                        Snippet(trigger: "calendar link", expansion: "https://cal.com/saideep")]
        #expect(SnippetMatcher.exactMatch("My address.", snippets: snippets)?.expansion == "12 Park Road, Hyderabad")
        #expect(SnippetMatcher.exactMatch("send my address", snippets: snippets) == nil)
        #expect(SnippetMatcher.expandInline("Book a slot here: Calendar link.", snippets: snippets)
                == "Book a slot here: https://cal.com/saideep.")
        #expect(SnippetMatcher.expandInline("Price is $5 at my address", snippets: [Snippet(trigger: "my address", expansion: "$1 Main St")])
                == "Price is $5 at $1 Main St")
    }

    @Test func autoLearnPicksUpCorrectedNames() {
        let learned = AutoLearner.learnedTerms(inserted: "Send it to Sai Deep and pria today.",
                                               fieldBefore: "Hello team,",
                                               fieldAfter: "Hello team, Send it to Saideep and Priya today.", known: [])
        #expect(learned.contains("Priya"))
        #expect(learned.contains("Saideep"))
    }

    @Test func autoLearnIgnoresOrdinaryRewrites() {
        #expect(AutoLearner.learnedTerms(inserted: "the meeting is today", fieldBefore: "",
                                         fieldAfter: "the call is tomorrow", known: []).isEmpty)
        #expect(AutoLearner.learnedTerms(inserted: "ask pria", fieldBefore: "", fieldAfter: "ask Priya", known: ["priya"]).isEmpty)
    }

    @Test func statsAndStreak() {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        func entry(_ daysAgo: Double, _ text: String, _ seconds: Double, app: String = "Notes") -> HistoryEntry {
            var e = HistoryEntry(kind: .dictation, appName: app, raw: text, final: text, audioSeconds: seconds, latencyMs: 500, outcome: "inserted")
            e.date = now.addingTimeInterval(-daysAgo * 86_400)
            return e
        }
        let entries = [entry(0, "one two three four", 3), entry(1, "five six", 3, app: "Slack"), entry(10, "seven eight nine ten", 6)]
        let s = UsageStats.compute(entries, now: now)
        #expect(s.totalWords == 10)
        #expect(s.wordsThisWeek == 6)
        #expect(s.dictations == 3)
        #expect(s.wordsPerMinute == 50)
        #expect(s.dayStreak == 2)
        #expect(s.topApps.first?.0 == "Notes")
        #expect(applyRetention(entries, days: 7, now: now).count == 2)
        #expect(applyRetention(entries, days: -1, now: now).count == 3)
        #expect(applyRetention(entries, days: 0, now: now).isEmpty)
    }
}

struct AITests {
    @Test func commandResultParsing() {
        #expect(CommandPrompt.parse(#"{"action":"replace","text":"Hello."}"#) == .replace("Hello."))
        #expect(CommandPrompt.parse(#"{"action":"insert","text":"Draft"}"#) == .insert("Draft"))
        #expect(CommandPrompt.parse(#"{"action":"search","query":"swift actors"}"#) == .search("swift actors"))
        #expect(CommandPrompt.parse(#"{"action":"answer","text":"It means yes."}"#) == .answer("It means yes."))
        #expect(CommandPrompt.parse(#"{"action":"replace","text":""}"#) == nil)
        #expect(CommandPrompt.parse("not json") == nil)
        #expect(CommandPrompt.searchURL("a b").absoluteString == "https://www.perplexity.ai/search?q=a%20b")
    }

    @Test func shortCleanDictationsSkipTheModel() {
        #expect(TextCleaner.canSkipAI("Okay thanks"))
        #expect(TextCleaner.canSkipAI("git status"))
        #expect(!TextCleaner.canSkipAI("um okay"))
        #expect(!TextCleaner.canSkipAI("buy milk new line"))
        #expect(!TextCleaner.canSkipAI("this has more than three words"))
    }

    @Test func tidyRespectsStyleAndCursor() {
        #expect(TextCleaner.tidy("okay thanks", style: .casual, beforeCursor: "") == "Okay thanks")
        #expect(TextCleaner.tidy("okay thanks", style: .casual, beforeCursor: "I said") == "okay thanks")
        #expect(TextCleaner.tidy("okay thanks", style: .casual, beforeCursor: "Done. ") == "Okay thanks")
        #expect(TextCleaner.tidy("git status", style: .code, beforeCursor: "") == "git status")
    }

    @Test func modelOutputSanityChecks() {
        #expect(TextCleaner.problem(with: "", raw: "hello") != nil)
        #expect(TextCleaner.problem(with: "<transcript>hi</transcript>", raw: "hi") != nil)
        #expect(TextCleaner.problem(with: String(repeating: "word ", count: 40), raw: "what is the capital of france") != nil)
        #expect(TextCleaner.problem(with: "What is the capital of France?", raw: "what is the capital of france") == nil)
    }

    @Test func userMessageIncludesCursorAndFiles() {
        let msg = CleanupPrompt.userMessage(transcript: "open auth controller", style: "code", app: "Cursor", names: [],
                                            beforeCursor: "Please ", files: ["AuthController.swift"], tagFiles: true)
        #expect(msg.contains("<before_cursor>Please </before_cursor>"))
        #expect(msg.contains("<files>AuthController.swift</files>\n<tag_files>yes</tag_files>"))
    }

    @MainActor @Test func leadingSpaceOnlyAfterAWord() {
        #expect(TextInserter.withLeadingSpace("hello", beforeCursor: "Hi") == " hello")
        #expect(TextInserter.withLeadingSpace("hello", beforeCursor: "Hi ") == "hello")
        #expect(TextInserter.withLeadingSpace("hello", beforeCursor: "") == "hello")
        #expect(TextInserter.withLeadingSpace(", and more", beforeCursor: "Hi") == ", and more")
        #expect(TextInserter.withLeadingSpace("name", beforeCursor: "@") == "name")
    }
}

/// End to end against Groq with a real fixture clip. Runs only when GROQ_API_KEY is set.
struct LivePipelineTests {
    static let key = ProcessInfo.processInfo.environment["GROQ_API_KEY"]
    static let fixtures = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
        .appending(path: "../../../fixtures/clips").standardized

    @Test(.enabled(if: key != nil))
    func selfCorrectionClipBecomesCleanText() async throws {
        let wav = try Data(contentsOf: Self.fixtures.appending(path: "self-correction.wav"))
        let start = Date()
        let transcript = try await GroqTranscriber(apiKey: Self.key!).transcribe(wav: wav, language: "en", prompt: nil)
        let heard = Date()
        let outcome = await TextCleaner(chat: GroqChat(apiKey: Self.key!), model: "qwen/qwen3.8-27b")
            .clean(CleanupRequest(transcript: transcript, style: .casual, appName: "Slack"))
        let done = Date()
        print("transcript: \(transcript)\ncleaned:    \(outcome.text)\ntimes: STT \(heard.timeIntervalSince(start))s, cleanup \(done.timeIntervalSince(heard))s")
        #expect(outcome.usedAI)
        #expect(outcome.text.contains("Wednesday"))
        #expect(!outcome.text.contains("Tuesday"))
    }

    @Test(.enabled(if: key != nil))
    func commandModeRewritesSelection() async throws {
        let result = try await CommandRunner(chat: GroqChat(apiKey: Self.key!), model: "openai/gpt-oss-120b")
            .run(app: "Mail", fullText: "hey can u send the report", selectedText: "hey can u send the report",
                 instruction: "make this more formal")
        guard case .replace(let text) = result else {
            Issue.record("expected replace, got \(result)")
            return
        }
        print("command result: \(text)")
        #expect(text.localizedCaseInsensitiveContains("report"))
        #expect(!text.contains(" u "))
    }
}


struct UIFeatureTests {
    @Test func misspellingCorrectionsKeepSavedCasing() {
        let entries = [DictionaryEntry(term: "Saideep", misspelling: "sai deep", source: .manual),
                       DictionaryEntry(term: "LearnX", misspelling: "learn x", source: .manual),
                       DictionaryEntry(term: "Priya", source: .manual)]
        let result = DictionaryCorrector.apply("Ask Sai Deep about learn x. Saidee is fine.", entries: entries)
        #expect(result.text == "Ask Saideep about LearnX. Saidee is fine.")
        #expect(result.fixes == 2)
    }

    @Test func snippetValidationMessages() {
        let existing = [Snippet(trigger: "my address", expansion: "12 Park Road")]
        #expect(SnippetMatcher.validate(trigger: "", expansion: "x", existing: []) == "Please enter a snippet and its expansion.")
        #expect(SnippetMatcher.validate(trigger: "My Address!", expansion: "x", existing: existing)?.contains("already exists") == true)
        #expect(SnippetMatcher.validate(trigger: String(repeating: "a", count: 61), expansion: "x", existing: []) != nil)
        #expect(SnippetMatcher.validate(trigger: "link", expansion: String(repeating: "x", count: 4_001), existing: [])
                == "This expansion is too long. Please shorten it and try again.")
        #expect(SnippetMatcher.validate(trigger: "my address", expansion: "new", existing: existing, editing: existing[0].id) == nil)
        #expect(SnippetMatcher.expandInlineCounting("send my address and my address", snippets: existing).count == 2)
    }

    @Test func wordsChangedCountsFillersAndCorrections() {
        #expect(HistoryEntry.wordsChanged(raw: "um so let's meet tuesday no actually wednesday", final: "Let's meet Wednesday.") == 5)
        #expect(HistoryEntry.wordsChanged(raw: "hello world", final: "Hello, world.") == 0)
    }

    @Test func insightsStats() {
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        func entry(_ daysAgo: Double, _ usage: UsageCategory, fixes: Int, dict: Int) -> HistoryEntry {
            var e = HistoryEntry(kind: .dictation, appName: "App", raw: "a b c", final: "a b c", audioSeconds: 2, latencyMs: 300, outcome: "inserted")
            e.date = now.addingTimeInterval(-daysAgo * 86_400)
            e.usage = usage
            e.wordsCorrected = fixes
            e.dictionaryFixes = dict
            return e
        }
        let s = UsageStats.compute([entry(0, .email, fixes: 2, dict: 1), entry(1, .email, fixes: 1, dict: 0),
                                    entry(2, .work, fixes: 0, dict: 0), entry(10, .personal, fixes: 0, dict: 3),
                                    entry(11, .personal, fixes: 0, dict: 0)], now: now)
        #expect(s.wordsCorrected == 3)
        #expect(s.dictionaryFixes == 4)
        #expect(s.dayStreak == 3)
        #expect(s.longestStreak == 3)
        #expect(Set(s.byCategory.prefix(2).map(\.0)) == [.email, .personal])   // 6 words each
        #expect(s.byCategory.last?.0 == .work)
        #expect(s.dailyWords.count == 5)
    }

    @Test func scratchpadShortcutIsOptionS() {
        let m = HotkeyEventMapper(hotkey: .fn)
        #expect(m.shortcut(keyCode: 1, flags: 0x8_0000) == .scratchpad)
        #expect(m.shortcut(keyCode: 1, flags: 0x8_0000 | 0x10_0000) == nil)   // ⌥⌘S is someone else's
        #expect(m.shortcut(keyCode: 1, flags: 0) == nil)
    }

    @Test func noteTitles() {
        var note = Note(body: "Call the bank about the home loan rates tomorrow morning")
        #expect(note.displayTitle == "Call the bank about the home")
        note.title = "Bank"
        #expect(note.displayTitle == "Bank")
        #expect(Note().displayTitle == "Untitled")
        #expect(Note().isBlank)
    }

    @Test func cleanupLevelNoneSkipsTheModel() async {
        let cleaner = TextCleaner(chat: GroqChat(apiKey: ""), model: "unused")
        let out = await cleaner.clean(CleanupRequest(transcript: "um so this stays exactly as said", style: .casual, level: .none))
        #expect(out.text == "um so this stays exactly as said")
        #expect(!out.usedAI)
    }

    @Test func promptCarriesCleanupLevel() {
        let msg = CleanupPrompt.userMessage(transcript: "x", style: "excited", app: nil, names: [], cleanup: "medium")
        #expect(msg.contains("<cleanup>medium</cleanup>"))
        #expect(!CleanupPrompt.userMessage(transcript: "x", style: "casual", app: nil, names: []).contains("<cleanup>"))
        #expect(CleanupPrompt.system.contains("- excited:"))
    }
}

struct TeluguTests {
    private func t(_ text: String, _ lang: String?, _ conf: Double?) -> DetailedTranscript {
        DetailedTranscript(text: text, language: lang, segments: [], confidence: conf)
    }

    @Test func parallelLanguageOnlyWithSeveralLanguages() {
        var prefs = Preferences()
        prefs.languages = ["en", "te"]
        #expect(prefs.whisperLanguage == nil)
        #expect(prefs.parallelLanguage == "te")
        prefs.languages = ["te"]
        #expect(prefs.whisperLanguage == "te")
        #expect(prefs.parallelLanguage == nil)
        #expect(Languages.code(forWhisperName: "Telugu") == "te")
    }

    /// Cases measured with Groq Whisper on 2026-10-17.
    @Test func resolverPicksTheRightTranscript() {
        let langs = ["en", "te"]
        // English speech: keep English (forced Telugu is gibberish and far less confident).
        let en = LanguageResolver.choose(auto: t("Can you check the invoice?", "en", -0.14),
                                         forced: t("క్ినిడాన్ిరాలు", "te", -0.70), forcedLanguage: "te", languages: langs)
        #expect(en.text.hasPrefix("Can you"))
        // Telugu heard as Hindi, more confident than Telugu: the script gives it away.
        let hi = LanguageResolver.choose(auto: t("नेनु रेपू ओफीस की वस्तानू", "hi", -0.079),
                                         forced: t("నేను రేపు ఆఫీస్ కి వస్తాను", "te", -0.106), forcedLanguage: "te", languages: langs)
        #expect(hi.language == "te")
        // Telugu heard as Tamil.
        let ta = LanguageResolver.choose(auto: t("நேனு ரேபோ மீட்டின்க்கி", "ta", -0.44),
                                         forced: t("నేను రేపు మీటింగ్ కి", "te", -0.11), forcedLanguage: "te", languages: langs)
        #expect(ta.language == "te")
        // Telugu heard as garbled English: Telugu is clearly more confident.
        let garbled = LanguageResolver.choose(auto: t("Neme Rebu meeting Ghe Atena Bono", "en", -0.72),
                                              forced: t("నేను రేపు మీటింగ్ కి అటెండ్ అవుతాను", "te", -0.20), forcedLanguage: "te", languages: langs)
        #expect(garbled.language == "te")
        // Close call stays with auto-detect.
        let close = LanguageResolver.choose(auto: t("Okay", "en", -0.30), forced: t("ఓకే", "te", -0.25), forcedLanguage: "te", languages: langs)
        #expect(close.language == "en")
        // No forced result (request failed): auto.
        #expect(LanguageResolver.choose(auto: t("Hi", "en", -0.1), forced: nil, forcedLanguage: "te", languages: langs).text == "Hi")
    }

    @Test func scriptDetection() {
        #expect(Script.dominant(in: "నేను office కి వస్తాను") == .telugu)
        #expect(Script.dominant(in: "Hello 123") == .latin)
        #expect(Script.dominant(in: "मीटिंग") == .devanagari)
        #expect(Script.dominant(in: "123 !!") == nil)
    }

    @Test func verboseJSONParsing() throws {
        let json = #"{"text":" నమస్తే ","language":"Telugu","segments":[{"start":0.0,"end":1.5,"text":" నమస్తే","avg_logprob":-0.2},{"start":1.5,"end":2.0,"text":"","avg_logprob":-0.6}]}"#
        let d = try GroqTranscriber.parseVerbose(Data(json.utf8))
        #expect(d.text == "నమస్తే")
        #expect(d.language == "te")
        #expect(d.segments.first == .init(start: 0, end: 1.5, text: "నమస్తే"))
        #expect(abs((d.confidence ?? 0) - (-0.3)) < 0.0001)     // (−0.2×1.5 + −0.6×0.5) / 2
    }

    @Test func promptCarriesTeluguMode() {
        let msg = CleanupPrompt.userMessage(transcript: "x", style: "casual", app: nil, names: [], telugu: "roman")
        #expect(msg.contains("<telugu>roman</telugu>"))
    }

    /// Live: English and Telugu selected, both requests in parallel. Runs only with GROQ_API_KEY.
    @Test(.enabled(if: LivePipelineTests.key != nil))
    func liveTeluguAndEnglishResolve() async throws {
        var prefs = Preferences()
        prefs.languages = ["en", "te"]
        let transcriber = GroqTranscriber(apiKey: LivePipelineTests.key!)
        for (clip, expectTelugu) in [("telugu-english", true), ("question", false), ("email", false), ("indian-english", false)] {
            let wav = try Data(contentsOf: LivePipelineTests.fixtures.appending(path: "\(clip).wav"))
            let result = try await transcriber.transcribeResolving(wav: wav, language: prefs.whisperLanguage,
                                                                   parallelLanguage: prefs.parallelLanguage,
                                                                   languages: prefs.languages, prompt: nil)
            print("\(clip): [\(result.language ?? "?")] \(result.text)")
            #expect((Script.dominant(in: result.text) == .telugu) == expectTelugu)
        }
    }
}
