import Foundation

/// What is true at the moment text is about to be typed.
public struct InsertionFacts: Equatable, Sendable {
    public var contextIsSecure = false
    public var contextIsExcluded = false
    /// The field or app focused right now is a password field, an excluded app, or macOS secure input.
    public var focusIsProtected = false
    /// The app the dictation started in. 0 when it was never known.
    public var contextPID: pid_t = 0
    /// The app that is frontmost now, if any.
    public var frontmostPID: pid_t?
    /// True only when both the old and new focused window are known and they differ.
    public var windowChanged = false
    /// Accessibility says the focused element takes text. False is common and means little in Electron apps.
    public var elementIsEditable = false
    /// Apps where Accessibility writes are unreliable.
    public var pasteFirst = false
    public var textIsEmpty = false

    public init() {}
}

/// The path text will take. Separate from `TextInserter` so the rules can be tested without a running app.
public enum InsertionPlan: Equatable, Sendable {
    case block
    case copyFocusChanged
    case copyNoField
    /// Write through Accessibility, then paste if the write doesn't take.
    case writeThenPaste
    case paste
}

public enum PasteVerification: Equatable, Sendable {
    case landed
    /// The field is readable and did not change.
    case notLanded
    /// The field can't be read, or changed in a way that says nothing either way.
    case unknown
}

public enum InsertionPolicy {
    public static func plan(_ facts: InsertionFacts) -> InsertionPlan {
        // Safety rules come first and nothing below can override them.
        if facts.contextIsSecure || facts.contextIsExcluded || facts.focusIsProtected { return .block }
        if facts.textIsEmpty { return .copyNoField }
        if facts.contextPID != 0, facts.frontmostPID != facts.contextPID { return .copyFocusChanged }
        guard facts.frontmostPID != nil else { return .copyNoField }
        if facts.windowChanged { return .copyFocusChanged }
        // Same app, nothing protected: type even when Accessibility can't name the field. Electron and web
        // apps often answer with nothing for a field that takes a paste perfectly well.
        return facts.elementIsEditable && !facts.pasteFirst ? .writeThenPaste : .paste
    }

    /// Whether a paste is visible in the field. Whitespace is ignored because editors reflow what they receive.
    public static func verifyPaste(before: String?, after: String?, inserted: String) -> PasteVerification {
        // Monaco and many web editors report an empty value whatever they hold: that is not evidence.
        guard let after, !after.isEmpty else { return .unknown }
        if let before, before == after { return .notLanded }
        return squeezed(after).contains(squeezed(inserted).prefix(60)) ? .landed : .unknown
    }

    private static func squeezed(_ text: String) -> String { text.filter { !$0.isWhitespace } }
}
