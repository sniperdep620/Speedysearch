#!/usr/bin/env bash
set -Eeuo pipefail

if (( $# != 1 )) || [[ -z "$1" ]] || [[ "$1" != /* ]] || [[ "$1" == "/" ]]; then
  printf 'Usage: %s /absolute/XDG_DATA_HOME\n' "${0##*/}" >&2
  exit 2
fi

data_home="${1%/}"
legacy_install_root="$data_home/cosmic-search"
legacy_binary="$legacy_install_root/bin/cosmic-search-ui"
legacy_desktop_file="$data_home/applications/dev.cosmic.search.desktop"
legacy_icon_file="$data_home/icons/hicolor/128x128/apps/dev.cosmic.search.png"

# Remove the old application-manager identity even if its integration helper
# cannot safely restore a launcher setting changed by the user.
rm -f -- "$legacy_desktop_file" "$legacy_icon_file"

if [[ -x "$legacy_binary" ]]; then
  if "$legacy_binary" --restore-system-search; then
    rm -f -- "$legacy_binary"
    rmdir -- "$legacy_install_root/bin" "$legacy_install_root" 2>/dev/null || true
  else
    printf 'Warning: the legacy Cosmic Search launcher setting could not be restored; its hidden binary was kept at %s.\n' \
      "$legacy_binary" >&2
  fi
fi
