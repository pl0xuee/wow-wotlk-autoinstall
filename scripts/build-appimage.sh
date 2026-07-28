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

# Desktop integration reads the version from the entry, not from the assembly, so a hardcoded
# one makes every release claim to be the first.
sed -i "s/^X-AppImage-Version=.*/X-AppImage-Version=$VERSION/" \
    "$APPDIR/wow-wotlk-autoinstall.desktop"

# Downloads a pinned artifact and verifies it. The verification is deliberately outside the
# download, so a file already cached at that path is checked too — a gate that only runs on
# the download path is no gate at all, since the cache is what actually gets executed.
fetch_pinned() {
    local url=$1 dest=$2 want=$3
    if [[ ! -f "$dest" ]]; then
        curl -fLo "$dest" "$url"
    fi
    local actual
    actual="$(sha256sum "$dest" | cut -d' ' -f1)"
    if [[ "$actual" != "$want" ]]; then
        echo "ERROR: checksum mismatch for $dest (expected $want, got $actual)" >&2
        rm -f "$dest"
        exit 1
    fi
}

# appimagetool publishes versioned releases now, so pin a tag rather than the rolling
# "continuous" build whose contents change under a fixed URL.
APPIMAGETOOL_VERSION="${APPIMAGETOOL_VERSION:-1.9.1}"
APPIMAGETOOL="$ROOT/appimagetool-$APPIMAGETOOL_VERSION-x86_64.AppImage"
fetch_pinned \
    "https://github.com/AppImage/appimagetool/releases/download/$APPIMAGETOOL_VERSION/appimagetool-x86_64.AppImage" \
    "$APPIMAGETOOL" \
    "ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0"
chmod +x "$APPIMAGETOOL"

# The type2 runtime is the ELF stub that becomes the first bytes of the shipped AppImage —
# the code that actually executes on a user's machine. Left to itself appimagetool downloads
# it from a rolling "continuous" tag at build time with no verification, which would undo the
# point of pinning the tool. Pin and check it, and hand it over with --runtime-file.
RUNTIME_VERSION="${RUNTIME_VERSION:-20251108}"
RUNTIME="$ROOT/appimage-runtime-$RUNTIME_VERSION-x86_64"
fetch_pinned \
    "https://github.com/AppImage/type2-runtime/releases/download/$RUNTIME_VERSION/runtime-x86_64" \
    "$RUNTIME" \
    "2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d"

# APPIMAGE_EXTRACT_AND_RUN lets appimagetool run without FUSE (CI runners, containers).
# --no-appstream: this app ships no AppStream metainfo, and the validator is an extra
# dependency that would fail the build over a file we deliberately do not have.
APPIMAGE_EXTRACT_AND_RUN=1 "$APPIMAGETOOL" \
    --no-appstream --runtime-file "$RUNTIME" "$APPDIR" "$OUT"
chmod +x "$OUT"
echo "Built: $OUT"
