import Foundation

public enum Hotkey: String, CaseIterable, Codable, Sendable, Identifiable {
    case fn
    case rightOption
    case rightCommand

    public var id: String { rawValue }

    public var displayName: String {
        switch self {
        case .fn: "Fn (🌐)"
        case .rightOption: "Right Option"
        case .rightCommand: "Right Command"
        }
    }

    public var shortName: String {
        switch self {
        case .fn: "Fn"
        case .rightOption: "Right ⌥"
        case .rightCommand: "Right ⌘"
        }
    }
}

public enum EditorProvider: String, CaseIterable, Codable, Sendable, Identifiable {
    case groq
    case gemini

    public var id: String { rawValue }
}

/// How Telugu dictation is written.
public enum TeluguScript: String, CaseIterable, Codable, Sendable, Identifiable {
    /// నేను రేపు ఆఫీస్ కి వస్తాను
    case telugu
    /// నేను రేపు office కి వస్తాను
    case mixed
    /// nenu repu office ki vastanu
    case roman

    public var id: String { rawValue }

    public var displayName: String {
        switch self {
        case .telugu: "Telugu script"
        case .mixed: "Telugu + English words"
        case .roman: "Telish (English letters)"
        }
    }

    public var example: String {
        switch self {
        case .telugu: "నేను రేపు ఆఫీస్ కి వస్తాను, మీటింగ్ పది గంటలకి ఉంది."
        case .mixed: "నేను రేపు office కి వస్తాను, meeting 10 గంటలకి ఉంది."
        case .roman: "Nenu repu office ki vastanu, meeting 10 gantalaki undi."
        }
    }

    /// The word used in the cleanup prompt's <telugu> tag.
    public var promptName: String { rawValue }
}

/// How Hindi dictation and meeting transcripts are written.
public enum HindiScript: String, CaseIterable, Codable, Sendable, Identifiable {
    /// मैं कल ऑफिस आऊँगा
    case devanagari
    /// मैं कल office आऊँगा
    case mixed
    /// main kal office aaunga
    case hinglish

    public var id: String { rawValue }

    public var displayName: String {
        switch self {
        case .devanagari: "Devanagari"
        case .mixed: "Hindi + English words"
        case .hinglish: "Hinglish (English letters)"
        }
    }

    public var example: String {
        switch self {
        case .devanagari: "मैं कल ऑफिस आऊँगा, मीटिंग दस बजे है।"
        case .mixed: "मैं कल office आऊँगा, meeting 10 बजे है।"
        case .hinglish: "Main kal office aaunga, meeting 10 baje hai."
        }
    }

    /// The word used in the cleanup prompt's <hindi> tag.
    public var promptName: String { rawValue }
}

public enum CleanupLevel: String, CaseIterable, Codable, Sendable, Identifiable {
    case none
    case light
    case medium

    public var id: String { rawValue }

    public var displayName: String {
        switch self {
        case .none: "None"
        case .light: "Light"
        case .medium: "Medium"
        }
    }

    public var summary: String {
        switch self {
        case .none: "Exactly what you said. No AI editing."
        case .light: "Removes filler words, applies your corrections, fixes grammar and punctuation."
        case .medium: "Also rephrases for clarity and brevity, keeping your meaning."
        }
    }

    public var example: (said: String, typed: String) {
        let said = "um so I think we should uh push the launch to tuesday no actually wednesday because the the payment stuff isn't ready yet"
        switch self {
        case .none: return (said, said)
        case .light: return (said, "I think we should push the launch to Wednesday because the payment stuff isn't ready yet.")
        case .medium: return (said, "Let's push the launch to Wednesday; payments aren't ready yet.")
        }
    }
}

/// Typed user preferences. Decoding tolerates missing keys, so adding a field never resets
/// the user's other settings.
public struct Preferences: Equatable, Codable, Sendable {
    // Shortcuts
    public var hotkey: Hotkey = .fn

    // Models
    public var editorProvider: EditorProvider = .groq
    public var transcriptionModel: String = Groq.transcriptionModels[0]
    public var cleanupModel: String = Groq.textModels[0]
    public var commandModel: String = "openai/gpt-oss-120b"
    public var geminiModel: String = ""

    // Dictation
    /// Languages you speak (Whisper codes). One language is passed to Whisper; none or several means auto-detect,
    /// checked against this list (see `LanguageResolver`).
    public var languages: [String] = ["en"]
    public var teluguScript: TeluguScript = .mixed
    public var hindiScript: HindiScript = .mixed
    public var cleanupLevel: CleanupLevel = .light
    public var commandModeEnabled: Bool = true
    public var styles: [StyleCategory: WritingStyle] = [:]
    public var autoLearnWords: Bool = true

