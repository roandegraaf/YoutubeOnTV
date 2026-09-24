#!/usr/bin/env bash
# Downloads the reference assemblies the build compiles against into libs/.
#
# Only needed on a machine without a local Lethal Company install (the csproj
# prefers the Steam install when it is present). libs/ is gitignored, so run
# this once after cloning.

set -euo pipefail

GAMELIBS_VERSION="81.0.5-ngd.0"
YOUTUBEDLSHARP_PACKAGE="Lordfirespeed/YoutubeDLSharp/1.1.0"

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LIBS="$ROOT/libs"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

mkdir -p "$LIBS"

echo "Fetching Lethal Company reference assemblies ($GAMELIBS_VERSION)..."
curl -sSL --fail -o "$TMP/gamelibs.nupkg" \
  "https://api.nuget.org/v3-flatcontainer/lethalcompany.gamelibs.steam/$GAMELIBS_VERSION/lethalcompany.gamelibs.steam.$GAMELIBS_VERSION.nupkg"

for dll in Assembly-CSharp Unity.Netcode.Runtime Unity.TextMeshPro UnityEngine.UI; do
  unzip -o -j -q "$TMP/gamelibs.nupkg" "ref/netstandard2.1/$dll.dll" -d "$LIBS"
  echo "  $dll.dll"
done

fetch_thunderstore() {
  local package="$1" dll="$2"
  echo "Fetching $dll ($package)..."
  curl -sSL --fail -o "$TMP/package.zip" "https://thunderstore.io/package/download/$package/"
  unzip -o -j -q "$TMP/package.zip" "BepInEx/plugins/$dll" -d "$LIBS"
  echo "  $dll"
}

fetch_thunderstore "$YOUTUBEDLSHARP_PACKAGE" YoutubeDLSharp.dll

echo
echo "Reference assemblies in libs/:"
ls -1 "$LIBS"
