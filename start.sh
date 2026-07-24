#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT_DIR"

info() {
  printf '\n\033[1;38;5;208mSpeedysearch\033[0m  %s\n' "$1"
}

fail() {
  printf '\n\033[1;31mError:\033[0m %s\n' "$1" >&2
  exit 1
}

command -v cargo >/dev/null 2>&1 || fail "Rust/Cargo is required. Install it from https://rustup.rs"
command -v node >/dev/null 2>&1 || fail "Node.js 20 or newer is required."
command -v npm >/dev/null 2>&1 || fail "npm is required."

NODE_MAJOR="$(node --version | sed -E 's/^v([0-9]+).*/\1/')"
if [[ ! "$NODE_MAJOR" =~ ^[0-9]+$ ]] || (( NODE_MAJOR < 20 )); then
  fail "Node.js 20 or newer is required; found $(node --version)."
fi

install_native_dependencies() {
  local packages=(
    libgtk-3-dev
    libwebkit2gtk-4.1-dev
    libxdo-dev
    libssl-dev
    libayatana-appindicator3-dev
    librsvg2-dev
  )

  if command -v pkg-config >/dev/null 2>&1 \
    && pkg-config --exists gtk+-3.0 webkit2gtk-4.1; then
    return
  fi

  if ! command -v apt-get >/dev/null 2>&1; then
    fail "GTK3 and WebKitGTK development libraries are missing. See README.md for installation instructions."
  fi

  info "Native Tauri libraries are missing."
  if [[ ! -t 0 ]]; then
    fail "Run this script in a terminal so it can ask permission to install: ${packages[*]}"
  fi

  printf 'Install the required Pop!_OS/Ubuntu packages now? [Y/n] '
  read -r answer
  case "${answer:-Y}" in
    y|Y|yes|YES)
      sudo apt-get install -y "${packages[@]}"
      ;;
    *)
      fail "The native libraries are required to launch Speedysearch."
      ;;
  esac
}

install_native_dependencies

if [[ ! -x node_modules/.bin/tauri ]] \
  || [[ package.json -nt node_modules/.package-lock.json ]] \
  || [[ package-lock.json -nt node_modules/.package-lock.json ]]; then
  info "Installing frontend dependencies…"
  npm ci
fi

info "Starting the launcher…"
printf 'Use \033[1mCtrl+Space\033[0m to show or hide it. Open \033[1mSettings → Keyboard shortcut\033[0m to change that.\n\n'

exec npm run tauri dev
