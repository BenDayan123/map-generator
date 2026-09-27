#!/bin/bash
# Double-click installer for macOS (the Mac twin of a Windows .bat): downloads the latest
# My Maps Generator into /Applications and opens it. Same as the README's Terminal one-liner.
echo "Installing My Maps Generator..."
echo
curl -fsSL https://raw.githubusercontent.com/BenDayan123/map-generator/main/build/install-macos.sh | bash
status=$?
echo
if [ $status -eq 0 ]; then echo "Done. You can close this window."; else echo "Install failed (code $status)."; fi
read -n 1 -s -r -p "Press any key to close..."
echo
