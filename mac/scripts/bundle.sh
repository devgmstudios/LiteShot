#!/usr/bin/env bash
# Builds LiteShot.app (Apple silicon and Intel in one) and zips it: mac/dist/LiteShot-mac-<version>.zip.
# The app is signed ad hoc, so macOS asks once to open it (System Settings › Privacy & Security › Open Anyway).
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION="${VERSION:-0.3.1}"
BUILD="${BUILD:-1}"
swift build -c release --arch arm64 --arch x86_64
BIN="$(swift build -c release --arch arm64 --arch x86_64 --show-bin-path)/LiteShot"

APP="dist/LiteShot.app"
rm -rf dist
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/LiteShot"
sed -e "s/__VERSION__/$VERSION/" -e "s/__BUILD__/$BUILD/" Resources/Info.plist > "$APP/Contents/Info.plist"
# Find on screen's related words ("fish" finds salmon).
cp Sources/LiteShotCore/Resources/related-words.gz "$APP/Contents/Resources/"
# The text bar's fonts: the same files the Windows app bundles, with their licenses.
mkdir -p "$APP/Contents/Resources/Fonts"
cp ../src/LiteShot/Fonts/*.ttf ../src/LiteShot/Fonts/OFL.txt ../src/LiteShot/Fonts/Apache-LICENSE.txt "$APP/Contents/Resources/Fonts/"

# The app icon, from the website's.
ICONSET="$(mktemp -d)/LiteShot.iconset"
mkdir -p "$ICONSET"
SRC="../server/public/img/icon-256.png"
for s in 16 32 128 256; do
  sips -z "$s" "$s" "$SRC" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  sips -z $((s * 2)) $((s * 2)) "$SRC" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/LiteShot.icns"

codesign --force --sign - --timestamp=none "$APP"
codesign --verify --verbose "$APP"
ditto -c -k --keepParent "$APP" "dist/LiteShot-mac-$VERSION.zip"
echo "Built dist/LiteShot-mac-$VERSION.zip ($(lipo -archs "$APP/Contents/MacOS/LiteShot"))"
