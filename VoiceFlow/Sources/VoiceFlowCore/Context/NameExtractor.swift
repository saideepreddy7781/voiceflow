import Foundation

/// Finds names and terms on screen so they can be spelled right.
/// Local rules only, so it adds no latency.
public enum NameExtractor {
    static let common: Set<String> = [
        "The", "This", "That", "These", "Those", "There", "Then", "They", "Their", "We", "You", "Your", "He", "She",
        "It", "Its", "A", "An", "And", "But", "Or", "So", "If", "In", "On", "At", "To", "For", "From", "With", "Of",
        "As", "By", "Is", "Are", "Was", "Were", "Be", "Hi", "Hello", "Hey", "Thanks", "Thank", "Dear", "Regards",
        "Best", "Please", "Yes", "No", "Ok", "Okay", "What", "When", "Where", "Why", "How", "Who", "Which", "Can",
        "Could", "Would", "Should", "Will", "Do", "Does", "Did", "Have", "Has", "Had", "Not", "Just", "Also",
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday", "January", "February",
        "March", "April", "May", "June", "July", "August", "September", "October", "November", "December",
        "Re", "Fwd", "Sent", "Subject", "Cc", "Bcc", "Today", "Tomorrow", "Yesterday", "Let", "Here", "My", "Our",
    ]

    /// Up to `limit` distinct names and terms, most frequent first.
    public static func extract(from texts: [String], limit: Int = 30) -> [String] {
        var counts: [String: Int] = [:]
        var order: [String] = []
        let pattern = try! NSRegularExpression(pattern: #"@[A-Za-z0-9_.-]{2,}|\b[A-Z][a-z]*[A-Z][A-Za-z0-9]*\b|\b[A-Z][a-z]{2,}\b|\b[A-Z]{2,6}\b"#)
        for text in texts {
            let ns = text as NSString
            for match in pattern.matches(in: text, range: NSRange(location: 0, length: ns.length)) {
                let word = ns.substring(with: match.range)
                guard !common.contains(word) else { continue }
                if counts[word] == nil { order.append(word) }
                counts[word, default: 0] += 1
            }
        }
        return order.sorted { (counts[$0]!, -order.firstIndex(of: $0)!) > (counts[$1]!, -order.firstIndex(of: $1)!) }
            .prefix(limit).map { $0 }
    }

    /// Whisper's `prompt` is limited to 224 tokens; this keeps it to about 180 by character count.
    public static func whisperPrompt(dictionary: [String], names: [String], maxCharacters: Int = 650) -> String? {
        var seen = Set<String>()
        var parts: [String] = []
        var length = 0
        for term in dictionary + names where !term.isEmpty && seen.insert(term.lowercased()).inserted {
            if length + term.count + 2 > maxCharacters { break }
            parts.append(term)
            length += term.count + 2
        }
        return parts.isEmpty ? nil : parts.joined(separator: ", ")
    }
}
