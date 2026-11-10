using System.Globalization;
using VoiceFlow.Core.AI;
using VoiceFlow.Core.Audio;
using VoiceFlow.Core.Meetings;
using VoiceFlow.Core.Speech;
using Xunit;

namespace VoiceFlow.Core.Tests;

public class MeetingTests
{
    [Fact]
    public void ChunkLinesUseTheMeetingClock()
    {
        var map = new TimeMap([new(0, 60, 2), new(2.25, 90, 2)]);
        var t = new DetailedTranscript("a b", "en", [new(0, 1.9, "Hello there"), new(2.3, 3.9, "Hi")]);
        var lines = ChunkLines.Lines(t, map, Speaker.Others, 60);
        Assert.Equal([Speaker.Others, Speaker.Others], lines.Select(l => l.Speaker));
        Assert.Equal(60, lines[0].Time, 3);
        Assert.Equal(90.05, lines[1].Time, 3);
        Assert.Equal("en", lines[0].Language);
    }

    [Fact]
    public void MarkdownAndSafeFileName()
    {
        var m = new Meeting("Launch / review: Q4?", DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), "Zoom");
        m.End = m.Start.AddMinutes(30);
        m.Lines = [new TranscriptLine(5, Speaker.You, "Let's begin.")];
        m.Notes = new MeetingNotes
        {
            Title = "Launch review", Summary = ["Launch moves"], Decisions = ["Ship Nov 20"],
            ActionItems = [new ActionItem("Update notes", "Ramesh", "Friday")], Questions = ["Privacy review?"],
        };
        var md = MeetingMarkdown.Render(m, CultureInfo.InvariantCulture);
        Assert.Contains("## Decisions\n\n- Ship Nov 20", md);
        Assert.Contains("- [ ] Update notes (Ramesh), due Friday", md);
        Assert.Contains("**You** [0:05]: Let's begin.", md);
        Assert.Contains("· 30 min · Zoom", md);
        var name = MeetingMarkdown.FileName(m);
        foreach (var bad in "/\\:?*\"<>|") Assert.DoesNotContain(bad, name);
        Assert.EndsWith(".md", name);
        Assert.Equal("1:01:05", Meeting.Clock(3665));
    }

    [Fact]
    public void NotesJsonParsing()
    {
        var notes = MeetingNotesWriter.Parse("""{"title":"Sync","summary":["a"],"decisions":[],"action_items":[{"task":"Send deck","owner":"","due":""}],"questions":[]}""");
        Assert.Equal("Sync", notes?.Title);
        Assert.Equal("Send deck", notes?.ActionItems[0].Task);
        Assert.Null(notes?.ActionItems[0].Owner);         // empty owner becomes null
        Assert.Null(MeetingNotesWriter.Parse("not json"));
    }

    [Fact]
    public void LongMeetingsAreSplitForTheTokenLimit()
    {
        var line = new TranscriptLine(0, Speaker.Others, string.Concat(Enumerable.Repeat("word ", 500)));
        Assert.Equal(3, MeetingNotesWriter.Chunks(Enumerable.Repeat(line, 13).ToList()).Count);   // ~6,500 words
        Assert.Empty(MeetingNotesWriter.Chunks([]));
    }

    [Fact]
    public void TeluguCountsAsMoreTokens()
    {
        Assert.True(MeetingNotesWriter.EstimatedTokens("hello world, this is fine") < 10);
        Assert.True(MeetingNotesWriter.EstimatedTokens("నేను రేపు ఆఫీస్ కి వస్తాను") > 15);
        var line = new TranscriptLine(0, Speaker.Others, string.Concat(Enumerable.Repeat("నేను రేపు ఆఫీస్ ", 120)));
        var chunks = MeetingNotesWriter.Chunks(Enumerable.Repeat(line, 4).ToList());
        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, c => Assert.True(MeetingNotesWriter.EstimatedTokens(c) <= MeetingNotesWriter.ChunkTokens));
    }

    [Fact]
    public async Task LongMeetingNotesAreMappedThenMerged()
    {
        var chat = new ScriptedChat(user => user.Contains("<partials>")
            ? """{"title":"Merged","summary":["all"],"decisions":[],"action_items":[{"task":"Ship"}],"questions":[]}"""
            : """{"title":"Part","summary":["p"],"decisions":[],"action_items":[],"questions":[]}""");
        var meeting = new Meeting("Meeting")
        {
            // Different lines: identical ones would collapse as a Whisper loop.
            Lines = Enumerable.Range(0, 13).Select(i => new TranscriptLine(i * 30, Speaker.Others, string.Concat(Enumerable.Repeat("word ", 500)) + i)).ToList(),
        };
        var notes = await new MeetingNotesWriter(chat, "m").WriteAsync(meeting, NotesLanguage.English);
        Assert.Equal("Merged", notes.Title);
        Assert.Equal(4, chat.Calls);                     // 3 parts + 1 merge
    }

    [Fact]
    public void MeetingsRoundTripThroughJson()
    {
        using var dir = new TempFolder();
        var file = new JsonFile<List<Meeting>>("meetings.json", dir.Path);
        var m = new Meeting("Sync", app: "Teams") { Lines = [new(1, Speaker.Others, "Hi")], Notes = new MeetingNotes { Title = "x" } };
        file.Save([m]);
        var back = file.Load()![0];
        Assert.Equal("Teams", back.App);
        Assert.Equal(Speaker.Others, back.Lines[0].Speaker);
        Assert.Equal(m.Id, back.Id);
    }
}

