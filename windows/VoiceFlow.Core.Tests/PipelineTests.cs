using VoiceFlow.Core.AI;
using VoiceFlow.Core.Audio;
using VoiceFlow.Core.Library;
using VoiceFlow.Core.Speech;
using Xunit;

namespace VoiceFlow.Core.Tests;

public class AudioMathTests
{
    internal static float[] Tone(double seconds, float amplitude = 0.3f) =>
        Enumerable.Range(0, (int)(seconds * 16_000)).Select(i => amplitude * MathF.Sin(i * 2 * MathF.PI * 220 / 16_000)).ToArray();

    [Fact]
    public void WavHeaderIsMono16kHz16Bit()
    {
        var wav = AudioMath.Wav([0, 0.5f, -0.5f, 1]);
        Assert.Equal(44 + 8, wav.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVEfmt ", System.Text.Encoding.ASCII.GetString(wav, 8, 8));
        int U16(int o) => wav[o] | wav[o + 1] << 8;
        long U32(int o) => BitConverter.ToUInt32(wav, o);
        Assert.Equal(1, U16(20));          // PCM
        Assert.Equal(1, U16(22));          // mono
        Assert.Equal(16_000, U32(24));     // sample rate
        Assert.Equal(16, U16(34));         // bits
        Assert.Equal(8, U32(40));          // data bytes
        Assert.Equal(new[] { 0f, 0.5f, -0.5f, 1f }, AudioMath.ReadWav16(wav).Select(v => MathF.Round(v, 3)));
    }

    [Fact]
    public void SilenceIsDetected()
    {
        Assert.True(AudioMath.IsSilent(new float[48_000]));
        Assert.True(AudioMath.IsSilent(Enumerable.Repeat(0.001f, 48_000).ToArray()));   // room hum, -60 dB
        Assert.False(AudioMath.IsSilent(Tone(1)));
    }

    [Fact]
    public void WhisperModeBoostsQuietSpeechButNotSilenceOrLoudSpeech()
    {
        var boosted = AudioMath.Normalized(Tone(0.5, 0.05f));
        Assert.True(boosted.Max(Math.Abs) >= 0.59f);    // 0.05 × 12 (gain cap)
        var loud = Tone(0.5, 0.8f);
        Assert.Same(loud, AudioMath.Normalized(loud));
        Assert.Equal(new float[3], AudioMath.Normalized(new float[3]));
        Assert.True(AudioMath.Normalized(Tone(0.5, 0.001f)).Max(Math.Abs) < 0.02f);
    }

    [Fact]
    public void HallucinationsOnSilenceAreDropped()
    {
        Assert.True(HallucinationFilter.IsLikelyHallucination("Thank you.", 0.3));
        Assert.False(HallucinationFilter.IsLikelyHallucination("Thank you.", 2));
        Assert.False(HallucinationFilter.IsLikelyHallucination("Send the report", 0.5));
        Assert.True(HallucinationFilter.IsLikelyHallucination("  ", 3));
    }

    [Fact]
    public void PcmFormatsDecode()
    {
        Assert.Equal(new[] { 0.5f, -1f }, PcmDecoder.ToFloat(new byte[] { 0x00, 0x40, 0x00, 0x80 }, 16, false));
        Assert.Equal(new[] { -1f }, PcmDecoder.ToFloat(new byte[] { 0x00, 0x00, 0x80 }, 24, false));
        var floats = BitConverter.GetBytes(0.25f).Concat(BitConverter.GetBytes(-0.75f)).ToArray();
        Assert.Equal(new[] { 0.25f, -0.75f }, PcmDecoder.ToFloat(floats, 32, true));
    }

    [Fact]
    public void ResamplerTurns48kStereoInto16kMonoWithoutLosingTheTone()
    {
        var r = new StreamingResampler(48_000, 2);
        var output = new List<float>();
        // 1 s of a 440 Hz tone in 10 ms stereo buffers, as WASAPI delivers it.
        for (int block = 0; block < 100; block++)
        {
            var buf = new float[480 * 2];
            for (int i = 0; i < 480; i++)
            {
                var v = 0.5f * MathF.Sin((block * 480 + i) * 2 * MathF.PI * 440 / 48_000);
                buf[i * 2] = v;
                buf[i * 2 + 1] = v;
            }
            output.AddRange(r.Process(buf));
        }
        Assert.InRange(output.Count, 15_950, 16_000);
        var rms = AudioMath.Rms(output.Skip(100).ToArray());
        Assert.InRange(rms, 0.33f, 0.38f);          // 0.5 / √2 ≈ 0.354
    }

    [Fact]
    public void ResamplerRemovesContentAboveEightKilohertz()
    {
        var r = new StreamingResampler(48_000, 1);
        var tone = Enumerable.Range(0, 48_000).Select(i => 0.5f * MathF.Sin(i * 2 * MathF.PI * 12_000 / 48_000)).ToArray();
        var output = r.Process(tone);
        Assert.True(AudioMath.Rms(output.Skip(100).ToArray()) < 0.02f);   // would alias to 4 kHz otherwise
    }

    [Fact]
    public void ResamplerPassesSixteenKilohertzMonoThrough()
    {
        var r = new StreamingResampler(16_000, 1);
        Assert.Equal(new[] { 0.1f, 0.2f }, r.Process(new[] { 0.1f, 0.2f }));
        var stereo = new StreamingResampler(16_000, 2);
        Assert.Equal(new[] { 0.5f }, stereo.Process(new[] { 0.4f, 0.6f }));
    }
}

public class SegmentCutterTests
{
    static float[] Speech(double seconds) => Enumerable.Range(0, (int)(seconds * 16_000)).Select(i => 0.3f * MathF.Sin(i * 0.17f)).ToArray();
    static float[] Pause(double seconds) => new float[(int)(seconds * 16_000)];
    static float[] Join(params float[][] parts) => parts.SelectMany(p => p).ToArray();

