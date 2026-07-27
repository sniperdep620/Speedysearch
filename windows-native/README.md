# Speedysearch for Windows

This folder contains the native Windows search backend for Speedysearch. It
provides local indexing, typo-tolerant search, frecency, personalized LightGBM
ranking, clickstream, incremental updates, Windows catalog discovery, native
shell actions, and the named-pipe daemon.

The supported Windows GUI is the same Tauri/React launcher used on Linux. It
wraps `bin\Release\speedysearch-cli.exe --daemon`; the WinForms implementation
is built only as `Speedysearch.Native.exe` for backend comparison and is not
installed or exposed through the normal Windows launch path.

## Run the finished app

From the repository root, build and install the Tauri launcher:

```powershell
.\install.ps1
```

Press `Ctrl+Space` to show or hide the launcher. Layout, opacity, ranking
preferences, and the global shortcut are available from the launcher's
settings button. Indexed folders and exclusions remain in the local backend
configuration.

The first full file scan runs in the background. Applications, Windows
settings, commands, and any cached file index are searchable immediately.

## Features

- Windows Start Menu and pinned-shortcut application discovery
- Windows Settings deep links, Windows Security, and administrative tools
- Files and folders under configurable roots
- precomputed inverted trigram index with bounded candidate work
- typo correction through bounded Levenshtein matching
- frecency and recency ranking
- compatible LightGBM text-model evaluator with the original 12 features
- local JSONL clickstream compatible with `train\train_ranker.py`
- versioned binary index with atomic replacement and corrupt-cache recovery
- batched `FileSystemWatcher` updates and overflow-triggered rescans
- Linux-matched frameless launcher, result cards, filters, and empty states
- nonblocking Tauri commands and clamped keyboard navigation
- configurable global shortcut owned by the Tauri shell
- single-click and Enter activation, previews, and copy-location actions
- user-scoped start-at-sign-in support
- ACL-restricted newline-delimited JSON named-pipe service
- single-instance GUI activation and daemon protection

All indexed content, preferences, usage data, and model files remain local.

## Build and test

No SDK download is required on this machine. The scripts use the 64-bit
.NET Framework C# compiler at
`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`.

Because some Windows installations disable local PowerShell scripts by
default, invoke them with a process-scoped execution-policy override:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Configuration Release -Clean
powershell -NoProfile -ExecutionPolicy Bypass -File .\test.ps1 -Configuration Release
```

The test command compiles the real release binaries and then runs component,
integration, native-window, live-watcher, named-pipe, concurrency, persistence,
ranking, and 100,000-entry performance tests. It exits nonzero on any failure.

Build products:

- `src-tauri\target\release\speedysearch-ui.exe` — supported Tauri/React launcher
- `bin\Release\speedysearch-cli.exe` — command-line and daemon host
- `bin\Release\Speedysearch.exe` — compatibility forwarder to the Tauri launcher
- `bin\Release\Speedysearch.Native.exe` — legacy reference GUI, not installed
- `bin\Release\speedysearch-tests.exe` — standalone test suite

## Install or remove for the current user

From the repository root, double-click `install.cmd` or run:

```powershell
.\install.cmd
```

This builds and installs Speedysearch, creates a Start Menu shortcut, enables
start-at-sign-in, and launches the app. It writes only to the current user's
local programs, configuration, Start Menu, and startup registry locations, so
administrator access is not required. Use `.\start.cmd` from the repository
root to start or activate the installed app later.

Pass `-NoStartup` to leave automatic startup disabled, or `-NoLaunch` to install
without launching immediately. The lower-level installer remains available
from the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows-native\install-user.ps1 -StartAtLogin
```

Remove the application while retaining the local index, preferences, and
ranking history:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows-native\uninstall-user.ps1
```

Add `-PurgeLocalData` to the uninstall command only when the local index,
configuration, clickstream, and model should also be removed.

## Command-line interface

```powershell
# Search the warm index and return JSON
.\bin\Release\speedysearch-cli.exe --query "visual studio code"

# Force a complete file reindex
.\bin\Release\speedysearch-cli.exe --reindex

# Validate the configured personalized model
.\bin\Release\speedysearch-cli.exe --check-model

# Serve newline-delimited JSON on \\.\pipe\speedysearch
.\bin\Release\speedysearch-cli.exe --daemon
```

The named-pipe query request is:

```json
{"query":"document"}
```

A health check and shell actions use indexed IDs:

```json
{"action":"ping"}
{"action":"open","id":"123"}
{"action":"show_in_folder","id":"123"}
```

A local selection event uses decimal-string IDs so no JavaScript precision is
lost:

```json
{"query":"document","selected_id":"123","rank_position":1}
```

## Personalized ranking

Selections are stored in:

```text
%LOCALAPPDATA%\Speedysearch\Data\clickstream.jsonl
```

After collecting at least 50 selections, install the Python requirements from
the repository's `train` directory and run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\train-ranker.ps1
```

The resulting model is written to:

```text
%LOCALAPPDATA%\Speedysearch\Data\ranker.txt
```

Restart Speedysearch after training. A missing, disabled, or incompatible
model automatically falls back to deterministic match quality and frecency.

## Local paths

- configuration: `%LOCALAPPDATA%\Speedysearch\config.json`
- binary index: `%LOCALAPPDATA%\Speedysearch\Cache\index-win-v1.bin`
- clickstream: `%LOCALAPPDATA%\Speedysearch\Data\clickstream.jsonl`
- ranker model: `%LOCALAPPDATA%\Speedysearch\Data\ranker.txt`
- daemon pipe: `\\.\pipe\speedysearch`

Start from `config.example.json` only if editing the configuration by hand.
The in-app Preferences window is safer because it validates the shortcut and
numeric ranges.

## Architecture

```text
React UI -> Tauri commands -> named pipe daemon -> SearchEngine
                                                   |
FileSystemWatcher -> batched updates --------------+-- trigram/typo filtering
                                                   +-- LightGBM/frecency
                                                   +-- binary persistence
                                                   +-- local clickstream

Start Menu shortcuts ─┐
Windows Settings ─────┼─> WindowsCatalog ─> SearchIndex
Files and folders ────┘
```

The query path performs no directory traversal, model training, or unbounded
work. The daemon exposes the application/settings catalog immediately; a
missing first file index and later watcher recovery run on background workers.
