#!/bin/bash
# Installs, updates or removes VoiceFlow on macOS.
#
#   curl -fsSL https://raw.githubusercontent.com/saideepreddy7781/voiceflow/main/scripts/install.sh | bash
#
# Pass options after `bash -s --`, for example `... | bash -s -- --version 0.2.0`:
#   --version X.Y.Z   install that release instead of the latest one (or set VOICEFLOW_VERSION)
#   --dir PATH        put VoiceFlow.app in PATH instead of /Applications (or ~/Applications)
#   --no-launch       don't open VoiceFlow afterwards (or set VOICEFLOW_NO_LAUNCH=1)
#   --uninstall       remove VoiceFlow.app
#   --purge           with --uninstall, also delete settings, history, meetings and the saved Groq key
#   --yes             with --purge, don't ask first
#   --dry-run         with --uninstall, print what would be removed and change nothing
#
# Before anything is replaced, the script checks the DMG against the release's SHA256SUMS.txt, checks the
# GitHub build attestation when the gh command is installed and signed in, and checks that the app is signed
# by the project's certificate pinned below. It never uses sudo and never turns off Gatekeeper.
#
# Files fetched with curl don't get the com.apple.quarantine attribute, so Gatekeeper doesn't stop the first
# launch. That is why the signature check here matters: it does the job the Open Anyway click would have done.
#
# All the work happens inside functions and main runs on the last line, so a download that is cut short
# runs nothing.

set -euo pipefail

REPO="saideepreddy7781/voiceflow"
BUNDLE_ID="com.saideep.voiceflow"
# SHA-1 of the project's self-signed certificate. Every self-signed release (0.2.0 and later) uses it.
# If the certificate ever changes, this line changes in the same commit as the release that uses it.
CERT_LEAF="c5ba95cee6f634c2933e3c47cb68e5da8b511ed5"
EXPECTED_REQUIREMENT="identifier \"$BUNDLE_ID\" and certificate leaf = H\"$CERT_LEAF\""
# 0.1.0 was ad hoc signed, so there is no certificate to check it against.
MIN_VERSION="0.2.0"

WORK=""          # temporary folder, removed on exit
MOUNT=""         # DMG mount point while attached
STAGED=""        # new copy being prepared next to the destination
BACKUP=""        # previous copy, moved aside while the new one goes in
DEST=""          # .../VoiceFlow.app being installed

say() { printf '%s\n' "$*"; }
note() { printf 'Note: %s\n' "$*"; }
die() { printf 'Error: %s\n' "$*" >&2; exit 1; }

cleanup() {
  local status=$?
  if [ -n "$MOUNT" ]; then
    hdiutil detach -quiet "$MOUNT" >/dev/null 2>&1 || hdiutil detach -quiet -force "$MOUNT" >/dev/null 2>&1 || true
  fi
  if [ -n "$STAGED" ] && [ -e "$STAGED" ]; then rm -rf "$STAGED"; fi
  # A failure after the old copy was moved aside puts it back.
  if [ -n "$BACKUP" ] && [ -e "$BACKUP" ]; then
    if [ -n "$DEST" ] && [ ! -e "$DEST" ]; then
      mv "$BACKUP" "$DEST" && say "Put the previous VoiceFlow back at $DEST."
    else
      rm -rf "$BACKUP" 2>/dev/null || note "Couldn't delete the old copy at $BACKUP. Delete it in Finder."
    fi
  fi
  if [ -n "$WORK" ] && [ -d "$WORK" ]; then rm -rf "$WORK"; fi
  return "$status"
}

usage() {
  cat <<EOF
Installs, updates or removes VoiceFlow on macOS.

  curl -fsSL https://raw.githubusercontent.com/$REPO/main/scripts/install.sh | bash
  curl -fsSL https://raw.githubusercontent.com/$REPO/main/scripts/install.sh | bash -s -- [options]

  --version X.Y.Z   install that release instead of the latest one
  --dir PATH        put VoiceFlow.app in PATH instead of /Applications (or ~/Applications)
  --no-launch       don't open VoiceFlow afterwards
  --uninstall       remove VoiceFlow.app
  --purge           with --uninstall, also delete settings, history, meetings and the saved Groq key
  --yes             with --purge, don't ask first
  --dry-run         with --uninstall, print what would be removed and change nothing

More: https://github.com/$REPO#download-and-install
EOF
}

# curl restricted to HTTPS, including redirects (release files redirect to GitHub's download host).
fetch() {
  curl --proto '=https' --proto-redir '=https' --tlsv1.2 --fail --silent --show-error --location \
    --retry 3 --retry-delay 2 --connect-timeout 20 --output "$2" "$1"
}

