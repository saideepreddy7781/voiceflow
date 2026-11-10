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

    /// Share of the letters in `text` written in `script` (0 for text without letters).
    public static func share(of script: Script, in text: String) -> Double {
        var total = 0, hits = 0
        for scalar in text.unicodeScalars {
            guard let s = of(scalar) else { continue }
            total += 1
            if s == script { hits += 1 }
        }
        return total == 0 ? 0 : Double(hits) / Double(total)
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

/// What the resolver may assume about who is speaking.
public struct LanguagePlan: Sendable, Equatable {
    public enum Speaker: Sendable, Equatable {
        /// The person using the app: dictation, and your microphone in a meeting. They speak the listed languages.
        case user
        /// The call: other people, who may speak anything.
        case anyone
    }

    public var languages: [String]
    public var speaker: Speaker

    public init(languages: [String], speaker: Speaker = .user) {
        self.languages = languages
        self.speaker = speaker
    }

    public var openWorld: Bool { speaker == .anyone }

    /// The language to force on the first request, or nil to auto-detect. Forcing happens only when one language
    /// is listed and the speaker is the user; with several languages, or other people, Whisper detects it.
    public var forcedFirst: String? {
        languages.count == 1 && !openWorld ? languages[0] : nil
    }

    /// Listed languages other than English, worth a forced second opinion.
    public var opinionCandidates: [String] { languages.filter { $0 != "en" } }
}

/// One forced-language transcription, for comparing with auto-detect.
public struct LanguageOpinion: Sendable, Equatable {
    public var language: String
    public var transcript: DetailedTranscript

    public init(language: String, transcript: DetailedTranscript) {
        self.language = language
        self.transcript = transcript
    }
}

/// Picks the right transcription when several languages are in play.
///
/// Whisper's auto-detect mislabels Indian languages: in testing it heard Telugu as Hindi or Tamil (writing
/// Devanagari or Tamil script, at high confidence) and spelled it by sound in English letters, labelled
/// "English". Forcing the language instead ruins English speech. So the auto result is checked with the rules
/// below, and forced opinions are requested only when it looks wrong.
public enum LanguageResolver {
    /// How much more confident (average log-probability) a forced result must be to replace an accepted auto result.
    public static let margin = 0.1
    /// Below this, auto-detect is doubtful. English speech scored -0.06 to -0.22 in testing; -0.45 leaves room
    /// for accents while still catching garbled results.
    public static let lowConfidence = -0.45
    /// A forced result below this is assumed to be the wrong language or unusable audio.
    public static let forcedFloor = -0.8
    /// Most forced opinions per piece of audio. Each one costs the same audio budget as the first request.
    public static let maxOpinions = 2

    /// Languages Whisper confuses with each other. Telugu heard as Hindi is common; Telugu heard as Spanish isn't.
    static let indic: Set<String> = ["te", "hi", "ta", "kn", "ml", "mr", "bn", "gu", "pa", "ur", "ne", "as", "or", "si", "sa"]

    static func confusable(_ a: String, _ b: String) -> Bool { indic.contains(a) && indic.contains(b) }

    /// Why an auto-detect result can't be taken at face value.
    enum Doubt: Equatable {
        /// The label is believable but the text doesn't read as that language (Telugu spelled by sound as "English").
        case disguised
        /// Outside the user's languages. `real` is whether the text reads as its labelled language, if that can be judged.
        case unlisted(real: Bool?)
        case lowConfidence
    }

    static func doubt(about auto: DetailedTranscript, plan: LanguagePlan) -> Doubt? {
        guard !auto.text.isEmpty else { return nil }
        let allowedScripts = plan.languages.reduce(into: Set<Script>()) { $0.formUnion(Script.scripts(for: $1)) }
        let scriptOK = Script.dominant(in: auto.text).map(allowedScripts.contains) ?? true
        let listed = auto.language.map(plan.languages.contains) ?? false
        let confidence = auto.confidence ?? 0
        if !scriptOK || !listed {
            // Short text with no language label and nothing to compare: other people can't be judged at all.
            guard let language = auto.language else { return plan.openWorld ? nil : .unlisted(real: nil) }
            let real = LanguageEvidence.reads(as: language, text: auto.text)
            if plan.openWorld {
                // Someone else on the call may really speak this. Only a lookalike of a listed language is suspect.
                if real == true { return nil }
                if !plan.languages.contains(where: { confusable($0, language) }) && confidence >= lowConfidence { return nil }
            }
            return .unlisted(real: real)
        }
        if let language = auto.language, LanguageEvidence.reads(as: language, text: auto.text) == false,
           plan.opinionCandidates.contains(where: { $0 != language }) {
            return .disguised
        }
        return confidence < lowConfidence ? .lowConfidence : nil
    }

    public static func needsSecondOpinion(_ auto: DetailedTranscript, plan: LanguagePlan) -> Bool {
        !opinionLanguages(after: auto, plan: plan).isEmpty
    }

    /// Which languages to force for a second opinion on this auto result, most likely first. Empty means accept it.
    public static func opinionLanguages(after auto: DetailedTranscript, plan: LanguagePlan) -> [String] {
        guard doubt(about: auto, plan: plan) != nil else { return [] }
        let others = plan.opinionCandidates.filter { $0 != auto.language || Script.dominant(in: auto.text) == .latin }
        let detected = auto.language
        let ranked = others.enumerated().sorted { a, b in
            let af = detected.map { confusable($0, a.element) } ?? false
            let bf = detected.map { confusable($0, b.element) } ?? false
            return af == bf ? a.offset < b.offset : af
        }.map(\.element)
        return Array(ranked.prefix(maxOpinions))
    }

    /// Whether a transcript looks like Whisper looping rather than speech.
    public static func isLooping(_ t: DetailedTranscript) -> Bool {
        WhisperSegmentFilter.isLooping(t.segments)
    }

    /// Picks between auto-detect and the forced opinions.
    public static func choose(auto: DetailedTranscript, opinions: [LanguageOpinion], plan: LanguagePlan) -> DetailedTranscript {
        let viable = opinions.filter { !$0.transcript.text.isEmpty && !isLooping($0.transcript) }
        let best = viable.max { ($0.transcript.confidence ?? -10) < ($1.transcript.confidence ?? -10) }
        if auto.text.isEmpty { return best?.transcript ?? auto }
        guard let best else { return auto }
        if isLooping(auto) { return best.transcript }
        let a = auto.confidence ?? 0
        let f = best.transcript.confidence ?? forcedFloor
        guard f >= forcedFloor else { return auto }
        switch doubt(about: auto, plan: plan) {
        case nil:
            if auto.language == best.language { return auto }
            return f > a + margin ? best.transcript : auto
        case .disguised:
            // The words already say auto-detect is wrong; only a much worse forced result keeps it.
            return f >= a - 0.5 ? best.transcript : auto
        case .unlisted(let real):
            let tolerance: Double
            if real == true {
                tolerance = 0                           // real Hindi, say, must be beaten outright
            } else {
                tolerance = plan.openWorld ? 0.12 : 0.25
            }
            return f >= a - tolerance ? best.transcript : auto
        case .lowConfidence:
            return f > a + margin ? best.transcript : auto
        }
    }

    /// Accepts a forced result without a second opinion (used for sticky-language chunks).
    public static func forcedResultAcceptable(_ t: DetailedTranscript) -> Bool {
        guard !t.text.isEmpty, !isLooping(t) else { return false }
        return (t.confidence ?? 0) >= lowConfidence
    }
}

/// Remembers a call's or mic's language so long stretches in Telugu don't pay for two uploads per chunk.
///
/// After `stickAfter` chunks that needed a forced opinion to come out right, the next chunks go straight to the
/// forced language. Every `recheckEvery`th chunk still runs auto-detect, so a meeting that moves to English follows.
public struct StreamLanguageTracker: Sendable, Equatable {
    public static let stickAfter = 2
    public static let recheckEvery = 5

    public private(set) var sticky: String?
    private var streakLanguage: String?
    private var streak = 0
    private var sinceRecheck = 0

    public init() {}

    /// The language to force on the next chunk, or nil to run auto-detect.
    public mutating func nextHint() -> String? {
        guard let sticky else { return nil }
        sinceRecheck += 1
        if sinceRecheck >= Self.recheckEvery {
            sinceRecheck = 0
            return nil
        }
        return sticky
    }

    /// Records how a chunk turned out. `viaOpinion`: a forced language won (or was used directly).
    public mutating func observe(language: String?, viaOpinion: Bool, listed: [String]) {
        guard viaOpinion, let language, language != "en", listed.contains(language) else {
            if !viaOpinion, language == "en" || language == nil {
                sticky = nil
                streak = 0
                streakLanguage = nil
            }
            return
        }
        if streakLanguage == language { streak += 1 } else { streakLanguage = language; streak = 1 }
        if streak >= Self.stickAfter { sticky = language }
    }
}
