#!/bin/bash
# Prints the release body: docs/releases/<tag>.md, then a "Verify your download" section for this build.
#   SIGNING_REQUIREMENT='designated => ...' scripts/release-notes.sh v0.2.0
set -euo pipefail

TAG="${1:?usage: release-notes.sh <tag>}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="${TAG#v}"
REPO="saideepreddy7781/voiceflow"
REQUIREMENT="${SIGNING_REQUIREMENT:-}"

cat "$ROOT/docs/releases/$TAG.md"

cat <<EOF

## Verify your download

Every file below has a SHA-256 in \`SHA256SUMS.txt\` and a build attestation from GitHub Actions.

\`\`\`bash
shasum -a 256 -c SHA256SUMS.txt --ignore-missing
gh attestation verify VoiceFlow-$VERSION-macOS.dmg -R $REPO
\`\`\`

On Windows, compare \`Get-FileHash VoiceFlow-$VERSION-Windows-x64.exe -Algorithm SHA256\` with \`SHA256SUMS.txt\`.

EOF

case "$REQUIREMENT" in
  *"certificate leaf"*)
    cat <<EOF
This macOS build is signed with the project's self-signed certificate, so it has the same identity as
other self-signed releases and keeps Microphone and Accessibility permission when you update. It is not
notarized, so macOS still asks you to approve it with System Settings > Privacy & Security > Open Anyway.
Check the signature with \`codesign -d -r- /Applications/VoiceFlow.app\`. It should print:

\`\`\`
$REQUIREMENT
\`\`\`
EOF
    ;;
  *)
    cat <<EOF
This macOS build is ad hoc signed and not notarized. macOS asks you to approve it with System Settings >
Privacy & Security > Open Anyway, and asks for Microphone and Accessibility again after you update.
EOF
    ;;
esac
