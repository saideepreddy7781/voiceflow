using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceFlow.Core.Library;
using VoiceFlow.Core.Meetings;
using VoiceFlow.Core.Speech;

namespace VoiceFlow.Core;

/// <summary>Dictation hotkeys offered on Windows. Most Windows keyboards do not report Fn to software.</summary>
public enum Hotkey { CtrlWin, RightAlt, RightCtrl }

public static class HotkeyInfo
{
    public static string DisplayName(Hotkey h) => h switch
    {
        Hotkey.CtrlWin => "Ctrl + Win",
        Hotkey.RightAlt => "Right Alt",
        _ => "Right Ctrl",
    };

    /// <summary>The key added to the hotkey for Command Mode.</summary>
    public static string CommandModifier(Hotkey h) => h == Hotkey.RightAlt ? "Shift" : "Alt";

    public static string HandsFree(Hotkey h) => DisplayName(h) + " + Space";
}

public enum EditorProvider { Groq, Gemini }

/// <summary>How Telugu dictation is written.</summary>
public enum TeluguScript
{
    /// <summary>నేను రేపు ఆఫీస్ కి వస్తాను</summary>
    Telugu,
    /// <summary>నేను రేపు office కి వస్తాను</summary>
    Mixed,
    /// <summary>nenu repu office ki vastanu</summary>
    Roman,
}

/// <summary>How Hindi dictation and meeting transcripts are written.</summary>
public enum HindiScript
{
    /// <summary>मैं कल ऑफिस आऊँगा</summary>
    Devanagari,
    /// <summary>मैं कल office आऊँगा</summary>
    Mixed,
    /// <summary>main kal office aaunga</summary>
    Hinglish,
}

public enum CleanupLevel { None, Light, Medium }

public static class EnumText
{
    /// <summary>The camelCase raw value used in JSON and prompts (matches the Swift raw values).</summary>
    public static string Raw<T>(T value) where T : struct, Enum
    {
        var s = value.ToString();
        return char.ToLowerInvariant(s[0]) + s[1..];
    }

    public static bool TryParse<T>(string? raw, out T value) where T : struct, Enum
    {
        value = default;
        if (string.IsNullOrEmpty(raw)) return false;
        foreach (var v in Enum.GetValues<T>())
        {
            if (Raw(v) == raw) { value = v; return true; }
        }
        return false;
    }

    public static string DisplayName(TeluguScript t) => t switch
    {
        TeluguScript.Telugu => "Telugu script",
        TeluguScript.Mixed => "Telugu + English words",
        _ => "Telish (English letters)",
    };

    public static string Example(TeluguScript t) => t switch
    {
        TeluguScript.Telugu => "నేను రేపు ఆఫీస్ కి వస్తాను, మీటింగ్ పది గంటలకి ఉంది.",
        TeluguScript.Mixed => "నేను రేపు office కి వస్తాను, meeting 10 గంటలకి ఉంది.",
        _ => "Nenu repu office ki vastanu, meeting 10 gantalaki undi.",
    };

    /// <summary>The word used in the cleanup prompt's &lt;telugu&gt; tag.</summary>
    public static string PromptName(TeluguScript t) => Raw(t);

    public static string DisplayName(HindiScript h) => h switch
    {
        HindiScript.Devanagari => "Devanagari",
        HindiScript.Mixed => "Hindi + English words",
        _ => "Hinglish (English letters)",
    };

    public static string Example(HindiScript h) => h switch
    {
        HindiScript.Devanagari => "मैं कल ऑफिस आऊँगा, मीटिंग दस बजे है।",
        HindiScript.Mixed => "मैं कल office आऊँगा, meeting 10 बजे है।",
        _ => "Main kal office aaunga, meeting 10 baje hai.",
    };

    /// <summary>The word used in the cleanup prompt's &lt;hindi&gt; tag.</summary>
    public static string PromptName(HindiScript h) => Raw(h);

    public static string DisplayName(CleanupLevel l) => l switch
    {
        CleanupLevel.None => "None",
        CleanupLevel.Light => "Light",
        _ => "Medium",
    };

    public static string Summary(CleanupLevel l) => l switch
    {
        CleanupLevel.None => "Exactly what you said. No AI editing.",
        CleanupLevel.Light => "Removes filler words, applies your corrections, fixes grammar and punctuation.",
        _ => "Also rephrases for clarity and brevity, keeping your meaning.",
    };
}

