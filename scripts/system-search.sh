#!/usr/bin/env bash
set -Eeuo pipefail

action="${1:-status}"
data_home="${XDG_DATA_HOME:-$HOME/.local/share}"
installed_binary="$data_home/speedysearch/bin/speedysearch-ui"

if [[ ! -x "$installed_binary" ]]; then
  printf 'Speedysearch is not installed for this user. Run npm run install:user first.\n' >&2
  exit 1
fi

case "$action" in
  enable)
    "$installed_binary" --replace-system-search
    ;;
  restore|disable)
    "$installed_binary" --restore-system-search
    ;;
  status)
    "$installed_binary" --system-search-status
    ;;
  *)
    printf 'Usage: %s {enable|restore|status}\n' "$0" >&2
    exit 2
    ;;
esac
