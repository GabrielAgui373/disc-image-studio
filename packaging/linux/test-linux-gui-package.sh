#!/usr/bin/env bash
# Smoke test of a Linux GUI archive: layout, a per-user install into a temporary PREFIX,
# validation of the installed desktop entry and metainfo, then uninstall.
# Usage: packaging/linux/test-linux-gui-package.sh <archive.tar.gz>
set -euo pipefail
archive="${1:?archive}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

(cd "$(dirname "$archive")" && sha256sum --strict --check "$(basename "$archive").sha256")
tar -xzf "$archive" -C "$work"
top="$(find "$work" -mindepth 1 -maxdepth 1 -type d)"
for path in disc-image-studio lib/DiscImageStudio.App.Avalonia install.sh uninstall.sh LICENSE THIRD-PARTY-NOTICES.txt \
            share/applications/disc-image-studio.desktop share/icons/hicolor/256x256/apps/disc-image-studio.png \
            share/metainfo/io.github.jiafeimiao_k_cat.discimagestudio.metainfo.xml; do
  [[ -e "$top/$path" ]] || { echo "missing $path"; exit 1; }
done
[[ -x "$top/disc-image-studio" && -x "$top/lib/DiscImageStudio.App.Avalonia" ]] || { echo "launcher or app not executable"; exit 1; }
if grep -q "@VERSION@" "$top/share/metainfo/"*.xml; then echo "metainfo version not filled in"; exit 1; fi

prefix="$work/prefix"
PREFIX="$prefix" "$top/install.sh"
[[ -x "$prefix/bin/disc-image-studio" ]] || { echo "launcher not installed"; exit 1; }
grep -q "^Exec=$prefix/bin/disc-image-studio$" "$prefix/share/applications/disc-image-studio.desktop"
if command -v desktop-file-validate >/dev/null; then desktop-file-validate "$prefix/share/applications/disc-image-studio.desktop"; fi
if command -v appstreamcli >/dev/null; then appstreamcli validate --no-net "$prefix/share/metainfo/"*.xml; fi
PREFIX="$prefix" "$top/uninstall.sh"
[[ ! -e "$prefix/bin/disc-image-studio" && ! -e "$prefix/lib/disc-image-studio" ]] || { echo "uninstall left files"; exit 1; }
echo "package ok: $(basename "$archive")"
