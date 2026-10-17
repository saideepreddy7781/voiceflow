# Security policy

VoiceFlow has Accessibility and microphone access on the machines it runs on, so security reports
get priority.

## Reporting a vulnerability

Please do not open a public issue. Use GitHub's private reporting instead:
**Security → Report a vulnerability** on this repository.

Include what you found, how to reproduce it, the VoiceFlow version (Settings → About, or the DMG name),
and your macOS version. You'll get a reply within 7 days. Fixes for confirmed issues ship in a new
release, and the advisory is published once users can update.

## Supported versions

Only the latest release receives security fixes.

## Scope

In scope: the VoiceFlow app and the scripts in this repository. Out of scope: Groq's and Perplexity's
services, and issues that need an attacker who already controls your Mac user account.

## Verifying downloads

Every release DMG is signed with a Developer ID certificate, notarized by Apple, and published with a
SHA-256 checksum. Check both before installing:

```bash
shasum -a 256 -c VoiceFlow-x.y.z.dmg.sha256
spctl --assess --type open --context context:primary-signature --verbose VoiceFlow-x.y.z.dmg
```
