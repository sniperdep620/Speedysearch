# Speedysearch User Guide

Speedysearch is a small local launcher that lets you search for files, apps,
and settings from one place. It is designed to be fast, lightweight, and
private. The index and optional personalization data stay on your computer.

## What it does

Speedysearch:

- scans configured local folders and watches them for file changes
- discovers `.desktop` launchers on Linux
- discovers Start Menu apps, Windows settings, and system commands on Windows
- answers search requests through local-only IPC
- ranks results using exact matches, typo tolerance, and frecency

## Search window

From a Linux development checkout, set up and launch the primary Tauri
interface:

```bash
./setup.sh
./start.sh
```

Use `Ctrl+Space` to show or hide the window. It uses a transparent,
keyboard-focused layout with filters, result cards, previews, and settings.

Use it like this:

- type to search
- scan the `Applications`, `Files`, and `Settings` result columns
- use any arrow key to move the selection
- press `Enter` or click a result to open it
- hover or focus a result to open its preview and copy its location
- press `Esc` to clear the query or hide the launcher

## Install and run

### Windows

Download
[`Speedysearch-Windows-x64-Setup.exe`](../../../releases/latest/download/Speedysearch-Windows-x64-Setup.exe)
and run it. The installer is per-user and does not require administrator
access. It adds Speedysearch to the Start Menu.

If you are working from a source checkout instead, double-click `install.cmd`
in the repository root or run:

```powershell
.\install.cmd
```

The source installer builds and installs Speedysearch, enables automatic
startup, and launches it. Automatic startup runs the Tauri launcher hidden
until `Ctrl+Space` is pressed.

Launch a release installation from the Start Menu. From a source checkout,
start or activate the installed app with:

```powershell
.\start.cmd
```

Use `.\install.cmd -NoStartup` if Speedysearch should not run at sign-in. The
installer can be run again with or without this flag to change that choice.
Release installations can be removed from **Settings → Apps → Installed
apps**.

### Linux

Debian and Ubuntu users can download
[`Speedysearch-Linux-amd64.deb`](../../../releases/latest/download/Speedysearch-Linux-amd64.deb)
and install it with:

```bash
sudo apt install ./Speedysearch-Linux-amd64.deb
```

Other x86_64 distributions can download
[`Speedysearch-Linux-x86_64.AppImage`](../../../releases/latest/download/Speedysearch-Linux-x86_64.AppImage),
then run:

```bash
chmod +x Speedysearch-Linux-x86_64.AppImage
./Speedysearch-Linux-x86_64.AppImage
```

For a desktop-integrated installation from a source checkout, run:

```bash
npm run install:user
```

This builds and installs Speedysearch under your XDG user data directory,
adds its desktop entry and icon, and refreshes the desktop application database.
It does not change the system launcher. After installation, COSMIC Settings
recognizes Speedysearch as an installed application.

Upgrading from the former Cosmic Search name removes its stale application-menu
entry and icon after the new Speedysearch entry has been installed.

To explicitly make it handle COSMIC's existing Super and Super+/ launcher keys:

```bash
npm run system-search:enable
```

This redirects the compositor's `Launcher` system action without replacing or
deleting COSMIC's shortcut definitions. Check or undo the choice at any time:

```bash
npm run system-search:status
npm run system-search:restore
```

To remove the user installation and restore the exact launcher command that was
present before installation:

```bash
npm run uninstall:user
```

The restore operation refuses to overwrite the setting if another application
or the user changed it after Speedysearch was installed.

There is no cross-desktop standard for a default local-search provider. On
Linux desktops other than COSMIC, install the native package and use the
universal shortcut described below.

The original native GUI and daemon can also be built from the root Rust crate:

```bash
cargo build --release
./target/release/speedysearch
```

Run only the backend service (optional):

```bash
./target/release/speedysearch --daemon
```

Without `--daemon`, the original GUI starts and connects to the local service
automatically. The Tauri interface does not need this socket because it links
the search crate directly.

## Linux native daemon API

The optional Linux native daemon speaks newline-delimited JSON over a Unix
socket:

- socket path: `~/.cache/speedysearch.sock`
- request format: `{"query":"your text"}`
- response format: JSON with `results`, `latency_ms`, and `index_stale`

Example:

```bash
printf '{"query":"document"}\n' | nc -U ~/.cache/speedysearch.sock
```

