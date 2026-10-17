#!/bin/zsh
# Builds the macOS download: a universal (Apple silicon + Intel) DMG and its SHA-256.
#
#   scripts/release.sh                                  ad hoc signed DMG (no Apple Developer account)
#   NOTARY_PROFILE=voiceflow-notary scripts/release.sh  Developer ID signed, notarized and stapled DMG
#
# Ad hoc builds open after the user clicks "Open Anyway" in System Settings → Privacy & Security once.
# Notarized builds open without any warning; they need a paid Apple Developer account, once:
#   1. A "Developer ID Application" certificate in your keychain (Xcode → Settings → Accounts → Manage Certificates).
#   2. Notary credentials saved as a keychain profile:
#        xcrun notarytool store-credentials voiceflow-notary --apple-id YOU@EXAMPLE.COM --team-id TEAMID
set -euo pipefail

ROOT="${0:A:h:h}"
APP="$ROOT/build/VoiceFlow.app"
VERSION="$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' "$ROOT/VoiceFlow/Support/Info.plist")"
DMG="$ROOT/build/VoiceFlow-$VERSION-macOS.dmg"
PROFILE="${NOTARY_PROFILE:-}"

if [[ -n "$PROFILE" ]]; then
  IDENTITY="${SIGN_IDENTITY:-$(security find-identity -v -p codesigning | awk -F'"' '/Developer ID Application/ {print $2; exit}')}"
  [[ "$IDENTITY" == Developer\ ID\ Application* ]] || { echo "No Developer ID Application identity found."; exit 1; }
else
  IDENTITY="-"
fi

SIGN_IDENTITY="$IDENTITY" UNIVERSAL=1 "$ROOT/scripts/build-app.sh" release

# Verify what we are about to ship.
codesign --verify --deep --strict --verbose=2 "$APP"
codesign -d --entitlements - "$APP" 2>/dev/null | grep -q "audio-input" || { echo "Missing audio-input entitlement"; exit 1; }
[[ "$(lipo -archs "$APP/Contents/MacOS/VoiceFlow")" == *arm64* && "$(lipo -archs "$APP/Contents/MacOS/VoiceFlow")" == *x86_64* ]] \
  || { echo "Not a universal binary"; exit 1; }

rm -f "$DMG"
STAGE="$(mktemp -d)"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
hdiutil create -volname "VoiceFlow $VERSION" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
rm -rf "$STAGE"

if [[ -n "$PROFILE" ]]; then
  codesign --sign "$IDENTITY" --timestamp "$DMG"
  xcrun notarytool submit "$DMG" --keychain-profile "$PROFILE" --wait
  xcrun stapler staple "$DMG"
  spctl --assess --type open --context context:primary-signature --verbose "$DMG"
else
  echo "Ad hoc signed: users approve it once in System Settings → Privacy & Security."
fi

(cd "${DMG:h}" && shasum -a 256 "${DMG:t}" | tee "${DMG:t}.sha256")
echo "Release ready: $DMG"
