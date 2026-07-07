# claude-usage-bar — Windows tray app (.NET)

**Date:** 2026-06-04
**Status:** Approved design, pending implementation plan
**Repo:** `github.com/samdvla/claude-usage-bar` (new `windows/` folder)

## Goal

Bring the macOS menu-bar app to Windows with the same zero-setup experience:
read Claude Code's existing login, probe Anthropic once, and show live 5h/7d
rate-limit utilization — plus a matching `ccc` CLI. Ships as a **single
self-contained `.exe`** that needs no .NET runtime, no Python, nothing on the
target machine. Distribution-quality, including a **polished custom UI** (not the
default Win32 context menu).

## Non-goals

- No telemetry, no accounts, no API key entry (zero-setup is the whole point).
- No Windows service / forced respawn — auto-start is an opt-in login entry only.
- Not a rewrite of the macOS app; the Python/bash versions stay as-is.

## Why .NET single-exe

Chosen over Python+PyInstaller and Go+systray because it ships cleanest to other
people: a self-contained `.exe` with no runtime dependency, no Windows Defender
false-positives (a recurring problem with `--onefile` PyInstaller bundles), and
native tray + custom-window behavior. The core probe logic is ~150 lines, so the
rewrite cost is low.

## Architecture

One .NET solution under `windows/`, producing two executables from a shared core
library:

- **`ClaudeUsageBar.exe`** — WinForms tray app (`OutputType=WinExe`, no console).
- **`ccc.exe`** — console build of the same core (`ccc`, `ccc --watch`, `ccc --json`).

Layout:

```
windows/
  ClaudeUsageBar.sln
  src/
    Core/            # ClaudeUsage.Core class library (no UI deps)
      Credentials.cs
      Probe.cs
      RateUsage.cs   # parsed model: u5, u7, reset5, reset7, plan, error-state
      Health.cs      # util -> color/level helpers (green/orange/red thresholds)
    TrayApp/         # ClaudeUsageBar.exe (WinForms)
      Program.cs
      TrayController.cs
      Flyout/        # custom owner-drawn popup window + controls
      IconRenderer.cs
    Cli/             # ccc.exe (console)
      Program.cs
  tests/
    Core.Tests/      # xUnit; fixtures for 200 / 429 / 401 responses
  build.ps1          # publish wrapper
  README.md          # Windows install/build instructions
```

Three independently testable units, each with a clear contract:

| Unit | Does what | Used by | Depends on |
|------|-----------|---------|------------|
| `Credentials` | Locate + read Claude Code OAuth token on Windows | `Probe` | filesystem / Credential Manager |
| `Probe` | One probe call + rate-header parse → `RateUsage` | Tray, CLI | `HttpClient`, `Credentials` |
| `TrayController` / `Cli` | Front-ends over `Probe` | — | `Probe`, WinForms / console |

## Component 1 — Credentials (main porting unknown)

macOS reads the Keychain (`security find-generic-password -s "Claude Code-credentials"`).
Windows has no equivalent. Resolution order:

1. **`%USERPROFILE%\.claude\.credentials.json`** — Claude Code's default plaintext
   store on Windows. Parse `claudeAiOauth.accessToken` and
   `claudeAiOauth.subscriptionType` (fall back to top-level object if no
   `claudeAiOauth` wrapper, mirroring the Python `data.get("claudeAiOauth", data)`).
2. **Windows Credential Manager** (`CredRead`, target `Claude Code-credentials`) —
   fallback in case a future Claude Code version moves there.

Returns `null` when no usable token is found → "no login" UI state.

> **Verify in planning:** confirm the exact path and JSON shape on a real Windows
> Claude Code install (any Windows box) before
> locking the reader. This is the one piece that can't be confirmed from the Mac.

## Component 2 — Probe (logic ported verbatim from Python)

Identical request to the Mac app:

- `POST https://api.anthropic.com/v1/messages`
- Body: `model: claude-haiku-4-5`, `max_tokens: 1`, one user message `"x"`, and the
  Claude-Code system prompt (`"You are Claude Code, Anthropic's official CLI for Claude."`).
- Headers: `Authorization: Bearer <token>`, `anthropic-version: 2023-06-01`,
  `anthropic-beta: oauth-2025-04-20`, `content-type: application/json`.

Read off the response headers (lowercased):
`anthropic-ratelimit-unified-{5h,7d}-{utilization,reset}`.

Error handling (parity with Python `fetch`):

- **429** still carries the headers → maxed-out is **data**, not failure. Read them.
- **401 / 403** → `expired` state ("open Claude Code to refresh").
- Any other failure, or a non-429 response missing the headers → `transient` state.

Runs with a 20s timeout, off the UI thread.

## Component 3 — Tray app + custom UI

### Tray icon: number-in-icon
Each refresh, render the 5h % as text onto a 32×32 (and 16×16 for DPI) transparent
bitmap via GDI+ (`System.Drawing`), tinted by health level, converted to `HICON`
and assigned to `NotifyIcon.Icon`. The previous `HICON` is destroyed every cycle
(`DestroyIcon`) to avoid a GDI handle leak. Tooltip:
`Claude usage · Max — 5h 61% · 7d 81%`.

Health thresholds (same as Mac): green `<50%`, orange `<80%`, red `≥80%`.