    // Audio
    /// Input device UID; empty means the system default.
    public var inputDeviceUID: String = ""
    /// Whisper mode: boosts quiet speech before transcription.
    public var whisperMode: Bool = true
    public var muteAudioWhileDictating: Bool = true
    public var playSounds: Bool = true

    // Notetaker
    /// Offer to take notes when a meeting app starts using the microphone.
    public var meetingsDetect: Bool = true
    /// Record the other people (system audio). Off means your microphone only.
    public var meetingsSystemAudio: Bool = true
    /// Folder where each meeting's notes are saved as Markdown (e.g. in iCloud Drive). Empty: don't save.
    public var meetingsFolder: String = ""
    public var meetingsNotesLanguage: MeetingNotesWriter.NotesLanguage = .english
    /// Use the calendar event happening now for the meeting title and attendee names.
    public var meetingsUseCalendar: Bool = false
    public var meetingsConsentReminder: Bool = true

    // System
    /// "Show Flow Bar at all times".
    public var showBarWhenIdle: Bool = false
    public var showInDock: Bool = true
    /// 0 keeps no history; -1 keeps it forever.
    public var historyRetentionDays: Int = 30
    public var launchAtLogin: Bool = false
    public var debugLogging: Bool = false
    public var onboarded: Bool = false

    public init() {}

    public func style(for category: StyleCategory) -> WritingStyle {
        let chosen = styles[category] ?? category.defaultStyle
        return category.options.contains(chosen) ? chosen : category.defaultStyle
    }

    /// The `language` sent to Whisper when there's no choosing to do: your one language, or nil.
    public var whisperLanguage: String? {
        languages.count == 1 ? languages[0] : nil
    }

    /// Who might be speaking and what they might speak, for the resolver.
    public func languagePlan(_ speaker: LanguagePlan.Speaker = .user) -> LanguagePlan {
        LanguagePlan(languages: languages, speaker: speaker)
    }

    public var speaksTelugu: Bool { languages.contains("te") }
    public var speaksHindi: Bool { languages.contains("hi") }

    private enum CodingKeys: String, CodingKey {
        case hotkey, editorProvider, transcriptionModel, cleanupModel, commandModel, geminiModel
        case languages, teluguScript, hindiScript, cleanupLevel, commandModeEnabled, styles, autoLearnWords
        case inputDeviceUID, whisperMode, muteAudioWhileDictating, playSounds
        case meetingsDetect, meetingsSystemAudio, meetingsFolder, meetingsNotesLanguage, meetingsUseCalendar, meetingsConsentReminder
        case showBarWhenIdle, showInDock, historyRetentionDays, launchAtLogin, debugLogging, onboarded
        // Older versions
        case language, cleanupEnabled
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = Preferences()
        func value<T: Decodable>(_ key: CodingKeys, _ fallback: T) -> T {
            (try? c.decodeIfPresent(T.self, forKey: key)) ?? fallback
        }
        hotkey = value(.hotkey, d.hotkey)
        editorProvider = value(.editorProvider, d.editorProvider)
        transcriptionModel = value(.transcriptionModel, d.transcriptionModel)
        cleanupModel = value(.cleanupModel, d.cleanupModel)
        commandModel = value(.commandModel, d.commandModel)
        geminiModel = value(.geminiModel, d.geminiModel)
        if let langs = try? c.decodeIfPresent([String].self, forKey: .languages) {
            languages = langs
        } else if let old = try? c.decodeIfPresent(String.self, forKey: .language) {
            languages = old.isEmpty ? [] : [old]
        }
        teluguScript = value(.teluguScript, d.teluguScript)
        hindiScript = value(.hindiScript, d.hindiScript)
        if let level = try? c.decodeIfPresent(CleanupLevel.self, forKey: .cleanupLevel) {
            cleanupLevel = level
        } else if (try? c.decodeIfPresent(Bool.self, forKey: .cleanupEnabled)) == false {
            cleanupLevel = .none
        }
        commandModeEnabled = value(.commandModeEnabled, d.commandModeEnabled)
        styles = value(.styles, d.styles)
        autoLearnWords = value(.autoLearnWords, d.autoLearnWords)
        inputDeviceUID = value(.inputDeviceUID, d.inputDeviceUID)
        whisperMode = value(.whisperMode, d.whisperMode)
        muteAudioWhileDictating = value(.muteAudioWhileDictating, d.muteAudioWhileDictating)
        playSounds = value(.playSounds, d.playSounds)
        meetingsDetect = value(.meetingsDetect, d.meetingsDetect)
        meetingsSystemAudio = value(.meetingsSystemAudio, d.meetingsSystemAudio)
        meetingsFolder = value(.meetingsFolder, d.meetingsFolder)
        meetingsNotesLanguage = value(.meetingsNotesLanguage, d.meetingsNotesLanguage)
        meetingsUseCalendar = value(.meetingsUseCalendar, d.meetingsUseCalendar)
        meetingsConsentReminder = value(.meetingsConsentReminder, d.meetingsConsentReminder)
        showBarWhenIdle = value(.showBarWhenIdle, d.showBarWhenIdle)
        showInDock = value(.showInDock, d.showInDock)
        historyRetentionDays = value(.historyRetentionDays, d.historyRetentionDays)
        launchAtLogin = value(.launchAtLogin, d.launchAtLogin)
        debugLogging = value(.debugLogging, d.debugLogging)
        onboarded = value(.onboarded, d.onboarded)
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(hotkey, forKey: .hotkey)
        try c.encode(editorProvider, forKey: .editorProvider)
        try c.encode(transcriptionModel, forKey: .transcriptionModel)
        try c.encode(cleanupModel, forKey: .cleanupModel)
        try c.encode(commandModel, forKey: .commandModel)
        try c.encode(geminiModel, forKey: .geminiModel)
        try c.encode(languages, forKey: .languages)
        try c.encode(teluguScript, forKey: .teluguScript)
        try c.encode(hindiScript, forKey: .hindiScript)
        try c.encode(cleanupLevel, forKey: .cleanupLevel)
        try c.encode(commandModeEnabled, forKey: .commandModeEnabled)
        try c.encode(styles, forKey: .styles)
        try c.encode(autoLearnWords, forKey: .autoLearnWords)
        try c.encode(inputDeviceUID, forKey: .inputDeviceUID)
        try c.encode(whisperMode, forKey: .whisperMode)
        try c.encode(muteAudioWhileDictating, forKey: .muteAudioWhileDictating)
        try c.encode(playSounds, forKey: .playSounds)
        try c.encode(meetingsDetect, forKey: .meetingsDetect)
        try c.encode(meetingsSystemAudio, forKey: .meetingsSystemAudio)
        try c.encode(meetingsFolder, forKey: .meetingsFolder)
        try c.encode(meetingsNotesLanguage, forKey: .meetingsNotesLanguage)
        try c.encode(meetingsUseCalendar, forKey: .meetingsUseCalendar)
        try c.encode(meetingsConsentReminder, forKey: .meetingsConsentReminder)
        try c.encode(showBarWhenIdle, forKey: .showBarWhenIdle)
        try c.encode(showInDock, forKey: .showInDock)
        try c.encode(historyRetentionDays, forKey: .historyRetentionDays)
        try c.encode(launchAtLogin, forKey: .launchAtLogin)
        try c.encode(debugLogging, forKey: .debugLogging)
        try c.encode(onboarded, forKey: .onboarded)
    }
}

