#!/usr/bin/env bash
# Removes what install.sh put under PREFIX (default ~/.local). Projects and settings stay.
set -euo pipefail
prefix="${PREFIX:-$HOME/.local}"
rm -rf "$prefix/lib/disc-image-studio"
rm -f "$prefix/bin/disc-image-studio" \
  "$prefix/share/applications/disc-image-studio.desktop" \
  "$prefix/share/icons/hicolor/256x256/apps/disc-image-studio.png" \
  "$prefix/share/metainfo/io.github.jiafeimiao_k_cat.discimagestudio.metainfo.xml"
command -v update-desktop-database >/dev/null && update-desktop-database -q "$prefix/share/applications" || true
echo "Removed. Projects, settings and caches under ~/.config, ~/.local/state and ~/.cache are kept."
