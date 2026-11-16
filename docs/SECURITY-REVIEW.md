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
| Free-tier limits | A two-stream meeting could use the whole hourly transcription allowance. | Each stream uploads only voiced audio through one rate-aware queue. Long notes are written in parts and wait out rate limits. |
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

## v0.3: distribution and supply chain (2026-11-16)

Scope: the new one-line installers (`scripts/install.sh`, `scripts/install.ps1`), the release and CI workflows,
the repository's GitHub settings, and a recheck of what the shipped apps are allowed to do. Settings were read
with `gh api` (read only) on 2026-11-16 and nothing was changed.

### Findings, most important first

| # | Severity | Finding | Recommendation |
|---|---|---|---|
| 1 | High | `main` has no branch protection and no rulesets. The installers are served from `main`, so any push to `main`, including a force push, changes what every new user runs, with no review and no CI gate. | Add a ruleset on `main` that blocks force pushes and deletion. Requiring pull requests and passing checks is stronger; the cask job then opens a pull request instead of pushing, which needs "Allow GitHub Actions to create and approve pull requests" turned on (it is off today). |
| 2 | High | The signing secrets are repository secrets, usable by any workflow run in the repository, and pushing any `v*` tag starts a release signed with them. The installers trust that signature, so whoever can push a tag can ship code that runs on users' Macs without a Gatekeeper prompt. Only the owner's account can push, and this Mac holds `gh` tokens for it with `repo` and `workflow` scope. | Move `MACOS_SIGN_P12` and `MACOS_SIGN_P12_PASSWORD` into an environment (for example `release`) with the owner as required reviewer and a deployment rule for `v*` tags, and give the `macos` release job `environment: release`. Add a tag ruleset for `v*` that blocks updates and deletion. Turn on immutable releases (off today). |
| 3 | Medium | On Windows the only proof of origin is the GitHub account: the exe is unsigned, and `SHA256SUMS.txt` comes from the same release as the exe. The checksum catches corruption and a tampered mirror. It doesn't catch a malicious release. The attestation check runs only when `gh` is installed and signed in. | Accepted under the no-paid-accounts decision. A free open-source signing program (SignPath Foundation) would close it and is also the only fix for Smart App Control. |
| 4 | Medium | Actions settings allow any action and don't require SHA pinning. Every `uses:` in the three workflows is pinned to a full SHA today, and all eight SHAs match the tag in their comment (checked against each action's repository on 2026-11-16). | Settings > Actions > General: allow only actions by GitHub (all eight are `actions/*`) and turn on "Require actions to be pinned to a full-length commit SHA". |
| 5 | Medium | Dependabot security updates are off (alerts are on, 0 open). No code scanning is set up. | Turn on Dependabot security updates. Turn on CodeQL default setup, which covers the workflows and C#. |
| 6 | Low | The single-file Windows exe unpacks Skia, HarfBuzz and ANGLE to `%TEMP%\.net\VoiceFlow` on first launch and loads them from there. Another program running as the same user could replace them. That gives it nothing it couldn't already do as that user, but antivirus heuristics notice the pattern. | Consider a folder install through `install.ps1` in a later release (exe and native DLLs side by side, no extraction). |
| 7 | Low | Windows has no way to ask VoiceFlow to quit, so the installer closes its window and then ends the process. A dictation in progress at that moment would skip the app's emergency cleanup, which restores muted speakers. | Add a quit signal to the app (a named event like the existing show event) and have the installer use it first. |
| 8 | Low | On macOS the installer asks the running copy to quit with an Apple event. Whether macOS shows an Automation consent prompt for `quit` sent from Terminal wasn't tested, because the owner's copy was running. If the event fails, the installer waits 10 s and sends SIGTERM. | Test once on a Mac without VoiceFlow running elsewhere. |
| 9 | Low | The Windows uninstall entry runs `powershell.exe -ExecutionPolicy Bypass -File uninstall.ps1` from the user's own install folder. Bypass is needed because Windows client systems don't run unsigned scripts by default. Anything that can rewrite that file already runs as the same user, and the entry is under HKCU, so it gives no elevation. | None needed. |
| 10 | Low | macOS code still has Gemini key handling (`SecretStore`, `KeyTester`, `Preferences`, and `Providers.swift` with `generativelanguage.googleapis.com`), although the UI field is gone and the project is Groq only. | Remove it, or confirm no UI path reaches it. |
| 11 | Low | The Windows logger's comment says Debug logging "may say more" than outcomes, and PRIVACY.md says logs hold what you said only "unless you turn on debug logging". Today's three Debug calls log gestures and hook events, never text. | Make the rule absolute in the comment and in PRIVACY.md: no dictated text at any level. |
| 12 | Low | Secret scanning for non-provider patterns and validity checks are off. Release tags are annotated but not signed. | Turn on non-provider patterns. Signing tags is optional. |
| 13 | Info | `app.manifest` still says version 0.1.0.0, and the exe's version resource names `VoiceFlow.dll` as its original file name (the .NET SDK default for an apphost). | Cosmetic. Update the manifest version with each release if it matters. |

