#!/usr/bin/env bash
# Installs Disc Image Studio for the current user (no root): the app stays in this folder's
# copy under $PREFIX/lib/disc-image-studio, the launcher goes to $PREFIX/bin, and the desktop
# entry, icon and metainfo to $PREFIX/share. PREFIX defaults to ~/.local.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
prefix="${PREFIX:-$HOME/.local}"
app_dir="$prefix/lib/disc-image-studio"

rm -rf "$app_dir"
mkdir -p "$app_dir" "$prefix/bin"
cp -a "$here/lib/." "$app_dir/"
ln -sfn "$app_dir/DiscImageStudio.App.Avalonia" "$prefix/bin/disc-image-studio"
install -Dm644 "$here/share/applications/disc-image-studio.desktop" "$prefix/share/applications/disc-image-studio.desktop"
sed -i "s|^Exec=.*|Exec=$prefix/bin/disc-image-studio|" "$prefix/share/applications/disc-image-studio.desktop"
install -Dm644 "$here/share/icons/hicolor/256x256/apps/disc-image-studio.png" "$prefix/share/icons/hicolor/256x256/apps/disc-image-studio.png"
for file in "$here"/share/metainfo/*.xml; do
  install -Dm644 "$file" "$prefix/share/metainfo/$(basename "$file")"
done
command -v update-desktop-database >/dev/null && update-desktop-database -q "$prefix/share/applications" || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q -t "$prefix/share/icons/hicolor" || true
echo "Installed. Start it from the application menu or run: $prefix/bin/disc-image-studio"