    [Fact]
    public void NoCutBeforeTenSeconds() =>
        Assert.Null(new SegmentCutter().NextCut(Join(Speech(4), Pause(1), Speech(4))));

    [Fact]
    public void CutsAtFirstPauseAfterTenSeconds()
    {
        var c = new SegmentCutter();
        var cut = c.NextCut(Join(Speech(6), Pause(1), Speech(6), Pause(0.6), Speech(3)));
        Assert.NotNull(cut);
        var seconds = cut!.Value.End.Value / 16_000.0;
        Assert.InRange(seconds, 13.0001, 13.5999);           // inside the second pause, not the first
        Assert.Equal(cut.Value.End.Value - 3_200, c.OpenStart);   // 200 ms overlap
    }

    [Fact]
    public void ForcesCutAtThirtySeconds()
    {
        var cut = new SegmentCutter().NextCut(Speech(31));
        Assert.Equal(0..(30 * 16_000), cut);
    }

    [Fact]
    public void TailCutsMatchWholeRecordingCuts()
    {
        var audio = Join(Speech(6), Pause(1), Speech(6), Pause(0.6), Speech(12), Pause(0.5), Speech(20), Pause(1), Speech(35));
        var whole = new SegmentCutter();
        var tailed = new SegmentCutter();
        for (int end = 16_000; end <= audio.Length; end += 16_000)
        {
            var expected = whole.NextCut(audio.AsSpan(0, end));
            var start = tailed.OpenStart;
            var actual = tailed.NextCut(audio.AsSpan(start, end - start), start);
            Assert.Equal(expected, actual);
            Assert.Equal(whole.OpenStart, tailed.OpenStart);
        }
        Assert.True(whole.OpenStart > 30 * 16_000);   // several cuts happened
    }

    [Fact]
    public void FinishReturnsTheRest()
    {
        var c = new SegmentCutter();
        Assert.Equal(0..1_000, c.Finish(1_000));
        Assert.Null(c.Finish(1_000));
    }

