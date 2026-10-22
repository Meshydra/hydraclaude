#!/bin/sh
# Builds HydraClaude.app with the Swift toolchain from Xcode or the Command Line Tools - no
# Xcode project, no dependencies.
#
# Output goes to ~/Applications/HydraClaude.app by default, outside Claude.app, so a Claude
# Desktop auto-update never touches it. Pass a folder to build somewhere else:
#
#     ./build.sh            # -> ~/Applications/HydraClaude.app
#     ./build.sh dist       # -> dist/HydraClaude.app
#
# Regenerate the icons first (needs Python + Pillow) with:  python3 ../tools/make_icons.py

set -eu

HERE=$(cd "$(dirname "$0")" && pwd)
ASSETS="$HERE/../assets"
OUTDIR=${1:-"$HOME/Applications"}
APP="$OUTDIR/HydraClaude.app"

for f in hydra.icns hydra-menubar.png hydra-menubar@2x.png; do
  if [ ! -f "$ASSETS/$f" ]; then
    echo "ERROR: assets/$f missing - run: python3 tools/make_icons.py" >&2
    exit 1
  fi
done

swift build --package-path "$HERE" -c release --arch arm64 --arch x86_64
BIN=$(swift build --package-path "$HERE" -c release --arch arm64 --arch x86_64 --show-bin-path)

mkdir -p "$OUTDIR"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN/HydraClaude" "$APP/Contents/MacOS/HydraClaude"
cp "$HERE/Resources/Info.plist" "$APP/Contents/Info.plist"
cp "$ASSETS/hydra.icns" "$ASSETS/hydra-menubar.png" "$ASSETS/hydra-menubar@2x.png" "$APP/Contents/Resources/"

# Ad-hoc signature: enough for Apple silicon to run it and for Launch Services to treat the
# bundle as one app. Distribution builds would sign with a Developer ID and notarise instead.
codesign --force --sign - "$APP"

echo "Built $APP"
