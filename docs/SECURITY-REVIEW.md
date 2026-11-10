# Security review

Review date: 2026-10-17. Scope: the macOS app (`VoiceFlow/`), the Windows app (`windows/`), build and
release scripts, and repository contents. Two passes: the author's own review, and an independent review of the code. All findings below
were fixed or have a stated reason for staying open.

## Why this app needs care

VoiceFlow holds three of the most sensitive permissions on a Mac: the **microphone**, **Accessibility**
(it can read and type into other apps), and a **keyboard event tap**. It also sends audio and some
screen text to a third-party API. A bug here can leak private text or type something the user did not
intend, so safety rules are enforced in code and covered by tests (`SecurityTests.swift`).

## Findings and status

| # | Severity | Finding | Status |
|---|---|---|---|
| 1 | High | Text on screen (a document, a terminal's output) could steer the AI output that gets typed, and a pasted newline or escape sequence could run a command in a terminal. | **Fixed.** Control characters, escape sequences and bidi overrides are stripped from everything typed. Terminals never receive a newline. No screen text from terminals is sent to the model. Both prompts mark screen text as untrusted. |
| 2 | High | If the user switched apps while a dictation was processing, the text was typed into the new app, possibly a password field or another chat. | **Fixed.** Text is typed only if the app in front is the one the dictation started in, and the focused field is re-checked for password fields right before typing. Otherwise the text is copied and the user is told. |
| 3 | Medium | Clipboard leaks: dictations were kept by clipboard managers and synced to other devices by Universal Clipboard; a password on the clipboard could be restored after being cleared; non-text clipboard content was lost after Command Mode copied a selection. | **Fixed.** The temporary paste is local-only (`currentHostOnly`) and marked Transient/Concealed. Concealed items are never restored. All clipboard types are restored. Restore waits 500 ms. |
| 4 | Medium | Dictating into a password field still sent audio to Groq, copied the text, and saved it in history. | **Fixed.** Password fields, password managers and macOS secure input stop recording immediately. Nothing is uploaded, typed, copied or saved. |
| 5 | Medium | In-app privacy text mentioned only audio. | **Fixed.** Onboarding, Settings, the microphone permission text and [PRIVACY.md](PRIVACY.md) list everything sent. History now defaults to 30 days. |
| 6 | Medium | Signing: no notarization, hardened runtime without the audio entitlement (the mic would fail in a signed build), ad-hoc builds without hardened runtime. | **Fixed, except notarization.** Entitlements file and hardened runtime on every build. Release builds use a stable self-signed certificate, or ad hoc when it isn't configured. Notarization needs a paid Apple account, which the project has decided against (see open items). |
| 7 | Medium | Repository: private working files and voice recordings in the project folder. | **Fixed.** Kept out of the repository (`fixtures/clips/`, build output and `.env` by `.gitignore`). CI fails on committed key patterns. |
| 8 | Low | Auto-learn could store account numbers or words planted in a shared document. | **Fixed in part.** Tokens with 4+ digits or over 32 characters are never learned; terminals are skipped; every learned word shows an Undo. Planted look-alike names remain possible (open item). |
| 9 | Low | One Accessibility call had no timeout; a frozen app could stall keyboard input. | **Fixed.** Every element read uses a 250 ms timeout. |
| 10 | Low | A crash while dictating could leave the Mac muted. | **Fixed.** Mute state is persisted and undone on next launch; audio is restored on quit. |
| 11 | Low | Provider error bodies went to the public system log. | **Fixed.** Logged as private. |
| 12 | Info | Keychain items could use `ThisDeviceOnly` accessibility. | **Open, by design.** That flag applies to the data-protection keychain, which needs a signed team identity. The login keychain used today does not sync to iCloud. The self-signed certificate has no team identity, so this stays open. |

## Things that were already sound

- No third-party Swift packages; Python scripts use only the standard library.
- API keys live in the Keychain, entered in a secure field, never logged or written to preferences.
- The keyboard tap reads only key codes and modifier flags. It records nothing, and only five keys
  (the hotkey, Esc, Space, Backspace, ⌃⌘V/⌃⌘C) can affect it.
- Network traffic goes only to fixed HTTPS hosts (`api.groq.com`, plus `perplexity.ai` opened in the
  browser for search). App Transport Security is not weakened.
- Only file names the user actually says are sent from code projects, never file contents.

## Open items

| Item | Why it is open | What closes it |
|---|---|---|
| Code signing | The project uses no paid signing accounts, by decision. macOS releases are self-signed with one stable certificate (ad hoc if the secrets aren't set), so permissions survive updates, but they are not notarized and users approve the first launch of each downloaded version. Windows executables are unsigned. Every download has a SHA-256 and a GitHub build attestation tying it to this repository's CI. | Accepted for macOS unless the project takes on a paid Apple account. On Windows, a free open-source signing program. |
| Certificate custody | Anyone holding `MACOS_SIGN_P12` and its password can produce builds macOS treats as the same app. It lives only in GitHub secrets and the maintainer's private backup. Losing it resets permissions for every user on the next release. | Keep the backup private; never commit it (`.gitignore`, and `scripts/make-signing-cert.sh` refuses to write inside a repository). |
| Name "VoiceFlow" | An existing company (Voiceflow, voiceflow.com) uses a very similar name for AI voice products. The author chose to keep the name. | Rename if a conflict arises; update the bundle id `com.saideep.voiceflow`. |
| Command Mode "replace" with no selection rewrites the whole field | A hostile document could talk the model into a destructive rewrite. Undo is offered for 6 s, and the model is told to follow only the spoken instruction. | Ask for confirmation when the replacement differs from the original by more than half. |
| Focus check compares the app, not the exact field | Not built yet. Accessibility element identity may change inside web views, so it needs testing across apps before it can block typing. | Compare element identity, tested in browsers, Slack and editors. |
| Planted look-alike names in shared documents | Auto-learn cannot tell who typed a word. | Learn only after the same correction is seen twice. |
| No automatic updates | Users must download new releases themselves, so fixes reach them slowly. | Add Sparkle with EdDSA-signed update feeds. |
| App is not sandboxed | The App Sandbox does not allow Accessibility control of other apps. The hardened runtime limits code injection instead. | None; inherent to this kind of app. |
| What Groq keeps | Set by Groq's API terms, not by VoiceFlow. | Link Groq's policy (done); support other providers later. |

## Addendum: Notetaker and Telugu (2026-10-17)

| Area | Risk | How it is handled |
|---|---|---|
| New permission: System Audio Recording | VoiceFlow can hear everything the Mac plays while Notetaker runs. | Capture starts only when you start Notetaker (detection only offers it), stops on Stop or quit, and a red chip with a timer is shown throughout. The tap is private and created per meeting, then destroyed. |
| Other people's speech | Recording without consent is illegal in some places. | A consent reminder on every start (on by default), and the README and privacy page say so. |
| Meeting transcript as model input | Someone on the call could say instructions aimed at the notes writer. | The prompt treats the transcript as a record, never instructions. The output is only displayed and saved as text; nothing is executed or typed into other apps. |
| Memory on long meetings | An hour of audio is ~230 MB per stream. | Audio is drained every second; only the current chunk (≤45 s) is kept. Nothing is written to disk. |
| Free-tier limits | A two-stream meeting would use the whole hourly transcription allowance. | Both streams are mixed into one upload; speakers are separated afterwards by comparing the two sources. Long notes are written in parts and wait out rate limits. |
| Calendar | Event titles and attendee names are personal data. | Off by default; read only at meeting start; never sent anywhere except as names inside the notes request. |
| Sync folder | Notes are written outside VoiceFlow's folder. | Only to the folder you choose; file names are sanitized; renaming a meeting replaces its old file. |
| Telugu auto-detect | Whisper heard Telugu as Hindi; forcing Telugu turned English into nonsense. | Auto-detect first, and a second pass in Telugu only when the detected language is not one you speak. |

## Addendum: Windows app (2026-10-17)

The Windows app was written to the same rules as the macOS app and then reviewed separately for
Windows-specific problems. Ten issues from that review were fixed before release, including a launch crash
when another copy runs as administrator, DLL search-order hijacking, and a log line that could contain
spoken search terms.

| Area | Risk | How it is handled |
|---|---|---|
| API keys | Keys in a settings file could be copied by any program the user runs. | Windows Credential Manager (`VoiceFlow/groq`), never in files or logs. |
| Keyboard hook | A global hook sees every keystroke. | The hook keeps no history: it reacts only to the dictation chord and its own shortcuts, and logs nothing about other keys. It runs on its own thread so a busy UI never delays typing. |
| Clipboard | Dictated text could land in Win+V history or sync to other devices. | Pasted text carries the history and cloud-clipboard exclusion formats; the previous clipboard is restored, except content from password managers. |
| Password fields and admin windows | Typing into the wrong place. | UI Automation password check, password managers and the Windows credential prompt are excluded, and text is never typed into a window that took focus during processing. Windows blocks input to elevated windows; VoiceFlow leaves the text on the clipboard instead. |
| DLL loading | A malicious DLL in the Downloads folder could be loaded by the exe. | All native imports load from System32 only. |
| Terminals | A dictated line break could run a command. | Line breaks become spaces in terminals; control and bidirectional-override characters are removed everywhere. |
| Supply chain | One third-party package (NAudio, MIT), pinned. GitHub Actions are pinned to full commit SHAs. | Dependabot alerts; review updates by hand. |
| Untested on real PCs | Built on macOS and checked by the `--self-test` mode on GitHub's Windows x64 and Arm runners, not yet by many real users. | Issues from early users; a manual test pass on a Windows PC. |
