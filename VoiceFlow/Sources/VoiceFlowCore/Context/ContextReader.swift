import AppKit
import ApplicationServices
import Foundation

/// What VoiceFlow knows about the place the user is typing, read through Accessibility.
public struct TypingContext: Sendable {
    public var appName: String = ""
    public var bundleID: String = ""
    public var pid: pid_t = 0
    public var windowTitle: String = ""
    /// File URL of the focused document, when the app exposes one (AXDocument).
    public var documentPath: String?
    public var role: String = ""
    public var isSecure = false
    public var isEditable = false
    /// Up to 2,000 characters around the cursor.
    public var excerpt: String = ""
    /// Up to 120 characters immediately before the cursor.
    public var beforeCursor: String = ""
    public var selectedText: String = ""
    /// The whole field value, capped at 20,000 characters (Command Mode).
    public var fullText: String = ""

    public init() {}

    public var hasSelection: Bool { !selectedText.isEmpty }
}

/// Reads `TypingContext` from the focused element. Each AX call times out after 250 ms so a hung
/// app cannot stall dictation. Call off the main thread.
public enum ContextReader {
    private static let timeout: Float = 0.25
    private static let electronApps = ["com.tinyspeck.slackmacgap", "com.microsoft.VSCode", "com.todesktop.230313mzl4w4u92",
                                        "com.exafunction.windsurf", "com.hnc.Discord", "notion.id", "com.figma.Desktop",
                                        "net.whatsapp.WhatsApp", "com.microsoft.teams2", "md.obsidian", "com.linear"]

    public static func read() -> TypingContext {
        var ctx = TypingContext()
        guard let app = NSWorkspace.shared.frontmostApplication else { return ctx }
        ctx.appName = app.localizedName ?? ""
        ctx.bundleID = app.bundleIdentifier ?? ""
        ctx.pid = app.processIdentifier
        // Password managers: never read anything, never type.
        if OutputSafety.isExcluded(bundleID: ctx.bundleID) {
            ctx.isSecure = true
            return ctx
        }

        let appElement = AXUIElementCreateApplication(app.processIdentifier)
        AXUIElementSetMessagingTimeout(appElement, timeout)
        // Electron and Chromium apps only expose their text to Accessibility when asked.
        if electronApps.contains(ctx.bundleID) || ctx.bundleID.hasPrefix("com.google.Chrome") || ctx.bundleID.hasPrefix("com.brave") {
            AXUIElementSetAttributeValue(appElement, "AXManualAccessibility" as CFString, kCFBooleanTrue)
        }

        if let window: AXUIElement = attribute(appElement, kAXFocusedWindowAttribute) {
            ctx.windowTitle = attribute(window, kAXTitleAttribute) ?? ""
            if let doc: String = attribute(window, kAXDocumentAttribute) { ctx.documentPath = URL(string: doc)?.path ?? doc }
        }

        guard let element = focusedElement(appElement) else { return ctx }
        AXUIElementSetMessagingTimeout(element, timeout)
        ctx.role = attribute(element, kAXRoleAttribute) ?? ""
        let subrole: String = attribute(element, kAXSubroleAttribute) ?? ""
        ctx.isSecure = subrole == (kAXSecureTextFieldSubrole as String)
        if ctx.isSecure { return ctx }

        ctx.isEditable = isSettable(element, kAXValueAttribute) || isSettable(element, kAXSelectedTextAttribute)
            || ["AXTextField", "AXTextArea", "AXComboBox", "AXSearchField"].contains(ctx.role)

        let value: String = attribute(element, kAXValueAttribute) ?? ""
        ctx.selectedText = attribute(element, kAXSelectedTextAttribute) ?? ""
        ctx.fullText = String(value.prefix(20_000))

        if let range = selectedRange(element), range.location >= 0, range.location <= (value as NSString).length {
            let ns = value as NSString
            let start = max(0, range.location - 1_500)
            ctx.excerpt = ns.substring(with: NSRange(location: start, length: min(ns.length - start, 2_000)))
            let beforeStart = max(0, range.location - 120)
            ctx.beforeCursor = ns.substring(with: NSRange(location: beforeStart, length: range.location - beforeStart))
        } else {
            ctx.excerpt = String(value.suffix(2_000))
        }
        return ctx
    }

    /// Title of the frontmost app's focused window (meeting detection in browsers).
    public static func frontWindowTitle() -> String? {
        guard let app = NSWorkspace.shared.frontmostApplication else { return nil }
        let appElement = AXUIElementCreateApplication(app.processIdentifier)
        AXUIElementSetMessagingTimeout(appElement, timeout)
        guard let window: AXUIElement = attribute(appElement, kAXFocusedWindowAttribute) else { return nil }
        return attribute(window, kAXTitleAttribute)
    }

    /// The focused element itself, for writing text and re-reading it later.
    public static func focusedElement() -> AXUIElement? {
        guard let app = NSWorkspace.shared.frontmostApplication else { return nil }
        let appElement = AXUIElementCreateApplication(app.processIdentifier)
        AXUIElementSetMessagingTimeout(appElement, timeout)
        guard let element = focusedElement(appElement) else { return nil }
        AXUIElementSetMessagingTimeout(element, timeout)
        return element
    }

    static func focusedElement(_ appElement: AXUIElement) -> AXUIElement? {
        attribute(appElement, kAXFocusedUIElementAttribute)
    }

    static func attribute<T>(_ element: AXUIElement, _ name: String) -> T? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, name as CFString, &value) == .success, let value else { return nil }
        return value as? T
    }

    static func isSettable(_ element: AXUIElement, _ name: String) -> Bool {
        var settable = DarwinBoolean(false)
        return AXUIElementIsAttributeSettable(element, name as CFString, &settable) == .success && settable.boolValue
    }

    static func selectedRange(_ element: AXUIElement) -> CFRange? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, kAXSelectedTextRangeAttribute as CFString, &value) == .success,
              let value, CFGetTypeID(value) == AXValueGetTypeID() else { return nil }
        var range = CFRange()
        guard AXValueGetValue(value as! AXValue, .cfRange, &range) else { return nil }
        return range
    }

    /// Reads the current value of an element (auto-learn re-reads the field after insertion).
    /// Returns nil for secure (password) fields.
    public static func value(of element: AXUIElement) -> String? {
        if isSecure(element) { return nil }
        return attribute(element, kAXValueAttribute)
    }

    public static func isSecure(_ element: AXUIElement) -> Bool {
        let subrole: String = attribute(element, kAXSubroleAttribute) ?? ""
        return subrole == (kAXSecureTextFieldSubrole as String)
    }
}
