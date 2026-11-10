# Changelog

All notable changes to VoiceFlow are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.0] - 2026-11-10

### Security

- Settings and onboarding explain what Groq keeps, and link to Zero Data Retention in Groq's Data Controls.
- New network destination: an update check to `api.github.com` at launch and once a day. It carries nothing
  about you, downloads nothing, and can be turned off in Settings > About.
- On Windows, up to 120 characters before the cursor are now sent to Groq with your transcript, as on macOS.
  They are never read from password fields, password managers or terminals.
- Meeting chunks that fail to upload stay in memory only, for at most about 20 minutes of audio.
- Copy debug report and local diagnostics hold metadata only, never what you said or typed.
- Releases can be signed with a stable self-signed certificate, so macOS keeps Microphone and Accessibility
  permissions across updates. Each release's notes say how its build was signed.
- The release workflow fails if private strings appear in the repository.

### Fixed

- Text insertion re-reads the focused field when pasting, retries for Electron and Chromium apps, and checks
  that the text arrived. A one-click Paste is offered when it can't confirm.
- Dictation and meetings share one microphone engine on both platforms. Stalls and device changes are handled
  in one place.
- Windows meeting audio follows changes to the default speakers or headset.
- The onboarding hotkey step updates when the hotkey is pressed.

### Added

- A new Avalonia interface on Windows, with Home, Meetings, Insights, Dictionary, Snippets, Style, Scratchpad,
  Settings and Help.
- On Windows: text before the cursor, offers to take notes when a call starts, auto-learning from your edits,
  `@file` tags in Cursor and Windsurf, and Scratchpad on `Alt + Shift + S`.
- A release notice in both apps, with a link to the release. Nothing is downloaded automatically.
- Any number of languages, with auto-detect checked against your list and a second opinion when in doubt.
- Hindi writing choices: Devanagari, Hindi with English words, or Hinglish.
- Separate transcription of your microphone and the call, voiced-only uploads, echo and repeat removal, and
  rewriting of Telugu and Hindi meeting lines in the writing you chose.
- One Groq queue that leaves room for dictation during meetings, and a second model for notes when the first
  runs out.
- Meetings page search, renaming of Others, action items you can tick, and copying notes as Markdown.
- A new monochrome design for the Mac app and a Settings > About page.
- A Homebrew cask in this repository, a winget manifest draft, and a verification section in release notes.
- Contributor documentation, issue forms, an architecture guide and a project landing page.

### Removed

- The notarization path in `scripts/release.sh`. The project has no paid Apple account.
- The WinForms interface on Windows.

## [0.1.0] - 2026-10-17

The first public release of VoiceFlow, for macOS 14 or later and Windows 10 and 11.

### Added

- Dictation in any app: Fn on Mac, Ctrl + Win on Windows, with a hands-free mode of up to 20 minutes.
- Cleanup that removes filler words, applies spoken corrections, punctuates and formats lists, in None, Light
  or Medium strength, with a per-app writing style.
- Telugu and Telugu-English, typed in Telugu script, mixed, or Telish in English letters, plus 40+ other
  languages.
- Command Mode: select text and say what to do with it.
- Dictionary, snippets and history, stored only on your computer.
- Notetaker: a live meeting transcript labelled You / Others, then a summary, decisions, action items and
  open questions, saved as Markdown.
- On macOS only: meeting detection and calendar titles, Scratchpad, Insights, auto-learning from your edits,
  and `@file` tags in code editors.

### Security

- API keys are stored in the macOS Keychain or Windows Credential Manager, never in files or logs.
- VoiceFlow never reads or types into password fields or password managers.
- Pasted text is kept out of clipboard history and cross-device clipboards, and your clipboard is restored.

[Unreleased]: https://github.com/saideepreddy7781/voiceflow/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/saideepreddy7781/voiceflow/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/saideepreddy7781/voiceflow/releases/tag/v0.1.0