sealed class ScriptedChat(Func<string, string> reply) : GroqChat("test")
{
    int _calls;
    public int Calls => _calls;

    public override Task<string> CompleteAsync(string model, string system, string user, int maxTokens = 800, bool json = false,
                                               double timeoutSeconds = 6, GroqPriority priority = GroqPriority.Interactive, CancellationToken cancel = default)
    {
        Interlocked.Increment(ref _calls);
        return Task.FromResult(reply(user));
    }
}

/// <summary>Simulated audio source: hands out the samples pushed into it.</summary>
sealed class FakeSource : IMeetingAudioSource
{
    readonly object _lock = new();
    List<float> _queue = new();
    public bool FailStart { get; set; }
    public bool Disposed { get; private set; }

    public void Push(float[] samples) { lock (_lock) _queue.AddRange(samples); }
    public void StartCapture() { if (FailStart) throw new InvalidOperationException("no loopback device"); }
    public void StopCapture() { }
    public float[] DrainSamples() { lock (_lock) { var q = _queue.ToArray(); _queue = new(); return q; } }
    public void Dispose() => Disposed = true;
}

public class MeetingRecorderTests
{
    internal static float[] Speech(double s) => Enumerable.Range(0, (int)(s * 16_000)).Select(i => 0.3f * MathF.Sin(i * 0.17f)).ToArray();
    internal static float[] Silence(double s) => new float[(int)(s * 16_000)];
    internal static float[] Cat(params float[][] parts) => parts.SelectMany(p => p).ToArray();
    internal static readonly MeetingRecorder.Settings Settings = new("test", "m", ["en"], new WhisperPrompts(), null, true);
    static Task NoWait(TimeSpan _) => Task.CompletedTask;

    [Fact]
    public async Task ChunksAreTranscribedInOrderWithSpeakers()
    {
        var mic = new FakeSource();
        var system = new FakeSource();
        int n = 0;
        var recorder = new MeetingRecorder(_ => mic, () => system, (wav, r, ct) =>
        {
            Interlocked.Increment(ref n);
            var text = r.Plan.Speaker == PlanSpeaker.User ? "chunk 1" : "chunk 2";
            return Task.FromResult(new DetailedTranscript(text, "en", [new(0, 2, text)]));
        }, NoWait);
        recorder.Start("T", "Zoom", [], Settings);
        // 25 s: you talk for 22 s, then a pause; others silent. Then others talk 12 s.
        mic.Push(Cat(Speech(22), Silence(3))); system.Push(Silence(25));
        recorder.Tick();                                   // first chunk cut at the pause
        mic.Push(Silence(12)); system.Push(Speech(12));
        recorder.Tick();                                   // not long enough to cut
        var meeting = await recorder.StopAsync();          // final piece is sent on stop
        Assert.Equal(["chunk 1", "chunk 2"], meeting!.Lines.Select(l => l.Text));
        Assert.Equal([Speaker.You, Speaker.Others], meeting.Lines.Select(l => l.Speaker));
        Assert.True(meeting.Lines[^1].Time > 20);          // second chunk starts after the first
        Assert.False(meeting.MicOnly);
        Assert.Null(meeting.Error);
        Assert.True(mic.Disposed && system.Disposed);      // devices are released on stop
    }

    [Fact]
    public async Task ShortMeetingIsStillTranscribedOnStop()
    {
        var mic = new FakeSource();
        var recorder = new MeetingRecorder(_ => mic, () => null,
            (_, _, _) => Task.FromResult(new DetailedTranscript("hello", "en", [])), NoWait);
        recorder.Start("T", "", [], Settings);
        mic.Push(Speech(4));
        recorder.Tick();
        var meeting = await recorder.StopAsync();
        Assert.Equal(["hello"], meeting!.Lines.Select(l => l.Text));
        Assert.True(meeting.MicOnly);
    }