/// <summary>
/// Typed user preferences. Decoding tolerates missing keys and unknown values, so adding a field never
/// resets the user's other settings.
/// </summary>
public sealed class Preferences : IEquatable<Preferences>
{
    // Shortcuts
    public Hotkey Hotkey { get; set; } = Hotkey.CtrlWin;

    // Models
    public EditorProvider EditorProvider { get; set; } = EditorProvider.Groq;
    public string TranscriptionModel { get; set; } = Groq.TranscriptionModels[0];
    public string CleanupModel { get; set; } = Groq.TextModels[0];
    public string CommandModel { get; set; } = "openai/gpt-oss-120b";
    public string GeminiModel { get; set; } = "";

    // Dictation
    /// <summary>Languages you speak (Whisper codes). One is passed to Whisper; none or several means auto-detect.</summary>
    public List<string> Languages { get; set; } = ["en"];
    public TeluguScript TeluguScript { get; set; } = TeluguScript.Mixed;
    public HindiScript HindiScript { get; set; } = HindiScript.Mixed;
    public CleanupLevel CleanupLevel { get; set; } = CleanupLevel.Light;
    public bool CommandModeEnabled { get; set; } = true;
    /// <summary>Adds a word to the dictionary when you correct it right after dictating.</summary>
    public bool AutoLearnWords { get; set; } = true;
    public Dictionary<StyleCategory, WritingStyle> Styles { get; set; } = [];

    // Audio
    /// <summary>Capture endpoint ID; empty means the default microphone.</summary>
    public string InputDeviceId { get; set; } = "";
    /// <summary>Whisper mode: boosts quiet speech before transcription.</summary>
    public bool WhisperMode { get; set; } = true;
    public bool MuteAudioWhileDictating { get; set; } = true;
    public bool PlaySounds { get; set; } = true;

    // Notetaker
    /// <summary>Offers Notetaker when a call starts. It never starts recording on its own.</summary>
    public bool MeetingsDetect { get; set; } = true;
    public bool MeetingsSystemAudio { get; set; } = true;
    /// <summary>Folder where each meeting's notes are saved as Markdown. Empty: don't save.</summary>
    public string MeetingsFolder { get; set; } = "";
    public NotesLanguage MeetingsNotesLanguage { get; set; } = NotesLanguage.English;
    public bool MeetingsConsentReminder { get; set; } = true;

    // System
    public bool ShowBarWhenIdle { get; set; } = false;
    /// <summary>0 keeps no history; -1 keeps it forever.</summary>
    public int HistoryRetentionDays { get; set; } = 30;
    public bool LaunchAtLogin { get; set; }
    /// <summary>Asks GitHub for the latest release at launch and once a day. Nothing is downloaded.</summary>
    public bool CheckForUpdates { get; set; } = true;
    public bool DebugLogging { get; set; }
    public bool Onboarded { get; set; }

    public WritingStyle StyleFor(StyleCategory category)
    {
        var chosen = Styles.TryGetValue(category, out var s) ? s : category.DefaultStyle();
        return category.Options().Contains(chosen) ? chosen : category.DefaultStyle();
    }

    /// <summary>The language sent to Whisper: the one language you chose, or null (auto-detect).</summary>
    public string? WhisperLanguage => Languages.Count == 1 ? Languages[0] : null;

    /// <summary>Who might be speaking and what they might speak, for the resolver.</summary>
    public LanguagePlan LanguagePlanFor(PlanSpeaker speaker = PlanSpeaker.User) => new(Languages, speaker);

    public bool SpeaksTelugu => Languages.Contains("te");
    public bool SpeaksHindi => Languages.Contains("hi");

    public bool Equals(Preferences? other) =>
        other is not null && ToJson() == other.ToJson();

    public override bool Equals(object? obj) => Equals(obj as Preferences);

    public override int GetHashCode() => ToJson().GetHashCode(StringComparison.Ordinal);

    public Preferences Clone() => FromJson(ToJson());

