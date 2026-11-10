#!/bin/zsh
# Builds VoiceFlow.app from the Swift package.
#   scripts/build-app.sh            debug build
#   scripts/build-app.sh release    release build
#   UNIVERSAL=1 scripts/build-app.sh release    one binary for Apple silicon and Intel (for downloads)
#
# Signing:
#   SIGN_IDENTITY=<name or SHA-1>   that identity; add SIGN_KEYCHAIN=<path> to look only in one keychain
#   SIGN_IDENTITY=-                 ad hoc
#   neither                         a "VoiceFlow Self-Signed" or "Apple Development" identity if the
#                                   keychain has one, otherwise ad hoc
# A signing identity keeps macOS permissions across rebuilds. Ad hoc signatures change with every build.
set -euo pipefail

ROOT="${0:A:h:h}"
PKG="$ROOT/VoiceFlow"
CONFIG="${1:-debug}"
APP="$ROOT/build/VoiceFlow.app"
ENTITLEMENTS="$PKG/Support/VoiceFlow.entitlements"

ARCH_FLAGS=()
[[ "${UNIVERSAL:-0}" == 1 ]] && ARCH_FLAGS=(--arch arm64 --arch x86_64)
swift build --package-path "$PKG" -c "$CONFIG" "${ARCH_FLAGS[@]}"
BIN="$(swift build --package-path "$PKG" -c "$CONFIG" "${ARCH_FLAGS[@]}" --show-bin-path)/VoiceFlow"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/VoiceFlow"
cp "$PKG/Support/Info.plist" "$APP/Contents/Info.plist"
cp "$PKG/Support/AppIcon.icns" "$APP/Contents/Resources/AppIcon.icns"

IDENTITY="${SIGN_IDENTITY:-}"
if [[ -z "$IDENTITY" ]]; then
  # No -v here: find-identity -v hides self-signed certificates that are not trusted.
  IDENTITY="$(security find-identity -p codesigning | awk '/"VoiceFlow Self-Signed"/ {print $2; exit}')"
fi
if [[ -z "$IDENTITY" ]]; then
  IDENTITY="$(security find-identity -v -p codesigning | awk '/"Apple Development/ {print $2; exit}')"
fi
[[ -n "$IDENTITY" ]] || IDENTITY="-"

KEYCHAIN_ARGS=()
[[ -n "${SIGN_KEYCHAIN:-}" ]] && KEYCHAIN_ARGS=(--keychain "$SIGN_KEYCHAIN")

codesign --force --options runtime --entitlements "$ENTITLEMENTS" "${KEYCHAIN_ARGS[@]}" --sign "$IDENTITY" "$APP"
if [[ "$IDENTITY" == "-" ]]; then
  echo "Signed ad hoc. macOS permissions reset whenever this build is replaced."
else
  echo "Signed with identity $IDENTITY"
fi

codesign --verify --strict "$APP"
echo "Built $APP"