You can also send typo-heavy queries. For example, `dcument` still matches
`document`.

## What gets indexed on Linux

By default, the daemon looks at:

- `~/`
- `~/Desktop`
- `~/Documents`
- `~/Downloads`

It also scans desktop launcher files from:

- `~/.local/share/applications`
- `/usr/share/applications`
- any additional `applications` directories listed in `XDG_DATA_DIRS`

COSMIC settings panels are indexed even when their launcher is hidden from app
menus. Other visible launchers in the `Settings` category, such as NVIDIA
Settings, are classified as settings too. Names, generic names, comments,
keywords, executable names, and common aliases are searchable, so queries such
as `vs code`, `vscode`, and `general settings` work as expected.

## Linux configuration

The config file lives at:

```text
~/.config/speedysearch/config.toml
```

If the file does not exist, Speedysearch uses built-in defaults.

Important options:

- `indexing.watch_paths`: folders to scan and watch
- `indexing.exclude_patterns`: folder names to skip during indexing
- `indexing.batch_update_interval_ms`: how often filesystem events are batched
- `performance.max_stage1_candidates`: upper bound for Stage 1 candidate work

Start from the maintained example:

```bash
mkdir -p "${XDG_CONFIG_HOME:-$HOME/.config}/speedysearch"
cp config.example.toml \
  "${XDG_CONFIG_HOME:-$HOME/.config}/speedysearch/config.toml"
```

The relevant portion looks like:

```toml
[indexing]
watch_paths = ["~/", "~/Desktop", "~/Documents", "~/Downloads"]
exclude_patterns = [".git", ".cache", "__pycache__", "node_modules"]
batch_update_interval_ms = 500

[performance]
max_stage1_candidates = 100
```

## How results are ranked

The current implementation uses:

1. exact matches
2. trigram overlap
3. bounded Levenshtein distance for short queries
4. an optional local personalized model
5. frecency, based on access count and decay over time

If no valid personalized model exists, search falls back automatically to match
quality and frecency. Recently used items therefore tend to rise to the top.

## Performance expectations

The project is built to be fast, but the actual speed depends on how many files
you have and how much of your home directory is watched.

Useful checks:

- `cargo test`
- `cargo bench --release`
- `scripts/measure_idle_cpu.sh`

## Troubleshooting

If the GUI says the search service is offline:

- make sure `HOME` is set
- make sure the socket directory under `~/.cache` is writable
- check that your watched paths actually exist
- try launching `./target/release/speedysearch --daemon` directly once

For Windows backend, indexing, or shortcut problems, use the
[Windows troubleshooting guide](windows-tauri.md#troubleshooting).

If a file does not appear:

- verify that it is not excluded by your config
- wait for the watcher batch interval to flush
- restart the daemon if you changed the config and want a fresh scan

If results look stale:

- the response field `index_stale` tells you whether the index is older than
  about one second
- a full reindex runs automatically when the cache is missing or empty

## Personalized ranking

Speedysearch records selections locally in
`~/.cache/speedysearch/clickstream.jsonl` when clickstream logging is enabled.
Nothing is uploaded. Run `python3 train/train_ranker.py` from the project after
collecting 50 or more selections to create a personalized model. Restart the
daemon to load it. Search continues with frecency ranking when no model exists.

## Tauri launcher

Press `Ctrl+Space` to show or hide the launcher. Type to search, use any arrow
key or Tab to move through results, press Enter to open, and press Escape to
clear the query or hide the window. Hover a result for its preview and actions.
The preferences button controls layout, preview position, opacity, and score
display. Preferences are stored locally in
`~/.config/speedysearch/ui-config.json` on Linux and
`%LOCALAPPDATA%\Speedysearch\ui-config.json` on Windows.

To change the universal shortcut, open Preferences, choose **Keyboard
shortcut**, and select **Change shortcut**. Press a combination using Ctrl,
Alt, or Super plus another key. Speedysearch checks it immediately; if the
desktop or another application already uses it, the previous shortcut is
restored. Escape cancels recording, and **Reset default** returns to
`Ctrl+Space` (`Cmd+Space` on macOS).

On COSMIC Wayland, Speedysearch installs this binding through COSMIC's custom
shortcut configuration. On X11 it uses the native global shortcut backend.
Pressing the shortcut again hides the launcher.
