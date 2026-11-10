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
    /// The focused window when the dictation started, to notice a switch within the same app.
    public var focusedWindow: AXReference?

    public init() {}

    public var hasSelection: Bool { !selectedText.isEmpty }
}

/// An Accessibility element carried between threads. AXUIElement is a thread-safe CF object.
public struct AXReference: @unchecked Sendable {
    public let element: AXUIElement
    public init(_ element: AXUIElement) { self.element = element }

    public func isSameElement(as other: AXReference) -> Bool { CFEqual(element, other.element) }
}

/// The focused field as read just before typing.
public struct FocusSnapshot: @unchecked Sendable {
    public var element: AXUIElement?
    public var window: AXReference?
    public var role = ""
    public var isSecure = false
    public var isEditable = false
    /// Nil when the field's contents can't be read.
    public var value: String?
    public var beforeCursor: String?

    public init() {}
}

/// Reads `TypingContext` from the focused element. Each AX call times out after 250 ms so a hung
/// app cannot stall dictation. Call off the main thread.
public enum ContextReader {
    private static let timeout: Float = 0.25
    private static let electronApps = ["com.tinyspeck.slackmacgap", "com.microsoft.VSCode", "com.todesktop.230313mzl4w4u92",
                                        "com.exafunction.windsurf", "com.hnc.Discord", "notion.id", "com.figma.Desktop",
                                        "net.whatsapp.WhatsApp", "desktop.WhatsApp", "com.microsoft.teams2", "md.obsidian",
                                        "com.linear", "com.microsoft.edgemac", "company.thebrowser.Browser"]
    private static let retryDelay: useconds_t = 120_000

    /// Electron and Chromium apps only expose their text to Accessibility when asked.
    static func needsManualAccessibility(bundleID: String) -> Bool {
        electronApps.contains(bundleID) || bundleID.hasPrefix("com.google.Chrome") || bundleID.hasPrefix("com.brave")
    }

    /// Reads the focused element, retrying when an app that just had Accessibility switched on answers
    /// late. Blocks while waiting: call off the main thread.
    static func awaitFocusedElement(_ appElement: AXUIElement, attempts: Int = 3) -> AXUIElement? {
        var element = focusedElement(appElement)
        var tries = 1
        while tries < attempts, element == nil || (attribute(element!, kAXRoleAttribute) as String?)?.isEmpty != false {
            usleep(retryDelay)
            element = focusedElement(appElement) ?? element
            tries += 1
        }
        return element
    }

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
        if needsManualAccessibility(bundleID: ctx.bundleID) {
            AXUIElementSetAttributeValue(appElement, "AXManualAccessibility" as CFString, kCFBooleanTrue)
        }

        if let window: AXUIElement = attribute(appElement, kAXFocusedWindowAttribute) {
            ctx.focusedWindow = AXReference(window)
            ctx.windowTitle = attribute(window, kAXTitleAttribute) ?? ""
            if let doc: String = attribute(window, kAXDocumentAttribute) { ctx.documentPath = URL(string: doc)?.path ?? doc }
        }

        guard let element = awaitFocusedElement(appElement) else { return ctx }
        AXUIElementSetMessagingTimeout(element, timeout)
        ctx.role = attribute(element, kAXRoleAttribute) ?? ""
        let subrole: String = attribute(element, kAXSubroleAttribute) ?? ""
        ctx.isSecure = subrole == (kAXSecureTextFieldSubrole as String)
        if ctx.isSecure { return ctx }

        ctx.isEditable = isEditable(element, role: ctx.role)

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

    static func isEditable(_ element: AXUIElement, role: String) -> Bool {
        isSettable(element, kAXValueAttribute) || isSettable(element, kAXSelectedTextAttribute)
            || ["AXTextField", "AXTextArea", "AXComboBox", "AXSearchField"].contains(role)
    }

    /// The focused field right now, read just before typing. Blocks while retrying: call off the main thread.
    public static func focusSnapshot() -> FocusSnapshot {
        var snapshot = FocusSnapshot()
        guard let app = NSWorkspace.shared.frontmostApplication else { return snapshot }
        let bundleID = app.bundleIdentifier ?? ""
        // Password managers: never read anything.
        if OutputSafety.isExcluded(bundleID: bundleID) {
            snapshot.isSecure = true
            return snapshot
        }
        let appElement = AXUIElementCreateApplication(app.processIdentifier)
        AXUIElementSetMessagingTimeout(appElement, timeout)
        if needsManualAccessibility(bundleID: bundleID) {
            AXUIElementSetAttributeValue(appElement, "AXManualAccessibility" as CFString, kCFBooleanTrue)
        }
        if let window: AXUIElement = attribute(appElement, kAXFocusedWindowAttribute) { snapshot.window = AXReference(window) }
        guard let element = awaitFocusedElement(appElement) else { return snapshot }
        AXUIElementSetMessagingTimeout(element, timeout)
        snapshot.element = element
        snapshot.role = attribute(element, kAXRoleAttribute) ?? ""
        snapshot.isSecure = isSecure(element)
        if snapshot.isSecure { return snapshot }
        snapshot.isEditable = isEditable(element, role: snapshot.role)
        snapshot.value = value(of: element)
        if let value = snapshot.value, let range = selectedRange(element), range.location >= 0, range.location <= (value as NSString).length {
            let beforeStart = max(0, range.location - 120)
            snapshot.beforeCursor = (value as NSString).substring(with: NSRange(location: beforeStart, length: range.location - beforeStart))
        }
        return snapshot
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
