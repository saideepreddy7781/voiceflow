import Foundation
import VoiceFlowCore

/// Everything a screenshot needs, invented in memory. Nothing here comes from this Mac.
struct DemoContent {
    struct LiveInputs {
        var micLevel: Float
        var systemLevel: Float
        var micLanguage: String?
        var callLanguage: String?
        var micHealth: MicHealthState
    }

    let now: Date
    var preferences = Preferences()
    var history: [HistoryEntry] = []
    var dictionary: [DictionaryEntry] = []
    var snippets: [Snippet] = []
    var notes: [Note] = []
    var meetings: [Meeting] = []
    var liveMeeting: Meeting?
    var liveInputs: LiveInputs?
    var storedKeys: Set<SecretKey> = [.groq]
    var hasAccessibility = true
    var microphoneAllowed = true
    var hotkeyActive = true
    var inputDevices = [AudioInputDevice(uid: "demo-built-in", name: "MacBook Pro Microphone"),
                        AudioInputDevice(uid: "demo-usb", name: "USB Microphone")]
    var update: UpdateService.State = .upToDate

    static var demoNow: Date { Calendar.current.date(bySettingHour: 17, minute: 12, second: 0, of: Date()) ?? Date() }

    /// Normal use: a populated library, the latest version installed.
    @MainActor static func populated() -> DemoContent {
        if let cachedPopulated { return cachedPopulated }
        let content = makePopulated()
        cachedPopulated = content
        return content
    }

    @MainActor private static var cachedPopulated: DemoContent?

    private static func makePopulated() -> DemoContent {
        let now = demoNow
        var content = DemoContent(now: now)
        content.preferences.onboarded = true
        content.preferences.languages = ["en", "te", "hi"]
        content.preferences.meetingsFolder = "/Users/you/Documents/Notes/Meetings"
        content.history = history(now: now)
        content.dictionary = dictionarySamples
        content.snippets = snippetSamples
        // Notes show relative times, which the view measures against the real clock.
        content.notes = notes(now: Date())
        content.meetings = meetings(now: now)
        return content
    }

    /// A newer release is out.
    @MainActor static func updateAvailable() -> DemoContent {
        var content = populated()
        content.update = .available(ReleaseInfo(version: "0.2.0", url: URL(string: "https://github.com/saideepreddy7781/voiceflow/releases/tag/v0.2.0")!))
        return content
    }

    /// Setup not finished: Groq key missing, microphone blocked.
    @MainActor static func needsSetup() -> DemoContent {
        var content = populated()
        content.storedKeys = []
        content.microphoneAllowed = false
        return content
    }

    /// A meeting being recorded, with the mic healthy.
    @MainActor static func liveMeeting() -> DemoContent {
        var content = populated()
        content.liveMeeting = liveTemplate(now: content.now)
        content.liveInputs = LiveInputs(micLevel: 0.62, systemLevel: 0.34, micLanguage: "en", callLanguage: "te", micHealth: .healthy)
        return content
    }

    /// A meeting being recorded whose microphone has gone silent.
    @MainActor static func liveMeetingMicProblem() -> DemoContent {
        var content = liveMeeting()
        content.liveInputs = LiveInputs(micLevel: 0, systemLevel: 0.34, micLanguage: nil, callLanguage: "te", micHealth: .silent)
        return content
    }

    /// First run: nothing recorded yet.
    static func empty() -> DemoContent {
        var content = DemoContent(now: demoNow)
        content.preferences.onboarded = true
        return content
    }

    // MARK: Samples

    private static let samples: [(app: String, usage: UsageCategory, raw: String, final: String)] = [
        ("Slack", .work, "um can you push the release notes to the channel before standup", "Can you push the release notes to the channel before standup?"),
        ("Outlook", .email, "hi priya thanks for the proposal i'll review it by tuesday no actually wednesday", "Hi Priya,\n\nThanks for the proposal. I'll review it by Wednesday."),
        ("WhatsApp", .personal, "nenu repu office ki vastanu meeting ten gantalaki undi", "నేను రేపు office కి వస్తాను, meeting 10 గంటలకి ఉంది."),
        ("Claude", .aiPrompts, "write a sql query that groups signups by week and shows the retention for each cohort", "Write a SQL query that groups signups by week and shows the retention for each cohort."),
        ("Notion", .documents, "first finish the deck second call the bank third book train tickets", "1. Finish the deck.\n2. Call the bank.\n3. Book train tickets."),
        ("Pages", .documents, "the pilot ran for six weeks across two teams and reduced support tickets by a third", "The pilot ran for six weeks across two teams and reduced support tickets by a third."),
        ("Slack", .work, "i'll send the updated figures after lunch", "I'll send the updated figures after lunch."),
        ("WhatsApp", .personal, "are we still meeting at six let me know if the plan changed", "Are we still meeting at 6? Let me know if the plan changed"),
        ("Safari", .other, "best time to visit hampi in december", "Best time to visit Hampi in December"),
        ("Mail", .email, "hi team just a reminder that the quarterly numbers are due by friday please send your sheets to ananya by noon so we have time to reconcile them before the review if anything is blocked tell me today and we will sort it out", "Hi team,\n\nJust a reminder that the quarterly numbers are due by Friday. Please send your sheets to Ananya by noon so we have time to reconcile them before the review. If anything is blocked, tell me today and we'll sort it out."),
        ("Claude", .aiPrompts, "review this function and tell me where it can throw when the audio device disappears mid recording and how you would make it recover without dropping the first few seconds of speech", "Review this function and tell me where it can throw when the audio device disappears mid-recording, and how you would make it recover without dropping the first few seconds of speech."),
    ]

