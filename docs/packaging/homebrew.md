# Homebrew

VoiceFlow can be installed with Homebrew from this repository. There is no separate tap repository.

```bash
brew tap saideepreddy7781/voiceflow https://github.com/saideepreddy7781/voiceflow
brew install --cask saideepreddy7781/voiceflow/voiceflow
```

The second command names the tap on purpose. Since Homebrew 6.0, Homebrew refuses to load software from a
tap that you have not trusted, so the short form `brew install --cask voiceflow` fails with "Refusing to load
cask from untrusted tap". The fully qualified name trusts this one cask and nothing else in the tap, as
explained in [Homebrew's tap trust page](https://docs.brew.sh/Tap-Trust). After that first install, the short
name works.

## What happens on first launch

Homebrew does not make the app any more trusted than a download from the Releases page.

Homebrew sets the macOS quarantine flag on casks, and on the installed VoiceFlow app the flag is present
(checked with `xattr -p com.apple.quarantine` on Homebrew 7.0.12). VoiceFlow isn't notarized, so Gatekeeper
blocks the first launch and `spctl` reports it as rejected. Do this once:

1. Open VoiceFlow.
2. Go to System Settings > Privacy & Security, scroll down and click **Open Anyway**.

The older `--no-quarantine` option is not a way around this. Homebrew deprecated it in
[5.0.0](https://brew.sh/2025/11/12/homebrew-5.0.0/) and on 7.0.12 the option is gone.

## Updates

`brew upgrade --cask voiceflow` installs new releases. The release workflow updates the cask's version and
SHA-256 itself.

Homebrew 7.0.12 carries your approval forward during an upgrade only if you approved the previous version
and the new app has the same signing identity. That is one more reason the stable self-signed certificate
helps: self-signed releases should not need Open Anyway again after `brew upgrade`, and an ad hoc build
causes a "signer changed" warning and a new approval. This comes from reading Homebrew's upgrade code, and it
has not been tested with a real approved install.

Moving from the ad hoc 0.1.0 to the first self-signed release counts as a new signer, so that one update
needs Open Anyway and new Microphone and Accessibility permissions once.

## Why VoiceFlow isn't in homebrew/cask

The official tap only accepts apps that [pass Gatekeeper](https://docs.brew.sh/Acceptable-Casks) on a default
macOS setup, and Homebrew said in its 5.0.0 notes that casks failing that check would be disabled in
September 2026. Passing needs a Developer ID certificate
and notarization, which need a paid Apple account, and this project doesn't have one. A cask in the project's
own tap has no such requirement: `brew audit --cask --strict --online` passes on 7.0.12, because Homebrew's
signing audit runs only on the official tap unless you ask for it.

## Uninstall

```bash
brew uninstall --cask voiceflow        # removes the app
brew uninstall --zap --cask voiceflow  # also removes your settings and history
```

`--zap` deletes the same files that [docs/PRIVACY.md](../PRIVACY.md) lists under Removing everything, except
the Keychain entry. Remove that one with the `security` commands on that page.