### The flyout (the "very good UI")
**Left-click** the tray icon opens a custom borderless popup — *not* the default
`ContextMenuStrip` (which looks dated). The flyout is a TopMost `Form` styled to
match the clean Mac dropdown:

- **Window chrome:** borderless, Win11 **rounded corners** + drop shadow via
  `DwmSetWindowAttribute` (`DWMWA_WINDOW_CORNER_PREFERENCE`, `DWMWA_SYSTEMBACKDROP_TYPE`
  for Mica/acrylic where available; solid themed fallback on Win10).
- **Theming:** follow OS light/dark (`AppsUseLightTheme` registry key + `WM_SETTINGCHANGE`
  live-update). Dark: near-black panel, light text; light: white panel, dark text.
- **Typography:** Segoe UI Variable, with a monospace face (Cascadia Mono /
  Consolas) for the aligned bar rows.
- **Content:**
  - Header: `Claude usage` + plan badge (e.g. `Max`) right-aligned.
  - Two **owner-drawn progress bars** (5h, 7d): rounded-rect track, rounded fill,
    health-color gradient, % label aligned right. Drawn with anti-aliased GDI+
    (`SmoothingMode.AntiAlias`), not a stock `ProgressBar`.
  - Reset lines: `5h resets in 2h 39m`, `7d resets in 5d 3h` in a dimmed secondary color.
  - Footer row of subtle text buttons: **Refresh**, **Open in Terminal**.
- **Behavior:** positioned just above the tray (work-area aware, multi-monitor /
  DPI aware via `WorkingArea` + per-monitor-v2 DPI awareness in the app manifest).
  Closes on focus-loss (`Deactivate`) or Esc. Light fade-in is optional polish.
- **Always current:** the flyout triggers a fresh probe on open (mirrors the Mac
  `menuWillOpen_` refresh) and renders the last value immediately while it loads.

### Right-click menu (minimal native)
A small `ContextMenuStrip` for utility actions only — **Refresh now**,
**Auto-start at login** (checkable), **Open in Terminal**, separator, **Quit**.
This keeps the heavy, attractive view in the flyout and the OS-conventional
actions where users expect a right-click.

### Refresh cadence & threading
- `System.Windows.Forms.Timer` every **120s** → background probe → update icon +
  tooltip in place (no flyout rebuild).
- Probe runs on a background thread (`Task.Run`); results marshaled to the UI
  thread via `Control.Invoke` / `SynchronizationContext`.
- **Single-instance guard:** a named `Mutex` (`Global\ClaudeUsageBar`) — second
  launch exits silently (mirrors the Mac socket-bind guard).

## Auto-start

Checkable menu item toggles
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run\ClaudeUsageBar` → quoted exe
path. No service, no scheduled task — same "no forced respawn, quit stays quit"
spirit as the macOS LaunchAgent. State of the checkbox reflects the key's presence.

## `ccc.exe` CLI

Console build over the same `Probe`/`Credentials` core, matching the bash CLI:

- `ccc` — one-shot colored readout (ANSI; enable VT processing on the Windows
  console). Bars + 5h/7d % + 5h reset countdown.
- `ccc --watch` (`-w N` interval) — live refresh, hidden cursor, Ctrl-C clean exit.
- `ccc --json` — raw `anthropic-ratelimit-unified-*` header lines.
- `ccc open` — launch `ClaudeUsageBar.exe` if present.

## Error states (parity with Mac)

| State | Icon | Flyout / message |
|-------|------|------------------|
| No login | grey/dim icon | "No Claude Code login found — open Claude Code." |
| Expired (401/403) | ⚠ | "Claude Code login expired — open Claude Code to refresh." |
| Transient | ⚠ | "Couldn't read usage — try again shortly." |
| OK | number-in-icon | full flyout |

## Distribution

```powershell
dotnet publish src/TrayApp -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
dotnet publish src/Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

`windows/build.ps1` wraps both. App manifest sets **per-monitor-v2 DPI awareness**.
Existing `assets/icon.png` is reused as the app/fallback icon. README (root +
`windows/`) updated to document both platforms. Optional later: code-sign the exe
to further reduce SmartScreen friction; out of scope for v1.

## Testing

**Unit (`xUnit`, headless `dotnet test`):**
- `Probe` header parsing against captured fixtures: HTTP 200 (normal), 429
  (maxed — headers present), 401 (expired), and a non-429 missing-header response.
- `Credentials` JSON parse: `claudeAiOauth`-wrapped and bare shapes; missing file.
- `Health` thresholds at boundaries (0.49 / 0.50 / 0.79 / 0.80).
- `IconRenderer` produces a non-empty bitmap of expected size for sample %.

**Manual smoke (real Windows box w/ Claude Code logged in):**
- Icon shows live 5h %; color matches level.
- Flyout opens on left-click, themed correctly (toggle OS dark/light), bars and
  countdowns correct, closes on focus-loss/Esc.
- Right-click actions work; Auto-start toggles the registry key; Quit stays quit.
- `ccc`, `ccc --watch`, `ccc --json` match the menu numbers.

## Open question for planning

Confirm Windows Claude Code credential storage (path + JSON shape) on a real
install before finalizing `Credentials`. Everything else is a direct port of
known-good macOS logic.