### Repository settings (read 2026-11-16)

| Setting | State |
|---|---|
| Visibility | Public, one collaborator (`saideepreddy7781`, admin). No deploy keys, no webhooks |
| Branch protection and rulesets on `main` | None (`protected: false`, no rulesets, no tag rules) |
| Secret scanning, push protection | On, 0 open alerts |
| Secret scanning non-provider patterns, validity checks | Off |
| Dependabot alerts | On, 0 open. Security updates off. Version updates configured for actions, NuGet and Swift |
| Private vulnerability reporting | On |
| Code scanning | Not set up |
| Actions | All actions allowed, SHA pinning not required, default token read only, Actions can't create or approve pull requests, approval needed for first-time contributors' workflows |
| Secrets | `MACOS_SIGN_P12` and `MACOS_SIGN_P12_PASSWORD`, repository level, updated 2026-11-10 |
| Environments | `github-pages`, deployments from `main` only |
| Releases | Immutable releases off. v0.2.0 is not immutable |
| Pages | Built by Actions, HTTPS enforced |

### Release provenance

Each release has `SHA256SUMS.txt` and a GitHub build attestation for the DMG and both exes, made in the
`publish` job with `id-token: write`. From 0.3.0, `designated-requirement.txt` is attested and published too.
`gh attestation verify VoiceFlow-0.2.0-macOS.dmg --repo saideepreddy7781/voiceflow --signer-workflow
saideepreddy7781/voiceflow/.github/workflows/release.yml` succeeds (run on 2026-11-16), and the 0.2.0 app's
designated requirement is `identifier "com.saideep.voiceflow" and certificate leaf = H"c5ba95cee6f634c2933e3c47cb68e5da8b511ed5"`.

The signing certificate is decoded into a temporary keychain with a random password only for the macOS
release job, after the unit tests and before the build, and the decoded `.p12` is deleted on exit. Pull
requests from forks never see the secrets. The swift package has no third-party dependencies, so no outside
code runs while the keychain is unlocked. The release workflow now fails if the build is signed with a
certificate that `install.sh` doesn't pin.

### The installers

**Transport.** `install.sh` uses curl with `--proto =https --proto-redir =https --tlsv1.2`, so neither the
request nor a redirect can drop to plain HTTP. `install.ps1` accepts only `https://` addresses and turns on
TLS 1.2 for Windows PowerShell 5.1, then restores the session's setting. Release downloads redirect from
github.com to GitHub's download host over HTTPS.

**What is verified before anything is replaced.** Both check the file against `SHA256SUMS.txt` and stop on a
mismatch or on a missing or duplicated entry. Both run `gh attestation verify` with the repository and the
release workflow as signer when `gh` is installed and signed in, and say so when they skip it. `install.sh`
also requires the app to satisfy `identifier "com.saideep.voiceflow" and certificate leaf = H"c5ba95ce..."`
with `codesign --verify --deep --strict`, requires the app's own designated requirement to equal it, requires
the version inside the DMG to be the one requested, and checks the copy again after `ditto`. The certificate
hash is pinned in the script itself, which is served from `main` and not from the release, so a release can't
vouch for itself. A published `designated-requirement.txt` that disagrees with the pin stops the install.
Tested on this Mac: Apple's Calculator, an ad hoc re-signed copy, and a copy with one byte appended to a
resource are all refused; a file that isn't the attested DMG fails the attestation check.

