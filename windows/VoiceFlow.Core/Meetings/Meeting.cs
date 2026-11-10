using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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

/// <param name="Time">Seconds since the meeting started.</param>
/// <param name="End">When the line ended, for lining up the two sides of the call. Missing in older meetings.</param>
/// <param name="Language">Whisper's language for this line. Missing in older meetings.</param>
public sealed record TranscriptLine(double Time, Speaker Speaker, string Text, double? End = null, string? Language = null);

/// <summary>A stretch of the meeting where your microphone delivered no usable audio.</summary>
public sealed record MicGap(double Time, double Seconds);

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

    /// <summary>Keeps the ticks on action items that are still there after the notes are rewritten.</summary>
    public MeetingNotes CarryingOverDone(MeetingNotes? old)
    {
        if (old == null) return this;
        var done = old.ActionItems.Where(a => a.Done).Select(a => a.Task.ToLowerInvariant()).ToHashSet();
        foreach (var item in ActionItems)
            if (done.Contains(item.Task.ToLowerInvariant())) item.Done = true;
        return this;
    }

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
    /// <summary>Where your voice is missing from the recording because the microphone failed.</summary>
    public List<MicGap> MicGaps { get; set; } = [];
    /// <summary>The Markdown file last written to the notes folder, so a rename replaces it.</summary>
    public string? SyncedFileName { get; set; }
    /// <summary>Names the user gave the two speakers. A missing entry shows the default label.</summary>
    public Dictionary<Speaker, string> SpeakerNames { get; set; } = [];
    /// <summary>A plain-language note about gaps, shown with the transcript (for example a daily limit running out).</summary>
    public string? Note { get; set; }
    /// <summary>Lines were rewritten into the chosen writing (Telugu or Hindi script choice).</summary>
    public bool? ScriptRewritten { get; set; }

    public Meeting() { }

    public Meeting(string title, DateTimeOffset? start = null, string app = "")
    {
        Title = title;
        Start = start ?? DateTimeOffset.Now;
        App = app;
    }

    public TimeSpan Duration => (End ?? DateTimeOffset.Now) - Start;

    public string LabelFor(Speaker speaker) =>
        SpeakerNames.TryGetValue(speaker, out var name) && name.Trim().Length > 0 ? name.Trim() : speaker.Label();

    /// <summary>What "Others" is called in this meeting, after a rename. Null or blank shows "Others".</summary>
    [JsonIgnore]
    public string? OthersLabel
    {
        get => SpeakerNames.GetValueOrDefault(Speaker.Others);
        set
        {
            if (string.IsNullOrWhiteSpace(value)) SpeakerNames.Remove(Speaker.Others);
            else SpeakerNames[Speaker.Others] = value;
        }
    }

    /// <summary>The language spoken most in this meeting, by word count, from the per-line languages.</summary>
    [JsonIgnore]
    public string? DominantLanguage =>
        Lines.Where(l => l.Language != null).GroupBy(l => l.Language!)
            .Select(g => (Language: g.Key, Words: g.Sum(l => l.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length)))
            .OrderByDescending(p => p.Words).Select(p => p.Language).FirstOrDefault();

    public string TranscriptText => string.Join("\n", Lines.Select(l => $"[{Clock(l.Time)}] {LabelFor(l.Speaker)}: {l.Text}"));

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
        if (!string.IsNullOrEmpty(m.Note)) sb.Append($"\n_{m.Note}_\n");
        if (m.Lines.Count > 0)
            sb.Append("\n## Transcript\n\n" + string.Join("\n\n", m.Lines.Select(l => $"**{m.LabelFor(l.Speaker)}** [{Meeting.Clock(l.Time)}]: {l.Text}")) + "\n");
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

/// <summary>Finds meetings by title, notes or transcript.</summary>
public static class MeetingSearch
{
    /// <summary>Whether the meeting mentions <paramref name="query"/> anywhere. An empty query matches everything.</summary>
    public static bool Matches(Meeting meeting, string query)
    {
        var q = query.Trim();
        if (q.Length == 0) return true;
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        return SearchableParts(meeting).Any(p => compare.IndexOf(p, q, CompareOptions.IgnoreCase) >= 0);
    }

    /// <summary>A short piece of the first matching transcript line or note, for the results list. Null when only the title matches.</summary>
    public static string? Snippet(Meeting meeting, string query, int radius = 40)
    {
        var q = query.Trim();
        if (q.Length == 0) return null;
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        foreach (var part in SearchableParts(meeting).Skip(1))
        {
            var at = compare.IndexOf(part, q, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace, out var length);
            if (at < 0) continue;
            // Cut on whole characters: a Telugu vowel sign is its own UTF-16 unit.
            var starts = StringInfo.ParseCombiningCharacters(part);
            var first = Array.FindLastIndex(starts, s => s <= at);
            var last = Array.FindIndex(starts, s => s >= at + length);
            if (last < 0) last = starts.Length;
            var from = Math.Max(0, first - radius);
            var to = Math.Min(starts.Length, last + radius);
            var end = to >= starts.Length ? part.Length : starts[to];
            return (from > 0 ? "…" : "") + part[starts[from]..end] + (end < part.Length ? "…" : "");
        }
        return null;
    }

    static List<string> SearchableParts(Meeting m)
    {
        var parts = new List<string> { m.Title };
        if (m.Notes is { } n)
        {
            parts.AddRange(n.Summary);
            parts.AddRange(n.Decisions);
            parts.AddRange(n.ActionItems.Select(a => a.Task));
            parts.AddRange(n.Questions);
        }
        parts.AddRange(m.Lines.Select(l => l.Text));
        return parts;
    }
}
