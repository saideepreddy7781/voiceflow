# Releasing

Releases are built by GitHub Actions (`.github/workflows/release.yml`) when a version tag is pushed.
The workflow tests both apps, builds the macOS DMG and the Windows x64 and arm64 executables,
runs the Windows self-test on x64 and Arm runners, publishes `SHA256SUMS.txt`, and records build
provenance so anyone can check that a download came from this repository:

```bash
gh attestation verify VoiceFlow-0.1.0-macOS.dmg -R saideepreddy7781/voiceflow
```

## Each release

1. Bump the version in both places, to the same number:
   - `CFBundleShortVersionString` (and `CFBundleVersion`) in `VoiceFlow/Support/Info.plist`
   - `<Version>` in `windows/Directory.Build.props`
2. Write the notes in `docs/releases/vX.Y.Z.md`. List security fixes first.
3. Run the checks locally:
   ```bash
   swift test --package-path VoiceFlow
   dotnet test windows/VoiceFlow.Core.Tests
   GROQ_API_KEY=... swift test --package-path VoiceFlow --filter Live
   GROQ_API_KEY=... python3 scripts/eval_cleanup.py
   python3 scripts/check_prompt_parity.py
   ```
4. Tag and push: `git tag vX.Y.Z && git push origin vX.Y.Z`. The workflow refuses to publish if the
   tag and the two app versions differ, or if tracked files contain private notes or personal paths
   (`scripts/check_private_strings.py`).

## Code signing

VoiceFlow has no paid signing accounts of any kind. That is a project decision, so these are the rules:

**macOS is self-signed, with one certificate kept for the life of the project.** An ad hoc signature changes
with every build, so macOS treats each update as a new app and resets Microphone and Accessibility. A
self-signed code-signing certificate gives every build the same designated requirement, which is what those
permissions are tied to. It needs no Apple account.

This is signing, not notarization. Gatekeeper still blocks the first launch of a downloaded build, and people
open it with System Settings > Privacy & Security > Open Anyway. Say so plainly anywhere the install is
described.

Setup is a one-time step:

```bash
scripts/make-signing-cert.sh ~/voiceflow-signing
gh secret set MACOS_SIGN_P12 -R saideepreddy7781/voiceflow < ~/voiceflow-signing/voiceflow-signing.p12.base64
gh secret set MACOS_SIGN_P12_PASSWORD -R saideepreddy7781/voiceflow < ~/voiceflow-signing/voiceflow-signing.p12.password
```

The script refuses to write inside a git work tree and never overwrites an existing identity. Keep a copy of
the `.p12` and its password somewhere private. If it is lost, the next release needs a new certificate and
every user grants permissions again.

The release workflow imports the certificate into a temporary keychain and signs with the hardened runtime
and `VoiceFlow.entitlements`. Without the secrets it warns and builds ad hoc, and with only one of the two
secrets it fails. Each release's notes say which of these happened. CI on pull requests stays ad hoc, and
forks don't get the secrets.

To sign locally, import the `.p12` into a keychain and set `SIGN_IDENTITY` to its SHA-1 hash, or let
`scripts/build-app.sh` find a "VoiceFlow Self-Signed" identity by itself.

A Developer ID certificate and notarization would remove the Gatekeeper step and are possible with a paid
Apple account, but this project doesn't use them.

**Windows** executables are unsigned, so SmartScreen warns on first launch. A free program for open-source
projects, such as SignPath's, is the route if that changes.

## Homebrew and winget

The release workflow updates `Casks/voiceflow.rb` on `main` using its own token, so no personal access token
is stored. If `main` doesn't accept the push it opens a pull request instead. See
[packaging/homebrew.md](packaging/homebrew.md) for what users see. A winget manifest is drafted in
[packaging/winget/](packaging/winget/) and nothing has been submitted.

## GitHub Pages

The landing page in `docs/site/` deploys through `.github/workflows/pages.yml`, by hand or automatically
after a release. It is a static page with no build step.

## Repository settings

Settings → Code security: enable **Private vulnerability reporting**, **Secret scanning** with push
protection, and **Dependabot alerts**. Protect `main` against force pushes and deletion. Requiring pull
requests or status checks on `main` stops the release workflow pushing the cask update, and it will open a
pull request instead, which needs a manual merge. Settings → Actions → General must allow workflows to create
pull requests for that fallback to work. Settings → Pages → Source must be **GitHub Actions**.

## Never

- Commit API keys, `.env` files, certificates or signing keys.
- Ship a build with a hard-coded Groq key. Every user brings their own.