    [Fact]
    public async Task SilentMeetingExplainsWhy()
    {
        var mic = new FakeSource();
        var uploaded = false;
        var recorder = new MeetingRecorder(_ => mic, () => null, (_, _, _) =>
        {
            uploaded = true;
            return Task.FromResult(new DetailedTranscript("", null, []));
        }, NoWait);
        recorder.Start("T", "", [], Settings);
        mic.Push(Silence(30));
        recorder.Tick();
        var meeting = await recorder.StopAsync();
        Assert.False(uploaded);                            // silence is never uploaded
        Assert.Empty(meeting!.Lines);
        Assert.Contains("No speech was picked up", meeting.Error);
    }

    [Fact]
    public async Task SystemAudioFailureFallsBackToMicOnly()
    {
        var mic = new FakeSource();
        var system = new FakeSource { FailStart = true };
        var recorder = new MeetingRecorder(_ => mic, () => system,
            (_, _, _) => Task.FromResult(new DetailedTranscript("hi", "en", [])), NoWait);
        recorder.Start("T", "", [], Settings);
        Assert.NotNull(recorder.LastError);
        Assert.True(system.Disposed);
        mic.Push(Speech(3));
        var meeting = await recorder.StopAsync();
        Assert.True(meeting!.MicOnly);
        Assert.Single(meeting.Lines);
    }

    [Fact]
    public async Task FailedChunkIsMarkedNotLost()
    {
        var mic = new FakeSource();
        var waits = new List<TimeSpan>();
        var recorder = new MeetingRecorder(_ => mic, () => null, (_, _, _) => throw ProviderException.Network("offline"),
            t => { lock (waits) waits.Add(t); return Task.CompletedTask; });
        recorder.Start("T", "", [], Settings);
        mic.Push(Speech(5));
        var meeting = await recorder.StopAsync();
        Assert.Contains("could not be transcribed", meeting!.Lines[0].Text);
        Assert.Equal([2, 2, 4], waits.Select(w => w.TotalSeconds));   // 2 tries live, 3 more at Stop
    }

    [Fact]
    public async Task BacklogIsSentInPiecesOfAtMost45Seconds()
    {
        var mic = new FakeSource();
        var sizes = new List<int>();
        var recorder = new MeetingRecorder(_ => mic, () => null, (wav, _, _) =>
        {
            lock (sizes) sizes.Add(wav.Length);
            return Task.FromResult(new DetailedTranscript("x", "en", []));
        }, NoWait);
        recorder.Start("T", "", [], Settings);
        mic.Push(Speech(100));                             // 100 s piled up in one tick (no pauses)
        recorder.Tick();
        mic.Push(Speech(70));                              // more continuous speech before Stop
        var meeting = await recorder.StopAsync();
        var seconds = sizes.Select(s => (s - 44) / 2 / 16_000.0).ToList();
        Assert.All(seconds, s => Assert.True(s <= 45.01));
        Assert.True(Math.Abs(seconds.Sum() - 170) < 0.1);  // nothing lost
        Assert.Equal(seconds.Count, meeting!.Lines.Count);
    }

    [Fact]
    public async Task StalledMicIsPaddedSoMeetingAudioKeepsFlowing()
    {
        var mic = new FakeSource();
        var system = new FakeSource();
        var uploads = new TaskCompletionSource();
        var recorder = new MeetingRecorder(_ => mic, () => system, (_, _, _) =>
        {
            uploads.TrySetResult();
            return Task.FromResult(new DetailedTranscript("others", "en", []));
        }, NoWait);
        recorder.Start("T", "", [], Settings);
        system.Push(Cat(Speech(22), Silence(3), Speech(5)));   // the mic delivers nothing (device restarting)
        recorder.Tick();                                       // the mic side is padded, so a chunk goes out now
        var first = await Task.WhenAny(uploads.Task, Task.Delay(5_000));
        Assert.Same(uploads.Task, first);
        await recorder.StopAsync();
    }

    [Fact]
    public async Task RateLimitedChunkIsRetried()
    {
        var mic = new FakeSource();
        int attempts = 0;
        var recorder = new MeetingRecorder(_ => mic, () => null, (_, _, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw ProviderException.RateLimited(0);
            return Task.FromResult(new DetailedTranscript("recovered", "en", []));
        }, NoWait);
        recorder.Start("T", "", [], Settings);
        mic.Push(Speech(5));
        var meeting = await recorder.StopAsync();
        Assert.Equal(["recovered"], meeting!.Lines.Select(l => l.Text));
    }

    [Fact]
    public async Task UpdatesArriveAsCopies()
    {
        var mic = new FakeSource();
        var updates = new List<Meeting>();
        var recorder = new MeetingRecorder(_ => mic, () => null,
            (_, _, _) => Task.FromResult(new DetailedTranscript("hello", "en", [])), NoWait);
        recorder.Updated += m => { lock (updates) updates.Add(m); };
        recorder.Start("T", "", [], Settings);
        mic.Push(Speech(3));
        var meeting = await recorder.StopAsync();
        Assert.Single(updates);
        Assert.NotSame(meeting, updates[0]);
    }
}
