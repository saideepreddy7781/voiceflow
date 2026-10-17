using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceFlow.Core.Audio;
using VoiceFlow.Core.Speech;

namespace VoiceFlow.Core.Meetings;

public enum Speaker
{
    /// <summary>Your microphone.</summary>
    You,
    /// <summary>Everyone else: the meeting's audio.</summary>
    Others,
}

public enum NotesLanguage { English, Meeting }

public static class MeetingText
{
    public static string Label(this Speaker s) => s == Speaker.You ? "You" : "Others";

    public static string DisplayName(this NotesLanguage l) => l == NotesLanguage.English ? "English" : "Same as the meeting";
}

public sealed record TranscriptLine(double Time, Speaker Speaker, string Text);

public sealed class ActionItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Task { get; set; } = "";
    public string? Owner { get; set; }
    public string? Due { get; set; }
    public bool Done { get; set; }

    public ActionItem() { }

    public ActionItem(string task, string? owner = null, string? due = null)
    {
        Task = task;
        Owner = owner;
        Due = due;
    }
}

public sealed class MeetingNotes
{
    public string Title { get; set; } = "";
    public List<string> Summary { get; set; } = [];
    public List<string> Decisions { get; set; } = [];
    public List<ActionItem> ActionItems { get; set; } = [];
    public List<string> Questions { get; set; } = [];

