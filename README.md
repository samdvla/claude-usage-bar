# claude-usage-bar

See your **Claude and Codex usage at a glance** — real 5-hour and 7-day
rate-limit utilization, color-coded, one section per provider — right in your
**macOS menu bar** or **Windows system tray**, plus a matching `ccc` terminal
command.

If you use **Claude Code**, there's nothing to set up: the app reads the login
token Claude Code already stored on your machine and shows your usage. No
sign-in, no API key, no config. If you also use **OpenAI Codex**, its usage
shows up the same effortless way — read straight from Codex's own local
session logs, with no login of its own and no extra API calls. Show or hide
either provider independently from Settings.

```
  Claude · Max

  5h  ████████░░░░  61%
  7d  ███████████░  81%

  resets 5h in 2h 39m · 7d in 6d 1h
  ─────────────
  Codex · Plus   · as of 12m ago

  5h  ███░░░░░░░░░  20%
  7d  ███░░░░░░░░░  25%

  resets 5h in 1h 10m · 7d in 2d 1h
  ─────────────
  Refresh
  Open in Terminal
```

| | macOS | Windows |
|---|---|---|
| Lives in | Menu bar (top) | System tray / taskbar (bottom-right) |
| At-a-glance | One menu bar item per enabled provider (Claude spark / OpenAI blossom glyph + 5h %) | One badge per enabled provider (Claude-orange / neutral-gray Codex) with the 5h % |
| Detail view | Shared dropdown, one section per provider | Shared flyout (left-click), one section per provider |
| Toggle providers | Dropdown ▸ Settings ▸ Show Claude / Show Codex | Right-click ▸ Show Claude icon / Show Codex icon |
| CLI | `ccc` | `ccc.exe` |
| Stack | Python (rumps) | .NET 8 (WinForms), single self-contained `.exe` |

Each provider is read the same way on both platforms and shows the same
numbers.

---

## macOS

### Requirements
- macOS 11+
- **Claude Code**, installed and logged in (the app reads its Keychain token).
- Optional: **OpenAI Codex CLI**, run at least once (the app reads its local
  session logs at `~/.codex/sessions` — no separate login, no API calls).
- `python3` (the installer builds a small virtualenv for the menu bar app).
- `jq` + `curl` (for the `ccc` CLI; preinstalled on modern macOS).

### Install
```sh
git clone https://github.com/samdvla/claude-usage-bar.git
cd claude-usage-bar
./install.sh
```

Within a few seconds your menu bar gains one item per enabled provider, side
by side — the Claude spark and the OpenAI blossom glyph, each with its own
5h % once it has data. On first launch macOS may ask permission to read your
Claude Code login from the Keychain — click **Allow** (or **Always Allow**).

### Use
- Each menu bar item shows that provider's current 5h utilization. Click
  either for the shared dropdown (5h/7d bars and reset countdowns per
  provider, Refresh, Open in Terminal, Quit).
- **Settings ▸** in the dropdown: **Show Claude** / **Show Codex** toggle each
  provider independently (both on by default), plus **Auto-restore icon**.
- `ccc` (one-shot, Claude + Codex sections), `ccc --watch` (live), `ccc --json`
  (raw Claude headers plus the Codex snapshot record), `ccc open` (open the
  menu bar app).
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
- Optional: **OpenAI Codex CLI**, run at least once (the app reads its local
  session logs at `%USERPROFILE%\.codex\sessions` — no separate login, no API
  calls).
- To build from source: the **.NET 8 SDK** (`winget install Microsoft.DotNet.SDK.8`).

### Install (prebuilt)
Download `ClaudeUsageBar.exe` (and optionally `ccc.exe`) from
[Releases](https://github.com/samdvla/claude-usage-bar/releases) and run.

> The exe isn't code-signed, so Windows SmartScreen may show **"Windows
> protected your PC."** Click **More info → Run anyway**. (It's a self-contained
> .NET app — large because it bundles the runtime so nothing else is needed.)

