#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT="$ROOT_DIR/src/Jellyfin.Plugin.YtdlArchive/Jellyfin.Plugin.YtdlArchive.csproj"
SOLUTION="$ROOT_DIR/JellyfinYtdl.sln"
DIST_DIR="$ROOT_DIR/dist/YtdlArchive"
PACKAGE_DIR="$ROOT_DIR/dist/package"
ZIP_PATH="$PACKAGE_DIR/YtdlArchive-0.0.0.1.zip"
EXTENSION_DIST_DIR="$DIST_DIR/chrome-extension"
EXTENSION_ZIP_PATH="$PACKAGE_DIR/YtdlArchive-ChromeExtension-0.0.0.1.zip"

cd "$ROOT_DIR"

find "$ROOT_DIR" -name '._*' ! -path "$ROOT_DIR/.git/*" -delete
rm -rf "$DIST_DIR" "$PACKAGE_DIR"
mkdir -p "$DIST_DIR" "$PACKAGE_DIR"

python3 -m py_compile "$ROOT_DIR/yt-downloader-server.py"
dotnet test "$SOLUTION" -c Release
dotnet publish "$PROJECT" -c Release -o "$DIST_DIR"
find "$DIST_DIR" -name '._*' -delete
cp "$ROOT_DIR/packaging/YtdlArchive/meta.json" "$DIST_DIR/meta.json"
mkdir -p "$EXTENSION_DIST_DIR"
cp "$ROOT_DIR"/extension/* "$EXTENSION_DIST_DIR"/
find "$EXTENSION_DIST_DIR" -name '._*' -delete
find "$DIST_DIR" -name '._*' -delete

for forbidden in \
  "$DIST_DIR"/Jellyfin.Controller.dll \
  "$DIST_DIR"/Jellyfin.Data.dll \
  "$DIST_DIR"/Jellyfin.Model.dll \
  "$DIST_DIR"/MediaBrowser.Common.dll \
  "$DIST_DIR"/MediaBrowser.Controller.dll \
  "$DIST_DIR"/MediaBrowser.Model.dll
do
  if [[ -e "$forbidden" ]]; then
    echo "Refusing package with Jellyfin host assembly: $forbidden" >&2
    exit 1
  fi
done

rm -f "$DIST_DIR"/*.pdb

(
  cd "$EXTENSION_DIST_DIR"
  find . -name '._*' -delete
  zip -q -r "$EXTENSION_ZIP_PATH" .
)

(
  cd "$DIST_DIR"
  find . -name '._*' -delete
  zip -q -r "$ZIP_PATH" .
)

echo "Plugin folder: $DIST_DIR"
echo "Plugin zip:    $ZIP_PATH"
echo "Chrome zip:    $EXTENSION_ZIP_PATH"
