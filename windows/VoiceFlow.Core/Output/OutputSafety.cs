using System.Text;
using System.Text.RegularExpressions;

namespace VoiceFlow.Core.Output;

/// <summary>Rules that keep dictated or model-generated text from doing more than typing.</summary>
public static class OutputSafety
{
    /// <summary>
    /// Apps whose content is never read and where VoiceFlow never types: password managers, plus the
    /// Windows credential prompt (CredentialUIBroker) and the sign-in / UAC hosts.
    /// </summary>
    public static readonly HashSet<string> ExcludedApps =
    [
        "1password.exe", "keepass.exe", "keepassxc.exe", "bitwarden.exe", "dashlane.exe", "lastpass.exe",
        "enpass.exe", "nordpass.exe", "keeperpasswordmanager.exe", "keeper password manager.exe", "roboform.exe",
        "proton pass.exe", "protonpass.exe", "credentialuibroker.exe", "consent.exe", "logonui.exe",
    ];

    /// <summary>Terminals: a newline there runs a command, so inserted text never contains one.</summary>
    public static readonly HashSet<string> Terminals =
    [
        "windowsterminal.exe", "wt.exe", "openconsole.exe", "conhost.exe", "cmd.exe", "powershell.exe", "pwsh.exe",
        "powershell_ise.exe", "mintty.exe", "putty.exe", "kitty.exe", "alacritty.exe", "wezterm-gui.exe", "tabby.exe",
        "hyper.exe", "warp.exe", "wsl.exe", "bash.exe", "ubuntu.exe",
    ];

    /// <summary>
    /// Terminals where Ctrl+V does not paste (it is passed to the shell as ^V). Shift+Insert pastes there and
    /// in every other terminal listed above.
    /// </summary>
    public static bool PastesWithShiftInsert(string processName) => IsTerminal(processName);

    public static bool IsExcluded(string processName) => ExcludedApps.Contains(AppClassifier.Normalize(processName));

    public static bool IsTerminal(string processName) => Terminals.Contains(AppClassifier.Normalize(processName));

    /// <summary>
    /// Removes characters that can do something other than display as text:
    /// C0/C1 control characters (ESC sequences can rewrite a terminal or end a bracketed paste), keeping
    /// tab and newline; Unicode bidirectional overrides and isolates, which make text display differently
    /// from what is actually inserted; in terminals, newlines and carriage returns become spaces.
    /// On Windows a newline is written as CRLF so plain edit controls show a line break.
    /// </summary>
    public static string Sanitize(string text, bool forTerminal)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            int v = rune.Value;
            if (v == 0x0A || v == 0x0D)
            {
                if (forTerminal) sb.Append(' ');
                else if (v == 0x0A) sb.Append('\n');
                continue;
            }
            if (v == 0x09) { sb.Append('\t'); continue; }
            if (v < 0x20 || (v >= 0x7F && v <= 0x9F)) continue;
            if ((v >= 0x202A && v <= 0x202E) || (v >= 0x2066 && v <= 0x2069) || v == 0x200E || v == 0x200F || v == 0x061C) continue;
            sb.Append(rune.ToString());
        }
        var result = sb.ToString();
        if (forTerminal)
        {
            result = Regex.Replace(result, " {2,}", " ").Trim(' ', '\t');
        }
        return result;
    }

    /// <summary>Clipboard text for Windows: line breaks as CRLF.</summary>
    public static string ToClipboardText(string sanitized) => sanitized.Replace("\n", "\r\n", StringComparison.Ordinal);
}
