#!/usr/bin/env bash
# Wrap the osx-arm64 folder publish into an ad-hoc-signed .app, then a drag-install .dmg.
# macOS tools only (codesign, hdiutil, ditto, xattr) — no Homebrew, no Apple Developer account.
#
# Why sign at all: an unsealed bundle + the download quarantine makes macOS say "is damaged
# and can't be opened", with no way past it. A validly ad-hoc-signed bundle instead gets the
# normal "Apple could not verify…" prompt, cleared once via System Settings -> Privacy &
# Security -> Open Anyway (or skipped entirely by build/install-macos.sh). Every file in
# Contents/MacOS is signed inside-out (files, then the executable, then the bundle) — the
# Avalonia macOS recipe.
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
DMG_STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGING" "$DMG_STAGE"' EXIT
APP="$STAGING/$APP_NAME.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

# ditto keeps exec bits and symlinks that cp -R can mangle.
ditto "$PUBLISH_DIR" "$APP/Contents/MacOS"
chmod +x "$APP/Contents/MacOS/$EXE"
# Playwright's node driver must stay executable inside the bundle.
find "$APP/Contents/MacOS/.playwright" -type f -name node -exec chmod +x {} + 2>/dev/null || true
cp "$ROOT/src/GmapPlanner.App/Assets/icon.icns" "$APP/Contents/Resources/icon.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>CFBundleName</key><string>$APP_NAME</string>
  <key>CFBundleDisplayName</key><string>$APP_NAME</string>
  <key>CFBundleIdentifier</key><string>com.bendayan.mymapsgenerator</string>
  <key>CFBundleExecutable</key><string>$EXE</string>
  <key>CFBundleIconFile</key><string>icon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSPrincipalClass</key><string>NSApplication</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

# Stray xattrs (Finder info, quarantine) make codesign fail with "resource fork ... not allowed".
xattr -cr "$APP"

# Inside-out: every file under MacOS except the main executable, then the executable,
# then the bundle (which seals Info.plist and Resources).
find "$APP/Contents/MacOS" -type f ! -path "$APP/Contents/MacOS/$EXE" -print0 |
  while IFS= read -r -d '' f; do codesign --force --sign - "$f"; done
codesign --force --sign - "$APP/Contents/MacOS/$EXE"
# --deep here (only here): a plain bundle sign tries to validate loose executables under
# .playwright (no Info.plist, not a .framework/.xpc) as a nested "bundle" and fails with
# "bundle format unrecognized, invalid, or unsuitable"; --deep re-signs each nested Mach-O
# directly instead of doing that bundle-shaped discovery.
codesign --force --deep --sign - "$APP"
codesign --verify --deep --strict --verbose=2 "$APP"

# DMG layout: the app next to an /Applications shortcut, so the user just drags to install.
ditto "$APP" "$DMG_STAGE/$APP_NAME.app"
ln -s /Applications "$DMG_STAGE/Applications"
mkdir -p "$ROOT/build"
rm -f "$OUT_DMG"
hdiutil create -volname "$APP_NAME" -srcfolder "$DMG_STAGE" -ov -format UDZO "$OUT_DMG"

echo "Created $OUT_DMG (ad-hoc signed)"
