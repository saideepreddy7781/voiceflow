#!/bin/zsh
# Builds VoiceFlow.app from the Swift package.
#   scripts/build-app.sh            debug build
#   scripts/build-app.sh release    release build
#   UNIVERSAL=1 scripts/build-app.sh release    one binary for Apple silicon and Intel (for downloads)
#
# Signing: uses $SIGN_IDENTITY if set, otherwise the first "Developer ID Application" or
# "Apple Development" identity in the keychain, otherwise signs ad hoc (local use only).
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
  IDENTITY="$(security find-identity -v -p codesigning | awk -F'"' '/Developer ID Application/ {print $2; exit}')"
fi
if [[ -z "$IDENTITY" ]]; then
  IDENTITY="$(security find-identity -v -p codesigning | awk -F'"' '/Apple Development/ {print $2; exit}')"
fi

if [[ -n "$IDENTITY" ]]; then
  # Hardened runtime + entitlements are required for notarization; --timestamp for Developer ID.
  TIMESTAMP=()
  [[ "$IDENTITY" == Developer\ ID* ]] && TIMESTAMP=(--timestamp)
  codesign --force --options runtime --entitlements "$ENTITLEMENTS" "${TIMESTAMP[@]}" --sign "$IDENTITY" "$APP"
  echo "Signed with: $IDENTITY"
else
  codesign --force --options runtime --entitlements "$ENTITLEMENTS" --sign - "$APP"
  echo "Signed ad hoc (no signing identity found). Others can open it via System Settings → Privacy & Security → Open Anyway."
fi

codesign --verify --strict "$APP"
echo "Built $APP"
