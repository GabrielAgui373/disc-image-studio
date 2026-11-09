#!/usr/bin/env bash
# Builds the self-contained Linux GUI archive:
#   DiscImageStudio-Linux-<tag>-<rid>.tar.gz (+ .sha256)
# Layout inside the archive (one top folder, relocatable):
#   disc-image-studio-<tag>/
#     disc-image-studio                launcher
#     lib/                             the published app
#     share/applications/disc-image-studio.desktop
#     share/icons/hicolor/256x256/apps/disc-image-studio.png
#     share/metainfo/io.github.jiafeimiao_k_cat.discimagestudio.metainfo.xml
#     install.sh / uninstall.sh        per-user install into ~/.local (or PREFIX)
#     LICENSE, THIRD-PARTY-NOTICES.txt
# Usage: packaging/linux/build-linux-gui.sh <rid> <tag> <output-directory>
set -euo pipefail

rid="${1:?runtime identifier, e.g. linux-x64}"
tag="${2:?release tag, e.g. v1.3.6}"
out="${3:?output directory}"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
app_id="io.github.jiafeimiao_k_cat.discimagestudio"
name="disc-image-studio-${tag}"
archive="DiscImageStudio-Linux-${tag}-${rid}.tar.gz"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
stage="$work/$name"

version="$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' "$root/Directory.Build.props" | head -n1)"
suffix="$(sed -n 's:.*<VersionSuffix>\(.*\)</VersionSuffix>.*:\1:p' "$root/Directory.Build.props" | head -n1)"
[[ -n "$suffix" ]] && version="$version-$suffix"

dotnet publish "$root/src/DiscImageStudio.App.Avalonia/DiscImageStudio.App.Avalonia.csproj" \
  --configuration Release --runtime "$rid" --self-contained true \
  -p:PublishTrimmed=false -p:DebugType=none \
  --output "$stage/lib"

install -Dm755 /dev/stdin "$stage/disc-image-studio" <<'LAUNCHER'
#!/bin/sh
# Starts Disc Image Studio from wherever this folder lives.
here="$(dirname "$(readlink -f "$0")")"
exec "$here/lib/DiscImageStudio.App.Avalonia" "$@"
LAUNCHER

install -Dm644 "$root/packaging/linux/disc-image-studio.desktop" "$stage/share/applications/disc-image-studio.desktop"
install -Dm644 "$root/src/DiscImageStudio.App/Assets/DiscImageStudio-256.png" "$stage/share/icons/hicolor/256x256/apps/disc-image-studio.png"
sed -e "s/@VERSION@/$version/" -e "s/@DATE@/$(date -u +%Y-%m-%d)/" \
  "$root/packaging/linux/$app_id.metainfo.xml" > "$work/metainfo.xml"
install -Dm644 "$work/metainfo.xml" "$stage/share/metainfo/$app_id.metainfo.xml"
install -Dm755 "$root/packaging/linux/install.sh" "$stage/install.sh"
install -Dm755 "$root/packaging/linux/uninstall.sh" "$stage/uninstall.sh"
install -Dm644 "$root/LICENSE" "$stage/LICENSE"
install -Dm644 "$root/THIRD-PARTY-NOTICES.txt" "$stage/THIRD-PARTY-NOTICES.txt"

mkdir -p "$out"
tar -czf "$out/$archive" -C "$work" "$name"
(cd "$out" && sha256sum "$archive" > "$archive.sha256")
echo "$out/$archive"
