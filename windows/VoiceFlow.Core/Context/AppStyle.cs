using VoiceFlow.Core.Output;

namespace VoiceFlow.Core;

/// <summary>How dictated text should sound. Chosen per style category on the Style page.</summary>
public enum WritingStyle
{
    Formal,
    Casual,
    VeryCasual,
    Excited,
    /// <summary>Not user-selectable: used automatically in code editors and terminals.</summary>
    Code,
}

/// <summary>The four tabs on the Style page.</summary>
public enum StyleCategory { Personal, Work, Email, Other }

/// <summary>The six usage categories shown on Home.</summary>
public enum UsageCategory { Personal, Work, Email, AiPrompts, Documents, Other }

public static class StyleText
{
    /// <summary>The word used in the cleanup prompt.</summary>
    public static string PromptName(this WritingStyle s) => s switch
    {
        WritingStyle.Formal => "formal",
        WritingStyle.Casual => "casual",
        WritingStyle.VeryCasual => "very casual",
        WritingStyle.Excited => "excited",
        _ => "code",
    };

    public static string DisplayName(this WritingStyle s) => s switch
    {
        WritingStyle.Formal => "Formal",
        WritingStyle.Casual => "Casual",
        WritingStyle.VeryCasual => "very casual",
        WritingStyle.Excited => "Excited!",
        _ => "Code",
    };

    public static string Summary(this WritingStyle s) => s switch
    {
        WritingStyle.Formal => "Caps + punctuation",
        WritingStyle.Casual => "Caps + less punctuation",
        WritingStyle.VeryCasual => "No caps + less punctuation",
        WritingStyle.Excited => "More exclamation points",
        _ => "Keeps identifiers and commands exact",
    };

    public static string Example(this WritingStyle s, StyleCategory category) => (category, s) switch
    {
        (StyleCategory.Email, WritingStyle.Formal) => "Hi Priya,\n\nThanks for sending the proposal. I'll review it and reply by Friday.\n\nBest,\nSai",
        (StyleCategory.Email, WritingStyle.Casual) => "Hi Priya,\n\nThanks for the proposal. I'll take a look and get back to you by Friday\n\nSai",
        (StyleCategory.Email, WritingStyle.Excited) => "Hi Priya!\n\nThanks so much for the proposal! I'll review it and get back to you by Friday!\n\nSai",
        (_, WritingStyle.Formal) => "Are we still meeting at 6? Let me know if the plan changed.",
        (_, WritingStyle.Casual) => "Are we still meeting at 6? Let me know if the plan changed",
        (_, WritingStyle.VeryCasual) => "are we still meeting at 6 let me know if the plan changed",
        (_, WritingStyle.Excited) => "Are we still meeting at 6? Let me know if the plan changed!",
        _ => "rename userId to accountId in loginHandler",
    };

    public static string DisplayName(this StyleCategory c) => c switch
    {
        StyleCategory.Personal => "Personal messages",
        StyleCategory.Work => "Work messages",
        StyleCategory.Email => "Email",
        _ => "Other",
    };

    public static string AppsHint(this StyleCategory c) => c switch
    {
        StyleCategory.Personal => "WhatsApp, Telegram, Signal, Discord",
        StyleCategory.Work => "Slack, Teams, Linear, Jira",
        StyleCategory.Email => "Outlook, Thunderbird, Gmail",
        _ => "Documents, notes, AI assistants and everything else",
    };

    /// <summary>Style cards offered in this tab.</summary>
    public static WritingStyle[] Options(this StyleCategory c) => c == StyleCategory.Personal
        ? [WritingStyle.Formal, WritingStyle.Casual, WritingStyle.VeryCasual]
        : [WritingStyle.Formal, WritingStyle.Casual, WritingStyle.Excited];

    public static WritingStyle DefaultStyle(this StyleCategory c) =>
        c is StyleCategory.Personal or StyleCategory.Work ? WritingStyle.Casual : WritingStyle.Formal;

    public static string DisplayName(this UsageCategory u) => u switch
    {
        UsageCategory.Personal => "Personal messages",
        UsageCategory.Work => "Work messages",
        UsageCategory.Email => "Emails",
        UsageCategory.AiPrompts => "AI prompts",
        UsageCategory.Documents => "Documents",
        _ => "Other tasks",
    };

    public static StyleCategory ToStyleCategory(this UsageCategory u) => u switch
    {
        UsageCategory.Personal => StyleCategory.Personal,
        UsageCategory.Work => StyleCategory.Work,
        UsageCategory.Email => StyleCategory.Email,
        _ => StyleCategory.Other,
    };
}

