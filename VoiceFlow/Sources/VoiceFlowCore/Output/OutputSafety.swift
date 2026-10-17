import Carbon
import Foundation

/// Rules that keep dictated or model-generated text from doing more than typing.
public enum OutputSafety {
    /// Apps whose content is never read and where VoiceFlow never types: password managers.
    public static let excludedApps: Set<String> = [
        "com.1password.1password", "com.agilebits.onepassword7", "com.agilebits.onepassword-osx",
        "com.bitwarden.desktop", "com.dashlane.Dashlane", "com.dashlane.dashlanephonefinal",
        "com.lastpass.LastPass", "org.keepassxc.keepassxc", "com.apple.keychainaccess", "com.apple.Passwords",
        "com.enpass.Enpass-Desktop", "com.nordpass.macos.NordPass", "com.keepersecurity.passwordmanager",
    ]

    /// Terminals: a newline there runs a command, so inserted text never contains one.
    public static let terminals: Set<String> = [
        "com.apple.Terminal", "com.googlecode.iterm2", "dev.warp.Warp-Stable", "com.mitchellh.ghostty",
        "net.kovidgoyal.kitty", "org.alacritty", "co.zeit.hyper", "com.github.wez.wezterm", "io.tabby",
    ]

    public static func isExcluded(bundleID: String) -> Bool { excludedApps.contains(bundleID) }

    /// macOS secure input is on while a password prompt or Terminal's Secure Keyboard Entry is active.
    public static func secureInputActive() -> Bool { IsSecureEventInputEnabled() }
    public static func isTerminal(bundleID: String) -> Bool { terminals.contains(bundleID) }

    /// Removes characters that can do something other than display as text:
    /// - C0/C1 control characters (ESC sequences can rewrite a terminal or end a bracketed paste),
    ///   keeping tab and newline;
    /// - Unicode bidirectional overrides and isolates, which make text display differently from what
    ///   is actually inserted;
    /// - in terminals, newlines and carriage returns, so nothing is executed. They become spaces.
    public static func sanitize(_ text: String, forTerminal: Bool) -> String {
        var out = String.UnicodeScalarView()
        for scalar in text.unicodeScalars {
            let v = scalar.value
            if v == 0x0A || v == 0x0D {
                if forTerminal { out.append(" ") } else if v == 0x0A { out.append(scalar) }
                continue
            }
            if v == 0x09 { out.append(scalar); continue }
            if v < 0x20 || (0x7F...0x9F).contains(v) { continue }
            if (0x202A...0x202E).contains(v) || (0x2066...0x2069).contains(v) || v == 0x200E || v == 0x200F || v == 0x061C { continue }
            out.append(scalar)
        }
        var result = String(out)
        if forTerminal {
            result = result.replacingOccurrences(of: #" {2,}"#, with: " ", options: .regularExpression)
                .trimmingCharacters(in: .whitespaces)
        }
        return result
    }
}
