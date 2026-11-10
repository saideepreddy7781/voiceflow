using System.Text.Json;
using VoiceFlow.Core.Library;
using VoiceFlow.Core.Meetings;
using Xunit;

namespace VoiceFlow.Core.Tests;

public class NoteAndSpeakerTests
{
    [Fact]
    public void NoteDisplayTitlePrefersTitleThenFirstWordsThenUntitled()
    {
        Assert.Equal("Trip", new Note("body here") { Title = "  Trip " }.DisplayTitle);
        Assert.Equal("one two three four five six", new Note("one two three four five six seven").DisplayTitle);
        Assert.Equal("Untitled", new Note().DisplayTitle);
    }

    [Fact]
    public void NoteIsBlankOnlyWhenBothFieldsAreEmpty()
    {
        Assert.True(new Note("  ").IsBlank);
        Assert.False(new Note("x").IsBlank);
    }

    [Fact]
    public void SpeakerNamesFallBackToDefaultLabel()
    {
        var m = new Meeting("Sync");
        Assert.Equal("You", m.LabelFor(Speaker.You));
        m.SpeakerNames[Speaker.Others] = " Rahul ";
        Assert.Equal("Rahul", m.LabelFor(Speaker.Others));
        Assert.Equal("You", m.LabelFor(Speaker.You));
    }

    [Fact]
    public void MarkdownAndTranscriptUseSpeakerNames()
    {
        var m = new Meeting("Sync") { End = DateTimeOffset.Now };
        m.Lines.Add(new TranscriptLine(5, Speaker.Others, "Hello"));
        m.SpeakerNames[Speaker.Others] = "Rahul";
        Assert.Contains("**Rahul**", MeetingMarkdown.Render(m));
        Assert.Contains("Rahul: Hello", m.TranscriptText);
    }

    [Fact]
    public void MeetingsWithoutSpeakerNamesStillDecode()
    {
        var json = """{"id":"1a1d7d3c-1e71-4f5c-92ee-2c1b0a9d2f11","title":"Old","status":"done"}""";
        var m = JsonSerializer.Deserialize<Meeting>(json, JsonFile<Meeting>.Options)!;
        Assert.Empty(m.SpeakerNames);
        Assert.Equal("You", m.LabelFor(Speaker.You));
    }
}