valid_version() {
  printf '%s' "$1" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$'
}

# version_at_least 0.10.1 0.2.0 -> true. A pre-release suffix is ignored.
version_at_least() {
  local a b i x y
  a="${1%%-*}"; b="${2%%-*}"
  for i in 1 2 3; do
    x="$(printf '%s' "$a" | cut -d. -f"$i")"; y="$(printf '%s' "$b" | cut -d. -f"$i")"
    if [ "$((10#${x:-0}))" -gt "$((10#${y:-0}))" ]; then return 0; fi
    if [ "$((10#${x:-0}))" -lt "$((10#${y:-0}))" ]; then return 1; fi
  done
  return 0
}

latest_version() {
  local json tag="" location
  json="$WORK/latest.json"
  if curl --proto '=https' --tlsv1.2 --fail --silent --show-error --location --connect-timeout 20 \
      -H 'Accept: application/vnd.github+json' --output "$json" \
      "https://api.github.com/repos/$REPO/releases/latest" 2>/dev/null; then
    tag="$(plutil -extract tag_name raw -o - "$json" 2>/dev/null || true)"
  fi
  if [ -z "$tag" ]; then
    # The API allows 60 requests an hour without a token. The releases page redirect has no such limit.
    location="$(curl --proto '=https' --tlsv1.2 --fail --silent --show-error --connect-timeout 20 \
      --output /dev/null --write-out '%{redirect_url}' "https://github.com/$REPO/releases/latest" || true)"
    case "$location" in */releases/tag/v*) tag="v${location##*/releases/tag/v}" ;; esac
  fi
  [ -n "$tag" ] || die "Couldn't find the latest release. Check your internet connection, or pass --version X.Y.Z."
  printf '%s' "${tag#v}"
}

# Process IDs of VoiceFlow running from the given VoiceFlow.app.
pids_running_from() {
  ps -axww -o pid=,comm= | awk -v exe="$1/Contents/MacOS/VoiceFlow" \
    '{ pid = $1; sub(/^ *[0-9]+ +/, ""); if ($0 == exe) print pid }'
}

# Paths of every running VoiceFlow app bundle.
running_copies() {
  ps -axww -o comm= | sed -n 's#^ *\(/.*\.app\)/Contents/MacOS/VoiceFlow$#\1#p' | sort -u
}

# Asks the copy at $1 to quit the normal way, so a meeting in progress is saved, then waits for it.
quit_copy() {
  local app="$1" pids pid osa i
  pids="$(pids_running_from "$app")"
  [ -n "$pids" ] || return 0
  say "Quitting the running VoiceFlow..."
  osascript -e "tell application \"$app\" to quit" >/dev/null 2>&1 &
  osa=$!
  for i in $(seq 1 40); do
    [ -n "$(pids_running_from "$app")" ] || break
    if [ "$i" = 20 ]; then
      kill "$osa" 2>/dev/null || true
      for pid in $(pids_running_from "$app"); do kill -TERM "$pid" 2>/dev/null || true; done
    fi
    sleep 0.5
  done
  kill "$osa" 2>/dev/null || true
  wait "$osa" 2>/dev/null || true
  if [ -n "$(pids_running_from "$app")" ]; then
    die "VoiceFlow didn't quit. Quit it from its menu bar icon, then run the installer again."
  fi
}

bundle_value() {
  /usr/libexec/PlistBuddy -c "Print :$2" "$1/Contents/Info.plist" 2>/dev/null || true
}

is_voiceflow() {
  [ -d "$1" ] && [ "$(bundle_value "$1" CFBundleIdentifier)" = "$BUNDLE_ID" ]
}

# The app must be intact and signed by the pinned certificate, with exactly the requirement macOS ties
# Microphone and Accessibility permission to.
check_signature() {
  local app="$1" designated
  codesign --verify --deep --strict -R "=$EXPECTED_REQUIREMENT" "$app" 2>/dev/null \
    || die "The app's code signature doesn't match the VoiceFlow certificate. Nothing was installed."
  designated="$(codesign -d -r- "$app" 2>/dev/null | sed -n 's/^designated => //p')"
  [ "$designated" = "$EXPECTED_REQUIREMENT" ] \
    || die "The app's designated requirement is unexpected ($designated). Nothing was installed."
}