    /// The same generator on every run.
    private struct SeededGenerator {
        var state: UInt64
        mutating func next(_ range: Int) -> Int {
            state = state &* 6_364_136_223_846_793_005 &+ 1_442_695_040_888_963_407
            return Int((state >> 33) % UInt64(max(range, 1)))
        }
        mutating func unit() -> Double { Double(next(10_000)) / 10_000 }
    }

    private static func entry(_ sample: (app: String, usage: UsageCategory, raw: String, final: String), date: Date, gen: inout SeededGenerator, index: Int) -> HistoryEntry {
        var e = HistoryEntry(kind: .dictation, appName: sample.app, raw: sample.raw, final: sample.final,
                             audioSeconds: max(2, Double(sample.final.split(separator: " ").count) / 2.4),
                             latencyMs: 420 + gen.next(980), outcome: "inserted")
        e.date = date
        e.usage = sample.usage
        e.wordsCorrected = HistoryEntry.wordsChanged(raw: sample.raw, final: sample.final)
        e.dictionaryFixes = index % 4 == 0 ? 1 : 0
        return e
    }

    private static func history(now: Date) -> [HistoryEntry] {
        var gen = SeededGenerator(state: 7)
        let calendar = Calendar.current
        let today = calendar.startOfDay(for: now)
        var out: [HistoryEntry] = []
        for day in 0..<250 {
            if day % 7 == 5 && gen.unit() < 0.75 { continue }
            if day > 4 && gen.unit() < (day > 90 ? 0.5 : 0.25) { continue }
            let count = day == 0 ? 8 : (day % 13 == 3 ? 25 + gen.next(15) : 2 + gen.next(12))
            var minute = 60 * 18 + gen.next(70) - 30
            for i in 0..<count {
                let sample = samples[(day * 3 + i * 2) % samples.count]
                minute -= 20 + gen.next(75)
                let date = calendar.date(byAdding: .minute, value: max(5, minute), to: calendar.date(byAdding: .day, value: -day, to: today)!)!
                out.append(entry(sample, date: date, gen: &gen, index: i))
            }
        }
        out.sort { $0.date > $1.date }
        // One of each row state on Home.
        var command = HistoryEntry(kind: .command, appName: "Pages", raw: "make this more formal", final: "Please find the updated proposal attached for your review.",
                                   audioSeconds: 3.1, latencyMs: 1_320, outcome: "inserted")
        command.date = out[1].date
        command.usage = .documents
        command.wordsCorrected = 4
        out[1] = command
        out[3].flagged = true
        out[4].outcome = "failed"
        return out
    }

    private static let dictionarySamples: [DictionaryEntry] = [
        DictionaryEntry(term: "Ananya", source: .manual),
        DictionaryEntry(term: "Hampi", source: .autoLearned),
        DictionaryEntry(term: "Kubernetes", misspelling: "cooper netties", source: .manual),
        DictionaryEntry(term: "Groq", misspelling: "grok", source: .manual),
        DictionaryEntry(term: "Telangana", source: .manual),
        DictionaryEntry(term: "VoiceFlow", source: .manual),
        DictionaryEntry(term: "Nginx", misspelling: "engine x", source: .autoLearned),
    ]

    private static let snippetSamples: [Snippet] = [
        Snippet(trigger: "my calendar link", expansion: "https://cal.example.com/priya/30min"),
        Snippet(trigger: "my address", expansion: "Flat 402, Lake View Residency, Madhapur, Hyderabad 500081"),
        Snippet(trigger: "thanks reply", expansion: "Thanks for getting back to me. I'll take a look at this today and send you an update by the end of the day."),
        Snippet(trigger: "intro blurb", expansion: "I help small teams ship faster. Over the last few years I've built internal tools for support, finance and sales."),
    ]

    private static func notes(now: Date) -> [Note] {
        var friday = Note(body: "Talking points for Friday:\n\nBeta goes out once the resume crash is closed.\nAsk Ananya for the tester list.\nConfirm the Telugu glossary owner.")
        friday.title = "Friday talking points"
        friday.pinned = true
        friday.updated = now.addingTimeInterval(-2 * 3_600)
        var trip = Note(body: "Book trains for the Hampi trip, check the December weather, and ask Rahul about the homestay.")
        trip.title = "Trip"
        trip.updated = now.addingTimeInterval(-26 * 3_600)
        var idea = Note(body: "Idea: show the streak on the home page once it passes a week, and not before.")
        idea.updated = now.addingTimeInterval(-3 * 86_400)
        return [friday, trip, idea]
    }