/// <summary>
/// Classifies the app being dictated into, by Windows process name (for example "outlook.exe") and, for
/// browsers, the window title. Process names are compared case-insensitively, with or without ".exe".
/// </summary>
public static class AppClassifier
{
    public static string Normalize(string processName)
    {
        var n = processName.Trim().ToLowerInvariant();
        var slash = n.LastIndexOfAny(['\\', '/']);
        if (slash >= 0) n = n[(slash + 1)..];
        return n.EndsWith(".exe", StringComparison.Ordinal) ? n : n + ".exe";
    }

    public static UsageCategory Usage(string processName, string windowTitle)
    {
        var p = Normalize(processName);
        if (ByProcess.TryGetValue(p, out var known)) return known;
        if (!Browsers.Contains(p)) return UsageCategory.Other;
        var title = windowTitle.ToLowerInvariant();
        if (new[] { "gmail", "outlook", "inbox", "proton mail", "yahoo mail" }.Any(title.Contains)) return UsageCategory.Email;
        if (new[] { "whatsapp", "messenger", "telegram", "instagram", "discord", "signal" }.Any(title.Contains)) return UsageCategory.Personal;
        if (new[] { "slack", "teams", "linear", "jira", "asana", "linkedin", "notion calendar" }.Any(title.Contains)) return UsageCategory.Work;
        if (new[] { "chatgpt", "claude", "gemini", "perplexity", "copilot", "deepseek" }.Any(title.Contains)) return UsageCategory.AiPrompts;
        if (new[] { "google docs", "notion", "confluence", "medium", "substack", "google slides", "dropbox paper" }.Any(title.Contains)) return UsageCategory.Documents;
        return UsageCategory.Other;
    }

    /// <summary>Code editors and terminals: identifiers and commands are kept exact regardless of the Style page.</summary>
    public static bool IsCode(string processName)
    {
        var p = Normalize(processName);
        return CodeApps.Contains(p) || OutputSafety.IsTerminal(p);
    }

    /// <summary>Editors where spoken file names become @file tags.</summary>
    public static bool TagsFiles(string processName) => Normalize(processName) is "cursor.exe" or "windsurf.exe";

    public static WritingStyle Style(string processName, string windowTitle, Preferences preferences)
    {
        if (IsCode(processName)) return WritingStyle.Code;
        return preferences.StyleFor(Usage(processName, windowTitle).ToStyleCategory());
    }

    internal static readonly HashSet<string> CodeApps =
    [
        "code.exe", "code - insiders.exe", "cursor.exe", "windsurf.exe", "devenv.exe", "zed.exe", "sublime_text.exe",
        "idea64.exe", "pycharm64.exe", "webstorm64.exe", "rider64.exe", "clion64.exe", "goland64.exe", "phpstorm64.exe",
        "rubymine64.exe", "datagrip64.exe", "rustrover64.exe", "studio64.exe", "fleet.exe",
    ];

    internal static readonly HashSet<string> Browsers =
    [
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe", "arc.exe", "zen.exe",
        "librewolf.exe", "chromium.exe",
    ];

    internal static readonly Dictionary<string, UsageCategory> ByProcess = new()
    {
        ["outlook.exe"] = UsageCategory.Email, ["olk.exe"] = UsageCategory.Email, ["hxoutlook.exe"] = UsageCategory.Email,
        ["thunderbird.exe"] = UsageCategory.Email, ["mailspring.exe"] = UsageCategory.Email, ["em client.exe"] = UsageCategory.Email,
        ["whatsapp.exe"] = UsageCategory.Personal, ["whatsapp.root.exe"] = UsageCategory.Personal, ["telegram.exe"] = UsageCategory.Personal,
        ["discord.exe"] = UsageCategory.Personal, ["signal.exe"] = UsageCategory.Personal, ["messenger.exe"] = UsageCategory.Personal,
        ["slack.exe"] = UsageCategory.Work, ["teams.exe"] = UsageCategory.Work, ["ms-teams.exe"] = UsageCategory.Work,
        ["zoom.exe"] = UsageCategory.Work, ["linear.exe"] = UsageCategory.Work,
        ["chatgpt.exe"] = UsageCategory.AiPrompts, ["claude.exe"] = UsageCategory.AiPrompts, ["perplexity.exe"] = UsageCategory.AiPrompts,
        ["winword.exe"] = UsageCategory.Documents, ["notepad.exe"] = UsageCategory.Documents, ["obsidian.exe"] = UsageCategory.Documents,
        ["notion.exe"] = UsageCategory.Documents, ["onenote.exe"] = UsageCategory.Documents, ["wordpad.exe"] = UsageCategory.Documents,
        ["voiceflow.exe"] = UsageCategory.Documents,
    };
}
