import Foundation

/// Tests whether Whisper's text reads as the language it claims. Whisper sometimes spells Telugu or Hindi
/// speech by sound in English letters, labels it "English", and reports good confidence. The confidence
/// can't catch that, but the words can: real English is full of common words, and sound-spelled Telugu isn't.
public enum LanguageEvidence {
    /// Below this many words there isn't enough text to judge, so the check says nothing.
    public static let minimumEnglishWords = 8
    public static let minimumHindiWords = 6

    /// Passing score for `englishScore`. Tuned on 14 real meetings in 35-second windows: 5.7% of windows from
    /// English meetings fell below it, and 50% of windows from meetings that contained Telugu (those meetings
    /// also had long English stretches, which correctly pass).
    public static let englishPass = 0.46
    /// How much of a Devanagari text must be Hindi function words to count as Hindi.
    public static let hindiPass = 0.18

    /// Share of the words that are common English minus half the share of words ending in a, i, o or u.
    /// Romanized Telugu and Hindi end in those vowels far more often than English does. Nil when too short.
    public static func englishScore(_ text: String) -> Double? {
        let words = latinWords(text)
        guard words.count >= minimumEnglishWords else { return nil }
        let n = Double(words.count)
        let common = Double(words.filter(commonEnglish.contains).count) / n
        let vowelEnd = Double(words.filter { "aiou".contains($0.last!) }.count) / n
        return common - 0.5 * vowelEnd
    }

    /// Nil when the text is too short to tell or isn't mostly Latin letters.
    public static func readsAsEnglish(_ text: String) -> Bool? {
        guard Script.dominant(in: text) == .latin, let score = englishScore(text) else { return nil }
        return score >= englishPass
    }

    /// Share of Devanagari words that are Hindi function words. "की" and "तो" are left out because Telugu heard as
    /// Hindi produces them too (కి, తో).
    public static func readsAsHindi(_ text: String) -> Bool? {
        guard Script.dominant(in: text) == .devanagari else { return nil }
        let words = text.split { $0.isWhitespace || "।॥,.!?;:\"'()-".contains($0) }.map(String.init)
        guard words.count >= minimumHindiWords else { return nil }
        let hits = words.filter(hindiFunctionWords.contains).count
        return Double(hits) / Double(words.count) >= hindiPass
    }

    /// Whether the text reads as real `language`, when that can be judged at all.
    public static func reads(as language: String, text: String) -> Bool? {
        switch language {
        case "en": readsAsEnglish(text)
        case "hi": readsAsHindi(text)
        default: nil
        }
    }

    private static func latinWords(_ text: String) -> [String] {
        text.lowercased().split { !($0.isASCII && ($0.isLetter || $0 == "'")) }.map(String.init)
    }

    static let hindiFunctionWords: Set<String> = [
        "है", "हैं", "था", "थी", "थे", "हो", "हूँ", "हूं", "मैं", "हम", "तुम", "आप", "वह", "यह", "ये", "वे", "वो", "और", "या",
        "लेकिन", "पर", "में", "से", "को", "ने", "का", "के", "नहीं", "भी", "ही", "कि", "जो", "क्या", "कैसे", "क्यों", "कब", "कहाँ",
        "बहुत", "कुछ", "सब", "अब", "तक", "कर", "करना", "करते", "करता", "करती", "करें", "करेंगे", "करेगा", "करके", "किया",
        "होगा", "होगी", "होता", "होती", "होना", "रहा", "रही", "रहे", "गया", "गई", "गए", "जा", "जाना", "जाएगा", "आया", "दिया", "लिया",
        "ले", "दे", "बात", "ठीक", "अच्छा", "मुझे", "मेरा", "मेरी", "तुम्हारा", "हमारा", "उसका", "उनका", "उसे", "उन्हें", "इसे", "इस", "उस",
    ]

    static let commonEnglish: Set<String> = [
        "the", "a", "an", "is", "are", "was", "were", "be", "been", "am", "i", "you", "he", "she", "it", "we", "they", "this",
        "that", "these", "those", "of", "to", "in", "on", "at", "for", "from", "with", "and", "or", "but", "not", "so", "if", "as",
        "by", "do", "does", "did", "have", "has", "had", "will", "would", "can", "could", "should", "what", "when", "where", "why",
        "how", "who", "which", "there", "here", "my", "your", "our", "their", "his", "her", "me", "him", "them", "us", "just",
        "about", "all", "more", "than", "then", "very", "out", "up", "into", "over", "know", "think", "like", "yes", "no", "okay",
        "ok", "yeah", "right", "good", "well", "get", "got", "go", "going", "want", "need", "see", "look", "make", "made", "take",
        "took", "come", "came", "say", "said", "tell", "told", "talk", "work", "works", "working", "people", "time", "day", "week",
        "month", "year", "today", "tomorrow", "now", "one", "two", "three", "four", "five", "six", "ten", "first", "next", "last",
        "new", "old", "some", "any", "many", "much", "few", "other", "another", "same", "each", "every", "because", "actually",
        "really", "maybe", "also", "still", "only", "even", "back", "again", "after", "before", "while", "during", "through",
        "between", "both", "own", "such", "too", "most", "off", "down", "way", "thing", "things", "something", "anything", "nothing",
        "everything", "someone", "everyone", "anyone", "call", "meeting", "team", "project", "product", "issue", "problem",
        "question", "answer", "data", "system", "file", "files", "app", "user", "users", "code", "test", "build", "plan", "phone",
        "email", "send", "sent", "mail", "share", "screen", "open", "close", "start", "end", "stop", "help", "use", "used", "using",
        "try", "tried", "let", "give", "gave", "put", "keep", "find", "found", "feel", "mean", "means", "sure", "great", "nice",
        "fine", "thanks", "thank", "please", "sorry", "hello", "hi", "hey", "bye", "yep", "nope", "gonna", "wanna", "kind", "sort",
        "lot", "bit", "little", "big", "small", "long", "short", "high", "low", "different", "able", "might", "may", "must", "shall",
        "ever", "never", "always", "often", "already", "yet", "once", "twice", "since", "until", "though", "although", "however",
        "therefore", "whether", "either", "neither", "instead", "rather", "quite", "pretty", "enough", "around", "across", "along",
        "against", "under", "above", "below", "front", "behind", "inside", "outside", "without", "within", "among", "per", "via",
        "done", "doing", "having", "being", "gets", "getting", "saying", "asking", "asked", "looks", "looking", "wants", "wanted",
        "needs", "needed", "thinks", "thought", "knows", "knew", "says", "makes", "making", "takes", "taking", "comes", "coming",
        "goes", "went", "gone", "seems", "seem", "show", "shows", "showing", "change", "changes", "create", "created", "update",
        "updated", "add", "added", "remove", "delete", "check", "checking", "run", "running", "set", "setting", "part", "parts",
        "place", "point", "case", "cases", "number", "numbers", "money", "price", "cost", "pay", "paid", "buy", "company",
        "business", "customer", "client", "service", "market", "sale", "sales", "manager", "boss", "job", "office", "home", "house",
        "school", "college", "class", "student", "students", "course", "exam", "interview", "offer", "letter", "name", "names",
        "word", "words", "line", "lines", "page", "side", "i'm", "i'll", "i've", "i'd", "it's", "don't", "doesn't", "didn't",
        "can't", "won't", "that's", "you're", "we're", "they're", "let's", "what's", "there's", "isn't", "wasn't",
    ]
}
