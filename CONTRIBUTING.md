# Contributing to Speedysearch

Thank you for helping improve Speedysearch. Contributions of code,
documentation, tests, performance data, and well-scoped bug reports are
welcome.

By participating, you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).
By submitting a contribution, you agree that it is licensed under
[Apache-2.0](LICENSE).

## Before starting

For a substantial feature, architecture change, new permission, or
persistence-format change, open an issue first. This lets maintainers and
contributors agree on scope before significant work begins. Small fixes and
documentation improvements can go directly to a pull request.

Do not include local indexes, clickstreams, models, credentials, private file
paths, or personal configuration in issues, fixtures, screenshots, or commits.

## Set up the project

On Linux, install Rust stable and Node.js 20.19+ or 22.12+, then run:

```bash
./setup.sh
```

The script uses the committed lockfiles and runs the standard validation suite.
See [README.md](README.md) for platform details and setup flags.

On Windows, also install the MSVC build tools and WebView2 prerequisites listed
in [the Windows Tauri guide](docs/windows-tauri.md), then run:

```powershell
npm install
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows-native\test.ps1 -Configuration Release
npm run test:unit
npm run build
```

## Make a change

- Keep changes focused and preserve nonblocking behavior in the query path.
- Format Rust with `cargo fmt`.
- Use four spaces in Rust and two spaces in TypeScript/CSS/JSON.
- Use `snake_case` for Rust functions/modules and `PascalCase` for types and
  React components.
- Keep component styles next to their components.
- Add user-visible behavior tests instead of testing implementation details.
- Update documentation and `CHANGELOG.md` when behavior changes.
- Preserve ID validation and least-privilege Tauri permissions.

## Validate

Run:

```bash
npm run check
```

For Windows-specific changes, also run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows-native\test.ps1 -Configuration Release
cargo test --manifest-path src-tauri/Cargo.toml --locked --lib --bins --tests
npm run test:unit
npm run build
```

For interaction changes, also run:

```bash
./scripts/check.sh --with-e2e
```

For ranking, indexing, or persistence changes, run the relevant Criterion
benchmarks and include before/after results in the pull request:

```bash
cargo bench --release
```

## Commit and pull request

Use a short imperative commit subject, such as `Fix stale result selection`.
Keep unrelated changes in separate commits.

A pull request should include:

- The problem and why the change is needed.
- A concise description of the chosen behavior.
- Validation commands and results.
- Screenshots or a recording for visible UI changes.
- Platform-specific, configuration, persistence, or permission impact.
- A linked issue when one exists.

Pull requests must pass CI and review before merging. Maintainers may ask for a
change to be split when it mixes unrelated concerns.

## Report a vulnerability

Do not report vulnerabilities in a public issue. Follow
[SECURITY.md](SECURITY.md).
