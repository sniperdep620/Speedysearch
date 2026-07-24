#!/usr/bin/env bash
set -Eeuo pipefail

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
temporary_root="$(mktemp -d)"
trap 'rm -rf -- "$temporary_root"' EXIT

fail() {
  printf 'application identity test failed: %s\n' "$1" >&2
  exit 1
}

assert_missing() {
  [[ ! -e "$1" ]] || fail "expected $1 to be removed"
}

assert_exists() {
  [[ -e "$1" ]] || fail "expected $1 to be preserved"
}

make_legacy_install() {
  local data_home="$1"
  local restore_exit="$2"
  mkdir -p \
    "$data_home/applications" \
    "$data_home/icons/hicolor/128x128/apps" \
    "$data_home/cosmic-search/bin"
  printf '[Desktop Entry]\nName=Cosmic Search\n' > "$data_home/applications/dev.cosmic.search.desktop"
  printf 'legacy icon\n' > "$data_home/icons/hicolor/128x128/apps/dev.cosmic.search.png"
  printf 'user data\n' > "$data_home/cosmic-search/keep-me"
  {
    printf '#!/usr/bin/env bash\n'
    printf 'printf "%%s\\n" "$1" > "%s"\n' "$data_home/restore-argument"
    printf 'exit %s\n' "$restore_exit"
  } > "$data_home/cosmic-search/bin/cosmic-search-ui"
  chmod +x "$data_home/cosmic-search/bin/cosmic-search-ui"
}

successful_home="$temporary_root/success/share"
make_legacy_install "$successful_home" 0
"$project_dir/scripts/migrate-legacy-user-install.sh" "$successful_home"
assert_missing "$successful_home/applications/dev.cosmic.search.desktop"
assert_missing "$successful_home/icons/hicolor/128x128/apps/dev.cosmic.search.png"
assert_missing "$successful_home/cosmic-search/bin/cosmic-search-ui"
assert_exists "$successful_home/cosmic-search/keep-me"
[[ "$(< "$successful_home/restore-argument")" == "--restore-system-search" ]] \
  || fail "legacy integration was not restored before removing its binary"

conflict_home="$temporary_root/conflict/share"
make_legacy_install "$conflict_home" 1
"$project_dir/scripts/migrate-legacy-user-install.sh" "$conflict_home"
assert_missing "$conflict_home/applications/dev.cosmic.search.desktop"
assert_missing "$conflict_home/icons/hicolor/128x128/apps/dev.cosmic.search.png"
assert_exists "$conflict_home/cosmic-search/bin/cosmic-search-ui"

desktop_file="$project_dir/src-tauri/linux/dev.speedysearch.desktop"
desktop-file-validate "$desktop_file"
grep -Fxq 'Name=Speedysearch' "$desktop_file" || fail "desktop name is not Speedysearch"
grep -Fxq 'Exec=speedysearch-ui' "$desktop_file" || fail "desktop executable is not speedysearch-ui"
grep -Fxq 'Icon=dev.speedysearch' "$desktop_file" || fail "desktop icon ID is not dev.speedysearch"
grep -Fxq 'StartupWMClass=dev.speedysearch' "$desktop_file" \
  || fail "desktop window identity is not dev.speedysearch"
grep -Fxq 'Categories={{categories}}FileTools;' \
  "$project_dir/src-tauri/linux/speedysearch.desktop.hbs" \
  || fail "bundle desktop categories would render an empty category"

node --input-type=module - "$project_dir" <<'NODE'
import { readFileSync } from "node:fs";

const projectDir = process.argv[2];
const config = JSON.parse(readFileSync(`${projectDir}/src-tauri/tauri.conf.json`, "utf8"));
const window = config.app.windows.find(({ label }) => label === "main");
const expected = {
  productName: "Speedysearch",
  identifier: "dev.speedysearch",
  title: "Speedysearch",
};
const actual = {
  productName: config.productName,
  identifier: config.identifier,
  title: window?.title,
};
if (JSON.stringify(actual) !== JSON.stringify(expected)) {
  throw new Error(`unexpected Tauri identity: ${JSON.stringify(actual)}`);
}
if (JSON.stringify(config.bundle.icon) !== JSON.stringify(["icons/icon.png"])) {
  throw new Error(`unexpected bundle icon: ${JSON.stringify(config.bundle.icon)}`);
}
NODE

printf 'application identity tests passed\n'
