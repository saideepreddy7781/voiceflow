import Foundation

/// Writing systems, for checking that a transcript is in a script the speaker uses.
public enum Script: String, Sendable {
    case latin, telugu, devanagari, tamil, kannada, malayalam, bengali, gujarati, gurmukhi, arabic, cyrillic, greek,
         hebrew, thai, han, kana, hangul, other

    static func of(_ scalar: Unicode.Scalar) -> Script? {
        let v = scalar.value
        switch v {
        case 0x41...0x5A, 0x61...0x7A, 0xC0...0x24F: return .latin
        case 0x0C00...0x0C7F: return .telugu
        case 0x0900...0x097F: return .devanagari
        case 0x0B80...0x0BFF: return .tamil
        case 0x0C80...0x0CFF: return .kannada
        case 0x0D00...0x0D7F: return .malayalam
        case 0x0980...0x09FF: return .bengali
        case 0x0A80...0x0AFF: return .gujarati
        case 0x0A00...0x0A7F: return .gurmukhi
        case 0x0600...0x06FF, 0x0750...0x077F: return .arabic
        case 0x0400...0x04FF: return .cyrillic
        case 0x0370...0x03FF: return .greek
        case 0x0590...0x05FF: return .hebrew
        case 0x0E00...0x0E7F: return .thai
        case 0x4E00...0x9FFF: return .han
        case 0x3040...0x30FF: return .kana
        case 0xAC00...0xD7AF: return .hangul
        default: return nil   // digits, punctuation, spaces
        }
    }

    /// The script most letters in `text` use, or nil for text without letters.
    public static func dominant(in text: String) -> Script? {
        var counts: [Script: Int] = [:]
        for scalar in text.unicodeScalars { if let s = of(scalar) { counts[s, default: 0] += 1 } }
        return counts.max { $0.value < $1.value }?.key
    }

    /// Scripts a language is normally written in.
    public static func scripts(for language: String) -> Set<Script> {
        switch language {
        case "te": [.telugu]
        case "hi", "mr", "ne", "sa": [.devanagari]
        case "ta": [.tamil]
        case "kn": [.kannada]
        case "ml": [.malayalam]
        case "bn", "as": [.bengali]
        case "gu": [.gujarati]
        case "pa": [.gurmukhi]
        case "ur", "ar", "fa", "ps": [.arabic]
        case "ru", "uk", "bg", "sr", "kk": [.cyrillic]
        case "el": [.greek]
        case "he", "yi": [.hebrew]
        case "th": [.thai]
        case "zh": [.han]
        case "ja": [.kana, .han]
        case "ko": [.hangul]
        default: [.latin]
        }
    }
}

/// Picks the right transcription when you speak more than one language.
///
/// Whisper's auto-detect often mislabels Indian languages: in testing it heard Telugu as Hindi or Tamil
/// (writing Devanagari or Tamil script) and, with some voices, as English spelled phonetically. Forcing
/// Telugu instead ruins English speech. So both are run in parallel and compared:
/// 1. A result in a script or language you don't speak is rejected.
/// 2. When both are plausible, the one Whisper is more confident in wins, by a margin.
public enum LanguageResolver {
    /// How much more confident (average log-probability) the forced language must be to replace auto-detect.
    public static let margin = 0.1

    /// Auto-detect is doubtful: wrong script or language, unknown language, or low confidence.
    /// English speech scored −0.06 to −0.22 in testing; −0.45 leaves room for accents while still
    /// catching garbled results.
    public static func needsSecondOpinion(_ auto: DetailedTranscript, languages: [String]) -> Bool {
        let allowedScripts = languages.reduce(into: Set<Script>()) { $0.formUnion(Script.scripts(for: $1)) }
        if let script = Script.dominant(in: auto.text), !allowedScripts.contains(script) { return true }
        guard let language = auto.language, languages.contains(language) else { return true }
        return (auto.confidence ?? 0) < -0.45
    }

    public static func choose(auto: DetailedTranscript, forced: DetailedTranscript?, forcedLanguage: String,
                              languages: [String]) -> DetailedTranscript {
        guard let forced else { return auto }
        if auto.text.isEmpty { return forced }
        if forced.text.isEmpty { return auto }
        let allowedScripts = languages.reduce(into: Set<Script>()) { $0.formUnion(Script.scripts(for: $1)) }
        let autoScriptOK = Script.dominant(in: auto.text).map(allowedScripts.contains) ?? true
        let autoLanguageOK = auto.language.map(languages.contains) ?? false
        if !autoScriptOK || !autoLanguageOK { return forced }
        if auto.language == forcedLanguage { return auto }
        guard let a = auto.confidence, let f = forced.confidence else { return auto }
        return f > a + margin ? forced : auto
    }
}
