# Developer guide

This document describes the `0.2.x` architecture and the constraints that are
important when changing it. Start with `./setup.sh`; run `npm run check` before
opening a pull request.

## Architecture

The React interface uses the Rust search crate directly on Linux. On Windows,
the same Tauri command contract forwards to the existing C# search engine over
the user-restricted `\\.\pipe\speedysearch` named pipe. Tauri starts and owns
the `speedysearch-cli.exe --daemon` subprocess.

Speedysearch has a reusable Rust search crate and two desktop entry points:

```text
React UI ──Tauri commands──> speedysearch Rust crate

Native Rust GUI ──Unix socket──> daemon ──> speedysearch Rust crate
```

The Tauri UI is the primary desktop experience. It links the root crate
directly, so its queries do not pass through the daemon socket. The older Rust
GUI and daemon remain useful for development, IPC integrations, and comparison.

### Rust search crate

- `src/index.rs` — indexed entry model, desktop-file discovery, trigrams,
  filesystem scans, snapshots, and frecency.
- `src/stage0.rs` — lightweight intent classification.
- `src/stage1.rs` — candidate filtering and bounded Levenshtein matching.
- `src/stage2.rs` — feature extraction and native evaluation of LightGBM text
  models.
- `src/stage3.rs` — filesystem watching and batched incremental updates.
- `src/launcher.rs` — query pipeline, result assembly, click logging, and
  ranking fallback.
- `src/cache.rs` — cache paths and atomic bincode persistence.
- `src/ipc.rs` — newline-delimited JSON protocol and Unix socket server.
- `src/gui.rs` — original `eframe` launcher and daemon client.
- `src/config.rs` — defaults, TOML loading, and home-directory expansion.

`src/lib.rs` defines the public crate surface. `src/main.rs` launches the
original GUI by default and accepts `--daemon` or `--check-model`.

### Tauri application

- `src-tauri/src/daemon.rs` - Windows subprocess lifecycle and named-pipe client.
- `src-tauri/src/windows_commands.rs` - Windows mappings for the shared frontend contract.
- `frontend/src/` — React components, hooks, backend adapter, and styles.
- `src-tauri/src/commands.rs` — validated commands exposed to the webview.
- `src-tauri/src/system_integration.rs` — reversible COSMIC launcher handling.
- `src-tauri/src/lib.rs` — application state, plugins, and Tauri startup.
- `src-tauri/capabilities/default.json` — intentionally minimal webview
  permissions.

IDs cross the JavaScript boundary as decimal strings because Rust `u64` values
can exceed JavaScript's safe integer range. Search, indexing, and click logging
must remain off the main webview thread.

## Query flow

On Windows, opening and revealing results cross the pipe by decimal ID. The
daemon resolves each ID against its current index; the webview never supplies
a path or command for execution.

1. Normalize the query and determine its likely intent.
2. Build a bounded candidate set with trigram overlap.
3. Apply Levenshtein fallback for short or sparse queries.
4. Extract normalized context and match features.
5. Evaluate the local ranker when a valid compatible model is available.
6. Fall back to deterministic match quality and frecency otherwise.
7. Return results with latency and index-staleness metadata.

The query path should not perform filesystem scans, model training, or
unbounded work.

## Index lifecycle

The daemon serves an empty index immediately, then loads the persisted snapshot
or scans the configured roots on a blocking worker. Application launchers and
settings are merged into the catalog. The watcher batches later filesystem
events and atomically refreshes the snapshot.

Default runtime paths:

- `~/.cache/speedysearch/index.bin`
- `~/.cache/speedysearch/clickstream.jsonl`
- `~/.cache/speedysearch/ranker.txt`
- `~/.cache/speedysearch.sock`
- `~/.config/speedysearch/config.toml`
- `~/.config/speedysearch/ui-config.json`

Respect `XDG_CONFIG_HOME` and the existing path helpers when adding
configuration. Persist derived data atomically and assume old/corrupt caches
may exist.

## Personalized ranking

When clickstream logging is enabled, selection events and the displayed
candidates' feature vectors are appended locally. `train/train_ranker.py`
creates a LightGBM text model after enough selections:

```bash
python3 -m venv .venv
. .venv/bin/activate
python -m pip install -r train/requirements.txt
python train/train_ranker.py
```

The Rust evaluator validates feature order while loading. A missing,
incompatible, or disabled model must leave search functional through the
frecency fallback.

## Security boundaries

Treat these areas as security-sensitive:

- Opening a result: keep ID validation in Rust; never accept an arbitrary shell
  command from the webview.
- Tauri capabilities: add only the permission required for a specific feature.
- Global shortcuts: preserve unrelated bindings and roll back partial updates.
- Installation scripts: limit writes to the documented application files.
- Indexed data: never upload, commit, or silently expose user paths.

The asset protocol currently allows local image/icon access needed for previews.
Any expansion of its scope requires explicit review.

## Tests

The canonical gate is:

```bash
npm run check
```

Focused coverage lives in:

- Rust unit tests next to their modules.
- Rust integration tests in `tests/integration_tests.rs`.
- React tests in colocated `*.test.tsx` files.
- Playwright scenarios in `frontend/tests/*.spec.ts`.
- Application/install identity checks in `tests/application_identity_test.sh`.

Use Testing Library for visible UI behavior. Add a regression test with each bug
fix where practical. Run `./scripts/check.sh --with-e2e` for interaction
changes.

## Performance

`benches/benchmarks.rs` covers classification, candidate filtering, full
queries, and index serialization. Run:

```bash
cargo bench --release
scripts/measure_idle_cpu.sh
```

Ranking or indexing changes should include before/after measurements. Avoid
allocations, blocking I/O, and lock contention in the query path.

## Packaging

On Windows, `npm run tauri build` first compiles the C# daemon and packages it
as a private NSIS resource. On Linux, the existing deb and AppImage targets are
unchanged. See `docs/windows-tauri.md` for the Windows build and protocol.

`npm run tauri build` produces Linux bundles under
`src-tauri/target/release/bundle/`. `npm run install:user` builds the embedded
binary without bundling and installs it under `XDG_DATA_HOME`.

Version values must agree in:

- `Cargo.toml`
- `src-tauri/Cargo.toml`
- `src-tauri/tauri.conf.json`
- `package.json`
- `package-lock.json`

See [releasing.md](releasing.md) for the release checklist.
