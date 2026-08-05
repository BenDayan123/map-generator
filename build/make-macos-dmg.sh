#!/usr/bin/env bash
# Wrap the osx-arm64 self-contained publish into a .app bundle, then a drag-install .dmg.
# Uses only hdiutil (ships with macOS) — no Homebrew. The app is unsigned, so first launch
# needs a right-click -> Open to clear Gatekeeper.
#
# Usage:  ./build/make-macos-dmg.sh <publish-dir> [version]
#   e.g.  dotnet publish src/GmapPlanner.App -c Release -r osx-arm64 -o publish/osx-arm64
#         ./build/make-macos-dmg.sh publish/osx-arm64 1.0.0
set -euo pipefail

PUBLISH_DIR="${1:?usage: make-macos-dmg.sh <publish-dir> [version]}"
VERSION="${2:-1.0.0}"

APP_NAME="My Maps Generator"
EXE="GmapPlanner.App"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT_DMG="$ROOT/build/MyMapsGenerator-osx-arm64.dmg"

if [ ! -e "$PUBLISH_DIR/$EXE" ]; then
  echo "Error: $PUBLISH_DIR/$EXE not found. Publish osx-arm64 first." >&2
  exit 1
fi

STAGING="$(mktemp -d)"
APP="$STAGING/$APP_NAME.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

cp -R "$PUBLISH_DIR/." "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/$EXE"
# Playwright's node driver must stay executable inside the bundle.
find "$APP/Contents/MacOS/.playwright" -name node -type f -exec chmod +x {} \; 2>/dev/null || true

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>$APP_NAME</string>
  <key>CFBundleDisplayName</key><string>$APP_NAME</string>
  <key>CFBundleIdentifier</key><string>com.bendayan.mymapsgenerator</string>
  <key>CFBundleExecutable</key><string>$EXE</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

# DMG layout: the app next to an /Applications shortcut, so the user just drags to install.
DMG_STAGE="$(mktemp -d)"
cp -R "$APP" "$DMG_STAGE/"
ln -s /Applications "$DMG_STAGE/Applications"

mkdir -p "$ROOT/build"
rm -f "$OUT_DMG"
hdiutil create -volname "$APP_NAME" -srcfolder "$DMG_STAGE" -ov -format UDZO "$OUT_DMG"

rm -rf "$STAGING" "$DMG_STAGE"
echo "Created $OUT_DMG"
