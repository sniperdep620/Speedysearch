#!/usr/bin/env bash
set -Eeuo pipefail

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$project_dir"

with_e2e=false
if [[ "${1:-}" == "--with-e2e" ]]; then
  with_e2e=true
  shift
fi
if (($#)); then
  printf 'Usage: %s [--with-e2e]\n' "$0" >&2
  exit 2
fi

run() {
  printf '\n==> %s\n' "$*"
  "$@"
}

run cargo fmt --all -- --check
run ./scripts/verify-version.sh
run cargo clippy --locked --all-targets -- -D warnings
run cargo clippy --manifest-path src-tauri/Cargo.toml --locked --all-targets -- -D warnings
run cargo test --locked --lib --bins --tests
run cargo test --manifest-path src-tauri/Cargo.toml --locked --lib --bins --tests
run npm test
run npm run build

while IFS= read -r script; do
  run bash -n "$script"
done < <(find . -path './node_modules' -prune -o -path './target' -prune \
  -o -path './src-tauri/target' -prune -o -type f -name '*.sh' -print | sort)

if [[ "$with_e2e" == "true" ]]; then
  run npm run test:e2e
fi

printf '\nAll checks passed.\n'