    public string ToJson()
    {
        var styles = new JsonObject();
        foreach (var (k, v) in Styles.OrderBy(p => p.Key)) styles[EnumText.Raw(k)] = EnumText.Raw(v);
        var o = new JsonObject
        {
            ["hotkey"] = EnumText.Raw(Hotkey),
            ["editorProvider"] = EnumText.Raw(EditorProvider),
            ["transcriptionModel"] = TranscriptionModel,
            ["cleanupModel"] = CleanupModel,
            ["commandModel"] = CommandModel,
            ["geminiModel"] = GeminiModel,
            ["languages"] = new JsonArray(Languages.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray()),
            ["teluguScript"] = EnumText.Raw(TeluguScript),
            ["hindiScript"] = EnumText.Raw(HindiScript),
            ["cleanupLevel"] = EnumText.Raw(CleanupLevel),
            ["commandModeEnabled"] = CommandModeEnabled,
            ["autoLearnWords"] = AutoLearnWords,
            ["styles"] = styles,
            ["inputDeviceId"] = InputDeviceId,
            ["whisperMode"] = WhisperMode,
            ["muteAudioWhileDictating"] = MuteAudioWhileDictating,
            ["playSounds"] = PlaySounds,
            ["meetingsDetect"] = MeetingsDetect,
            ["meetingsSystemAudio"] = MeetingsSystemAudio,
            ["meetingsFolder"] = MeetingsFolder,
            ["meetingsNotesLanguage"] = EnumText.Raw(MeetingsNotesLanguage),
            ["meetingsConsentReminder"] = MeetingsConsentReminder,
            ["showBarWhenIdle"] = ShowBarWhenIdle,
            ["historyRetentionDays"] = HistoryRetentionDays,
            ["launchAtLogin"] = LaunchAtLogin,
            ["checkForUpdates"] = CheckForUpdates,
            ["debugLogging"] = DebugLogging,
            ["onboarded"] = Onboarded,
        };
        return o.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Reads preferences. Anything missing, malformed or unknown keeps its default.</summary>
    public static Preferences FromJson(string? json)
    {
        var p = new Preferences();
        if (string.IsNullOrWhiteSpace(json)) return p;
        JsonObject? o;
        try { o = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return p; }
        if (o == null) return p;

        static string? Str(JsonObject o, string key) =>
            o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        static bool? Bool(JsonObject o, string key) =>
            o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
        static int? Int(JsonObject o, string key) =>
            o[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
        void E<T>(string key, Action<T> set) where T : struct, Enum
        {
            if (EnumText.TryParse<T>(Str(o, key), out var e)) set(e);
        }

        E<Hotkey>("hotkey", v => p.Hotkey = v);
        E<EditorProvider>("editorProvider", v => p.EditorProvider = v);
        p.TranscriptionModel = Str(o, "transcriptionModel") ?? p.TranscriptionModel;
        p.CleanupModel = Str(o, "cleanupModel") ?? p.CleanupModel;
        p.CommandModel = Str(o, "commandModel") ?? p.CommandModel;
        p.GeminiModel = Str(o, "geminiModel") ?? p.GeminiModel;
        if (o["languages"] is JsonArray langs)
        {
            p.Languages = langs.OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null)
                .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
        }
        else if (Str(o, "language") is { } old)
        {
            // Older versions stored one language; "" meant auto-detect.
            p.Languages = old.Length == 0 ? [] : [old];
        }
        E<TeluguScript>("teluguScript", v => p.TeluguScript = v);
        E<HindiScript>("hindiScript", v => p.HindiScript = v);
        if (EnumText.TryParse<CleanupLevel>(Str(o, "cleanupLevel"), out var level)) p.CleanupLevel = level;
        else if (Bool(o, "cleanupEnabled") == false) p.CleanupLevel = CleanupLevel.None;
        p.CommandModeEnabled = Bool(o, "commandModeEnabled") ?? p.CommandModeEnabled;
        p.AutoLearnWords = Bool(o, "autoLearnWords") ?? p.AutoLearnWords;
        if (o["styles"] is JsonObject styles)
        {
            foreach (var (k, v) in styles)
            {
                if (EnumText.TryParse<StyleCategory>(k, out var cat) && v is JsonValue sv && sv.TryGetValue<string>(out var raw)
                    && EnumText.TryParse<WritingStyle>(raw, out var style))
                    p.Styles[cat] = style;
            }
        }
        p.InputDeviceId = Str(o, "inputDeviceId") ?? p.InputDeviceId;
        p.WhisperMode = Bool(o, "whisperMode") ?? p.WhisperMode;
        p.MuteAudioWhileDictating = Bool(o, "muteAudioWhileDictating") ?? p.MuteAudioWhileDictating;
        p.PlaySounds = Bool(o, "playSounds") ?? p.PlaySounds;
        p.MeetingsDetect = Bool(o, "meetingsDetect") ?? p.MeetingsDetect;
        p.MeetingsSystemAudio = Bool(o, "meetingsSystemAudio") ?? p.MeetingsSystemAudio;
        p.MeetingsFolder = Str(o, "meetingsFolder") ?? p.MeetingsFolder;
        E<NotesLanguage>("meetingsNotesLanguage", v => p.MeetingsNotesLanguage = v);
        p.MeetingsConsentReminder = Bool(o, "meetingsConsentReminder") ?? p.MeetingsConsentReminder;
        p.ShowBarWhenIdle = Bool(o, "showBarWhenIdle") ?? p.ShowBarWhenIdle;
        p.HistoryRetentionDays = Int(o, "historyRetentionDays") ?? p.HistoryRetentionDays;
        p.LaunchAtLogin = Bool(o, "launchAtLogin") ?? p.LaunchAtLogin;
        p.CheckForUpdates = Bool(o, "checkForUpdates") ?? p.CheckForUpdates;
        p.DebugLogging = Bool(o, "debugLogging") ?? p.DebugLogging;
        p.Onboarded = Bool(o, "onboarded") ?? p.Onboarded;
        return p;
    }
}

/// <summary>Persists <see cref="Preferences"/> as settings.json in the data folder (atomic writes).</summary>
public sealed class PreferenceStore(string folder)
{
    public string Path { get; } = System.IO.Path.Combine(folder, "settings.json");

