#!/bin/zsh
# Builds a release VoiceFlow.app, installs it in /Applications and launches it.
set -euo pipefail

ROOT="${0:A:h:h}"
"$ROOT/scripts/build-app.sh" release

# Quit any running copy and wait until it is gone: VoiceFlow allows one copy, so a copy still shutting
# down would make the new one quit straight away.
pkill -x VoiceFlow 2>/dev/null || true
for _ in {1..50}; do pgrep -x VoiceFlow >/dev/null || break; sleep 0.1; done
pkill -9 -x VoiceFlow 2>/dev/null || true
rm -rf /Applications/VoiceFlow.app
cp -R "$ROOT/build/VoiceFlow.app" /Applications/VoiceFlow.app

# An ad-hoc signed rebuild has a new code signature, so macOS's old permission entries no longer
# match it and only cause "already on but not working" confusion. Clear them so the app asks again.
if codesign -dv /Applications/VoiceFlow.app 2>&1 | grep -q "Signature=adhoc"; then
  for service in Accessibility Microphone ListenEvent; do
    tccutil reset "$service" com.saideep.voiceflow >/dev/null 2>&1 || true
  done
  echo "Permissions reset: VoiceFlow will ask for Microphone and Accessibility again."
fi

# Make Finder and the Dock pick up the current icon.
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f /Applications/VoiceFlow.app >/dev/null 2>&1 || true
touch /Applications/VoiceFlow.app

open /Applications/VoiceFlow.app
echo "Installed /Applications/VoiceFlow.app"
