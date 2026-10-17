import Foundation

/// How dictated text should sound. Chosen per style category on the Style page.
public enum WritingStyle: String, CaseIterable, Codable, Sendable, Identifiable {
    case formal
    case casual
    case veryCasual
    case excited
    /// Not user-selectable: used automatically in code editors and terminals.
    case code

    public var id: String { rawValue }

    /// The word used in the cleanup prompt.
    public var promptName: String {
        switch self {
        case .formal: "formal"
        case .casual: "casual"
        case .veryCasual: "very casual"
        case .excited: "excited"
        case .code: "code"
        }
    }

    public var displayName: String {
        switch self {
        case .formal: "Formal"
        case .casual: "Casual"
        case .veryCasual: "very casual"
        case .excited: "Excited!"
        case .code: "Code"
        }
    }

    public var summary: String {
        switch self {
        case .formal: "Caps + punctuation"
        case .casual: "Caps + less punctuation"
        case .veryCasual: "No caps + less punctuation"
        case .excited: "More exclamation points"
        case .code: "Keeps identifiers and commands exact"
        }
    }

    public func example(for category: StyleCategory) -> String {
        switch (category, self) {
        case (.email, .formal): "Hi Priya,\n\nThanks for sending the proposal. I'll review it and reply by Friday.\n\nBest,\nSai"
        case (.email, .casual): "Hi Priya,\n\nThanks for the proposal. I'll take a look and get back to you by Friday\n\nSai"
        case (.email, .excited): "Hi Priya!\n\nThanks so much for the proposal! I'll review it and get back to you by Friday!\n\nSai"
        case (_, .formal): "Are we still meeting at 6? Let me know if the plan changed."
        case (_, .casual): "Are we still meeting at 6? Let me know if the plan changed"
        case (_, .veryCasual): "are we still meeting at 6 let me know if the plan changed"
        case (_, .excited): "Are we still meeting at 6? Let me know if the plan changed!"
        case (_, .code): "rename userId to accountId in loginHandler"
        }
    }
}

/// The four tabs on the Style page.
public enum StyleCategory: String, CaseIterable, Codable, Sendable, Identifiable {
    case personal
    case work
    case email
    case other

    public var id: String { rawValue }

    public var displayName: String {
        switch self {
        case .personal: "Personal messages"
        case .work: "Work messages"
        case .email: "Email"
        case .other: "Other"
        }
    }

    public var appsHint: String {
        switch self {
        case .personal: "Messages, WhatsApp, Telegram, Signal, Discord"
        case .work: "Slack, Teams, Linear, Jira"
        case .email: "Mail, Outlook, Gmail, Superhuman"
        case .other: "Documents, notes, AI assistants and everything else"
        }
    }

    /// Style cards offered in this tab.
    public var options: [WritingStyle] {
        self == .personal ? [.formal, .casual, .veryCasual] : [.formal, .casual, .excited]
    }

    public var defaultStyle: WritingStyle {
        switch self {
        case .personal, .work: .casual
        case .email, .other: .formal
        }
    }
}

/// The six usage categories shown on Insights.
public enum UsageCategory: String, CaseIterable, Codable, Sendable, Identifiable {
    case personal
    case work
    case email
    case aiPrompts
    case documents
    case other

    public var id: String { rawValue }

    public var displayName: String {
        switch self {
        case .personal: "Personal messages"
        case .work: "Work messages"
        case .email: "Emails"
        case .aiPrompts: "AI prompts"
        case .documents: "Documents"
        case .other: "Other tasks"
        }
    }

    public var styleCategory: StyleCategory {
        switch self {
        case .personal: .personal
        case .work: .work
        case .email: .email
        case .aiPrompts, .documents, .other: .other
        }
    }
}

/// Classifies the app being dictated into.
public enum AppClassifier {
    public static func usage(bundleID: String, windowTitle: String) -> UsageCategory {
        if let known = byBundle[bundleID] { return known }
        let browser = browsers.contains(bundleID) || bundleID.hasPrefix("com.google.Chrome")
        guard browser else { return .other }
        let title = windowTitle.lowercased()
        if ["gmail", "outlook", "inbox", "proton mail", "yahoo mail"].contains(where: title.contains) { return .email }
        if ["whatsapp", "messenger", "telegram", "instagram", "discord", "signal"].contains(where: title.contains) { return .personal }
        if ["slack", "teams", "linear", "jira", "asana", "linkedin", "notion calendar"].contains(where: title.contains) { return .work }
        if ["chatgpt", "claude", "gemini", "perplexity", "copilot", "deepseek"].contains(where: title.contains) { return .aiPrompts }
        if ["google docs", "notion", "confluence", "medium", "substack", "google slides", "dropbox paper"].contains(where: title.contains) { return .documents }
        return .other
    }

    /// Code editors and terminals: identifiers and commands are kept exact regardless of the Style page.
    public static func isCode(bundleID: String) -> Bool {
        codeApps.contains(bundleID) || bundleID.hasPrefix("com.jetbrains.") || OutputSafety.isTerminal(bundleID: bundleID)
    }

    /// Editors where spoken file names become `@file` tags.
    public static func tagsFiles(bundleID: String) -> Bool {
        ["com.todesktop.230313mzl4w4u92", "com.exafunction.windsurf"].contains(bundleID)
    }

    public static func style(bundleID: String, windowTitle: String, preferences: Preferences) -> WritingStyle {
        if isCode(bundleID: bundleID) { return .code }
        return preferences.style(for: usage(bundleID: bundleID, windowTitle: windowTitle).styleCategory)
    }

    static let codeApps: Set<String> = [
        "com.microsoft.VSCode", "com.todesktop.230313mzl4w4u92", "com.exafunction.windsurf", "com.apple.dt.Xcode",
        "dev.zed.Zed", "com.sublimetext.4", "com.panic.Nova",
    ]

    static let browsers: Set<String> = ["com.apple.Safari", "company.thebrowser.Browser", "com.brave.Browser",
                                        "org.mozilla.firefox", "com.microsoft.edgemac", "com.operasoftware.Opera",
                                        "com.vivaldi.Vivaldi", "app.zen-browser.zen"]

    static let byBundle: [String: UsageCategory] = [
        "com.apple.mail": .email, "com.microsoft.Outlook": .email, "com.readdle.smartemail-Mac": .email,
        "com.superhuman.electron": .email, "com.mimestream.Mimestream": .email,
        "com.apple.MobileSMS": .personal, "net.whatsapp.WhatsApp": .personal, "desktop.WhatsApp": .personal,
        "ru.keepcoder.Telegram": .personal, "com.hnc.Discord": .personal, "com.facebook.archon": .personal,
        "org.whispersystems.signal-desktop": .personal,
        "com.tinyspeck.slackmacgap": .work, "com.microsoft.teams2": .work, "us.zoom.xos": .work, "com.linear": .work,
        "com.openai.chat": .aiPrompts, "com.anthropic.claudefordesktop": .aiPrompts, "ai.perplexity.mac": .aiPrompts,
        "com.apple.Notes": .documents, "notion.id": .documents, "com.microsoft.Word": .documents,
        "com.apple.iWork.Pages": .documents, "md.obsidian": .documents, "com.apple.TextEdit": .documents,
        "com.saideep.voiceflow": .documents,
    ]
}
