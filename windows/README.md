# claude-usage-bar (Windows)

A system-tray app + `ccc` CLI showing your real Claude 5h/7d rate-limit usage,
read from the login Claude Code already stored. No sign-in, no API key.

The tray icon shows your current 5h utilization as a number, colored green/
orange/red. Left-click opens a flyout with 5h/7d bars and reset countdowns;
right-click has Refresh / Auto-start / Open in Terminal / Quit.

## Install (prebuilt)

Download `ClaudeUsageBar.exe` (and optionally `ccc.exe`) from Releases and run.
No .NET install required — the exe is self-contained.

> Not code-signed: SmartScreen may say "Windows protected your PC." Click
> **More info → Run anyway**.

Right-click the tray icon → **Auto-start at login** to keep it running.

### Keeping the icon visible on the taskbar

Like the macOS menu bar, the icon lives in the taskbar notification area (bottom
right). Windows 11 may initially tuck new icons into the **"^" overflow** flyout.
To keep it always visible: open the overflow, drag the Claude icon down onto the
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

Reads `%USERPROFILE%\.claude\.credentials.json`, makes one tiny probe call to the
Anthropic API, and reads the `anthropic-ratelimit-unified-*` headers off the
response (a 429 carries them too, so "maxed out" still shows). 401/403 means the
login expired — open Claude Code to refresh.