check_attestation() {
  local dmg="$1" gh_bin="$2"
  if [ -z "$gh_bin" ]; then
    say "  Build attestation: skipped, because the gh command isn't installed."
    return 0
  fi
  if ! "$gh_bin" auth status >/dev/null 2>&1; then
    say "  Build attestation: skipped, because gh isn't signed in (gh auth login)."
    return 0
  fi
  if "$gh_bin" attestation verify "$dmg" --repo "$REPO" \
      --signer-workflow "$REPO/.github/workflows/release.yml" >/dev/null 2>&1; then
    say "  Build attestation: verified, built by this repository's release workflow."
  else
    die "gh couldn't verify the build attestation for $(basename "$dmg"). Nothing was installed. Run 'gh attestation verify' on the file to see why."
  fi
}

# Where VoiceFlow.app goes when --dir isn't given.
default_dir() {
  local system="/Applications" user="$HOME/Applications"
  if [ -e "$system/VoiceFlow.app" ]; then
    if [ -w "$system" ]; then printf '%s' "$system"; return; fi
    die "VoiceFlow is in $system but this account can't replace it. Ask an administrator to delete $system/VoiceFlow.app, or run with --dir \"$user\"."
  fi
  if [ -e "$user/VoiceFlow.app" ]; then printf '%s' "$user"; return; fi
  if [ -w "$system" ]; then printf '%s' "$system"; else printf '%s' "$user"; fi
}