    public Preferences Load()
    {
        try
        {
            return File.Exists(Path) ? Preferences.FromJson(File.ReadAllText(Path)) : new Preferences();
        }
        catch (IOException) { return new Preferences(); }
        catch (UnauthorizedAccessException) { return new Preferences(); }
    }

    public void Save(Preferences prefs) => AtomicFile.WriteAllText(Path, prefs.ToJson());
}

/// <summary>Languages offered in Settings. Whisper supports about 100; auto-detect covers the rest.</summary>
public static class Languages
{
    /// <summary>Whisper reports detected languages by English name ("Telugu"); this maps them to codes.</summary>
    public static string? CodeForWhisperName(string name)
    {
        var n = name.ToLowerInvariant();
        if (n == "tagalog") return "tl";
        foreach (var (code, display) in Options)
            if (display.ToLowerInvariant() == n) return code;
        return null;
    }

    /// <summary>The name shown for a language code, or the code when the language isn't in the list.</summary>
    public static string DisplayName(string code)
    {
        foreach (var (c, name) in Options)
            if (c == code) return name;
        return code;
    }

    public static readonly (string Code, string Name)[] Options =
    [
        ("en", "English"), ("te", "Telugu"), ("hi", "Hindi"), ("ta", "Tamil"),
        ("kn", "Kannada"), ("ml", "Malayalam"), ("mr", "Marathi"), ("bn", "Bengali"), ("gu", "Gujarati"),
        ("pa", "Punjabi"), ("ur", "Urdu"), ("es", "Spanish"), ("fr", "French"), ("de", "German"),
        ("it", "Italian"), ("pt", "Portuguese"), ("nl", "Dutch"), ("ru", "Russian"), ("uk", "Ukrainian"),
        ("pl", "Polish"), ("tr", "Turkish"), ("ar", "Arabic"), ("fa", "Persian"), ("he", "Hebrew"),
        ("zh", "Chinese"), ("ja", "Japanese"), ("ko", "Korean"), ("vi", "Vietnamese"), ("th", "Thai"),
        ("id", "Indonesian"), ("ms", "Malay"), ("tl", "Filipino (Tagalog)"), ("sv", "Swedish"), ("no", "Norwegian"),
        ("da", "Danish"), ("fi", "Finnish"), ("el", "Greek"), ("cs", "Czech"), ("ro", "Romanian"), ("hu", "Hungarian"),
    ];
}
