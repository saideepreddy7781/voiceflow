#!/bin/zsh
# Builds the macOS download: a universal (Apple silicon + Intel) DMG and its SHA-256.
#
# Signing follows scripts/build-app.sh. CI sets SIGN_IDENTITY and SIGN_KEYCHAIN from the MACOS_SIGN_P12
# secrets (scripts/import-signing-cert.sh); without them the build is ad hoc signed.
#
# Either way the build is not notarized, so people opening it for the first time use
# System Settings > Privacy & Security > Open Anyway. See docs/RELEASING.md.
set -euo pipefail

ROOT="${0:A:h:h}"
APP="$ROOT/build/VoiceFlow.app"
VERSION="$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' "$ROOT/VoiceFlow/Support/Info.plist")"
DMG="$ROOT/build/VoiceFlow-$VERSION-macOS.dmg"

UNIVERSAL=1 "$ROOT/scripts/build-app.sh" release

# Verify what we are about to ship.
codesign --verify --deep --strict --verbose=2 "$APP"
codesign -d --entitlements - "$APP" 2>/dev/null | grep -q "audio-input" || { echo "Missing audio-input entitlement"; exit 1; }
[[ "$(lipo -archs "$APP/Contents/MacOS/VoiceFlow")" == *arm64* && "$(lipo -archs "$APP/Contents/MacOS/VoiceFlow")" == *x86_64* ]] \
  || { echo "Not a universal binary"; exit 1; }

# The designated requirement is what macOS privacy permissions are tied to.
codesign -d -r- "$APP" 2>/dev/null | sed -E 's/^# *//' > "$ROOT/build/designated-requirement.txt"
cat "$ROOT/build/designated-requirement.txt"

rm -f "$DMG"
STAGE="$(mktemp -d)"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
hdiutil create -volname "VoiceFlow $VERSION" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
rm -rf "$STAGE"

(cd "${DMG:h}" && shasum -a 256 "${DMG:t}" | tee "${DMG:t}.sha256")
echo "Release ready: $DMG"
