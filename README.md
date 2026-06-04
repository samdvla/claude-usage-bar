# claude-usage-bar

See your **Claude usage at a glance** — your real 5-hour and 7-day rate-limit
utilization, color-coded — right in your **macOS menu bar** or **Windows system
tray**, plus a matching `ccc` terminal command.

If you use **Claude Code**, there's nothing to set up: the app reads the login
token Claude Code already stored on your machine and shows your usage. No
sign-in, no API key, no config.

```
  Claude usage  ·  Max

  5h  ████████░░░░  61%
  7d  ███████████░  81%

  5h resets in 2h 39m
  ─────────────
  Refresh
  Open in Terminal
```

| | macOS | Windows |
|---|---|---|
| Lives in | Menu bar (top) | System tray / taskbar (bottom-right) |
| At-a-glance | `>` glyph + 5h % text | Claude-orange badge with the 5h % |
| Detail view | Menu dropdown | Custom flyout (left-click) |
| CLI | `ccc` | `ccc.exe` |
| Stack | Python (rumps) | .NET 8 (WinForms), single self-contained `.exe` |

Both read the same data the same way and show the same numbers.

---

## macOS

### Requirements
- macOS 11+
- **Claude Code**, installed and logged in (the app reads its Keychain token).
- `python3` (the installer builds a small virtualenv for the menu bar app).
- `jq` + `curl` (for the `ccc` CLI; preinstalled on modern macOS).

### Install
```sh
git clone https://github.com/samdvla/claude-usage-bar.git
cd claude-usage-bar
./install.sh
```

The `>` icon appears in your menu bar within a few seconds. On first launch
macOS may ask permission to read your Claude Code login from the Keychain —
click **Allow** (or **Always Allow**).

### Use
- The menu bar shows your current 5h utilization. Click it for the dropdown
  (5h/7d bars, reset countdowns, Refresh, Open in Terminal, Quit).
- `ccc` (one-shot), `ccc --watch` (live), `ccc --json` (raw headers),
  `ccc open` (open the menu bar app).
- Auto-starts at login via a LaunchAgent. Quit and it stays quit; reopen via
  Spotlight/Applications, `open -a "Claude Usage"`, or `ccc open`.

---

## Windows

A system-tray app (`ClaudeUsageBar.exe`) + a `ccc.exe` CLI, built on .NET 8.
Each ships as a **single self-contained `.exe`** — no .NET install, no Python,
nothing to set up on the target machine.

### Requirements
- Windows 10/11 (x64)
- **Claude Code**, logged in (the app reads
  `%USERPROFILE%\.claude\.credentials.json`).
- To build from source: the **.NET 8 SDK** (`winget install Microsoft.DotNet.SDK.8`).

### Install (prebuilt)
Download `ClaudeUsageBar.exe` (and optionally `ccc.exe`) from
[Releases](https://github.com/samdvla/claude-usage-bar/releases) and run.

> The exe isn't code-signed, so Windows SmartScreen may show **"Windows
> protected your PC."** Click **More info → Run anyway**. (It's a self-contained
> .NET app — large because it bundles the runtime so nothing else is needed.)

The app pins itself to the always-visible part of the taskbar automatically
(via the Windows 11 `IsPromoted` flag). If your Windows build keeps it in the
**"^" overflow**, drag the Claude-orange badge out onto the taskbar once, or
toggle it on in **Settings → Personalization → Taskbar → Other system tray
icons**.

### Build from source
```powershell
git clone https://github.com/samdvla/claude-usage-bar.git
cd claude-usage-bar\windows
pwsh .\build.ps1        # outputs windows\dist\ClaudeUsageBar.exe and ccc.exe
dotnet test tests\Core.Tests\Core.Tests.csproj   # run the unit tests
```

### Use
- The taskbar shows a **Claude-orange badge with your 5h %**.
- **Left-click** → flyout with 5h/7d health-colored bars + reset countdowns.
- **Right-click** → Refresh / Auto-start at login / Open in Terminal / Quit.
- `ccc.exe`, `ccc.exe --watch`, `ccc.exe --json`, `ccc.exe open`.

See [`windows/README.md`](windows/README.md) for more detail.

---

## Features (both platforms)

- **Real utilization**, not local estimates — straight from Anthropic's
  `anthropic-ratelimit-unified-*` rate-limit headers.
- **Color-coded** 5h and 7d: green `<50%`, orange `<80%`, red `≥80%`.
- **Reset countdowns** for both windows.
- **Always current** — refreshes when you open the view, plus a light
  background tick every 2 minutes.
- **Zero setup** when Claude Code is logged in. No API key, no sign-in.

## How it works

The app reads the OAuth token Claude Code stores on your machine — the macOS
login **Keychain** (`Claude Code-credentials`) or the Windows file
`%USERPROFILE%\.claude\.credentials.json` — and makes one tiny,
Claude-Code-shaped request to the API. Anthropic returns your real
`anthropic-ratelimit-unified-*` headers (5h/7d utilization and resets) on the
response — that's what you see. Same token, same request shape Claude Code
itself uses. Even a `429` (maxed out) carries those headers, so being at 100%
is an answer, not an error; a `401/403` means the login expired (re-open Claude
Code).

**Cost / fair warning:** each refresh is a real (tiny) API call on your account.
The apps keep it light (on-open + every 2 min). Cranking the interval way down
means lots of automated calls — go easy.

## License

[MIT](LICENSE) © 2026 Sam Davila

macOS app built with [rumps](https://github.com/jaredks/rumps) (BSD); Windows
app built on .NET / WinForms. All application code and icon artwork are original
to this project.
