# Token Bar for Windows

The Windows port uses a native .NET tray app. It reads Claude Code, Codex,
OpenCode, and pi data locally and embeds the same pricing snapshot as the macOS
app. No API credentials, network requests, or telemetry are used by Token Bar.

## Run

On Windows 11 (or a Windows 10 edition supported by .NET 10), extract the
`TokenBar-<version>-win-x64.zip` or `win-arm64.zip` matching your processor and
run `TokenBar.exe`. The portable release includes .NET; no runtime installation
is needed. Windows may show a SmartScreen prompt because the executable is
unsigned. Devices enforcing Windows Application Control may block unsigned
builds; this port does not yet include a signed installer.

Left-click the tray icon to open the dashboard. Hover for spend, input/output
tokens, and cache hit rate. Right-click for period, calendar/relative ranges,
graph, projects and sessions, theme, refresh, and quit. Closing the dashboard
keeps the tray app running. Windows may initially place the icon in the tray's
overflow menu.

The dashboard uses metric cards, segmented period controls, a chart with dollar
gridlines, and model rows with provider labels and tooltips for long names.
System theme follows the Windows app appearance. Use the Refresh button or F5
to rescan immediately, or start with `TokenBar.exe --show` to open the dashboard.

For start at login, press Win+R, enter `shell:startup`, and place a shortcut to
`TokenBar.exe` in that folder.

## Data locations

| Agent | Default path | Optional override |
|---|---|---|
| Claude Code | `%USERPROFILE%\.claude\projects\**\*.jsonl` | `CLAUDE_CONFIG_DIR` (base directory) |
| Codex | `%USERPROFILE%\.codex\sessions\**\*.jsonl` | `CODEX_HOME` (base directory) |
| OpenCode | `%USERPROFILE%\.local\share\opencode\opencode.db` | `XDG_DATA_HOME` (data directory) |
| pi | `%USERPROFILE%\.pi\agent\sessions\**\*.jsonl` | `PI_CODING_AGENT_DIR` (agent directory) |

Set overrides before starting the app. Sources are watched for changes, with a
60-second fallback, including sources installed after Token Bar starts. An
unreadable log or database is reported in the dashboard. Settings live in
`%LOCALAPPDATA%\TokenBar\settings.json`.

This initial Windows port includes spend graphs, per-model usage, all named
color themes, and optional project/session summaries. Project rows open Explorer
when clicked. Session resume buttons, screenshot export, provider logos,
and the experimental cat animation are currently macOS-only. Windows tray icons
show totals in a tooltip; they cannot display the macOS menu bar's inline text.

WSL homes are separate from Windows profiles and are not scanned automatically.
For an explicit diagnostic scan, pass a readable WSL home (such as
`\\wsl.localhost\Ubuntu\home\me`) to `token-bar-cli.exe --home`. Environment
overrides can also point the tray app at readable WSL paths. Windows project
links cannot resolve Linux working directories recorded inside those logs.

## Diagnostics

```powershell
.\token-bar-cli.exe --print
.\token-bar-cli.exe --period month --relative --json
.\token-bar-cli.exe --home C:\path\to\test-profile
```

`--home` scans only the supplied profile's default agent directories, ignoring
environment overrides. Exit codes: 0 success, 1 source read errors, 2 invalid
arguments. Spend is API-equivalent pricing rather than subscription billing.
Fallback matches are marked `~`; unresolved prices contribute $0 with `~`.

## Build and test

Install the [.NET 10 SDK](https://learn.microsoft.com/en-us/dotnet/core/install/windows)
and run these commands from the repository root:

```powershell
dotnet run --project Windows/TokenBar.Tests -c Release
dotnet build Windows/TokenBar.App -c Release
dotnet run --project Windows/TokenBar.App -c Release -- --smoke-test
.\build.ps1
# Cross-publish for Windows on ARM:
.\build.ps1 -Runtime win-arm64
```

Build output is `dist/TokenBar-win-x64/TokenBar.exe`, alongside
`token-bar-cli.exe`, licenses, and a versioned ZIP. The build script reads the
version from the existing macOS source so pricing releases stay in sync.
The scanner tests use isolated JSONL and SQLite fixtures without agent installs
or paid model requests. The UI smoke check renders light, dark, compact, and
empty dashboards offscreen, checks for clipping and horizontal scrolling,
and registers no tray icon.

The Windows core is a C# port of `Sources/TokenBarCore/TokenBarCore.swift`.
Changes to scanner formats or pricing rules should update both implementations
and their regression tests. The pricing JSON is embedded directly from the
existing resource, so there is only one catalog to maintain. OpenCode uses
Windows' built-in `winsqlite3.dll` in read-only mode, including WAL data.