The app pins itself to the always-visible part of the taskbar automatically
(via the Windows 11 `IsPromoted` flag) — one badge per enabled provider, the
Claude-orange badge and a neutral gray Codex badge. If your Windows build
keeps them in the **"^" overflow**, drag the badges out onto the taskbar once,
or toggle them on in **Settings → Personalization → Taskbar → Other system
tray icons**.

### Build from source
```powershell
git clone https://github.com/samdvla/claude-usage-bar.git
cd claude-usage-bar\windows
pwsh .\build.ps1        # outputs windows\dist\ClaudeUsageBar.exe and ccc.exe
dotnet test tests\Core.Tests\Core.Tests.csproj   # run the unit tests
```

### Use
- The taskbar shows one badge per enabled provider — **Claude-orange** and a
  **neutral gray Codex badge** — each with its own 5h %.
- **Left-click** either icon → shared flyout with 5h/7d health-colored bars +
  reset countdowns, one section per provider.
- **Right-click** either icon → Refresh / Auto-start at login / Open in
  Terminal / Quit, plus **Show Claude icon** / **Show Codex icon** (the last
  visible icon can't be unchecked, so the app can never go fully invisible).
- `ccc.exe`, `ccc.exe --watch`, `ccc.exe --json`, `ccc.exe open` — same
  Claude + Codex sections as the macOS `ccc`.

See [`windows/README.md`](windows/README.md) for more detail.

---

## Features (both platforms)

- **Claude + Codex**, side by side, each independently toggleable in Settings
  (default: both on).
- **Real utilization**, not local estimates — Claude via Anthropic's
  `anthropic-ratelimit-unified-*` rate-limit headers, Codex via its own local
  session-log snapshots.
- **Color-coded** 5h and 7d: green `<50%`, orange `<80%`, red `≥80%`.
- **Reset countdowns** for both windows, per provider.
- **Always current** — refreshes when you open the view, plus a light
  background tick every 2 minutes.
- **Zero setup** — no API key, no sign-in for either provider; Codex needs no
  login of its own at all.

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

## Codex support

Codex works on a completely different principle: **zero extra API calls.**
The Codex CLI already writes a `rate_limits` snapshot into
`~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl` (`%USERPROFILE%\.codex\sessions`
on Windows) every turn. This app just finds the newest snapshot on disk and
reads it — no OpenAI/ChatGPT login, no OAuth handling, nothing to authenticate
and nothing sent over the network.

The trade-off is staleness: Codex's numbers are only as fresh as your last
Codex session, since there's no live probe to fall back on. Once a snapshot is
more than a couple of minutes old, the dropdown, flyout, and CLI mark that
section `as of <age> ago` so you know you're looking at a snapshot rather than
a live read. And if a rate-limit window has already passed its reset since the
snapshot was taken, that window shows `0%` instead of a stale number — actual
usage since the reset is unknown, but it starts from zero. No Codex sessions
yet on this machine → the app shows "No Codex sessions found — run codex
once."

Both providers can be shown or hidden independently:
- **macOS:** dropdown ▸ **Settings** ▸ **Show Claude** / **Show Codex**
  (default: both on).
- **Windows:** right-click the tray icon ▸ **Show Claude icon** / **Show
  Codex icon** (default: both on), stored at `HKCU\Software\ClaudeUsageBar`.
  The last visible icon can't be hidden, so the app always stays reachable.

## License

[MIT](LICENSE) © 2026 Sam Davila

macOS app built with [rumps](https://github.com/jaredks/rumps) (BSD); Windows
app built on .NET / WinForms. Application code and the terminal/app icons are
original to this project; the Claude and OpenAI provider glyphs are rasterized
from the CC0-licensed [simple-icons](https://simpleicons.org) set and remain
their respective owners' trademarks, used nominatively to label each
provider's own usage number.