install_voiceflow() {
  local version="$1" dir="$2" launch="$3" gh_bin="$4"
  local base dmg sums expected actual published app previous="" was_adhoc=0 other

  [ "$(uname -s)" = "Darwin" ] || die "This installer is for macOS. On Windows, see https://github.com/$REPO#windows"
  version_at_least "$(sw_vers -productVersion)" "14.0" || die "VoiceFlow needs macOS 14 or later."

  WORK="$(mktemp -d "${TMPDIR:-/tmp}/voiceflow-install.XXXXXX")"

  if [ -z "$version" ]; then version="$(latest_version)"; fi
  version="${version#v}"
  valid_version "$version" || die "\"$version\" isn't a version number like 0.2.0."
  version_at_least "$version" "$MIN_VERSION" || die "VoiceFlow $version was ad hoc signed, so this installer can't check who built it. Install $MIN_VERSION or later, or download $version from https://github.com/$REPO/releases/tag/v$version"

  say "Installing VoiceFlow $version"
  base="https://github.com/$REPO/releases/download/v$version"
  dmg="$WORK/VoiceFlow-$version-macOS.dmg"
  sums="$WORK/SHA256SUMS.txt"
  fetch "$base/SHA256SUMS.txt" "$sums" || die "Couldn't download SHA256SUMS.txt for $version. Is $version a released version?"
  say "  Downloading $(basename "$dmg")..."
  fetch "$base/$(basename "$dmg")" "$dmg" || die "Couldn't download $(basename "$dmg")."

  expected="$(awk -v f="$(basename "$dmg")" '$2 == f || $2 == "*" f { print $1 }' "$sums")"
  printf '%s' "$expected" | grep -Eq '^[0-9a-f]{64}$' || die "SHA256SUMS.txt has no single checksum for $(basename "$dmg")."
  actual="$(shasum -a 256 "$dmg" | awk '{ print $1 }')"
  [ "$actual" = "$expected" ] || die "The download's SHA-256 doesn't match SHA256SUMS.txt. Nothing was installed."
  say "  SHA-256: matches SHA256SUMS.txt."
  check_attestation "$dmg" "$gh_bin"

  # Releases from 0.3.0 publish the requirement they were signed with. It has to agree with the pin above.
  if fetch "$base/designated-requirement.txt" "$WORK/designated-requirement.txt" 2>/dev/null; then
    published="$(sed -n 's/^designated => //p' "$WORK/designated-requirement.txt" | head -n 1)"
    [ "$published" = "$EXPECTED_REQUIREMENT" ] || die "This release says it is signed with a different certificate than this installer expects. Get the installer again from the main branch; if it still fails, don't install this build and open an issue."
  fi

  MOUNT="$WORK/mount"
  mkdir "$MOUNT"
  hdiutil attach -nobrowse -readonly -noautoopen -mountpoint "$MOUNT" "$dmg" >/dev/null \
    || { MOUNT=""; die "Couldn't open the DMG."; }
  app="$MOUNT/VoiceFlow.app"
  is_voiceflow "$app" || die "The DMG doesn't contain VoiceFlow.app."
  [ "$(bundle_value "$app" CFBundleShortVersionString)" = "$version" ] || die "The DMG holds a different version than $version."
  check_signature "$app"
  say "  Code signature: $BUNDLE_ID, signed by the VoiceFlow certificate."

  if [ -z "$dir" ]; then dir="$(default_dir)"; fi
  mkdir -p "$dir" || die "Couldn't create $dir."
  dir="$(cd "$dir" && pwd -P)"
  [ -w "$dir" ] || die "This account can't write to $dir."
  DEST="$dir/VoiceFlow.app"
  if [ -e "$DEST" ]; then
    is_voiceflow "$DEST" || die "$DEST exists and isn't VoiceFlow, so it was left alone."
    previous="$(bundle_value "$DEST" CFBundleShortVersionString)"
    if codesign -dv "$DEST" 2>&1 | grep -q 'Signature=adhoc'; then was_adhoc=1; fi
  fi

  # Copy next to the destination, so the final step is a rename on the same disk.
  STAGED="$dir/.VoiceFlow.app.installing.$$"
  ditto "$app" "$STAGED"
  hdiutil detach -quiet "$MOUNT" >/dev/null 2>&1 || true
  MOUNT=""
  check_signature "$STAGED"
  # curl doesn't add quarantine, so this normally does nothing. If something did add it, the copy has
  # passed the checksum and signature checks above, which is what Gatekeeper's first-launch prompt is for.
  if xattr -p com.apple.quarantine "$STAGED" >/dev/null 2>&1; then
    xattr -dr com.apple.quarantine "$STAGED"
    note "Removed a quarantine flag from the verified copy."
  fi

  quit_copy "$DEST"
  if [ -e "$DEST" ]; then
    BACKUP="$dir/.VoiceFlow.app.previous.$$"
    mv "$DEST" "$BACKUP" || die "Couldn't move the old VoiceFlow aside."
  fi
  mv "$STAGED" "$DEST" || die "Couldn't put the new VoiceFlow in $dir."
  STAGED=""
  if [ -n "$BACKUP" ]; then
    rm -rf "$BACKUP" 2>/dev/null || note "Couldn't delete the old copy at $BACKUP. Delete it in Finder."
    BACKUP=""
  fi

  /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$DEST" >/dev/null 2>&1 || true

  if [ -z "$previous" ]; then
    say "Installed VoiceFlow $version in $dir."
  elif [ "$previous" = "$version" ]; then
    say "Reinstalled VoiceFlow $version in $dir."
  else
    say "Updated VoiceFlow from $previous to $version in $dir."
  fi
  if xattr -p com.apple.quarantine "$DEST" >/dev/null 2>&1; then
    note "The app still has a quarantine flag, so macOS may ask you to approve it once."
  fi
  if [ "$was_adhoc" = 1 ]; then
    say "The previous copy had a different signature, so macOS asks for Microphone and Accessibility once more."
  elif [ -z "$previous" ]; then
    say "On first launch VoiceFlow asks for your Groq key, then for Microphone and Accessibility."
  fi
  if [ -d /opt/homebrew/Caskroom/voiceflow ] || [ -d /usr/local/Caskroom/voiceflow ]; then
    note "Homebrew also manages VoiceFlow here. 'brew upgrade' may replace this copy; that's fine, it is the same app."
  fi

  if [ "$launch" = 1 ]; then
    other="$(running_copies | grep -vxF "$DEST" | head -n 1 || true)"
    if [ -n "$other" ]; then
      say "Another VoiceFlow is running from $other, so the new one wasn't opened. Quit that one, then open $DEST."
    else
      open "$DEST"
      say "VoiceFlow is open. Look for its icon in the menu bar."
    fi
  fi
}

confirm() {
  local answer=""
  if ! (: </dev/tty) 2>/dev/null; then
    die "Can't ask for confirmation without a terminal. Add --yes to delete your VoiceFlow data."
  fi
  printf '%s [y/N] ' "$1" >/dev/tty
  read -r answer </dev/tty || true
  case "$answer" in y|Y|yes|YES|Yes) return 0 ;; *) return 1 ;; esac
}

# Runs a command, or only prints it with --dry-run.
# The message goes to file descriptor 3 (the terminal), so callers can silence the command's own output.
act() {
  if [ "$DRY_RUN" = 1 ]; then printf '  would run: %s\n' "$*" >&3; else "$@"; fi
}

