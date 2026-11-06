#!/usr/bin/env bash
# Linux release packages from one self-contained single-file publish:
#   scripts/package.sh                  → dist/steamrip-desktop_<v>_amd64.deb
#                                         dist/steamrip-desktop-<v>-1.x86_64.rpm
#                                         dist/steamrip-desktop-<v>-1-x86_64.pkg.tar.zst   (Arch; the AUR -bin package repacks the .deb)
#                                         dist/steamrip-desktop-<v>-x86_64.AppImage
#   VERSION=1.2.3 scripts/package.sh    → override the version (default: nearest v* tag, else 0.0.0-dev)
# deb/rpm/arch come from packaging/nfpm.yaml via nfpm; the AppImage from packaging/linux/* via appimagetool.
# Both tools are downloaded (pinned) into obj/tools/ when not on PATH, so this runs the same locally and in CI.
# The Windows installer is built by scripts/package-windows.ps1 (needs Windows + Inno Setup).
set -euo pipefail
cd "$(git rev-parse --show-toplevel)"

app=steamrip-desktop
proj=src/SteamRip.App
dist=dist
tools=obj/tools
nfpm_version=2.48.0
version="${VERSION:-$(git describe --tags --match 'v*' --abbrev=0 2>/dev/null | sed 's/^v//')}"
version="${version:-0.0.0-dev}"
export VERSION="$version"

fetch() { # url dest
  [ -x "$2" ] && return
  echo "== fetching $(basename "$2")"
  mkdir -p "$(dirname "$2")"
  curl -fsSL --retry 3 -o "$2" "$1"
  chmod +x "$2"
}

nfpm() {
  if type -P nfpm >/dev/null; then command nfpm "$@"; return; fi
  if [ ! -x "$tools/nfpm" ]; then
    echo "== fetching nfpm $nfpm_version"
    mkdir -p "$tools"
    curl -fsSL --retry 3 "https://github.com/goreleaser/nfpm/releases/download/v$nfpm_version/nfpm_${nfpm_version}_Linux_x86_64.tar.gz" |
      tar -xz -C "$tools" nfpm
  fi
  "$tools/nfpm" "$@"
}

appimagetool() {
  if type -P appimagetool >/dev/null; then command appimagetool "$@"; return; fi
  fetch "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage" "$tools/appimagetool"
  # --appimage-extract-and-run: works without FUSE (containers, CI runners)
  "$tools/appimagetool" --appimage-extract-and-run "$@"
}

echo "== publish linux-x64 ($version)"
dotnet publish "$proj" -c Release -r linux-x64 -o out/linux-x64 --nologo -v quiet \
  -p:Version="$version" -p:DebugType=none -p:TreatWarningsAsErrors=true
mkdir -p "$dist"

for pkg in deb rpm archlinux; do
  echo "== nfpm $pkg"
  nfpm package --config packaging/nfpm.yaml --packager "$pkg" --target "$dist/"
done

echo "== AppImage"
appdir="obj/AppDir"
rm -rf "$appdir"
mkdir -p "$appdir/usr/bin" "$appdir/usr/share/applications" "$appdir/usr/share/icons/hicolor/256x256/apps"
cp "out/linux-x64/$app" "$appdir/usr/bin/$app"
cp "packaging/linux/$app.desktop" "$appdir/usr/share/applications/"
cp "$proj/Assets/app.png" "$appdir/usr/share/icons/hicolor/256x256/apps/$app.png"
ln -s "usr/bin/$app" "$appdir/AppRun"
ln -s "usr/share/applications/$app.desktop" "$appdir/$app.desktop"
ln -s "usr/share/icons/hicolor/256x256/apps/$app.png" "$appdir/$app.png"
ln -s "$app.png" "$appdir/.DirIcon"
ARCH=x86_64 appimagetool --no-appstream "$appdir" "$dist/$app-$version-x86_64.AppImage" >/dev/null 2>"$dist/.appimagetool.log" ||
  { cat "$dist/.appimagetool.log"; exit 1; }
rm -f "$dist/.appimagetool.log"

echo "== done"
ls -1 "$dist"