**Removing the download marks.** Files fetched with curl carry no `com.apple.quarantine` attribute; checked on
the installed 0.2.0 copy with `xattr -l`, which lists only `com.apple.provenance`. Gatekeeper's first-launch
assessment applies to quarantined files, so there is no Open Anyway step. `spctl -a -vv` still reports
"rejected, origin=VoiceFlow Self-Signed" because the build isn't notarized. On Windows, Microsoft documents
the Mark of the Web as the `Zone.Identifier` stream that `Unblock-File` removes, and asks people to review a
file and its source before unblocking it ([Unblock-File](https://learn.microsoft.com/powershell/module/microsoft.powershell.utility/unblock-file)).
The installer prints whether its download carries the mark, and the CI test records whether
`Invoke-WebRequest -OutFile` adds one in each PowerShell version. If a mark is present, it is removed only
from the copy whose SHA-256 has just matched. That is acceptable because the prompt the mark triggers asks a
person to judge a file of unknown origin, and here the origin has been checked by the person's decision to run
the installer plus the checksum (and the signature on macOS). Defender's real-time scanning still scans the exe
when it runs, and Smart App Control still blocks it, because neither depends on the mark. `install.sh` never
runs `xattr -d` on anything that hasn't passed the signature check.

**Piping.** Both scripts define everything in functions and call them on the last line, so a truncated
download runs nothing (bash stops at the incomplete function; PowerShell fails to parse the whole text).
`install.sh` redirects its standard input to `/dev/null` once running, because when piped, standard input is
the script itself, and it reads the `--purge` confirmation from `/dev/tty`. It narrows `PATH` to the system
folders after finding `gh`, so a tool earlier in the user's `PATH` can't stand in for `codesign` or `shasum`.
`install.ps1` never calls `exit`, which would close the user's PowerShell window under `iex`, and keeps its
preference changes inside its own function. People who want to read the script first can download it and
run it from disk; the README says so.

**Privileges.** Neither script uses sudo or administrator rights. `install.sh` writes `/Applications` only when
the account can, and otherwise uses `~/Applications`. If a copy in `/Applications` can't be replaced, it stops
and says why instead of leaving two copies. `install.ps1` writes only to the user's profile and HKCU.

**Replacing and removing.** `install.sh` copies the new app next to the old one, quits only a VoiceFlow running
from the path it replaces (it never touched the owner's running copy during testing), swaps by rename, and
puts the old copy back if the swap fails. Every exit path removes the temporary folder and detaches the DMG;
tested by sending SIGTERM during download, checksum, attestation and verification, and by a refusal after
the DMG was mounted. `--uninstall` removes only bundles whose identifier is `com.saideep.voiceflow`.
`--purge` asks first (or needs `--yes`), refuses while VoiceFlow is running, and `--dry-run` lists what it
would delete. `install.ps1` closes only processes whose version information names VoiceFlow and Saideep Reddy,
keeps the previous exe until the new one is in place, rewrites a "Start with Windows" entry only if one
exists, and removes only that entry if it points at its own copy.

### The macOS app

Rechecked against the 0.2.0 release build. The hardened runtime flag is set (`flags=0x10000(runtime)`), and
the only entitlements are `com.apple.security.device.audio-input` and
`com.apple.security.personal-information.calendars`. There are no runtime exceptions such as JIT or disabled
library validation. The app isn't sandboxed, because the sandbox doesn't allow controlling other apps through
Accessibility.

| Permission | Why |
|---|---|
| Microphone | Dictation and your side of a meeting |
| Accessibility | Noticing the hotkey, reading the focused field, typing the result |
| System Audio Recording | The other side of a meeting, only while Notetaker records |
| Calendars (optional) | Meeting titles and attendee names, read at meeting start |

Network destinations are `api.groq.com` (transcription and cleanup) and `api.github.com` (the release check,
which can be turned off). Perplexity is opened in the browser for "Search for...". Data lives in
`~/Library/Application Support/VoiceFlow` with folder mode `0700` and file mode `0600`, the Groq key is in the
login Keychain, and audio is never written to disk. Logs use the system log with private values, and the
local diagnostics hold metadata only.

### The Windows exe

It installs one `WH_KEYBOARD_LL` hook, which reacts only to VoiceFlow's own shortcuts and keeps no history, and
a foreground-change event hook. It uses the clipboard to paste, with the formats that keep the text out of
Win+V history and cloud clipboard, and restores the previous contents except from password managers. It runs
as the signed-in user (`asInvoker`, no UI Access), so it can't type into elevated windows. Its own native
imports load from System32 only (`DefaultDllImportSearchPaths`). Installed by `install.ps1`, it sits in a
folder that holds only VoiceFlow's files, which also avoids DLL planting from a shared Downloads folder. The
bundled native libraries load from the extraction folder described in finding 6. Logs are in
`%LOCALAPPDATA%\VoiceFlow\logs` and settings in `%APPDATA%\VoiceFlow`, both inside the user's profile.

### What was and wasn't tested

Run on this Mac (macOS 26.5, Apple silicon): `install.sh` against the published 0.2.0 into a temporary folder,
piped through `bash` as a user would, plus reinstall, refusal of 0.1.0, bad and missing versions, the
signature and attestation refusals above, interruption at several points, and uninstall. Launching the
installed app was skipped, because the owner's copy was running and a second copy hands over to it.
`install.ps1` was checked with PSScriptAnalyzer 1.25.0 and PowerShell 7.7 on macOS, and its download, version
and checksum functions were run there against the real release. The Windows install itself (shortcut,
Installed apps entry, Mark of the Web, upgrade, uninstall, purge, x64 and Arm) is covered by
`scripts/test-install-windows.ps1` in CI and hasn't run yet, because nothing was pushed.