uninstall_voiceflow() {
  local dir="$1" purge="$2" yes="$3" app found=0 support
  [ "$(uname -s)" = "Darwin" ] || die "This uninstaller is for macOS."

  local candidates
  if [ -n "$dir" ]; then candidates="$dir/VoiceFlow.app"; else candidates="/Applications/VoiceFlow.app
$HOME/Applications/VoiceFlow.app"; fi

  while IFS= read -r app; do
    [ -e "$app" ] || continue
    if ! is_voiceflow "$app"; then note "$app isn't VoiceFlow, so it was left alone."; continue; fi
    found=1
    if [ "$DRY_RUN" = 1 ]; then say "  would quit VoiceFlow if it is running from $app"; else quit_copy "$app"; fi
    act /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -u "$app" >/dev/null 2>&1 || true
    if [ "$DRY_RUN" = 1 ]; then act rm -rf "$app"; continue; fi
    if rm -rf "$app"; then say "Removed $app"; else note "Couldn't remove $app. Drag it to the Trash in Finder."; fi
  done <<EOF
$candidates
EOF
  [ "$found" = 1 ] || say "VoiceFlow.app wasn't found${dir:+ in $dir}."

  support="$HOME/Library/Application Support/VoiceFlow"
  if [ "$purge" != 1 ]; then
    if [ -d "$support" ]; then
      say "Your settings and history are still in $support. Run again with --uninstall --purge to delete them."
    fi
    return 0
  fi

  if [ "$DRY_RUN" != 1 ] && [ -n "$(running_copies)" ]; then
    die "VoiceFlow is still running from $(running_copies | head -n 1). Quit it, then run --uninstall --purge again."
  fi
  if [ "$yes" != 1 ] && [ "$DRY_RUN" != 1 ]; then
    confirm "Delete VoiceFlow's settings, history, dictionary, snippets, meetings and saved Groq key?" \
      || { say "Kept your data."; return 0; }
  fi
  act rm -rf "$support"
  act rm -rf "$HOME/Library/Caches/$BUNDLE_ID" "$HOME/Library/HTTPStorages/$BUNDLE_ID" \
    "$HOME/Library/Saved Application State/$BUNDLE_ID.savedState"
  act defaults delete "$BUNDLE_ID" >/dev/null 2>&1 || true
  act security delete-generic-password -s "$BUNDLE_ID" -a groq >/dev/null 2>&1 || true
  act security delete-generic-password -s "$BUNDLE_ID" -a gemini >/dev/null 2>&1 || true
  if [ "$DRY_RUN" = 1 ]; then return 0; fi
  say "Deleted VoiceFlow's data. Meeting notes you saved to a folder of your own are kept."
  say "To remove its permissions, open System Settings > Privacy & Security, then Accessibility and Microphone."
}

DRY_RUN=0

main() {
  local version="${VOICEFLOW_VERSION:-}" dir="" launch=1 mode=install purge=0 yes=0 gh_bin=""
  if [ "${VOICEFLOW_NO_LAUNCH:-0}" = 1 ]; then launch=0; fi

  while [ $# -gt 0 ]; do
    case "$1" in
      --version) [ $# -ge 2 ] || die "--version needs a value, like 0.2.0"; version="$2"; shift 2 ;;
      --version=*) version="${1#*=}"; shift ;;
      --dir) [ $# -ge 2 ] || die "--dir needs a folder"; dir="$2"; shift 2 ;;
      --dir=*) dir="${1#*=}"; shift ;;
      --no-launch) launch=0; shift ;;
      --uninstall) mode=uninstall; shift ;;
      --purge) purge=1; shift ;;
      --yes|-y) yes=1; shift ;;
      --dry-run) DRY_RUN=1; shift ;;
      -h|--help) usage; return 0 ;;
      *) die "Unknown option $1. Run with --help to see the options." ;;
    esac
  done
  if [ "$purge" = 1 ] && [ "$mode" != uninstall ]; then die "--purge only goes with --uninstall."; fi
  if [ "$DRY_RUN" = 1 ] && [ "$mode" != uninstall ]; then die "--dry-run only goes with --uninstall."; fi

  # Find gh before narrowing PATH, then use only the system's own tools.
  gh_bin="$(command -v gh 2>/dev/null || true)"
  PATH="/usr/bin:/bin:/usr/sbin:/sbin"
  export PATH
  umask 022
  # When piped into bash, standard input is this script. Nothing below should read it.
  exec </dev/null 3>&1

  trap cleanup EXIT
  trap 'exit 130' INT
  trap 'exit 143' TERM HUP

  if [ "$mode" = uninstall ]; then
    uninstall_voiceflow "$dir" "$purge" "$yes"
  else
    install_voiceflow "$version" "$dir" "$launch" "$gh_bin"
  fi
}

main "$@"