    [Fact]
    public void JoinDropsWordRepeatedAcrossOverlap()
    {
        Assert.Equal("We should ship on Friday.", SegmentCutter.Join(["We should ship on", "on Friday."]));
        Assert.Equal("Hello there. General Kenobi.", SegmentCutter.Join(["Hello there.", "", "General Kenobi."]));
    }
}

public class ContextTests
{
    [Fact]
    public void NamesFromScreenText()
    {
        var names = NameExtractor.Extract(["Hi Priya, Ramesh shared the LearnX deck. The deck from Ramesh is in @saideep's folder. Thanks"]);
        Assert.Equal("Ramesh", names[0]);
        Assert.Contains("Priya", names);
        Assert.Contains("LearnX", names);
        Assert.True(names.Contains("@saideep's") || names.Contains("@saideep"));
        Assert.DoesNotContain("The", names);
        Assert.DoesNotContain("Thanks", names);
    }

    [Fact]
    public void WhisperPromptFitsTheLimitAndDedupes()
    {
        Assert.Equal("Saideep, LearnX, Priya", NameExtractor.WhisperPrompt(["Saideep", "LearnX"], ["saideep", "Priya"]));
        var longPrompt = NameExtractor.WhisperPrompt(Enumerable.Range(0, 200).Select(i => $"Term{i}"), [])!;
        Assert.True(longPrompt.Length <= 650);
        Assert.Null(NameExtractor.WhisperPrompt([], []));
    }

    [Fact]
    public void AppClassificationByWindowsProcess()
    {
        Assert.Equal(UsageCategory.Email, AppClassifier.Usage("OUTLOOK.EXE", ""));
        Assert.Equal(UsageCategory.Email, AppClassifier.Usage("olk.exe", ""));
        Assert.Equal(UsageCategory.Work, AppClassifier.Usage("slack.exe", ""));
        Assert.Equal(UsageCategory.Work, AppClassifier.Usage("ms-teams.exe", ""));
        Assert.Equal(UsageCategory.Personal, AppClassifier.Usage("WhatsApp.exe", ""));
        Assert.Equal(UsageCategory.Email, AppClassifier.Usage("chrome.exe", "Inbox (3) - me@gmail.com - Gmail - Google Chrome"));
        Assert.Equal(UsageCategory.Personal, AppClassifier.Usage("msedge.exe", "WhatsApp"));
        Assert.Equal(UsageCategory.AiPrompts, AppClassifier.Usage("firefox.exe", "ChatGPT"));
        Assert.Equal(UsageCategory.Other, AppClassifier.Usage("unknown.exe", "Gmail"));
        Assert.Equal(StyleCategory.Other, UsageCategory.AiPrompts.ToStyleCategory());
        Assert.True(AppClassifier.IsCode("WindowsTerminal.exe"));
        Assert.True(AppClassifier.IsCode("idea64.exe"));
        Assert.True(AppClassifier.IsCode(@"C:\Program Files\Microsoft VS Code\Code.exe"));
        Assert.Equal(WritingStyle.Code, AppClassifier.Style("code.exe", "", new Preferences()));
        Assert.Equal(WritingStyle.Formal, AppClassifier.Style("outlook", "", new Preferences()));
        Assert.True(AppClassifier.TagsFiles("Cursor.exe"));
        Assert.False(AppClassifier.TagsFiles("code.exe"));
    }
}

public class LibraryTests
{
    [Fact]
    public void SnippetExactAndInline()
    {
        var snippets = new[] { new Snippet("my address", "12 Park Road, Hyderabad"), new Snippet("calendar link", "https://cal.com/saideep") };
        Assert.Equal("12 Park Road, Hyderabad", SnippetMatcher.ExactMatch("My address.", snippets)?.Expansion);
        Assert.Null(SnippetMatcher.ExactMatch("send my address", snippets));
        Assert.Equal("Book a slot here: https://cal.com/saideep.", SnippetMatcher.ExpandInline("Book a slot here: Calendar link.", snippets));
        Assert.Equal("Price is $5 at $1 Main St", SnippetMatcher.ExpandInline("Price is $5 at my address", [new Snippet("my address", "$1 Main St")]));
    }

