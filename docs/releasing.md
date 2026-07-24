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
5. Run `./setup.sh --with-e2e` from a clean checkout.
6. Build packages locally with `npm run tauri build` and smoke-test the
   resulting Debian package or AppImage.

## Publish

Merge the release preparation, then create and push an annotated version tag:

```bash
git tag -a v0.2.0 -m "Speedysearch 0.2.0"
git push origin v0.2.0
```

The release workflow validates the tag/version match, runs the release gate,
builds Linux bundles, and creates a GitHub release with the `.deb` and
`.AppImage` artifacts.

## Verify

- Confirm the workflow and GitHub release completed successfully.
- Download the published artifacts rather than reusing local builds.
- Verify installation, launch, search, shortcut registration, and uninstall.
- Confirm the release notes describe known early-preview limitations.

If verification fails, fix forward with a patch release. Do not move or replace
a published version tag.
