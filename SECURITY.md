# Security policy

VoiceFlow has Accessibility and microphone access on the machines it runs on, so security reports
get priority.

## Reporting a vulnerability

Please do not open a public issue. Use GitHub's private reporting instead:
**Security → Report a vulnerability** on this repository.

Include what you found, how to reproduce it, the VoiceFlow version (Settings → About, or the download's file name),
and your OS version. You'll get a reply within 7 days. Fixes for confirmed issues ship in a new
release, and the advisory is published once users can update.

## Supported versions

Only the latest release receives security fixes.

## Scope

In scope: the VoiceFlow app and the scripts in this repository. Out of scope: Groq's and Perplexity's
services, and issues that need an attacker who already controls your user account.

## Verifying downloads

Every release has a `SHA256SUMS.txt` and a build attestation from GitHub Actions:

```bash
shasum -a 256 -c SHA256SUMS.txt --ignore-missing
gh attestation verify VoiceFlow-x.y.z-macOS.dmg -R saideepreddy7781/voiceflow
```

macOS builds are signed with the project's self-signed certificate, or ad hoc if it isn't configured, and
each release's notes say which. They are not notarized by Apple, because the project has no paid Apple
account, so Gatekeeper asks you to approve the first launch. Windows builds are not code-signed.
