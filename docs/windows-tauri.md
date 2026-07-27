# Windows Tauri integration

The Windows build keeps the existing C# indexing, ranking, watcher, persistence,
clickstream, and shell integration. Tauri owns the launcher window and embeds
the shared React UI.

```text
React UI
   |
   | validated Tauri invoke commands
   v
Rust Tauri bridge
   |
   | newline-delimited JSON on \\.\pipe\speedysearch
   v
speedysearch-cli.exe --daemon
   |
   +-- Windows catalog and filesystem index
   +-- fuzzy and personalized ranking
   +-- clickstream and frecency
   +-- native open/show-in-folder actions
```

The named pipe is restricted to the current Windows user. The bridge launches
only the bundled daemon and sends indexed result IDs for shell actions; paths
from the webview are never executed directly.

There is one supported GUI on both platforms: `frontend\src`. The Windows
installer copies `speedysearch-ui.exe` as the installed `Speedysearch.exe`;
it does not install the legacy WinForms launcher.

## Build and run

Prerequisites:

- Windows 10 or Windows 11
- Rust stable with the MSVC Windows target
- Visual Studio C++ Build Tools and WebView2, as required by Tauri
- Node.js 20.19+ or 22.12+
- .NET Framework 4.x (the native build uses its bundled C# compiler)

Install dependencies and launch development mode:

```powershell
npm install
npm run tauri dev
```

The Windows-specific Tauri configuration runs `npm run dev:windows`, which
builds `windows-native\bin\Release\speedysearch-cli.exe` before Vite starts.
For a release installer:

```powershell
npm run tauri build
```

The NSIS installer is written below
`src-tauri\target\release\bundle\nsis\`. The daemon executable is bundled as a
private application resource and is started automatically with `--daemon`.

For a current-user install directly from the checkout:

```powershell
.\install.ps1
```

This builds the Tauri application, installs it under
`%LOCALAPPDATA%\Programs\Speedysearch`, and points both the Start Menu shortcut
and optional sign-in entry to that Tauri binary.

## Test the daemon independently

Build and run the native test suite:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows-native\build.ps1 -Configuration Release -Clean
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows-native\test.ps1 -Configuration Release
```

For a one-shot JSON search:

```powershell
.\windows-native\bin\Release\speedysearch-cli.exe --query "visual studio code"
```

For daemon mode:

```powershell
.\windows-native\bin\Release\speedysearch-cli.exe --daemon
```

Daemon requests and responses are one JSON object per line. Supported requests:

```json
{"action":"ping"}
{"query":"document"}
{"query":"document","selected_id":"123","rank_position":1}
{"action":"open","id":"123"}
{"action":"show_in_folder","id":"123"}
```

## Runtime behavior

The daemon loads a cached index and the Windows application/settings catalog
before opening the pipe. If no file cache exists, it serves catalog results
immediately and performs the first file scan on a background worker. Tauri
shuts down the child process when the application exits. If a compatible
daemon is already running for the current user, the bridge reuses it.

Set `SPEEDYSEARCH_WINDOWS_DAEMON` to an absolute executable path to test a
specific daemon build without changing Tauri packaging.

## Troubleshooting

### `speedysearch-cli.exe was not found`

Run `npm run build:windows-native`. In development, the bridge checks the
release and debug output folders. Installed builds resolve the executable from
Tauri's resource directory.

### Backend unavailable or startup timeout

Run the daemon directly to see initialization errors. Only one daemon can own
the `Local\Speedysearch.Native.Daemon` mutex, and only the current user can
connect to its pipe. End a stale `speedysearch-cli.exe` process and relaunch
the Tauri app if an incompatible older daemon is still running.

### Search works but opening fails

The daemon rejects stale or unknown IDs. Search again so the UI receives the
current index entry, then retry. App and setting targets must also remain
registered with Windows.

### The launcher still has the old WinForms appearance

An old native build is being launched. Re-run `.\install.ps1` from the
repository root and use the refreshed Start Menu shortcut. Do not launch
`windows-native\bin\Release\Speedysearch.Native.exe`; it exists only for
backend comparison.

### Global shortcut is unavailable

Another program may already own `Ctrl+Space`. Open Speedysearch settings and
record a different shortcut. The Tauri shell owns the global shortcut; the
background C# daemon does not create a second launcher window.
