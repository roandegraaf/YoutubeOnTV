#!/usr/bin/env bash
# Builds the Thunderstore package. Unix counterpart of Build-Thunderstore.ps1.
#
# Usage: ./build-thunderstore.sh [BuildConfig]
# The version is read from manifest.json so it cannot drift from the package.

set -euo pipefail

BUILD_CONFIG="${1:-Debug}"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN="$ROOT/YoutubeOnTV/bin/$BUILD_CONFIG"
PACKAGE_DIR="$ROOT/thunderstore_package"
PLUGIN_DIR="$PACKAGE_DIR/BepInEx/plugins/YoutubeOnTV"

VERSION="$(python3 -c "import json;print(json.load(open('$ROOT/manifest.json'))['version_number'])")"
ZIP_NAME="YoutubeOnTV-$VERSION.zip"
ZIP_PATH="$ROOT/$ZIP_NAME"

echo "[1/6] Validating files..."
missing=0
for f in "$ROOT/manifest.json" "$ROOT/README.md" "$ROOT/icon.png" "$ROOT/CHANGELOG.md" \
         "$BIN/YoutubeOnTV.dll" "$BIN/fallback.mp4"; do
  if [ -f "$f" ]; then
    echo "  OK    ${f#$ROOT/}"
  else
    echo "  MISS  ${f#$ROOT/}"
    missing=1
  fi
done
if [ "$missing" -ne 0 ]; then
  echo
  echo "ERROR: missing required files. Run 'dotnet build YoutubeOnTV/YoutubeOnTV.csproj -c $BUILD_CONFIG' first." >&2
  exit 1
fi

echo "[2/6] Cleaning old build artifacts..."
rm -rf "$PACKAGE_DIR" "$ZIP_PATH"

echo "[3/6] Creating package structure..."
mkdir -p "$PLUGIN_DIR"

echo "[4/6] Copying mod files..."
cp "$BIN/YoutubeOnTV.dll" "$PLUGIN_DIR/"
cp "$BIN/fallback.mp4" "$PLUGIN_DIR/"

echo "[5/6] Copying package metadata..."
cp "$ROOT/manifest.json" "$ROOT/README.md" "$ROOT/icon.png" "$ROOT/CHANGELOG.md" "$PACKAGE_DIR/"

python3 - "$PACKAGE_DIR/icon.png" <<'PY'
import struct, sys
with open(sys.argv[1], 'rb') as f:
    header = f.read(24)
width, height = struct.unpack('>II', header[16:24])
status = "valid" if (width, height) == (256, 256) else "WARNING: Thunderstore requires 256x256"
print(f"  icon.png is {width}x{height} ({status})")
PY

echo "[6/6] Creating $ZIP_NAME..."
(
  cd "$PACKAGE_DIR"
  zip -r -q -X "$ZIP_PATH" manifest.json README.md icon.png CHANGELOG.md BepInEx \
    -x '*.DS_Store'
)

echo
echo "Build complete: $ZIP_NAME ($(du -h "$ZIP_PATH" | cut -f1))"
unzip -Z1 "$ZIP_PATH" | sed 's/^/  /'
echo
echo "Upload at https://thunderstore.io/c/lethal-company/create/"
