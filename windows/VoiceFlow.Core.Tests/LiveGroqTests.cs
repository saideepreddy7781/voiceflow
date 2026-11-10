using System.Diagnostics;
using VoiceFlow.Core.AI;
using VoiceFlow.Core.Audio;
using VoiceFlow.Core.Meetings;
using VoiceFlow.Core.Speech;
using Xunit;
using Xunit.Abstractions;

namespace VoiceFlow.Core.Tests;

/// <summary>Skips a test when GROQ_API_KEY is not set (CI without secrets, contributors without a key).</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("GROQ_API_KEY")))
            Skip = "GROQ_API_KEY is not set";
        else if (!Directory.Exists(LiveGroqTests.Fixtures))
            Skip = "fixtures/clips is missing (run scripts/make_fixtures.py on a Mac)";
    }
}

/// <summary>Skips when the local audio fixtures are missing (they are generated, not committed).</summary>
public sealed class FixtureFactAttribute : FactAttribute
{
    public FixtureFactAttribute()
    {
        if (!Directory.Exists(LiveGroqTests.Fixtures)) Skip = "fixtures/clips is missing (run scripts/make_fixtures.py on a Mac)";
    }
}

public class FixtureTests
{
    [FixtureFact]
    public void SilenceClipNeverReachesTheApi()
    {
        var silence = AudioMath.ReadWav16(File.ReadAllBytes(Path.Combine(LiveGroqTests.Fixtures, "silence.wav")));
        Assert.True(AudioMath.IsSilent(silence));
        var speech = AudioMath.ReadWav16(File.ReadAllBytes(Path.Combine(LiveGroqTests.Fixtures, "short.wav")));
        Assert.False(AudioMath.IsSilent(speech));
    }
}

/// <summary>End to end against Groq with real fixture clips. The key is read from the environment and never printed.</summary>
public class LiveGroqTests(ITestOutputHelper output)
{
    static string Key => Environment.GetEnvironmentVariable("GROQ_API_KEY")!.Trim();

