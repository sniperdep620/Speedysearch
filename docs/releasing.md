# Release process

Speedysearch uses Semantic Versioning. During the early preview, minor releases
may include configuration or persistence-format changes.

## Prepare

1. Choose a version and update it consistently in `Cargo.toml`,
   `src-tauri/Cargo.toml`, `src-tauri/tauri.conf.json`, and `package.json`.
2. Run `npm install --package-lock-only` to update `package-lock.json`.
3. Run `cargo check --locked` and
   `cargo check --manifest-path src-tauri/Cargo.toml --locked` to refresh and
   verify both Cargo lockfiles.
4. Move the relevant `CHANGELOG.md` entries under the release version and add
   the release date.
5. Run `./setup.sh --with-e2e` from a clean Linux checkout.
6. Run
   `powershell -NoProfile -ExecutionPolicy Bypass -File .\windows-native\test.ps1 -Configuration Release`
   from a clean Windows checkout.
7. Build and smoke-test `npm run tauri build` on both platforms.

## Publish

Merge the release preparation, then create and push an annotated version tag:

```bash
git tag -a v0.2.0 -m "Speedysearch 0.2.0"
git push origin v0.2.0
```

The release workflow validates and tests both platforms before it publishes
anything. It creates a normal GitHub release so the stable `releases/latest`
download links work, and uploads:

- `Speedysearch-Windows-x64-Setup.exe`
- `Speedysearch-Linux-amd64.deb`
- `Speedysearch-Linux-x86_64.AppImage`
- `SHA256SUMS.txt`

## Verify

- Confirm the workflow and GitHub release completed successfully.
- Download the published artifacts rather than reusing local builds.
- Verify the SHA-256 checksums.
- On Windows, verify installation, launch, search, shortcut registration, and
  uninstall.
- On Linux, smoke-test both the Debian package and AppImage, including launch,
  search, shortcut registration, and uninstall.
- Confirm the release notes describe known early-preview limitations.

If verification fails, fix forward with a patch release. Do not move or replace
a published version tag.
