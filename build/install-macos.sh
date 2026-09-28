#!/usr/bin/env bash
# One-line install for Macs (free — no Apple Developer account involved):
#   curl -fsSL https://raw.githubusercontent.com/BenDayan123/map-generator/main/build/install-macos.sh | bash
# curl downloads aren't quarantined, so Gatekeeper never blocks the app — this is the
# "just works" path for our ad-hoc-signed build.
set -euo pipefail

URL="https://github.com/BenDayan123/map-generator/releases/latest/download/MyMapsGenerator-osx-arm64.dmg"
APP="My Maps Generator.app"

[ "$(uname -m)" = "arm64" ] || { echo "This build is for Apple Silicon (M1 or newer) Macs." >&2; exit 1; }

TMP="$(mktemp -d)"
trap 'hdiutil detach "$TMP/mnt" -quiet 2>/dev/null || true; rm -rf "$TMP"' EXIT
echo "Downloading My Maps Generator..."
curl -fL --progress-bar "$URL" -o "$TMP/app.dmg"
mkdir "$TMP/mnt"
hdiutil attach "$TMP/app.dmg" -nobrowse -quiet -mountpoint "$TMP/mnt"
if [ ! -w "/Applications" ]; then
  echo "Your Mac account can't write to /Applications. Ask an administrator, or run this installer from an admin account." >&2
  exit 1
fi
osascript -e 'quit app "My Maps Generator"' 2>/dev/null || true
while pgrep -xq GmapPlanner.App; do sleep 0.5; done
# Copy beside the old app first and swap only once the copy succeeded, so a failed copy
# (disk full, etc.) never leaves the user with no app.
rm -rf "/Applications/$APP.new"
ditto "$TMP/mnt/$APP" "/Applications/$APP.new"
rm -rf "/Applications/$APP"
mv "/Applications/$APP.new" "/Applications/$APP"
xattr -dr com.apple.quarantine "/Applications/$APP" 2>/dev/null || true
echo "Installed to /Applications. Opening..."
open "/Applications/$APP"