/// Persists `Preferences` as one JSON value in UserDefaults.
public struct PreferenceStore: @unchecked Sendable {
    public static let defaultsKey = "preferences.v1"
    private let defaults: UserDefaults

    public init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    public func load() -> Preferences {
        guard let data = defaults.data(forKey: Self.defaultsKey),
              let prefs = try? JSONDecoder().decode(Preferences.self, from: data)
        else { return Preferences() }
        return prefs
    }

    public func save(_ prefs: Preferences) {
        guard let data = try? JSONEncoder().encode(prefs) else { return }
        defaults.set(data, forKey: Self.defaultsKey)
    }
}

/// Languages offered in Settings. Whisper supports about 100; these are the common ones,
/// and "Auto-detect" covers the rest.
public enum Languages {
    /// Whisper reports detected languages by English name ("Telugu"); this maps them to codes.
    public static func code(forWhisperName name: String) -> String? {
        let n = name.lowercased()
        if n == "tagalog" { return "tl" }
        return options.first { $0.name.lowercased() == n }?.code
    }

    public static let options: [(code: String, name: String)] = [
        ("en", "English"), ("te", "Telugu"), ("hi", "Hindi"), ("ta", "Tamil"),
        ("kn", "Kannada"), ("ml", "Malayalam"), ("mr", "Marathi"), ("bn", "Bengali"), ("gu", "Gujarati"),
        ("pa", "Punjabi"), ("ur", "Urdu"), ("es", "Spanish"), ("fr", "French"), ("de", "German"),
        ("it", "Italian"), ("pt", "Portuguese"), ("nl", "Dutch"), ("ru", "Russian"), ("uk", "Ukrainian"),
        ("pl", "Polish"), ("tr", "Turkish"), ("ar", "Arabic"), ("fa", "Persian"), ("he", "Hebrew"),
        ("zh", "Chinese"), ("ja", "Japanese"), ("ko", "Korean"), ("vi", "Vietnamese"), ("th", "Thai"),
        ("id", "Indonesian"), ("ms", "Malay"), ("tl", "Filipino (Tagalog)"), ("sv", "Swedish"), ("no", "Norwegian"),
        ("da", "Danish"), ("fi", "Finnish"), ("el", "Greek"), ("cs", "Czech"), ("ro", "Romanian"), ("hu", "Hungarian"),
    ]
}
