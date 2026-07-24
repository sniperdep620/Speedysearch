# Speedysearch

Speedysearch is a fast, private desktop search and launcher for Linux. It
indexes applications, settings, files, and folders locally, then combines fuzzy
matching, frecency, and an optional personalized ranker to return results
quickly.

> **Project status:** `0.2.0` is an early public preview. Linux is the supported
> platform, with the smoothest integration on Pop!_OS, Ubuntu, and the COSMIC
> desktop. Expect rough edges and persistence-format changes before `1.0`.

## Highlights

- Local-only indexing and ranking; no account or cloud service is required.
- Typo-tolerant trigram and Levenshtein matching.
- Frecency ranking with an optional locally trained LightGBM model.
- Incremental filesystem updates and a persisted index.
- Keyboard-first React/Tauri launcher plus the original Rust launcher.
- Reversible COSMIC launcher-key integration.
- User preferences and clickstream data stay on the local machine.

## Quick start

Install [Rust stable](https://rustup.rs/) and
[Node.js 20.19+ or 22.12+](https://nodejs.org/), clone the repository, and run:

```bash
./setup.sh
```

That single command installs the locked project dependencies, installs missing
native packages on Debian/Ubuntu-based systems, adds Rust formatting/linting
components, and validates the checkout. It may ask for `sudo` to install GTK
and WebKitGTK development packages.

Then launch the development build:

```bash
./start.sh
```

Use `Ctrl+Space` to show or hide the launcher. The shortcut can be changed in
**Settings → Keyboard shortcut**.

Useful setup variants:

```bash
./setup.sh --with-e2e          # also install Chromium and run browser tests
./setup.sh --no-check          # install only
./setup.sh --skip-system-deps  # never invoke apt/sudo
```

For Linux distributions without `apt`, install the Tauri prerequisites listed
in the [Tauri Linux guide](https://v2.tauri.app/start/prerequisites/#linux),
then use `./setup.sh --skip-system-deps`.

## Install for the current user

Build the release binary, desktop entry, and icon into the current user's XDG
data directory:

```bash
npm run install:user
```

This does not replace the desktop's default launcher. COSMIC users can opt in
to a reversible replacement:

```bash
npm run system-search:enable
npm run system-search:status
npm run system-search:restore
```

Remove the user installation with:

```bash
npm run uninstall:user
```

## How it fits together

```text
frontend/       React/TypeScript launcher interface
src-tauri/      Tauri shell, commands, permissions, and OS integration
src/            Rust index, ranking, IPC, watcher, and original GUI
tests/          Rust integration and application-identity tests
benches/        Criterion performance benchmarks
train/          Optional offline personalized-ranker training
scripts/        Setup checks, installation, and system integration
docs/           User, developer, and release documentation
```

The Tauri application links the Rust search crate directly. The original native
launcher uses the same crate through a local Unix socket daemon. See the
[developer guide](docs/developer-guide.md) for the full request flow.

## Configuration and local data

Speedysearch uses built-in defaults when no configuration file exists. To
customize it:

```bash
mkdir -p "${XDG_CONFIG_HOME:-$HOME/.config}/speedysearch"
cp config.example.toml \
  "${XDG_CONFIG_HOME:-$HOME/.config}/speedysearch/config.toml"
```

Default local paths:

- Configuration: `~/.config/speedysearch/`
- Search index and ranking data: `~/.cache/speedysearch/`
- Native daemon socket: `~/.cache/speedysearch.sock`

The index, clickstream, configuration, and trained model are deliberately
ignored by Git and must never be committed.

## Development

Run the complete release gate:

```bash
npm run check
```

This checks Rust formatting and lints, runs both Rust crates' tests, runs the
frontend and shell tests, verifies the production frontend build, and checks
all shell-script syntax. Browser tests are opt-in:

```bash
./scripts/check.sh --with-e2e
```

Common focused commands:

```bash
cargo test --locked --lib --bins --tests
npm test
npm run build
npm run test:e2e
cargo bench --release
```

## Documentation

- [User guide](docs/user-guide.md)
- [Developer guide](docs/developer-guide.md)
- [Contributing guide](CONTRIBUTING.md)
- [Release process](docs/releasing.md)
- [Security policy](SECURITY.md)
- [Changelog](CHANGELOG.md)

## Contributing and support

Bug reports, focused pull requests, documentation fixes, and performance
measurements are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md) before
submitting a change. Please use the repository's issue tracker for normal bugs
and feature ideas, and follow [SECURITY.md](SECURITY.md) for vulnerabilities.

## License

Copyright 2026 Speedysearch contributors.

Licensed under the [Apache License, Version 2.0](LICENSE). Contributions are
accepted under the same license.
