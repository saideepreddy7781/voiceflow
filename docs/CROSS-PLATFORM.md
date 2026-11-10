# Cross-platform design

VoiceFlow runs on macOS and Windows (since 0.1.0). Linux is not started. VoiceFlow on macOS is a native Swift app because the parts that matter most (global hotkey, typing
into other apps, reading the focused field) are operating-system APIs. Each platform gets its own native
app. What is shared is everything that does not touch the OS.

## What is shared, what is per platform

| Part | Shared | macOS | Windows | Linux (later) |
|---|---|---|---|---|
| Cleanup, Command Mode and meeting-notes prompts | Identical text, checked in CI by `scripts/check_prompt_parity.py` | Swift source | C# source (`windows/VoiceFlow.Core`) | same |
| Test cases and eval scripts | Yes: `fixtures/`, `scripts/eval_*.py` | | | |
| Groq API calls | Same HTTP shapes | URLSession | HttpClient | reqwest / libcurl |
| Global hotkey | | CGEventTap | `SetWindowsHookEx(WH_KEYBOARD_LL)` | X11 XInput2 / Wayland portal (limited) |
| Microphone | | AVAudioEngine | WASAPI (NAudio) | PipeWire / PulseAudio |
| Meeting audio | | Core Audio process tap | WASAPI loopback (NAudio) | PipeWire monitor |
| Read focused field | | Accessibility (AXUIElement) | UI Automation (password check; selection via Ctrl+C) | AT-SPI |
| Insert text | | AX write, or ⌘V with clipboard restore | `SendInput` Ctrl+V (Shift+Insert in terminals) with clipboard restore | AT-SPI / `wtype` / `xdotool` |
| Secret storage | | Keychain | Windows Credential Manager | libsecret |
| Floating bar | | NSPanel (non-activating) | WinForms topmost tool window, `WS_EX_NOACTIVATE` | Layer-shell / override-redirect |
| Packaging | | Universal DMG, self-signed or ad hoc, not notarized (see [RELEASING.md](RELEASING.md)) | Single self-contained `.exe` per architecture, unsigned | AppImage / Flatpak |

## Windows stack

**C# on .NET 8 with WinForms**, code-only UI. Windows' text APIs (UI Automation, low-level keyboard
hooks, `SendInput`, Credential Manager) are first-class in .NET. `VoiceFlow.Core` targets plain `net8.0`,
so the shared logic and its tests run on any OS; only `VoiceFlow.Windows` needs Windows.

Windows specifics handled in the app:
- Most Windows keyboards do not report Fn to software, so the default is the **Ctrl + Win** chord.
- Low-level keyboard hooks must return quickly or Windows silently removes them; work is posted to the UI thread.
- Releasing Win opens the Start menu; an unassigned key (VK 0xE8) is sent first to suppress it.
- Apps running as Administrator do not accept input from a non-elevated app (UIPI). VoiceFlow leaves the
  text on the clipboard instead; do not run VoiceFlow elevated.
- SmartScreen warns on unsigned apps until the executables are Authenticode signed.

## Next steps

1. Windows: meeting detection, Scratchpad, Insights, text before the cursor, auto-learning.
2. Windows code signing through a free program for open-source projects (see [RELEASING.md](RELEASING.md)).
3. Move the prompts into shared files that both builds embed, instead of the parity check.

## Mobile

iOS and Android need a custom keyboard (iOS keyboard extension with Full Access, Android IME). Global
hotkeys and reading other apps' fields are not available there, so the product works differently.
Out of scope until the desktop apps are stable.
