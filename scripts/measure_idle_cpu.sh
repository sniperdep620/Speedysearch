#!/usr/bin/env bash
set -euo pipefail

binary="${1:-./target/release/speedysearch}"
samples="${SAMPLES:-30}"
temporary_home="$(mktemp -d)"
pid=""

cleanup() {
    if [[ -n "$pid" ]]; then
        kill "$pid" 2>/dev/null || true
        wait "$pid" 2>/dev/null || true
    fi
    rm -rf "$temporary_home"
}
trap cleanup EXIT

mkdir -p "$temporary_home/.config/speedysearch" "$temporary_home/watched"
sed "s|WATCH_PATH|$temporary_home/watched|" > "$temporary_home/.config/speedysearch/config.toml" <<'CONFIG'
[indexing]
watch_paths = ["WATCH_PATH"]
exclude_patterns = [".git", ".cache", "__pycache__", "node_modules"]
batch_update_interval_ms = 500
CONFIG

HOME="$temporary_home" "$binary" --daemon &
pid=$!
sleep 2

total="0"
for ((sample = 0; sample < samples; sample++)); do
    value="$(ps -p "$pid" -o %cpu= | tr -d ' ')"
    total="$(awk -v total="$total" -v value="${value:-0}" 'BEGIN { printf "%.4f", total + value }')"
    sleep 1
done

awk -v total="$total" -v samples="$samples" 'BEGIN {
    average = total / samples;
    printf "average_idle_cpu_percent=%.3f target_percent=0.500 status=%s\n", average, (average < 0.5 ? "PASS" : "MISS")
}'