    [Fact]
    public void AutoLearnPicksUpCorrectedNames()
    {
        var learned = AutoLearner.LearnedTerms("Send it to Sai Deep and pria today.", "Hello team,",
                                               "Hello team, Send it to Saideep and Priya today.", new HashSet<string>());
        Assert.Contains("Priya", learned);
        Assert.Contains("Saideep", learned);
    }

    [Fact]
    public void AutoLearnIgnoresOrdinaryRewrites()
    {
        Assert.Empty(AutoLearner.LearnedTerms("the meeting is today", "", "the call is tomorrow", new HashSet<string>()));
        Assert.Empty(AutoLearner.LearnedTerms("ask pria", "", "ask Priya", new HashSet<string> { "priya" }));
    }

    static HistoryEntry Entry(DateTimeOffset now, double daysAgo, string text, double seconds, string app = "Notes") =>
        new(HistoryKind.Dictation, app, text, text, seconds, 500, "inserted") { Date = now.AddDays(-daysAgo) };

    [Fact]
    public void StatsAndStreak()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var entries = new[] { Entry(now, 0, "one two three four", 3), Entry(now, 1, "five six", 3, "Slack"), Entry(now, 10, "seven eight nine ten", 6) };
        var s = UsageStats.Compute(entries, now, TimeZoneInfo.Utc);
        Assert.Equal(10, s.TotalWords);
        Assert.Equal(6, s.WordsThisWeek);
        Assert.Equal(3, s.Dictations);
        Assert.Equal(50, s.WordsPerMinute);
        Assert.Equal(2, s.DayStreak);
        Assert.Equal("Notes", s.TopApps[0].App);
        Assert.Equal(2, HistoryRetention.Apply(entries, 7, now).Count);
        Assert.Equal(3, HistoryRetention.Apply(entries, -1, now).Count);
        Assert.Empty(HistoryRetention.Apply(entries, 0, now));
    }

    [Fact]
    public void MisspellingCorrectionsKeepSavedCasing()
    {
        var entries = new[]
        {
            new DictionaryEntry("Saideep", "sai deep"), new DictionaryEntry("LearnX", "learn x"), new DictionaryEntry("Priya"),
        };
        var result = DictionaryCorrector.Apply("Ask Sai Deep about learn x. Saidee is fine.", entries);
        Assert.Equal("Ask Saideep about LearnX. Saidee is fine.", result.Text);
        Assert.Equal(2, result.Fixes);
    }

    [Fact]
    public void DictionaryValidation()
    {
        var existing = new[] { new DictionaryEntry("Priya") };
        Assert.Equal("Word cannot be empty.", DictionaryEntry.Validate("  ", null, existing));
        Assert.Contains("already in your dictionary", DictionaryEntry.Validate("priya", null, existing));
        Assert.NotNull(DictionaryEntry.Validate(new string('a', 61), null, existing));
        Assert.Null(DictionaryEntry.Validate("Priya", "pria", existing));
    }

    [Fact]
    public void SnippetValidationMessages()
    {
        var existing = new[] { new Snippet("my address", "12 Park Road") };
        Assert.Equal("Please enter a snippet and its expansion.", SnippetMatcher.Validate("", "x", []));
        Assert.Contains("already exists", SnippetMatcher.Validate("My Address!", "x", existing));
        Assert.NotNull(SnippetMatcher.Validate(new string('a', 61), "x", []));
        Assert.Equal("This expansion is too long. Please shorten it and try again.", SnippetMatcher.Validate("link", new string('x', 4_001), []));
        Assert.Null(SnippetMatcher.Validate("my address", "new", existing, existing[0].Id));
        Assert.Equal(2, SnippetMatcher.ExpandInlineCounting("send my address and my address", existing).Count);
    }

