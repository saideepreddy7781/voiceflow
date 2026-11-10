using System.Globalization;
using VoiceFlow.Core;
using VoiceFlow.Core.Library;
using VoiceFlow.Core.Meetings;

namespace VoiceFlow.Desktop.Services.Demo;

/// <summary>Realistic demo data for design work and screenshots. Nothing here touches the network or disk.</summary>
public sealed class DemoServices : IAppServices
{
    public DemoServices(DateTimeOffset? now = null, bool animated = true)
    {
        var clock = now ?? DateTimeOffset.Now;
        History = new DemoHistory(clock);
        Meetings = new DemoMeetings(clock, animated);
        Dictation = new DemoDictation();
    }

    public DemoSetup DemoSetup { get; } = new();
    public ISetupService Setup => DemoSetup;
    public IHistoryService History { get; }
    public IMeetingsService Meetings { get; }
    public IDictionaryService Dictionary { get; } = new DemoDictionary();
    public ISnippetsService Snippets { get; } = new DemoSnippets();
    public IScratchpadService Scratchpad { get; } = new DemoScratchpad();
    public IPreferencesService Preferences { get; } = new DemoPreferences();
    public IMicrophoneService Microphone { get; } = new DemoMicrophone();
    public IKeyService Keys { get; } = new DemoKeys();
    public IDictationService Dictation { get; }
    public IShellService Shell { get; } = new DemoShell();
    public IUpdateService Updates { get; } = new DemoUpdates();
    public IDiagnosticsService Diagnostics { get; } = new DemoDiagnostics();
}

public sealed class DemoSetup : ISetupService
{
    readonly List<SetupProblem> _problems = [];
    public IReadOnlyList<SetupProblem> Problems => _problems;
    public event EventHandler? Changed;

