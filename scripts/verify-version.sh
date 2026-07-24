#!/usr/bin/env bash
set -Eeuo pipefail

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
expected_version="${1:-}"
expected_version="${expected_version#v}"

node --input-type=module - "$project_dir" "$expected_version" <<'NODE'
import { readFileSync } from "node:fs";

const projectDir = process.argv[2];
const expectedVersion = process.argv[3];

if (expectedVersion && !/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/.test(expectedVersion)) {
  throw new Error(`Release tag is not a semantic version: ${expectedVersion}`);
}

function packageVersion(path) {
  return JSON.parse(readFileSync(path, "utf8")).version;
}

function cargoVersion(path) {
  const contents = readFileSync(path, "utf8");
  const packageSection = contents.match(/\[package\]([\s\S]*?)(?:\n\[|$)/);
  const version = packageSection?.[1].match(/^version\s*=\s*"([^"]+)"/m)?.[1];
  if (!version) {
    throw new Error(`Could not read package version from ${path}`);
  }
  return version;
}

const versions = new Map([
  ["Cargo.toml", cargoVersion(`${projectDir}/Cargo.toml`)],
  ["src-tauri/Cargo.toml", cargoVersion(`${projectDir}/src-tauri/Cargo.toml`)],
  ["src-tauri/tauri.conf.json", packageVersion(`${projectDir}/src-tauri/tauri.conf.json`)],
  ["package.json", packageVersion(`${projectDir}/package.json`)],
  ["package-lock.json", packageVersion(`${projectDir}/package-lock.json`)],
]);

function cargoLockVersion(path, packageName) {
  const contents = readFileSync(path, "utf8");
  const packages = [...contents.matchAll(/\[\[package\]\]\n([\s\S]*?)(?=\n\[\[package\]\]|$)/g)];
  const matching = packages.find(([, block]) => {
    const name = block.match(/^name\s*=\s*"([^"]+)"/m)?.[1];
    return name === packageName;
  });
  const version = matching?.[1].match(/^version\s*=\s*"([^"]+)"/m)?.[1];
  if (!version) {
    throw new Error(`Could not read ${packageName} version from ${path}`);
  }
  return version;
}

versions.set("Cargo.lock (speedysearch)", cargoLockVersion(`${projectDir}/Cargo.lock`, "speedysearch"));
versions.set(
  "src-tauri/Cargo.lock (speedysearch)",
  cargoLockVersion(`${projectDir}/src-tauri/Cargo.lock`, "speedysearch"),
);
versions.set(
  "src-tauri/Cargo.lock (speedysearch-tauri)",
  cargoLockVersion(`${projectDir}/src-tauri/Cargo.lock`, "speedysearch-tauri"),
);

const uniqueVersions = new Set(versions.values());
if (uniqueVersions.size !== 1) {
  const details = [...versions].map(([file, version]) => `${file}: ${version}`).join("\n");
  throw new Error(`Project versions do not match:\n${details}`);
}

const [version] = uniqueVersions;
if (expectedVersion && version !== expectedVersion) {
  throw new Error(`Tag/version mismatch: tag is ${expectedVersion}, project is ${version}`);
}

console.log(`Version metadata is consistent: ${version}`);
NODE