    [Fact]
    public void WordsChangedCountsFillersAndCorrections()
    {
        Assert.Equal(5, HistoryEntry.WordsChanged("um so let's meet tuesday no actually wednesday", "Let's meet Wednesday."));
        Assert.Equal(0, HistoryEntry.WordsChanged("hello world", "Hello, world."));
    }

    [Fact]
    public void InsightsStats()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        HistoryEntry E(double daysAgo, UsageCategory usage, int fixes, int dict) =>
            new(HistoryKind.Dictation, "App", "a b c", "a b c", 2, 300, "inserted")
            { Date = now.AddDays(-daysAgo), Usage = usage, WordsCorrected = fixes, DictionaryFixes = dict };
        var s = UsageStats.Compute([E(0, UsageCategory.Email, 2, 1), E(1, UsageCategory.Email, 1, 0), E(2, UsageCategory.Work, 0, 0),
                                    E(10, UsageCategory.Personal, 0, 3), E(11, UsageCategory.Personal, 0, 0)], now, TimeZoneInfo.Utc);
        Assert.Equal(3, s.WordsCorrected);
        Assert.Equal(4, s.DictionaryFixes);
        Assert.Equal(3, s.DayStreak);
        Assert.Equal(3, s.LongestStreak);
        Assert.Equal(new HashSet<UsageCategory> { UsageCategory.Email, UsageCategory.Personal }, s.ByCategory.Take(2).Select(p => p.Category).ToHashSet());
        Assert.Equal(UsageCategory.Work, s.ByCategory[^1].Category);
        Assert.Equal(5, s.DailyWords.Count);
    }

    [Fact]
    public void JsonFilesAreWrittenAtomicallyAndReadBack()
    {
        var folder = Path.Combine(Path.GetTempPath(), "vf-json-" + Guid.NewGuid().ToString("N"));
        try
        {
            var file = new JsonFile<List<HistoryEntry>>("history.json", folder);
            Assert.Null(file.Load());
            file.Save([new HistoryEntry(HistoryKind.Command, "Slack", "raw", "final", 1.5, 200, "inserted") { Usage = UsageCategory.Work }]);
            file.Save([new HistoryEntry(HistoryKind.Dictation, "Mail", "a", "b", 1, 2, "pasted")]);
            var back = file.Load()!;
            Assert.Single(back);
            Assert.Equal("Mail", back[0].AppName);
            Assert.Equal(HistoryKind.Dictation, back[0].Kind);
            Assert.Single(Directory.GetFiles(folder));          // no temp files left behind
            File.WriteAllText(file.Path, "{ not json");
            Assert.Null(file.Load());                         // a damaged file loads as empty, never throws
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}

public class AITests
{
    [Fact]
    public void CommandResultParsing()
    {
        Assert.Equal(new CommandResult(CommandAction.Replace, "Hello."), CommandPrompt.Parse("""{"action":"replace","text":"Hello."}"""));
        Assert.Equal(new CommandResult(CommandAction.Insert, "Draft"), CommandPrompt.Parse("""{"action":"insert","text":"Draft"}"""));
        Assert.Equal(new CommandResult(CommandAction.Search, "swift actors"), CommandPrompt.Parse("""{"action":"search","query":"swift actors"}"""));
        Assert.Equal(new CommandResult(CommandAction.Answer, "It means yes."), CommandPrompt.Parse("""{"action":"answer","text":"It means yes."}"""));
        Assert.Null(CommandPrompt.Parse("""{"action":"replace","text":""}"""));
        Assert.Null(CommandPrompt.Parse("not json"));
        Assert.Equal("https://www.perplexity.ai/search?q=a%20b", CommandPrompt.SearchUrl("a b").AbsoluteUri);
    }

    [Fact]
    public void ShortCleanDictationsSkipTheModel()
    {
        Assert.True(TextCleaner.CanSkipAI("Okay thanks"));
        Assert.True(TextCleaner.CanSkipAI("git status"));
        Assert.False(TextCleaner.CanSkipAI("um okay"));
        Assert.False(TextCleaner.CanSkipAI("buy milk new line"));
        Assert.False(TextCleaner.CanSkipAI("this has more than three words"));
    }

    [Fact]
    public void TidyRespectsStyleAndCursor()
    {
        Assert.Equal("Okay thanks", TextCleaner.Tidy("okay thanks", WritingStyle.Casual, ""));
        Assert.Equal("okay thanks", TextCleaner.Tidy("okay thanks", WritingStyle.Casual, "I said"));
        Assert.Equal("Okay thanks", TextCleaner.Tidy("okay thanks", WritingStyle.Casual, "Done. "));
        Assert.Equal("git status", TextCleaner.Tidy("git status", WritingStyle.Code, ""));
    }

    [Fact]
    public void ModelOutputSanityChecks()
    {
        Assert.NotNull(TextCleaner.Problem("", "hello"));
        Assert.NotNull(TextCleaner.Problem("<transcript>hi</transcript>", "hi"));
        Assert.NotNull(TextCleaner.Problem(string.Concat(Enumerable.Repeat("word ", 40)), "what is the capital of france"));
        Assert.Null(TextCleaner.Problem("What is the capital of France?", "what is the capital of france"));
    }

    [Fact]
    public void UserMessageIncludesCursorAndFiles()
    {
        var msg = CleanupPrompt.UserMessage("open auth controller", "code", "Cursor", [], "Please ", ["AuthController.swift"], true);
        Assert.Contains("<before_cursor>Please </before_cursor>", msg);
        Assert.Contains("<files>AuthController.swift</files>\n<tag_files>yes</tag_files>", msg);
    }

    [Fact]
    public async Task CleanupLevelNoneSkipsTheModel()
    {
        var cleaner = new TextCleaner(new GroqChat(""), "unused");
        var output = await cleaner.CleanAsync(new CleanupRequest("um so this stays exactly as said", WritingStyle.Casual, Level: CleanupLevel.None));
        Assert.Equal("um so this stays exactly as said", output.Text);
        Assert.False(output.UsedAI);
    }

    [Fact]
    public async Task MissingKeyFallsBackToTheTranscriptNeverToNothing()
    {
        var cleaner = new TextCleaner(new GroqChat(""), "unused");
        var output = await cleaner.CleanAsync(new CleanupRequest("um so we should meet tuesday", WritingStyle.Casual));
        Assert.Equal("Um so we should meet tuesday", output.Text);
        Assert.False(output.UsedAI);
        Assert.NotNull(output.Note);
    }

    [Fact]
    public async Task TeluguNeverSkipsTheModel()
    {
        var chat = new FakeChat("నేను వస్తాను.");
        var cleaner = new TextCleaner(chat, "m");
        var output = await cleaner.CleanAsync(new CleanupRequest("నేను వస్తాను", WritingStyle.Casual, Telugu: TeluguScript.Mixed));
        Assert.True(output.UsedAI);
        Assert.Contains("<telugu>mixed</telugu>", chat.LastUser);
        // Without Telugu speech the tag is left out, even for a Telugu speaker.
        await cleaner.CleanAsync(new CleanupRequest("please send the invoice to ramesh today", WritingStyle.Casual, Telugu: TeluguScript.Mixed));
        Assert.DoesNotContain("<telugu>", chat.LastUser);
    }

    [Fact]
    public void PromptCarriesCleanupLevel()
    {
        Assert.Contains("<cleanup>medium</cleanup>", CleanupPrompt.UserMessage("x", "excited", null, [], cleanup: "medium"));
        Assert.DoesNotContain("<cleanup>", CleanupPrompt.UserMessage("x", "casual", null, []));
        Assert.Contains("- excited:", CleanupPrompt.System);
    }

    [Fact]
    public void PromptsUseLfLineEndingsOnEveryCheckout()
    {
        Assert.DoesNotContain('\r', CleanupPrompt.System);
        Assert.StartsWith("You are the text-cleanup step of a dictation app.", CleanupPrompt.System);
        Assert.EndsWith("1. Clone the repo.\n2. Run npm install.", CleanupPrompt.System);
        Assert.DoesNotContain('\r', CommandPrompt.System);
        Assert.Equal(5388, CleanupPrompt.System.Length);   // the Windows self-test checks the same number
        Assert.Equal(6, CleanupPrompt.Version);
    }

    [Fact]
    public void UserMessageHasAllPartsInOrder()
    {
        Assert.Equal("<app>Mail</app>\n<style>formal</style>\n<names>Priya, Ramesh</names>\n<transcript>hi pria</transcript>",
                     CleanupPrompt.UserMessage("hi pria", "formal", "Mail", ["Priya", "Ramesh"]));
        Assert.Equal("<style>casual</style>\n<transcript>ok</transcript>", CleanupPrompt.UserMessage("ok", "casual", null, []));
    }

    [Fact]
    public void ChatResponseParsing()
    {
        Assert.Equal("Hi.", GroqChat.ParseCompletion("""{"choices":[{"message":{"content":" Hi. "},"finish_reason":"stop"}]}"""u8.ToArray()));
        var cut = Assert.Throws<ProviderException>(() => GroqChat.ParseCompletion("""{"choices":[{"message":{"content":"Hi"},"finish_reason":"length"}]}"""u8.ToArray()));
        Assert.Equal(ProviderErrorKind.Http, cut.Kind);
        Assert.Equal(ProviderErrorKind.BadResponse, Assert.Throws<ProviderException>(() => GroqChat.ParseCompletion("nope"u8.ToArray())).Kind);
    }

    [Fact]
    public void ReasoningEffortPerModel()
    {
        Assert.Equal("low", Groq.ReasoningEffort("openai/gpt-oss-120b"));
        Assert.Equal("none", Groq.ReasoningEffort("qwen/qwen3.8-27b"));
    }

    [Fact]
    public void ErrorsReadWellForUsers()
    {
        Assert.Contains("rejected the API key", ProviderException.Http(401, "{}").Message);
        Assert.True(ProviderException.Http(401, "").IsBadKey);
        Assert.Contains("internet", ProviderException.Network("x").Message);
        Assert.Contains("rate limit", ProviderException.RateLimited(3).Message);
    }
}

/// <summary>A chat that answers with a fixed reply and records the request.</summary>
sealed class FakeChat(string reply) : GroqChat("test")
{
    public string LastUser { get; private set; } = "";

    public override Task<string> CompleteAsync(string model, string system, string user, int maxTokens = 800, bool json = false,
                                               double timeoutSeconds = 6, GroqPriority priority = GroqPriority.Interactive, CancellationToken cancel = default)
    {
        LastUser = user;
        return Task.FromResult(reply);
    }
}

public class TeluguTests
{
    static DetailedTranscript T(string text, string? lang, double? conf) => new(text, lang, [], conf);

    [Fact]
    public void ForcedOnlyForOneLanguageAndTheUser()
    {
        var prefs = new Preferences { Languages = ["en", "te"] };
        Assert.Null(prefs.LanguagePlanFor().ForcedFirst);
        Assert.Equal(["te"], prefs.LanguagePlanFor().OpinionCandidates);
        prefs.Languages = ["te"];
        Assert.Equal("te", prefs.LanguagePlanFor().ForcedFirst);
        Assert.Null(prefs.LanguagePlanFor(PlanSpeaker.Anyone).ForcedFirst);
        Assert.Equal("te", Languages.CodeForWhisperName("Telugu"));
    }

    /// <summary>Cases measured with Groq Whisper on 2026-10-17 (from the macOS tests).</summary>
    [Fact]
    public void ResolverPicksTheRightTranscript()
    {
        var plan = new LanguagePlan(["en", "te"]);
        LanguageOpinion Te(string text, double conf) => new("te", T(text, "te", conf));
        Assert.StartsWith("Can you", LanguageResolver.Choose(T("Can you check the invoice?", "en", -0.14), [Te("క్ినిడాన్ిరాలు", -0.70)], plan).Text);
        Assert.Equal("te", LanguageResolver.Choose(T("नेनु रेपू ओफीस की वस्तानू", "hi", -0.079), [Te("నేను రేపు ఆఫీస్ కి వస్తాను", -0.106)], plan).Language);
        Assert.Equal("te", LanguageResolver.Choose(T("நேனு ரேபோ மீட்டின்க்கி", "ta", -0.44), [Te("నేను రేపు మీటింగ్ కి", -0.11)], plan).Language);
        Assert.Equal("te", LanguageResolver.Choose(T("Neme Rebu meeting Ghe Atena Bono", "en", -0.72), [Te("నేను రేపు మీటింగ్ కి అటెండ్ అవుతాను", -0.20)], plan).Language);
        Assert.Equal("en", LanguageResolver.Choose(T("Okay", "en", -0.30), [Te("ఓకే", -0.25)], plan).Language);
        Assert.Equal("Hi", LanguageResolver.Choose(T("Hi", "en", -0.1), [], plan).Text);
    }

    [Fact]
    public void ScriptDetection()
    {
        Assert.Equal(Script.Telugu, Scripts.Dominant("నేను office కి వస్తాను"));
        Assert.Equal(Script.Latin, Scripts.Dominant("Hello 123"));
        Assert.Equal(Script.Devanagari, Scripts.Dominant("मीटिंग"));
        Assert.Null(Scripts.Dominant("123 !!"));
    }

    [Fact]
    public void VerboseJsonParsing()
    {
        var json = """{"text":" నమస్తే ","language":"Telugu","segments":[{"start":0.0,"end":1.5,"text":" నమస్తే","avg_logprob":-0.2},{"start":1.5,"end":2.0,"text":"","avg_logprob":-0.6}]}""";
        var d = GroqTranscriber.ParseVerbose(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Equal("నమస్తే", d.Text);
        Assert.Equal("te", d.Language);
        Assert.Equal("నమస్తే", d.Segments[0].Text);
        Assert.Equal(1.5, d.Segments[0].End);
        Assert.True(Math.Abs((d.Confidence ?? 0) - (-0.3)) < 0.0001);   // (−0.2×1.5 + −0.6×0.5) / 2
    }

    [Fact]
    public void PromptCarriesTeluguMode() =>
        Assert.Contains("<telugu>roman</telugu>", CleanupPrompt.UserMessage("x", "casual", null, [], telugu: "roman"));

    [Fact]
    public void SecondOpinionRule()
    {
        var langs = new LanguagePlan(["en", "te"]);
        Assert.False(LanguageResolver.NeedsSecondOpinion(T("Can you check the invoice?", "en", -0.14), langs));
        Assert.True(LanguageResolver.NeedsSecondOpinion(T("नेनु रेपू", "hi", -0.08), langs));
        Assert.True(LanguageResolver.NeedsSecondOpinion(T("Neme Rebu meeting", "en", -0.7), langs));
        Assert.True(LanguageResolver.NeedsSecondOpinion(T("hello", null, -0.1), langs));
    }

    [Fact]
    public async Task MultipartBodyHasEveryField()
    {
        using var content = GroqTranscriber.BuildContent(AudioMath.Wav(new float[160]), "whisper-large-v3-turbo", "te", "Saideep, LearnX", "verbose_json");
        var body = await content.ReadAsStringAsync();
        foreach (var name in new[] { "model", "response_format", "temperature", "language", "prompt", "file" })
            Assert.Contains($"name={name}", body);
        Assert.Contains("filename=audio.wav", body);
        Assert.Contains("audio/wav", body);
        Assert.Contains("Saideep, LearnX", body);
    }
}
