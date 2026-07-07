# claude-usage-bar (Windows)

A system-tray app + `ccc` CLI showing your real Claude and Codex 5h/7d
rate-limit usage — Claude read from the login Claude Code already stored,
Codex from its own local session logs. No sign-in, no API key.

One tray icon per enabled provider — the Claude-orange badge and a light-gray
Codex badge — each shows that provider's current 5h utilization as a number,
colored green/orange/red. Left-click either opens a shared flyout with 5h/7d
bars and reset countdowns per provider (the Codex section adds an
"as of Xm ago" line when its snapshot is stale); right-click has Refresh /
Auto-start / Show Claude icon / Show Codex icon / Open in Terminal / Quit.

## Install (prebuilt)

Download `ClaudeUsageBar.exe` (and optionally `ccc.exe`) from Releases and run.
No .NET install required — the exe is self-contained.

> Not code-signed: SmartScreen may say "Windows protected your PC." Click
> **More info → Run anyway**.

Right-click either tray icon → **Auto-start at login** to keep it running.

### Showing / hiding providers

**Show Claude icon** / **Show Codex icon** in the right-click menu toggle each
provider's tray icon (stored at `HKCU\Software\ClaudeUsageBar`, both on by
default). The last visible icon refuses to hide, so the app always stays
reachable.

### Keeping the icons visible on the taskbar

Like the macOS menu bar, the icons live in the taskbar notification area (bottom
right). Windows 11 may initially tuck new icons into the **"^" overflow** flyout.
To keep them always visible: open the overflow, drag the badges down onto the
taskbar — or go to **Settings → Personalization → Taskbar → Other system tray
icons** and turn **Claude Usage** on.

## Build from source

Requires the .NET 8 SDK.

```powershell
cd windows
pwsh ./build.ps1   # outputs windows/dist/*.exe
```

Run the unit tests:

```powershell
dotnet test tests/Core.Tests/Core.Tests.csproj
```

## How it works

**Claude:** reads `%USERPROFILE%\.claude\.credentials.json`, makes one tiny
probe call to the Anthropic API, and reads the `anthropic-ratelimit-unified-*`
headers off the response (a 429 carries them too, so "maxed out" still shows).
401/403 means the login expired — open Claude Code to refresh.

**Codex:** zero API calls. The Codex CLI already writes rate-limit snapshots
into its `%USERPROFILE%\.codex\sessions` rollout logs on every turn; the app
reads the newest one straight off disk — no OpenAI login, nothing over the
network. Numbers are only as fresh as your last Codex session (hence the
"as of" line), and a window already past its reset shows 0% instead of a stale
number. No sessions yet → "No Codex sessions found — run codex once."

`ccc.exe` prints both sections; `ccc.exe --json` adds the `codex-*` lines to
the raw output.
