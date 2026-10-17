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
   tag and the two app versions differ.

## Code signing

Builds are currently unsigned, so first launch shows a warning on both systems. The README explains
the one-time steps for users. To remove the warnings:

**macOS.** Join the Apple Developer Program (paid, yearly). Create a **Developer ID Application**
certificate (Xcode → Settings → Accounts → Manage Certificates) and save notarization credentials:
```bash
xcrun notarytool store-credentials voiceflow-notary --apple-id YOU@EXAMPLE.COM --team-id TEAMID
NOTARY_PROFILE=voiceflow-notary scripts/release.sh
```
`scripts/release.sh` then signs, notarizes and staples the DMG. A signed build also keeps macOS
permissions across updates; ad hoc builds lose them on every update. Moving this into CI needs the
certificate and an app-specific password stored as repository secrets.

**Windows.** Sign the executables with an Authenticode certificate, for example Azure Trusted Signing
or SignPath's free plan for open-source projects, so SmartScreen stops warning after the certificate
builds reputation.

## Repository settings

Settings → Code security: enable **Private vulnerability reporting**, **Secret scanning** with push
protection, and **Dependabot alerts**. Protect `main` (require CI, no force pushes).

## Never

- Commit API keys, `.env` files, certificates or notarization passwords.
- Ship a build with a hard-coded Groq key. Every user brings their own.
