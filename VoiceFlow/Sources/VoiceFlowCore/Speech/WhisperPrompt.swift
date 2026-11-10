import Foundation

/// The `prompt` sent with each Whisper request.
///
/// A prompt is the transcript so far: Whisper imitates its script and spelling. Seeding a forced Telugu or Hindi
/// request with code-mixed sentences made Whisper write English words in English letters (or the speaker's script,
/// per their writing choice) and reduced errors (see docs/LANGUAGE-PIPELINE.md for the measurements).
/// Seeding an auto-detect request is not safe: English speech came back as Telugu gibberish. Auto-detect
/// requests get only the dictionary terms.
public struct WhisperPrompts: Sendable, Equatable {
    public var terms: [String]
    public var telugu: TeluguScript
    public var hindi: HindiScript

    public init(terms: [String] = [], telugu: TeluguScript = .mixed, hindi: HindiScript = .mixed) {
        self.terms = terms
        self.telugu = telugu
        self.hindi = hindi
    }

    /// The prompt for a request forcing `language`, or nil when there is nothing to send.
    public func text(forLanguage language: String?) -> String? {
        WhisperPrompt.build(seed: language.flatMap(seed(for:)), terms: terms)
    }

    func seed(for language: String) -> String? {
        switch language {
        case "te": telugu == .telugu ? WhisperPrompt.teluguScriptSeed : WhisperPrompt.teluguMixedSeed
        case "hi": hindi == .devanagari ? WhisperPrompt.hindiScriptSeed : WhisperPrompt.hindiMixedSeed
        default: nil
        }
    }
}

public enum WhisperPrompt {
    /// Groq rejects prompts over 896 characters (HTTP 400, measured 2026-11-10).
    public static let maximumCharacters = 896
    /// Whisper reads only 224 tokens and keeps the end. Telugu and Devanagari run about 1.5 tokens per
    /// character, so a character limit alone isn't enough for those scripts.
    static let maximumEstimatedTokens = 200.0

    static let teluguMixedSeed = "నేను రేపు office కి వస్తాను, meeting 10 గంటలకి ఉంది. project report ready చేసి పంపిస్తాను. ఈ రోజు client తో call ఉంది, deadline గురించి discuss చేద్దాం."
    static let teluguScriptSeed = "నేను రేపు ఆఫీస్ కి వస్తాను, మీటింగ్ పది గంటలకి ఉంది. ప్రాజెక్ట్ రిపోర్ట్ రెడీ చేసి పంపిస్తాను. ఈ రోజు క్లయింట్ తో కాల్ ఉంది."
    static let hindiMixedSeed = "मैं कल office आऊँगा, meeting दस बजे है। report ready करके भेज दूँगा। आज client के साथ call है, deadline के बारे में बात करेंगे।"
    static let hindiScriptSeed = "मैं कल ऑफिस आऊँगा, मीटिंग दस बजे है। रिपोर्ट तैयार करके भेज दूँगा। आज क्लाइंट के साथ कॉल है।"

    /// Dictionary terms first, seed last, because Whisper keeps the end of a long prompt.
    /// Terms are dropped from the end of the list when there isn't room.
    static func build(seed: String?, terms: [String]) -> String? {
        var seen = Set<String>()
        var kept: [String] = []
        let seedCost = estimatedTokens(seed ?? "")
        var tokens = seedCost, characters = (seed?.count ?? 0) + 4
        for term in terms where !term.isEmpty && seen.insert(term.lowercased()).inserted {
            let cost = estimatedTokens(term) + 1
            if tokens + cost > maximumEstimatedTokens || characters + term.count + 2 > maximumCharacters { break }
            kept.append(term)
            tokens += cost
            characters += term.count + 2
        }
        let glossary = kept.joined(separator: ", ")
        switch (glossary.isEmpty, seed) {
        case (true, nil): return nil
        case (false, nil): return glossary
        case (true, let seed?): return seed
        case (false, let seed?): return glossary + ". " + seed
        }
    }

    static func estimatedTokens(_ text: String) -> Double {
        var ascii = 0.0, other = 0.0
        for scalar in text.unicodeScalars { if scalar.isASCII { ascii += 1 } else { other += 1 } }
        return ascii / 3.5 + other * 1.5
    }
}
