# Changelog

All notable changes to VoiceFlow are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Releases can be signed with a stable self-signed certificate, so macOS keeps Microphone and Accessibility
  permissions across updates. Each release's notes say how its build was signed.
- A Homebrew cask in this repository, a winget manifest draft, and a verification section in release notes.
- Contributor documentation, issue forms, an architecture guide and a project landing page.

### Removed

- The notarization path in `scripts/release.sh`. The project has no paid Apple account.

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

[Unreleased]: https://github.com/saideepreddy7781/voiceflow/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/saideepreddy7781/voiceflow/releases/tag/v0.1.0