    internal static string Fixtures
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "fixtures", "clips"))) dir = dir.Parent;
            return dir == null ? "" : Path.Combine(dir.FullName, "fixtures", "clips");
        }
    }

    static byte[] Clip(string name) => File.ReadAllBytes(Path.Combine(Fixtures, name + ".wav"));

    [LiveFact]
    public async Task SelfCorrectionClipBecomesCleanText()
    {
        var watch = Stopwatch.StartNew();
        var transcript = await new GroqTranscriber(Key).TranscribeAsync(Clip("self-correction"), "en", null);
        var heard = watch.Elapsed;
        var outcome = await new TextCleaner(new GroqChat(Key), "qwen/qwen3.8-27b")
            .CleanAsync(new CleanupRequest(transcript, WritingStyle.Casual, "Slack"));
        output.WriteLine($"transcript: {transcript}\ncleaned:    {outcome.Text}\ntimes: STT {heard.TotalSeconds:0.00}s, cleanup {(watch.Elapsed - heard).TotalSeconds:0.00}s");
        Assert.True(outcome.UsedAI, outcome.Note);
        Assert.Contains("Wednesday", outcome.Text);
        Assert.DoesNotContain("Tuesday", outcome.Text);
    }

    [LiveFact]
    public async Task FillerClipLosesItsFillers()
    {
        var samples = AudioMath.ReadWav16(Clip("filler"));
        Assert.False(AudioMath.IsSilent(samples));
        // The same path the app uses: re-encoded through AudioMath.Wav, Whisper mode on.
        var wav = AudioMath.Wav(AudioMath.Normalized(samples));
        var detailed = await new GroqTranscriber(Key).TranscribeResolvingAsync(wav, new LanguagePlan(["en"]), new WhisperPrompts());
        var outcome = await new TextCleaner(new GroqChat(Key), "qwen/qwen3.8-27b")
            .CleanAsync(new CleanupRequest(detailed.Text, WritingStyle.Casual));
        output.WriteLine($"[{detailed.Language}] {detailed.Text}\n→ {outcome.Text}");
        Assert.Contains("launch", outcome.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("um,", outcome.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" uh", outcome.Text, StringComparison.OrdinalIgnoreCase);
    }

    [LiveFact]
    public async Task SpokenInstructionIsTypedNotObeyed()
    {
        var transcript = await new GroqTranscriber(Key).TranscribeAsync(Clip("injection"), "en", null);
        var outcome = await new TextCleaner(new GroqChat(Key), "qwen/qwen3.8-27b").CleanAsync(new CleanupRequest(transcript, WritingStyle.Formal));
        output.WriteLine($"{transcript}\n→ {outcome.Text}");
        Assert.Contains("poem", outcome.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(outcome.Text.Split(' ').Length < 25);   // it did not write the poem
    }

    [LiveFact]
    public async Task CommandModeRewritesSelection()
    {
        var result = await new CommandRunner(new GroqChat(Key), "openai/gpt-oss-120b")
            .RunAsync("Mail", "hey can u send the report", "hey can u send the report", "make this more formal");
        output.WriteLine($"command result: {result}");
        Assert.Equal(CommandAction.Replace, result.Action);
        Assert.Contains("report", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" u ", result.Text);
    }

    [LiveFact]
    public async Task TeluguAndEnglishResolve()
    {
        var prefs = new Preferences { Languages = ["en", "te"] };
        var transcriber = new GroqTranscriber(Key);
        foreach (var (clip, expectTelugu) in new[] { ("telugu-english", true), ("question", false), ("email", false) })
        {
            if (!File.Exists(Path.Combine(Fixtures, clip + ".wav"))) continue;
            var result = await transcriber.TranscribeResolvingAsync(Clip(clip), prefs.LanguagePlanFor(), new WhisperPrompts());
            output.WriteLine($"{clip}: [{result.Language ?? "?"}] {result.Text}");
            Assert.Equal(expectTelugu, Scripts.Dominant(result.Text) == Script.Telugu);
        }
    }

    [LiveFact]
    public async Task KeyCheckAcceptsTheRealKeyAndRejectsAFakeOne()
    {
        var tester = new KeyTester();
        var good = await tester.CheckAsync(SecretKey.Groq, Key);
        output.WriteLine(good.Summary);
        Assert.Equal(KeyCheckStatus.Ok, good.Status);
        Assert.Equal(KeyCheckStatus.InvalidKey, (await tester.CheckAsync(SecretKey.Groq, "gsk_not_a_real_key")).Status);
    }

    [LiveFact]
    public async Task MeetingLinesAreRewrittenInTheChosenWriting()
    {
        var result = await new TranscriptScriptWriter(new GroqChat(Key), "qwen/qwen3.8-27b")
            .RewriteAsync([new TranscriptLine(0, Speaker.Others, "నేను రేపు ఆఫీస్ కి వస్తాను")], TeluguScript.Mixed, null);
        output.WriteLine($"{result.Lines[0].Text} (note: {result.Note ?? "none"})");
        Assert.Null(result.Note);
        Assert.Contains("office", result.Lines[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    [LiveFact]
    public async Task MeetingChunkGetsNotes()
    {
        var mic = AudioMath.ReadWav16(Clip("email"));
        var transcript = await new GroqTranscriber(Key).TranscribeDetailedAsync(AudioMath.Wav(mic), "en", null, wordTimestamps: true);
        Assert.Contains(transcript.Segments, x => x.Words is { Count: > 0 });
        var lines = ChunkLines.Lines(transcript, new TimeMap([new(0, 0, 600)]), Speaker.You, 0);
        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.Equal(Speaker.You, l.Speaker));
        var notes = await new MeetingNotesWriter(new GroqChat(Key), "openai/gpt-oss-120b")
            .WriteAsync(new Meeting("Meeting") { Lines = lines }, NotesLanguage.English);
        output.WriteLine($"notes title: {notes.Title}; {notes.Summary.Count} summary points");
        Assert.NotEmpty(notes.Summary);
    }
}
