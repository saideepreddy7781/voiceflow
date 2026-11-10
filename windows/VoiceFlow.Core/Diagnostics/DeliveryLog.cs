using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceFlow.Core.Diagnostics;

/// <summary>Why a dictation ended the way it did. The written names match the macOS app.</summary>
public enum DeliveryReason
{
    /// <summary>Entries saved before reason codes existed.</summary>
    Unknown,
    /// <summary>Typed directly into the field through an accessibility API. Not produced on Windows, which always pastes.</summary>
    Inserted,
    PastedVerified,
    PastedUnverified,
    /// <summary>The field is readable and did not change after the paste.</summary>
    PasteNotLanded,
    CopiedNoField,
    CopiedFocusChanged,
    Blocked,
    NoSpeech,
    TooShort,
    Cancelled,
    TranscriptionFailed,
    MicFailed,
    MicRecovered,
}

public static class DeliveryReasonNames
{
    static readonly Dictionary<DeliveryReason, string> Names = new()
    {
        [DeliveryReason.Unknown] = "unknown",
        [DeliveryReason.Inserted] = "inserted",
        [DeliveryReason.PastedVerified] = "pasted-verified",
        [DeliveryReason.PastedUnverified] = "pasted-unverified",
        [DeliveryReason.PasteNotLanded] = "paste-not-landed",
        [DeliveryReason.CopiedNoField] = "copied-no-field",
        [DeliveryReason.CopiedFocusChanged] = "copied-focus-changed",
        [DeliveryReason.Blocked] = "blocked",
        [DeliveryReason.NoSpeech] = "no-speech",
        [DeliveryReason.TooShort] = "too-short",
        [DeliveryReason.Cancelled] = "cancelled",
        [DeliveryReason.TranscriptionFailed] = "transcription-failed",
        [DeliveryReason.MicFailed] = "mic-failed",
        [DeliveryReason.MicRecovered] = "mic-recovered",
    };

    static readonly Dictionary<string, DeliveryReason> Reasons = Names.ToDictionary(p => p.Value, p => p.Key);

    public static string WireName(this DeliveryReason reason) => Names[reason];

    /// <summary>A name from a newer version reads as <see cref="DeliveryReason.Unknown"/>.</summary>
    public static DeliveryReason Parse(string? name) => name != null && Reasons.TryGetValue(name, out var reason) ? reason : DeliveryReason.Unknown;
}

/// <summary>Writes the macOS names instead of the C# ones. Applied per property, since JsonFile has a converter for all enums.</summary>
public sealed class DeliveryReasonJsonConverter : JsonConverter<DeliveryReason>
{
    public override DeliveryReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DeliveryReasonNames.Parse(reader.TokenType == JsonTokenType.String ? reader.GetString() : null);

    public override void Write(Utf8JsonWriter writer, DeliveryReason value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.WireName());
}

/// <summary>
/// The only text allowed next to a reason code. A fixed vocabulary, so a future change cannot route something
/// the user said into diagnostics through this field.
/// </summary>
public static class DeliveryDetail
{
    public const string ExcludedApp = "excluded-app";
    public const string PasswordField = "password-field";
    public const string NoWindow = "no-window";
    public const string Elevated = "elevated";
    public const string AppNotResponding = "app-not-responding";
    public const string FieldUnreadable = "field-unreadable";
    public const string ValueUnchanged = "value-unchanged";
    public const string ClipboardLocked = "clipboard-locked";
    public const string InputRefused = "input-refused";
    public const string EmptyText = "empty-text";
    public const string Silent = "silent";
    public const string Hallucination = "hallucination";
    public const string DeadSignal = "dead-signal";
    public const string Stalled = "stalled";
    public const string DeviceLost = "device-lost";
    public const string StartFailed = "start-failed";
    public const string Provider = "provider";
    public const string CommandModel = "command-model";
    public const string Unexpected = "unexpected";

    static readonly HashSet<string> Known =
    [
        ExcludedApp, PasswordField, NoWindow, Elevated, AppNotResponding, FieldUnreadable, ValueUnchanged,
        ClipboardLocked, InputRefused, EmptyText, Silent, Hallucination, DeadSignal, Stalled, DeviceLost,
        StartFailed, Provider, CommandModel, Unexpected,
    ];

    /// <summary>The value when it is in the vocabulary, otherwise null.</summary>
    public static string? Allow(string? detail) => detail != null && Known.Contains(detail) ? detail : null;
}

public enum DeliverySession { Dictation, Command, Meeting, PasteLast }

/// <summary>One session's outcome. Holds codes and numbers only: no text, no app names, no window titles.</summary>
public sealed class DeliveryLogEntry
{
    public DateTimeOffset Date { get; set; } = DateTimeOffset.Now;
    public DeliverySession Session { get; set; }
    [JsonConverter(typeof(DeliveryReasonJsonConverter))]
    public DeliveryReason Reason { get; set; }
    /// <summary>Always from <see cref="DeliveryDetail"/>.</summary>
    public string? Detail { get; set; }
    public int AudioMs { get; set; }
    public int LatencyMs { get; set; }

    public DeliveryLogEntry() { }

    public DeliveryLogEntry(DeliverySession session, DeliveryReason reason, string? detail = null, int audioMs = 0, int latencyMs = 0)
    {
        Session = session;
        Reason = reason;
        Detail = DeliveryDetail.Allow(detail);
        AudioMs = audioMs;
        LatencyMs = latencyMs;
    }
}

/// <summary>
/// A small ring of recent delivery outcomes, including sessions that leave no history entry (no speech, blocked,
/// mic failures). Local only. Diagnostics must never break dictation, so disk errors are swallowed.
/// </summary>
public sealed class DeliveryLog
{
    public const int DefaultCapacity = 200;

    readonly object _gate = new();
    readonly JsonFile<List<DeliveryLogEntry>>? _file;
    readonly int _capacity;
    readonly List<DeliveryLogEntry> _entries;
    bool _persists;

    /// <param name="folder">Where to keep the file. Null keeps the log in memory.</param>
    /// <param name="persists">False when the user chose to store nothing: entries last for this run only.</param>
    public DeliveryLog(string? folder, int capacity = DefaultCapacity, bool persists = true)
    {
        _capacity = capacity;
        _persists = persists;
        _file = folder == null ? null : new JsonFile<List<DeliveryLogEntry>>("delivery-log.json", folder);
        _entries = (persists ? _file?.Load() : null) ?? [];
        foreach (var entry in _entries) entry.Detail = DeliveryDetail.Allow(entry.Detail);
        Trim();
    }

    public void Record(DeliveryLogEntry entry)
    {
        lock (_gate)
        {
            entry.Detail = DeliveryDetail.Allow(entry.Detail);
            _entries.Add(entry);
            Trim();
            if (_persists) Save();
        }
    }

    /// <summary>Turning this off deletes the file. Turning it on writes what this run has so far.</summary>
    public bool Persists
    {
        get { lock (_gate) return _persists; }
        set
        {
            lock (_gate)
            {
                if (_persists == value) return;
                _persists = value;
                if (value) Save();
                else if (_file != null)
                {
                    try { File.Delete(_file.Path); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
    }

    void Save()
    {
        try { _file?.Save(_entries); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>The most recent entries, newest first.</summary>
    public IReadOnlyList<DeliveryLogEntry> Recent(int count = DefaultCapacity)
    {
        lock (_gate) return _entries.AsEnumerable().Reverse().Take(count).ToList();
    }

    void Trim()
    {
        if (_entries.Count > _capacity) _entries.RemoveRange(0, _entries.Count - _capacity);
    }
}