    public void Set(params SetupProblem[] problems)
    {
        _problems.Clear();
        _problems.AddRange(problems);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

sealed class DemoHistory : IHistoryService
{
    readonly List<HistoryEntry> _entries = [];
    public DemoHistory(DateTimeOffset now) => Build(now);
    public IReadOnlyList<HistoryEntry> Entries => _entries;
    public event EventHandler? Changed;

    public void Delete(Guid id) { _entries.RemoveAll(e => e.Id == id); Changed?.Invoke(this, EventArgs.Empty); }

    public void ToggleFlag(Guid id)
    {
        var e = _entries.First(x => x.Id == id);
        e.Flagged = e.Flagged != true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task PasteAsync(string text) => Task.CompletedTask;

    static readonly (string App, UsageCategory Usage, string Raw, string Final)[] Samples =
    [
        ("Slack", UsageCategory.Work, "um can you push the release notes to the channel before standup", "Can you push the release notes to the channel before standup?"),
        ("Outlook", UsageCategory.Email, "hi priya thanks for the proposal i'll review it by tuesday no actually wednesday", "Hi Priya,\n\nThanks for the proposal. I'll review it by Wednesday."),
        ("WhatsApp", UsageCategory.Personal, "nenu repu office ki vastanu meeting ten gantalaki undi", "నేను రేపు office కి వస్తాను, meeting 10 గంటలకి ఉంది."),
        ("Claude", UsageCategory.AiPrompts, "write a sql query that groups signups by week and shows the retention for each cohort", "Write a SQL query that groups signups by week and shows the retention for each cohort."),
        ("Notion", UsageCategory.Documents, "first finish the deck second call the bank third book train tickets", "1. Finish the deck.\n2. Call the bank.\n3. Book train tickets."),
        ("Word", UsageCategory.Documents, "the pilot ran for six weeks across two teams and reduced support tickets by a third", "The pilot ran for six weeks across two teams and reduced support tickets by a third."),
        ("Teams", UsageCategory.Work, "i'll send the updated figures after lunch", "I'll send the updated figures after lunch."),
        ("WhatsApp", UsageCategory.Personal, "are we still meeting at six let me know if the plan changed", "Are we still meeting at 6? Let me know if the plan changed"),
        ("Chrome", UsageCategory.Other, "best time to visit hampi in december", "Best time to visit Hampi in December"),
        ("Outlook", UsageCategory.Email, "hi team just a reminder that the quarterly numbers are due by friday please send your sheets to ananya by noon so we have time to reconcile them before the review if anything is blocked tell me today and we will sort it out", "Hi team,\n\nJust a reminder that the quarterly numbers are due by Friday. Please send your sheets to Ananya by noon so we have time to reconcile them before the review. If anything is blocked, tell me today and we'll sort it out."),
        ("Claude", UsageCategory.AiPrompts, "review this function and tell me where it can throw when the audio device disappears mid recording and how you would make it recover without dropping the first few seconds of speech", "Review this function and tell me where it can throw when the audio device disappears mid-recording, and how you would make it recover without dropping the first few seconds of speech."),
    ];

    void Build(DateTimeOffset now)
    {
        var rng = new Random(7);
        var today = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, now.Offset);
        // Enough history to fill the heatmap with a believable pattern.
        for (int day = 0; day < 250; day++)
        {
            if (day % 7 == 5 && rng.NextDouble() < 0.75) continue;
            if (day > 4 && rng.NextDouble() < (day > 90 ? 0.5 : 0.25)) continue;
            int count = day == 0 ? 8 : day % 13 == 3 ? rng.Next(25, 40) : rng.Next(2, 14);
            int minute = 60 * 18 + rng.Next(-30, 40);
            for (int i = 0; i < count; i++)
            {
                var s = Samples[(day * 3 + i * 2) % Samples.Length];
                minute -= rng.Next(20, 95);
                _entries.Add(new HistoryEntry(HistoryKind.Dictation, s.App, s.Raw, s.Final,
                    Math.Max(2, s.Final.Split(' ').Length / 2.4), rng.Next(420, 1400), "inserted")
                {
                    Date = today.AddDays(-day).AddMinutes(Math.Max(5, minute)),
                    Usage = s.Usage,
                    WordsCorrected = HistoryEntry.WordsChanged(s.Raw, s.Final),
                    DictionaryFixes = i % 4 == 0 ? 1 : 0,
                });
            }
        }
        // A command, a failure and a flagged entry so every row state shows up.
        _entries[1] = new HistoryEntry(HistoryKind.Command, "Word", "make this more formal", "Please find the updated proposal attached for your review.", 3.1, 1320, "inserted")
        { Date = _entries[1].Date, Usage = UsageCategory.Documents, WordsCorrected = 4, DictionaryFixes = 0 };
        _entries[3].Flagged = true;
        _entries[4].Outcome = "failed";
        _entries.Sort((a, b) => b.Date.CompareTo(a.Date));
    }
}

sealed class DemoMeetings : IMeetingsService
{
    readonly List<Meeting> _meetings = [];
    readonly System.Threading.Timer? _timer;
    readonly bool _animated;
    double _phase;

    public DemoMeetings(DateTimeOffset now, bool animated)
    {
        _animated = animated;
        var start = now.AddHours(-3);
        var m = new Meeting("Weekly product sync", start, "Teams")
        {
            End = start.AddMinutes(34),
            Status = MeetingStatus.Done,
            Attendees = ["Priya", "Rahul", "Ananya"],
            Notes = new MeetingNotes
            {
                Title = "Weekly product sync",
                Summary =
                [
                    "The Windows beta is ready for testers once the crash on resume is fixed.",
                    "Support tickets fell by a third after the pilot, mostly on onboarding.",
                    "Pricing stays free through the first release.",
                ],
                Decisions =
                [
                    "Ship the beta on Friday if the resume crash is closed by Thursday.",
                    "Keep the free tier as the only plan for now.",
                ],
                ActionItems =
                [
                    new ActionItem("Fix the crash when resuming from sleep", "Rahul", "Thu"),
                    new ActionItem("Write the tester invite email", "You", "Wed") { Done = true },
                    new ActionItem("Collect ten testers from the support list", "Ananya"),
                ],
                Questions = ["Do we need a separate build for ARM laptops?", "Who owns the Telugu glossary after launch?"],
            },
            Lines =
            [
                new(4, Speaker.You, "Okay, let's start with the beta. Where are we on the resume crash?"),
                new(11, Speaker.Others, "I can reproduce it on two laptops. It happens when audio devices come back after sleep."),
                new(23, Speaker.You, "Can we close it by Thursday? Then Friday is still possible."),
                new(30, Speaker.Others, "Yes, Thursday works if I drop the settings cleanup."),
                new(44, Speaker.You, "Good. Ananya, can you get ten testers from the support list?"),
                new(52, Speaker.Others, "Sure, I'll have names by Wednesday."),
            ],
        };
        _meetings.Add(m);
        _meetings.Add(new Meeting("Design review: onboarding", now.AddDays(-1).AddHours(-2), "Zoom")
        { End = now.AddDays(-1).AddHours(-1.6), Status = MeetingStatus.Done, Attendees = ["Meera"],
          Notes = new MeetingNotes { Summary = ["Agreed to cut the setup flow to six steps."], Decisions = ["Drop the sample email from the practice step."] },
          Lines = [new(6, Speaker.Others, "The practice step is where people drop off."), new(14, Speaker.You, "Let's cut the sample email and keep notes only.")] });
        _meetings.Add(new Meeting("Investor update call", now.AddDays(-2).AddHours(1), "Google Meet")
        { End = now.AddDays(-2).AddHours(1.7), Status = MeetingStatus.Done, Attendees = ["Karthik", "Nisha"],
          Notes = new MeetingNotes { Summary = ["Runway is fourteen months."], ActionItems = [new ActionItem("Send the updated model", "You")] },
          Lines = [new(3, Speaker.You, "Quick update on runway first.")] });
        _meetings.Add(new Meeting("1:1 with Rahul", now.AddDays(-4).AddHours(2), "Teams")
        { End = now.AddDays(-4).AddHours(2.4), Status = MeetingStatus.Failed, Error = "Notes could not be written. Groq rate limit reached.",
          Lines = [new(5, Speaker.Others, "I wanted to talk about next quarter.")] });
        LiveTemplate = new Meeting("Client onboarding call", now.AddMinutes(-12), "Zoom") { Attendees = ["Sowmya"] };
        LiveTemplate.Lines.AddRange([
            new(41, Speaker.You, "Thanks for joining. Can you see the dashboard on your side?"),
            new(49, Speaker.Others, "Yes, it loaded. The export button is what I wanted to ask about."),
            new(58, Speaker.You, "That lives under Reports now. I'll send you a short guide after this."),
            new(66, Speaker.Others, "Perfect. We also need the Telugu invoice template enabled."),
        ]);
        if (animated) _timer = new System.Threading.Timer(_ => Tick(), null, 500, 50);
    }

    public Meeting LiveTemplate { get; }
    public IReadOnlyList<Meeting> Meetings => _meetings;
    public Meeting? Live { get; private set; }
    public TimeSpan LiveElapsed => _animated ? DateTimeOffset.Now - LiveTemplate.Start : TimeSpan.FromSeconds(767);
    public NotetakerState State { get; private set; } = NotetakerState.Idle;
    public double MicLevel { get; private set; }
    public double SystemLevel { get; private set; }
    public MicHealth MicHealth => MicLevel > 0.02 || Live == null ? MicHealth.Good : MicHealth.Quiet;
    public string? NotesFolder => "D:\\Notes\\Meetings";
    public event EventHandler? Changed;
    public event EventHandler? LevelsChanged;

    /// <summary>Puts the service in the recording state with demo levels.</summary>
    public void BeginDemoRecording(double mic = 0.72, double system = 0.38)
    {
        Live = LiveTemplate;
        State = NotetakerState.Recording;
        MicLevel = mic;
        SystemLevel = system;
        Changed?.Invoke(this, EventArgs.Empty);
        LevelsChanged?.Invoke(this, EventArgs.Empty);
    }

    void Tick()
    {
        if (Live == null) return;
        _phase += 0.28;
        MicLevel = 0.42 + 0.34 * Math.Sin(_phase) * Math.Sin(_phase * 0.37);
        SystemLevel = Math.Max(0, 0.32 + 0.3 * Math.Cos(_phase * 0.83));
        Avalonia.Threading.Dispatcher.UIThread.Post(() => LevelsChanged?.Invoke(this, EventArgs.Empty));
    }

    public Task StartAsync() { BeginDemoRecording(); return Task.CompletedTask; }

    public Task StopAsync()
    {
        if (Live is { } live)
        {
            live.End = DateTimeOffset.Now;
            live.Status = MeetingStatus.WritingNotes;
            _meetings.Insert(0, live.Copy());
        }
        Live = null;
        State = NotetakerState.Idle;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    Meeting Find(Guid id) => _meetings.First(m => m.Id == id);

    public void Rename(Guid id, string title) { Find(id).Title = title; Changed?.Invoke(this, EventArgs.Empty); }
    public void RenameSpeaker(Guid id, Speaker speaker, string name) { Find(id).SpeakerNames[speaker] = name; Changed?.Invoke(this, EventArgs.Empty); }

    public void ToggleActionItem(Guid id, Guid itemId)
    {
        var item = Find(id).Notes?.ActionItems.First(a => a.Id == itemId);
        if (item != null) item.Done = !item.Done;
    }

    public void Delete(Guid id) { _meetings.RemoveAll(m => m.Id == id); Changed?.Invoke(this, EventArgs.Empty); }
    public Task RewriteNotesAsync(Guid id) => Task.CompletedTask;
    public Task CopyMarkdownAsync(Guid id) => Task.CompletedTask;
    public Task ExportMarkdownAsync(Guid id) => Task.CompletedTask;
}

sealed class DemoDictionary : IDictionaryService
{
    readonly List<DictionaryEntry> _entries =
    [
        new("Saideep", "Sai deep"),
        new("Priya"),
        new("Hyderabad"),
        new("LearnX", "Learn X"),
        new("Groq", "Grok"),
        new("Kubernetes", null, DictionarySource.AutoLearned),
        new("Whisper", null, DictionarySource.AutoLearned),
        new("Ananya", "Anna ya", DictionarySource.AutoLearned),
    ];

    public IReadOnlyList<DictionaryEntry> Entries => _entries;
    public event EventHandler? Changed;

    public void Add(string term, string? misspelling) { _entries.Insert(0, new DictionaryEntry(term, misspelling)); Changed?.Invoke(this, EventArgs.Empty); }

    public void Update(Guid id, string term, string? misspelling)
    {
        var e = _entries.First(x => x.Id == id);
        e.Term = term;
        e.Misspelling = misspelling;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Delete(Guid id) { _entries.RemoveAll(e => e.Id == id); Changed?.Invoke(this, EventArgs.Empty); }
    public Task<int> ImportAsync() => Task.FromResult(0);
}

sealed class DemoSnippets : ISnippetsService
{
    readonly List<Snippet> _snippets =
    [
        new("my calendar link", "https://calendar.example.com/saideep/30min"),
        new("office address", "2nd floor, Plot 42, Road No. 7, Madhapur, Hyderabad 500081"),
        new("sign off", "Thanks,\nSaideep"),
        new("meeting reply", "Thanks for reaching out. I'm free Tuesday or Thursday after 3 pm, whichever suits you."),
    ];

    public IReadOnlyList<Snippet> Snippets => _snippets;
    public event EventHandler? Changed;

    public void Add(string trigger, string expansion) { _snippets.Insert(0, new Snippet(trigger, expansion)); Changed?.Invoke(this, EventArgs.Empty); }

    public void Update(Guid id, string trigger, string expansion)
    {
        var s = _snippets.First(x => x.Id == id);
        s.Trigger = trigger;
        s.Expansion = expansion;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Delete(Guid id) { _snippets.RemoveAll(s => s.Id == id); Changed?.Invoke(this, EventArgs.Empty); }
    public Task<int> ImportAsync() => Task.FromResult(0);
}

sealed class DemoScratchpad : IScratchpadService
{
    readonly List<Note> _notes;

    public DemoScratchpad()
    {
        var now = DateTimeOffset.Now;
        _notes =
        [
            new Note("Talking points for Friday:\n\nBeta goes out once the resume crash is closed.\nAsk Ananya for the tester list.\nConfirm the Telugu glossary owner.") { Title = "Friday talking points", Pinned = true, Updated = now.AddHours(-2) },
            new Note("Book train tickets to Warangal for the 22nd. Check whether the morning Shatabdi still runs.") { Title = "Trip", Updated = now.AddDays(-1) },
            new Note("Idea: show the streak on the Home page only after three days in a row.") { Updated = now.AddDays(-3) },
        ];
    }

    public IReadOnlyList<Note> Notes => _notes.OrderByDescending(n => n.Pinned).ThenByDescending(n => n.Updated).ToList();
    public event EventHandler? Changed;

    public Note Create()
    {
        var n = new Note();
        _notes.Insert(0, n);
        Changed?.Invoke(this, EventArgs.Empty);
        return n;
    }

    public void Update(Note note) { note.Updated = DateTimeOffset.Now; Changed?.Invoke(this, EventArgs.Empty); }
    public void Delete(Guid id) { _notes.RemoveAll(n => n.Id == id); Changed?.Invoke(this, EventArgs.Empty); }
}

sealed class DemoPreferences : IPreferencesService
{
    public Preferences Current { get; } = new()
    {
        Languages = ["en", "te", "hi"],
        TeluguScript = TeluguScript.Mixed,
        MeetingsFolder = "D:\\Notes\\Meetings",
    };

    public event EventHandler? Changed;
    public void Update(Action<Preferences> change) { change(Current); Changed?.Invoke(this, EventArgs.Empty); }
}

sealed class DemoMicrophone : IMicrophoneService
{
    public IReadOnlyList<MicrophoneDevice> Devices { get; } =
    [
        new("", "System default"),
        new("usb", "Blue Yeti USB Microphone"),
        new("headset", "Sony WH-1000XM5"),
        new("internal", "Realtek Audio (built-in)"),
    ];

    public bool IsTesting { get; private set; }
    public double TestLevel { get; private set; }
    public event EventHandler? TestLevelChanged;

    public void StartTest(string deviceId)
    {
        IsTesting = true;
        TestLevel = 0.62;
        TestLevelChanged?.Invoke(this, EventArgs.Empty);
    }

    public void StopTest()
    {
        IsTesting = false;
        TestLevel = 0;
        TestLevelChanged?.Invoke(this, EventArgs.Empty);
    }
}

sealed class DemoKeys : IKeyService
{
    readonly HashSet<SecretKey> _stored = [SecretKey.Groq];
    public bool IsStored(SecretKey key) => _stored.Contains(key);
    public event EventHandler? Changed;
    public void Save(SecretKey key, string value) { _stored.Add(key); Changed?.Invoke(this, EventArgs.Empty); }
    public void Remove(SecretKey key) { _stored.Remove(key); Changed?.Invoke(this, EventArgs.Empty); }

    public async Task<KeyCheckResult> TestAsync(SecretKey key, string? draft)
    {
        await Task.Delay(200);
        return new KeyCheckResult(KeyCheckStatus.Ok, "20 models available");
    }
}

public sealed class DemoDictation : IDictationService
{
    public FlowPhase Phase { get; private set; } = FlowPhase.Idle;
    public IReadOnlyList<float> Levels { get; private set; } = [];
    public FlowToast? Toast { get; private set; }
    public DateTimeOffset? MeetingSince { get; private set; }
    public event EventHandler? Changed;
    public event EventHandler? LevelsChanged;

    public void Set(FlowPhase phase, IReadOnlyList<float>? levels = null, FlowToast? toast = null, DateTimeOffset? meetingSince = null)
    {
        Phase = phase;
        Levels = levels ?? [];
        Toast = toast;
        MeetingSince = meetingSince;
        Changed?.Invoke(this, EventArgs.Empty);
        LevelsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Cancel() => Set(FlowPhase.Idle);
    public void Confirm() => Set(FlowPhase.Processing, Levels);
    public void StopMeeting() => Set(FlowPhase.Idle);
    public void DismissToast() => Set(Phase, Levels);
}

sealed class DemoShell : IShellService
{
    public void OpenUrl(string url) { }
    public void OpenDataFolder() { }
    public Task CopyToClipboardAsync(string text) => Task.CompletedTask;
    public Task<string?> PickFolderAsync() => Task.FromResult<string?>("D:\\Notes\\Meetings");
    public Task<string?> PickSaveFileAsync(string suggestedName, string extension) => Task.FromResult<string?>(null);
}

sealed class DemoUpdates : IUpdateService
{
    public string CurrentVersion => "0.1.0";
    public UpdateState State { get; private set; } = UpdateState.UpToDate;
    public string? AvailableVersion => null;
    public event EventHandler? Changed;

    public async Task CheckAsync()
    {
        State = UpdateState.Checking;
        Changed?.Invoke(this, EventArgs.Empty);
        await Task.Delay(400);
        State = UpdateState.UpToDate;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task InstallAsync() => Task.CompletedTask;
}

sealed class DemoDiagnostics : IDiagnosticsService
{
    public string BuildReport() => string.Join('\n',
        "VoiceFlow debug report (no dictated text)",
        "version: 0.1.0",
        "os: " + System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        "culture: " + CultureInfo.CurrentCulture.Name);
}
