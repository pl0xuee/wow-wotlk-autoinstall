#!/usr/bin/env bash
# Builds WowWotlkAutoinstall-x86_64.AppImage:
# self-contained dotnet publish -> AppDir -> appimagetool
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
APPDIR="$ROOT/packaging/WowWotlkAutoinstall.AppDir"
PUBLISH="$ROOT/publish"
OUT="$ROOT/WowWotlkAutoinstall-x86_64.AppImage"

VERSION="${VERSION:-0.1.0}"
DOTNET="${DOTNET:-dotnet}"

rm -rf "$PUBLISH" "$APPDIR/usr"
"$DOTNET" publish "$ROOT/src/WowWotlk.Gui/WowWotlk.Gui.csproj" \
    -c Release -r linux-x64 --self-contained -o "$PUBLISH" -p:Version="$VERSION"
rm -f "$PUBLISH"/*.pdb

mkdir -p "$APPDIR/usr/bin"
cp -r "$PUBLISH"/. "$APPDIR/usr/bin/"
chmod +x "$APPDIR/AppRun"

# appimagetool publishes versioned releases now, so pin a tag rather than the rolling
# "continuous" build whose contents change under a fixed URL. The SHA-256 is still checked: a
# tag can be moved, and a silently-swapped build tool is exactly the thing worth refusing.
APPIMAGETOOL_VERSION="${APPIMAGETOOL_VERSION:-1.9.1}"
APPIMAGETOOL_SHA256="ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0"
APPIMAGETOOL="$ROOT/appimagetool-$APPIMAGETOOL_VERSION-x86_64.AppImage"
if [[ ! -x "$APPIMAGETOOL" ]]; then
    curl -fLo "$APPIMAGETOOL" \
        "https://github.com/AppImage/appimagetool/releases/download/$APPIMAGETOOL_VERSION/appimagetool-x86_64.AppImage"
    actual="$(sha256sum "$APPIMAGETOOL" | cut -d' ' -f1)"
    if [[ "$actual" != "$APPIMAGETOOL_SHA256" ]]; then
        echo "ERROR: appimagetool checksum mismatch (expected $APPIMAGETOOL_SHA256, got $actual)" >&2
        rm -f "$APPIMAGETOOL"
        exit 1
    fi
    chmod +x "$APPIMAGETOOL"
fi

# APPIMAGE_EXTRACT_AND_RUN lets appimagetool run without FUSE (CI runners, containers).
# --no-appstream: this app ships no AppStream metainfo, and the validator is an extra
# dependency that would fail the build over a file we deliberately do not have.
APPIMAGE_EXTRACT_AND_RUN=1 "$APPIMAGETOOL" --no-appstream "$APPDIR" "$OUT"
chmod +x "$OUT"
echo "Built: $OUT"
