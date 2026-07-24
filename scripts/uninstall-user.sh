#!/usr/bin/env bash
set -Eeuo pipefail

data_home="${XDG_DATA_HOME:-$HOME/.local/share}"
install_root="$data_home/speedysearch"
installed_binary="$install_root/bin/speedysearch-ui"
desktop_file="$data_home/applications/dev.speedysearch.desktop"
icon_file="$data_home/icons/hicolor/128x128/apps/dev.speedysearch.png"

if [[ -x "$installed_binary" ]]; then
  "$installed_binary" --restore-system-search || {
    printf 'The system launcher was changed after installation; leaving it and the binary in place.\n' >&2
    exit 1
  }
fi

rm -f "$desktop_file" "$icon_file" "$installed_binary"
rmdir "$install_root/bin" "$install_root" 2>/dev/null || true
if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$data_home/applications"
fi
printf 'Speedysearch was removed and the previous system launcher was restored.\n'
