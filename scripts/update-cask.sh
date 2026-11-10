#!/bin/bash
# Sets the version and sha256 in Casks/voiceflow.rb.
#   scripts/update-cask.sh 0.2.0 <sha256 of VoiceFlow-0.2.0-macOS.dmg>
# Refuses anything that isn't a plain version and a 64-character hash, so the cask stays valid Ruby.
set -euo pipefail

VERSION="${1:-}"
SHA256="${2:-}"
CASK="$(cd "$(dirname "$0")/.." && pwd)/Casks/voiceflow.rb"

[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Not a release version: $VERSION" >&2; exit 1; }
[[ "$SHA256" =~ ^[0-9a-f]{64}$ ]] || { echo "Not a sha256: $SHA256" >&2; exit 1; }

sed -i.bak -E \
  -e "s/^  version \"[^\"]*\"/  version \"$VERSION\"/" \
  -e "s/^  sha256 \"[^\"]*\"/  sha256 \"$SHA256\"/" \
  "$CASK"
rm -f "$CASK.bak"

grep -q "version \"$VERSION\"" "$CASK" && grep -q "sha256 \"$SHA256\"" "$CASK" || { echo "Cask was not updated" >&2; exit 1; }
echo "Casks/voiceflow.rb: $VERSION $SHA256"
