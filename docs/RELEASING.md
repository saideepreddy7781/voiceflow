# Releasing

Releases are built by GitHub Actions (`.github/workflows/release.yml`) when a version tag is pushed.
The workflow tests both apps, builds the macOS DMG and the Windows x64 and arm64 executables,
runs the Windows self-test and the installer test on x64 and Arm runners, publishes `SHA256SUMS.txt` and
`designated-requirement.txt`, and records build provenance so anyone can check that a download came from this
repository:

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
   tag and the two app versions differ, if tracked files contain private notes or personal paths
   (`scripts/check_private_strings.py`), or if the macOS build is signed with a certificate that
   `scripts/install.sh` doesn't pin.
5. After the release is out, run both one-line installers from the README on a real Mac and a real PC.

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

**Windows** executables are unsigned, so SmartScreen warns on first launch of a downloaded copy, and Smart
App Control on Windows 11 blocks them outright. A free program for open-source projects, such as SignPath's,
is the route if that changes. Signing is the only thing that helps with Smart App Control.

## Installers

`scripts/install.sh` (macOS) and `scripts/install.ps1` (Windows) are what the README's one-line commands run.
They are served from the `main` branch, so a change merged to `main` reaches users straight away. The Pages
site also serves `install.sh` at `https://saideepreddy7781.github.io/voiceflow/install.sh`, copied from `main`
at each deploy.

They read these release assets, so don't rename them:

| Asset | Used by |
|---|---|
| `VoiceFlow-X.Y.Z-macOS.dmg` | install.sh |
| `VoiceFlow-X.Y.Z-Windows-x64.exe`, `VoiceFlow-X.Y.Z-Windows-arm64.exe` | install.ps1 |
| `SHA256SUMS.txt` | both |
| `designated-requirement.txt` (from 0.3.0) | install.sh, which refuses a release whose file disagrees with its pin |

install.sh installs 0.2.0 and later. 0.1.0 was ad hoc signed, so it says so and stops. install.ps1 installs
0.1.0 and later.

**If the signing certificate ever changes**, update `CERT_LEAF` in `scripts/install.sh` in the same pull request
as the release that uses the new certificate. The release workflow fails until you do. Users of the old
installer then get a clear refusal, and the new installer from `main` works. Every user also grants
Microphone and Accessibility again, which is why the certificate should outlive the project.

What the installers check, and why removing the macOS quarantine flag or the Windows Mark of the Web is
acceptable only after those checks, is written up in [SECURITY-REVIEW.md](SECURITY-REVIEW.md#v03-distribution-and-supply-chain-2026-11-16).

## Antivirus false positives on Windows

An unsigned program that installs a keyboard hook, reads the clipboard and sends keystrokes looks a lot like a
keylogger to a heuristic scanner. These choices keep the exe as easy to judge as possible:

- **The single-file bundle isn't compressed** (`EnableCompressionInSingleFile` is false in
  `VoiceFlow.Windows.csproj`). In the compressed 0.2.0 x64 exe, 35 of its 45 megabytes have more than 7.9
  bits of entropy per byte, which is what packed or encrypted payloads look like. Uncompressed, no megabyte
  does (measured 2026-11-16). Packing is a classic malware trait ([MITRE ATT&CK T1027.002](https://attack.mitre.org/techniques/T1027/002/)),
  and Microsoft notes that compression also costs start-up time, because the assemblies are decompressed
  into memory at every launch ([.NET single-file docs](https://learn.microsoft.com/dotnet/core/deploying/single-file/overview#compress-assemblies-in-single-file-apps)).
  The cost is download size: x64 goes from 47.1 MB to 97.8 MB and arm64 from 45.9 MB to 104.6 MB. No
  public test shows this flag alone changes Defender's verdict, so treat it as a likely help, and check
  VirusTotal results before and after.
- **Version information is complete.** The exe's file properties list VoiceFlow, Saideep Reddy, the
  copyright and the version, from `windows/Directory.Build.props`. CI fails if any of them goes missing.
- **One exe, not a zip.** A zip doesn't help: Windows copies the Mark of the Web to files extracted from a
  downloaded zip, and Defender has flagged zipped .NET programs that it accepted unzipped
  ([one report, December 2024](https://dylanbeattie.net/2024/12/09/dotnet-9-aot-zip-file-windows-defender-false-positives.html)).
  A folder install is possible later through the installer, which would avoid one thing scanners notice:
  on first launch the single exe unpacks its native libraries (Skia, HarfBuzz, ANGLE) to
  `%TEMP%\.net\VoiceFlow` and loads them from there.

### When a scanner flags a release

1. Look the file up on [VirusTotal](https://www.virustotal.com) by its SHA-256 from `SHA256SUMS.txt`. Uploading
   is fine too, since releases are public. Note which engines flag it and the detection names.
2. For Microsoft Defender, go to [microsoft.com/wdsi/filesubmission](https://www.microsoft.com/wdsi/filesubmission),
   sign in with a Microsoft account, and submit the exe as a **Software developer**. Choose Microsoft Defender
   Antivirus, say it is incorrectly detected, and give the detection name, the release page link and the
   source repository. Track it on the [submission history page](https://www.microsoft.com/wdsi/submissionhistory).
   If you disagree with the result, the results page has a developer contact form
   ([Microsoft's submission guide](https://learn.microsoft.com/defender-xdr/submission-guide)).
3. For every other engine, VirusTotal can't change results itself. Its
   [false positive contacts page](https://docs.virustotal.com/docs/false-positive-contacts) lists each vendor's
   form or address. Send the VirusTotal report link and the same details.
4. Write the submission IDs and dates in the release's notes or an issue, so users who report the same
   detection can be pointed to it.

## Homebrew and winget

The release workflow updates `Casks/voiceflow.rb` on `main` using its own token, so no personal access token
is stored. If `main` doesn't accept the push it opens a pull request instead. See
[packaging/homebrew.md](packaging/homebrew.md) for what users see. A winget manifest is drafted in
[packaging/winget/](packaging/winget/) and nothing has been submitted.

## GitHub Pages

The landing page in `docs/site/` deploys through `.github/workflows/pages.yml`, by hand or automatically
after a release. It is a static page with no build step.

## Repository settings

The settings as read on 2026-11-16, and what to change, are in
[SECURITY-REVIEW.md](SECURITY-REVIEW.md#repository-settings-read-2026-11-16). Requiring pull requests or
status checks on `main` stops the release workflow pushing the cask update, and it will open a pull request
instead, which needs a manual merge. Settings > Actions > General must allow workflows to create pull
requests for that fallback to work. Settings > Pages > Source must be **GitHub Actions**.

## Never

- Commit API keys, `.env` files, certificates or signing keys.
- Ship a build with a hard-coded Groq key. Every user brings their own.
