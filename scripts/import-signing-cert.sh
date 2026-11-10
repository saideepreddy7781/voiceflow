#!/bin/zsh
# CI only: imports the self-signed identity from the MACOS_SIGN_P12 secrets into a temporary keychain,
# then adds SIGN_IDENTITY and SIGN_KEYCHAIN to $GITHUB_ENV for scripts/build-app.sh.
#
# With neither secret set it exits 0 and the build falls back to ad hoc signing. With only one set it
# fails, because a half-configured repository would otherwise publish an ad hoc build by accident.
set -euo pipefail
umask 077

P12_B64="${MACOS_SIGN_P12:-}"
P12_PASSWORD="${MACOS_SIGN_P12_PASSWORD:-}"
P12_PASSWORD="${P12_PASSWORD%%[$'\r\n']*}"   # a secret pasted with a trailing newline still works

if [[ -z "$P12_B64" && -z "$P12_PASSWORD" ]]; then
  echo "::warning::MACOS_SIGN_P12 is not set: this build is ad hoc signed, so macOS permissions reset on update."
  exit 0
fi
if [[ -z "$P12_B64" || -z "$P12_PASSWORD" ]]; then
  echo "::error::Set both MACOS_SIGN_P12 and MACOS_SIGN_P12_PASSWORD, or neither."
  exit 1
fi

TEMP_DIR="${RUNNER_TEMP:?RUNNER_TEMP is not set; this script is for CI}"
: "${GITHUB_ENV:?GITHUB_ENV is not set; this script is for CI}"
KEYCHAIN="$TEMP_DIR/voiceflow-signing.keychain-db"
P12_FILE="$TEMP_DIR/voiceflow-signing.p12"
trap 'rm -f "$P12_FILE"' EXIT

KEYCHAIN_PASSWORD="$(openssl rand -hex 32)"
print -rn -- "$P12_B64" | base64 --decode > "$P12_FILE"

security create-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN"
security set-keychain-settings -lut 3600 "$KEYCHAIN"
security unlock-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN"
security import "$P12_FILE" -k "$KEYCHAIN" -P "$P12_PASSWORD" -f pkcs12 -T /usr/bin/codesign >/dev/null
security set-key-partition-list -S apple-tool:,apple: -s -k "$KEYCHAIN_PASSWORD" "$KEYCHAIN" >/dev/null

# codesign finds identities only in keychains on the search list, even when --keychain names the file.
existing=("${(@f)$(security list-keychains -d user | sed -E 's/^ *"//; s/"$//')}")
security list-keychains -d user -s "$KEYCHAIN" "${existing[@]}"

# Without -v, because find-identity -v hides self-signed certificates that are not trusted.
IDENTITY="$(security find-identity -p codesigning "$KEYCHAIN" | awk '$1 == "1)" {print $2; exit}')"
if [[ -z "$IDENTITY" ]]; then
  echo "::error::No code-signing identity found in MACOS_SIGN_P12."
  exit 1
fi

{
  echo "SIGN_IDENTITY=$IDENTITY"
  echo "SIGN_KEYCHAIN=$KEYCHAIN"
} >> "$GITHUB_ENV"
echo "Imported signing identity $IDENTITY into a temporary keychain."
