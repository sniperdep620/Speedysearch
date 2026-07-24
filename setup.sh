#!/usr/bin/env bash
set -Eeuo pipefail

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$project_dir"

with_e2e=false
run_checks=true
skip_system_dependencies="${SPEEDYSEARCH_SKIP_SYSTEM_DEPS:-false}"

usage() {
  cat <<'EOF'
Set up a Speedysearch development checkout.

Usage: ./setup.sh [options]

Options:
  --with-e2e            Install Chromium and run the browser tests
  --no-check            Install dependencies without running validation
  --skip-system-deps    Do not install Debian/Ubuntu native packages
  -h, --help            Show this help

Environment:
  SPEEDYSEARCH_SKIP_SYSTEM_DEPS=true
                        Equivalent to --skip-system-deps
EOF
}

while (($#)); do
  case "$1" in
    --with-e2e) with_e2e=true ;;
    --no-check) run_checks=false ;;
    --skip-system-deps) skip_system_dependencies=true ;;
    -h|--help) usage; exit 0 ;;
    *) printf 'Unknown option: %s\n\n' "$1" >&2; usage >&2; exit 2 ;;
  esac
  shift
done

info() {
  printf '\n\033[1;38;5;208mSpeedysearch setup\033[0m  %s\n' "$1"
}

fail() {
  printf '\n\033[1;31mSetup failed:\033[0m %s\n' "$1" >&2
  exit 1
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || fail "$2"
}

install_debian_dependencies() {
  local packages=(
    build-essential
    curl
    desktop-file-utils
    file
    libayatana-appindicator3-dev
    libgtk-3-dev
    librsvg2-dev
    libssl-dev
    libwebkit2gtk-4.1-dev
    libxdo-dev
    wget
  )
  local missing=()
  local package

  command -v apt-get >/dev/null 2>&1 || return
  command -v dpkg-query >/dev/null 2>&1 || return

  command -v pkg-config >/dev/null 2>&1 || packages+=(pkg-config)
  if ! command -v rustup >/dev/null 2>&1; then
    cargo fmt --version >/dev/null 2>&1 || packages+=(rustfmt)
    cargo clippy --version >/dev/null 2>&1 || packages+=(rust-clippy)
  fi

  for package in "${packages[@]}"; do
    if ! dpkg-query -W -f='${Status}' "$package" 2>/dev/null \
      | grep -Fq 'ok installed'; then
      missing+=("$package")
    fi
  done

  ((${#missing[@]} == 0)) && return

  if [[ "$skip_system_dependencies" == "true" ]]; then
    printf 'Skipping missing system packages: %s\n' "${missing[*]}"
    return
  fi

  info "Installing native build packages (sudo may prompt)"
  if ((EUID == 0)); then
    apt-get update
    apt-get install -y "${missing[@]}"
  elif command -v sudo >/dev/null 2>&1; then
    sudo apt-get update
    sudo apt-get install -y "${missing[@]}"
  else
    fail "Missing native packages and sudo is unavailable: ${missing[*]}"
  fi
}

require_command cargo "Rust and Cargo are required. Install them from https://rustup.rs/"
require_command rustc "Rust and Cargo are required. Install them from https://rustup.rs/"
require_command node "Node.js 20.19+ or 22.12+ is required: https://nodejs.org/"
require_command npm "npm 10 or newer is required and normally ships with Node.js."

if ! node -e '
  const [major, minor] = process.versions.node.split(".").map(Number);
  process.exit((major === 20 && minor >= 19) || (major === 22 && minor >= 12) || major > 22 ? 0 : 1);
'; then
  fail "Node.js 20.19+ or 22.12+ is required; found $(node --version)."
fi

npm_major="$(npm --version | sed -E 's/^([0-9]+).*/\1/')"
[[ "$npm_major" =~ ^[0-9]+$ ]] && ((npm_major >= 10)) \
  || fail "npm 10 or newer is required; found $(npm --version)."

install_debian_dependencies

if command -v rustup >/dev/null 2>&1; then
  info "Installing Rust formatting and linting components"
  rustup component add rustfmt clippy
elif ! cargo fmt --version >/dev/null 2>&1 \
  || ! cargo clippy --version >/dev/null 2>&1; then
  fail "rustfmt and Clippy are required. Install Rust with rustup, or add both tools through your system package manager."
fi

info "Installing locked JavaScript dependencies"
npm ci

info "Fetching locked Rust dependencies"
cargo fetch --locked
cargo fetch --manifest-path src-tauri/Cargo.toml --locked

if [[ "$with_e2e" == "true" ]]; then
  info "Installing the Playwright Chromium browser"
  npx playwright install chromium
fi

if [[ "$run_checks" == "true" ]]; then
  info "Validating the checkout"
  check_args=()
  [[ "$with_e2e" == "true" ]] && check_args+=(--with-e2e)
  ./scripts/check.sh "${check_args[@]}"
fi

info "Ready"
printf 'Start the desktop app with ./start.sh\n'
