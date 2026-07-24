#!/usr/bin/env bash
set -Eeuo pipefail

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$project_dir"

if [[ "$(uname -s)" != "Linux" ]]; then
  printf 'Speedysearch cannot replace the system search provider on %s.\n' "$(uname -s)" >&2
  printf 'Build the native Tauri package and use the in-app global shortcut instead.\n' >&2
  exit 1
fi

command -v npm >/dev/null 2>&1 || { printf 'npm is required.\n' >&2; exit 1; }

data_home="${XDG_DATA_HOME:-$HOME/.local/share}"
install_root="$data_home/speedysearch"
binary_dir="$install_root/bin"
applications_dir="$data_home/applications"
icon_dir="$data_home/icons/hicolor/128x128/apps"
installed_binary="$binary_dir/speedysearch-ui"
desktop_file="$applications_dir/dev.speedysearch.desktop"
desktop_template="$project_dir/src-tauri/linux/dev.speedysearch.desktop"

printf 'Building Speedysearch…\n'
npm run tauri build -- --no-bundle

mkdir -p "$binary_dir" "$applications_dir" "$icon_dir"
install -m 0755 "$project_dir/src-tauri/target/release/speedysearch-ui" "$installed_binary"
install -m 0644 "$project_dir/src-tauri/icons/icon.png" "$icon_dir/dev.speedysearch.png"

temporary_desktop="$(mktemp --suffix=.desktop)"
trap 'rm -f "$temporary_desktop"' EXIT
while IFS= read -r line || [[ -n "$line" ]]; do
  if [[ "$line" == Exec=* ]]; then
    printf 'Exec=%s\n' "${installed_binary// /\\ }"
  else
    printf '%s\n' "$line"
  fi
done < "$desktop_template" > "$temporary_desktop"

if command -v desktop-file-validate >/dev/null 2>&1; then
  desktop-file-validate "$temporary_desktop"
fi
install -m 0644 "$temporary_desktop" "$desktop_file"

# Versions before 0.2.0 used the Cosmic Search name and application ID. Remove
# that stale application-manager entry only after the replacement is installed.
"$project_dir/scripts/migrate-legacy-user-install.sh" "$data_home"

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$applications_dir"
fi

desktop="${XDG_CURRENT_DESKTOP:-}"
if [[ ":${desktop,,}:" == *":cosmic:"* ]] \
  || [[ -f /usr/share/cosmic/com.system76.CosmicSettings.Shortcuts/v1/system_actions ]]; then
  printf 'Installed. COSMIC Settings can now discover Speedysearch.\n'
  printf 'Your current launcher is unchanged. To replace it later, run: npm run system-search:enable\n'
else
  printf 'Installed. Your desktop can now discover Speedysearch in its application settings.\n'
  printf 'This desktop has no standard replaceable search-provider role; configure its global shortcut if desired.\n'
fi