    /// <summary>The JSON shape the model reads and writes (snake_case action_items).</summary>
    public JsonObject ToModelJson()
    {
        static JsonArray Arr(IEnumerable<string> items) => new(items.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        return new JsonObject
        {
            ["title"] = Title,
            ["summary"] = Arr(Summary),
            ["decisions"] = Arr(Decisions),
            ["action_items"] = new JsonArray(ActionItems.Select(a => (JsonNode?)new JsonObject
            {
                ["task"] = a.Task, ["owner"] = a.Owner ?? "", ["due"] = a.Due ?? "", ["done"] = a.Done,
            }).ToArray()),
            ["questions"] = Arr(Questions),
        };
    }
}

public enum MeetingStatus { Recording, WritingNotes, Done, Failed }

public sealed class Meeting
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public DateTimeOffset Start { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? End { get; set; }
    /// <summary>The meeting app, e.g. "Zoom".</summary>
    public string App { get; set; } = "";
    public List<string> Attendees { get; set; } = [];
    public List<TranscriptLine> Lines { get; set; } = [];
    public MeetingNotes? Notes { get; set; }
    public MeetingStatus Status { get; set; } = MeetingStatus.Recording;
    public string? Error { get; set; }
    /// <summary>True when only the microphone could be recorded.</summary>
    public bool MicOnly { get; set; }
    /// <summary>The Markdown file last written to the notes folder, so a rename replaces it.</summary>
    public string? SyncedFileName { get; set; }

    public Meeting() { }

    public Meeting(string title, DateTimeOffset? start = null, string app = "")
    {
        Title = title;
        Start = start ?? DateTimeOffset.Now;
        App = app;
    }

    public TimeSpan Duration => (End ?? DateTimeOffset.Now) - Start;

    public string TranscriptText => string.Join("\n", Lines.Select(l => $"[{Clock(l.Time)}] {l.Speaker.Label()}: {l.Text}"));

    public static string Clock(double seconds)
    {
        var s = (int)seconds;
        return s >= 3600 ? $"{s / 3600}:{s % 3600 / 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }

    public Meeting Copy()
    {
        var json = JsonSerializer.Serialize(this, JsonFile<Meeting>.Options);
        return JsonSerializer.Deserialize<Meeting>(json, JsonFile<Meeting>.Options)!;
    }
}

/// <summary>Meeting notes as Markdown, for the notes folder and for export.</summary>
public static class MeetingMarkdown
{
    public static string Render(Meeting m, CultureInfo? culture = null)
    {
        var c = culture ?? CultureInfo.CurrentCulture;
        var sb = new StringBuilder();
        sb.Append($"# {m.Title}\n\n");
        var local = m.Start.ToLocalTime();
        var minutes = (int)Math.Round(m.Duration.TotalMinutes, MidpointRounding.AwayFromZero);
        sb.Append($"{local.ToString("D", c)} {local.ToString("t", c)} · {minutes} min");
        if (m.App.Length > 0) sb.Append($" · {m.App}");
        sb.Append('\n');
        if (m.Attendees.Count > 0) sb.Append($"Attendees: {string.Join(", ", m.Attendees)}\n");
        if (m.Notes is { } n)
        {
            if (n.Summary.Count > 0) sb.Append("\n## Summary\n\n" + string.Join("\n", n.Summary.Select(s => $"- {s}")) + "\n");
            if (n.Decisions.Count > 0) sb.Append("\n## Decisions\n\n" + string.Join("\n", n.Decisions.Select(s => $"- {s}")) + "\n");
            if (n.ActionItems.Count > 0)
            {
                sb.Append("\n## Action items\n\n" + string.Join("\n", n.ActionItems.Select(item =>
                {
                    var line = $"- [{(item.Done ? "x" : " ")}] {item.Task}";
                    if (item.Owner != null) line += $" ({item.Owner})";
                    if (item.Due != null) line += $", due {item.Due}";
                    return line;
                })) + "\n");
            }
            if (n.Questions.Count > 0) sb.Append("\n## Open questions\n\n" + string.Join("\n", n.Questions.Select(s => $"- {s}")) + "\n");
        }
        if (m.Lines.Count > 0)
            sb.Append("\n## Transcript\n\n" + string.Join("\n\n", m.Lines.Select(l => $"**{l.Speaker.Label()}** [{Meeting.Clock(l.Time)}]: {l.Text}")) + "\n");
        return sb.ToString();
    }

    /// <summary>"2026-10-17 1430 Weekly sync.md", with characters that are unsafe in Windows file names removed.</summary>
    public static string FileName(Meeting m)
    {
        var sb = new StringBuilder();
        foreach (var ch in m.Title)
            sb.Append("/\\:?*\"<>|\n\r\t".Contains(ch) || char.IsControl(ch) ? ' ' : ch);
        var safe = sb.ToString().Trim(' ', '\t');
        safe = Text.TextUtil.Prefix(safe, 80).TrimEnd(' ', '.');
        return $"{m.Start.ToLocalTime().ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture)} {(safe.Length == 0 ? "Meeting" : safe)}.md";
    }
}

/// <summary>Decides who spoke during a stretch of audio by comparing your mic with the meeting audio.</summary>
public static class SpeakerAttribution
{
    public static Speaker SpeakerFor(ReadOnlySpan<float> mic, ReadOnlySpan<float> system)
    {
        var micDb = AudioMath.Dbfs(AudioMath.Rms(mic));
        var systemDb = AudioMath.Dbfs(AudioMath.Rms(system));
        // Your voice reaches the meeting audio only faintly (echo cancellation), and the meeting audio reaches
        // your mic only through speakers, so the louder source wins.
        return micDb > systemDb ? Speaker.You : Speaker.Others;
    }

    /// <summary>Mixes your mic and the meeting audio into one stream for transcription (one upload instead of two).</summary>
    public static float[] Mix(IReadOnlyList<float> mic, IReadOnlyList<float> system)
    {
        var n = Math.Max(mic.Count, system.Count);
        var output = new float[n];
        for (int i = 0; i < n; i++)
        {
            var v = (i < mic.Count ? mic[i] : 0) + (i < system.Count ? system[i] : 0);
            output[i] = Math.Clamp(v, -1f, 1f);
        }
        return output;
    }

    /// <summary>Turns Whisper's timed segments for one chunk into transcript lines with speakers.</summary>
    public static List<TranscriptLine> Lines(DetailedTranscript transcript, double chunkStart, float[] mic, float[] system)
    {
        var rate = AudioMath.SampleRate;
        if (transcript.Segments.Count == 0)
        {
            return transcript.Text.Length == 0 ? [] : [new TranscriptLine(chunkStart, SpeakerFor(mic, system), transcript.Text)];
        }
        var lines = new List<TranscriptLine>();
        foreach (var seg in transcript.Segments)
        {
            if (seg.Text.Length == 0) continue;
            var a = Math.Max(0, Math.Min(mic.Length, (int)(seg.Start * rate)));
            var b = Math.Max(a, Math.Min(mic.Length, (int)(seg.End * rate)));
            var sa = Math.Max(0, Math.Min(system.Length, a));
            var sb = Math.Max(sa, Math.Min(system.Length, b));
            var who = SpeakerFor(mic.AsSpan(a, b - a), system.AsSpan(sa, sb - sa));
            lines.Add(new TranscriptLine(chunkStart + seg.Start, who, seg.Text));
        }
        return lines;
    }
}