    private static func meetings(now: Date) -> [Meeting] {
        let start = now.addingTimeInterval(-3 * 3_600)
        var sync = Meeting(title: "Weekly product sync", start: start, app: "Teams")
        sync.end = start.addingTimeInterval(34 * 60)
        sync.status = .done
        sync.attendees = ["Priya", "Rahul", "Ananya"]
        var writeEmail = ActionItem(task: "Write the tester invite email", owner: "You", due: "Wed")
        writeEmail.done = true
        sync.notes = MeetingNotes(
            title: "Weekly product sync",
            summary: ["The Windows beta is ready for testers once the crash on resume is fixed.",
                      "Support tickets fell by a third after the pilot, mostly on onboarding.",
                      "Pricing stays free through the first release."],
            decisions: ["Ship the beta on Friday if the resume crash is closed by Thursday.",
                        "Keep the free tier as the only plan for now."],
            actionItems: [ActionItem(task: "Fix the crash when resuming from sleep", owner: "Rahul", due: "Thu"),
                          writeEmail,
                          ActionItem(task: "Collect ten testers from the support list", owner: "Ananya")],
            questions: ["Do we need a separate build for Intel Macs?", "Who owns the Telugu glossary after launch?"])
        sync.lines = [
            TranscriptLine(time: 4, speaker: .you, text: "Okay, let's start with the beta. Where are we on the resume crash?", language: "en"),
            TranscriptLine(time: 11, speaker: .others, text: "I can reproduce it on two laptops. It happens when audio devices come back after sleep.", language: "en"),
            TranscriptLine(time: 23, speaker: .you, text: "Can we close it by Thursday? Then Friday is still possible.", language: "en"),
            TranscriptLine(time: 30, speaker: .others, text: "Yes, Thursday works if I drop the settings cleanup.", language: "en"),
            TranscriptLine(time: 44, speaker: .you, text: "Good. Ananya, can you get ten testers from the support list?", language: "en"),
            TranscriptLine(time: 52, speaker: .others, text: "Sure, I'll have names by Wednesday.", language: "en"),
        ]

        var design = Meeting(title: "Design review: onboarding", start: now.addingTimeInterval(-26 * 3_600), app: "Zoom")
        design.end = design.start.addingTimeInterval(24 * 60)
        design.status = .done
        design.attendees = ["Meera"]
        design.notes = MeetingNotes(summary: ["Agreed to cut the setup flow to six steps."], decisions: ["Drop the sample email from the practice step."])
        design.lines = [TranscriptLine(time: 6, speaker: .others, text: "The practice step is where people drop off."),
                        TranscriptLine(time: 14, speaker: .you, text: "Let's cut the sample email and keep notes only.")]

        var investor = Meeting(title: "Investor update call", start: now.addingTimeInterval(-47 * 3_600), app: "Google Meet")
        investor.end = investor.start.addingTimeInterval(42 * 60)
        investor.status = .done
        investor.attendees = ["Karthik", "Nisha"]
        investor.notes = MeetingNotes(summary: ["Runway is fourteen months."], actionItems: [ActionItem(task: "Send the updated model", owner: "You")])
        investor.lines = [TranscriptLine(time: 3, speaker: .you, text: "Quick update on runway first.")]

        var oneOnOne = Meeting(title: "1:1 with Rahul", start: now.addingTimeInterval(-94 * 3_600), app: "Teams")
        oneOnOne.end = oneOnOne.start.addingTimeInterval(24 * 60)
        oneOnOne.status = .failed
        oneOnOne.error = "Notes could not be written. Groq's rate limit was reached."
        oneOnOne.lines = [TranscriptLine(time: 5, speaker: .others, text: "I wanted to talk about next quarter.")]

        return [sync, design, investor, oneOnOne]
    }

    private static func liveTemplate(now: Date) -> Meeting {
        var live = Meeting(title: "Client onboarding call", start: now.addingTimeInterval(-767), app: "Zoom")
        live.attendees = ["Sowmya"]
        live.lines = [
            TranscriptLine(time: 41, speaker: .you, text: "Thanks for joining. Can you see the dashboard on your side?", language: "en"),
            TranscriptLine(time: 49, speaker: .others, text: "Yes, it loaded. The export button is what I wanted to ask about.", language: "en"),
            TranscriptLine(time: 58, speaker: .you, text: "That lives under Reports now. I'll send you a short guide after this.", language: "en"),
            TranscriptLine(time: 66, speaker: .others, text: "Perfect. We also need the Telugu invoice template enabled.", language: "en"),
        ]
        return live
    }
}
