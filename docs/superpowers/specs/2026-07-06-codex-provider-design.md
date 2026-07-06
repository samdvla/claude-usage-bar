# Codex provider + settings menu — design

**Date:** 2026-07-06
**Status:** approved (pending spec review)
**Scope:** macOS menu bar app + CLI, Windows tray app + CLI

## Goal

Show OpenAI Codex usage alongside Claude usage in the menu bar / tray:
`[Claude spark] 22%  [OpenAI blossom] 20%`, with a Settings menu to toggle
which providers are shown. Both platforms in one push, macOS first.

## Non-goals

- No live OpenAI API probing (session logs only — decided; avoids abuse-flag
  risk on the ChatGPT account and OAuth refresh handling).
- No third provider yet (the provider model must make one easy later).
- No per-provider refresh intervals; existing 2-min tick covers both.

## 1. Provider model

Every provider produces the same record; UI code renders a list of enabled
providers and never knows where numbers came from.

| field    | meaning                                             |
|----------|-----------------------------------------------------|
| `util5h` | 0.0–1.0 fraction of the ~5h window                  |
| `util7d` | 0.0–1.0 fraction of the 7d window                   |
| `reset5` | epoch seconds the 5h window resets                  |
| `reset7` | epoch seconds the 7d window resets                  |
| `plan`   | plan label ("Max", "Plus", …)                       |
| `asOf`   | epoch seconds of the data snapshot (None = live)    |
| `state`  | `ok` / `no_login` / `expired` / `no_data` / `error` |

- **Claude provider** = existing keychain probe, behavior unchanged
  (`asOf` = None, it's always live).
- **Codex provider** = new session-log reader (below). `no_login` doesn't
  apply; missing/empty `~/.codex/sessions` → `no_data`.

macOS: plain dicts/small class inside `bin/ccc-bar` (stays a single
installable file). Windows: extend `RateUsage` with `Provider` and `AsOf`
properties defaulting to Claude/null, so `Probe.cs` and its call sites are
untouched.

## 2. Codex session-log reader

Data source: Codex CLI writes `rate_limits` snapshots into
`~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl` on every turn
(`%USERPROFILE%\.codex\sessions` on Windows).

Event line shape (verified against Codex CLI 0.142.5 on 2026-07-06):

```json
{"timestamp":"2026-07-06T14:41:05.555Z","type":"event_msg","payload":{
  "type":"token_count","info":{...},
  "rate_limits":{
    "primary":  {"used_percent":20.0,"window_minutes":300,  "resets_at":1783366288},
    "secondary":{"used_percent":25.0,"window_minutes":10080,"resets_at":1783537493},
    "plan_type":"plus"}}}
```

Algorithm:

1. Find the newest session file: walk `sessions/` taking the
   lexicographically-largest year, month, day dir, then the newest few
   `rollout-*.jsonl` by mtime (a just-started session may not have a
   `rate_limits` event yet — fall back to the next-newest file, max 3).
2. Tail-read the last 64 KB of the file, split lines, scan backwards for the
   last line containing `"rate_limits"`, JSON-parse it. Malformed line →
   keep scanning backwards.
3. Map `primary` → 5h (`used_percent/100`, `resets_at`), `secondary` → 7d,
   `plan_type` → plan, line `timestamp` → `asOf`.
4. **Rollover zeroing:** if `now > resets_at` for a window, report 0.0 for
   that window and drop its reset countdown (window expired since the
   snapshot; actual usage since then is unknown but starts from zero).
5. No dir / no parseable event in 3 newest files → `no_data`.

Reads are local-disk and free, so the reader runs on every refresh (2-min
tick, menu open, manual refresh) with no rate concern.

Staleness display: when `now - asOf` > refresh interval (2 min), the
dropdown/flyout/CLI shows `as of <age> ago` (e.g. `as of 12m ago`) on the
Codex section. The menu-bar number itself shows the snapshot without markers
— rollover zeroing keeps it from being badly wrong.

## 3. macOS menu bar UI (`bin/ccc-bar`)

**Title.** Replace `self.title` text with an `NSAttributedString` set
directly on the status-item button (the app already reaches
`self._nsapp.nsstatusitem` for wake-restore). Composition per enabled
provider, joined with two spaces:

- glyph: `NSTextAttachment` with a template `NSImage` (Claude spark /
  OpenAI blossom), baseline-adjusted to the menu-bar font
- value: ` 22%` in the existing monospaced font; error states per provider:
  `⚠` (expired/error), `–` (no data / no login)

The rumps `icon` (terminal glyph) is dropped from the title area when at
least one provider is shown; with both providers toggled off, it comes back
so the item stays clickable. Wake-restore recreation repaints via the same
path (title composed in `_apply`, which already runs after restore).

**Dropdown.** Two sections, reusing `bar_row`/`info_row`:

```
usage
  Claude · Max
    5h  ███░░░░░░░░░  22%
    7d  █░░░░░░░░░░░   4%
    5h resets in 2h 35m · 7d in 3d 2h
  Codex · Plus                as of 12m ago
    5h  ███░░░░░░░░░  20%
    7d  ███░░░░░░░░░  25%
    5h resets in 1h 10m · 7d in 2d 1h
  ⚙ Settings ▸
  Refresh now / Open in Terminal / Quit
```

A disabled provider is omitted from title *and* dropdown sections.

## 4. Settings (macOS)

New `Settings` submenu in the dropdown tail:

- ☑ Show Claude (`showClaude`, default ON)
- ☑ Show Codex (`showCodex`, default ON)
- ☑ Auto-restore icon (existing `selfHeal` item moves in here)

Persisted in the existing NSUserDefaults suite `com.claude-usage-bar`.
Toggling repaints title + dropdown immediately (no restart).

## 5. Windows tray app

- **One `NotifyIcon` per enabled provider.** `TrayController` owns a small
  list of provider icon slots. Claude icon unchanged; Codex icon rendered by
  `IconRenderer` with a distinct badge (see Icons). Tooltip per provider
  (`Codex usage · Plus — 5h 20% · 7d 25%`).
- Left-click either icon → the same shared flyout, now rendering both
  provider sections (Codex section includes `as of` age line).
  Right-click either icon → shared context menu, gaining
  `Show Claude icon` / `Show Codex icon` checkboxes.
- Settings persisted at `HKCU\Software\ClaudeUsageBar`
  (`ShowClaude`/`ShowCodex` DWORDs, default 1). Unchecking the last visible
  icon keeps that icon (refuse + no-op) so the app never becomes unreachable.
- Both icons get the existing `IsPromoted` treatment in `PromoteToTaskbar`
  (match on ExecutablePath already covers both; verify both entries appear).
- **Core lib:** `CodexSessionReader` implementing the section-2 algorithm,
  reading `%USERPROFILE%\.codex\sessions`; unit-tested. `Probe.cs` untouched.

## 6. CLI

- `bin/ccc` (bash): Codex section under the Claude one — same bar rendering,
  header `codex usage · via session log · as of 12m ago`. Parsing via an
  embedded `python3` heredoc (jq can't tail-read; python3 is already a
  runtime dep of the app). `--json` prints Claude headers + Codex snapshot
  JSON. No Codex data → section shows a dim `no codex session data` line.
- `windows/src/Cli` (`ccc.exe`): same section using `CodexSessionReader`.

## 7. Icons

- **Menu bar glyphs (macOS):** monochrome template PNGs generated from the
  official logomark vector paths — Anthropic spark (Claude), OpenAI blossom
  (Codex) — committed at `assets/claude-glyph@1x/@2x.png`,
  `assets/codex-glyph@1x/@2x.png` (16 pt design size), installed by
  `install.sh` to `~/.config/ccc/`. Template mode (`setTemplate_(True)`)
  handles light/dark/highlight automatically. Missing glyph file → fall back
  to text labels `C:` / `X:` so the bar still works.
- **Windows badges:** `IconRenderer` gains a provider parameter — Claude
  keeps the existing orange badge; Codex gets a neutral (white/gray) badge so
  the two are distinguishable at 16 px. Glyph paths drawn in GDI+ or embedded
  PNG resources, whichever matches the existing renderer.
- Monochrome nominative use of both logomarks; no color brand marks.

## 8. Error handling

- Claude paths byte-for-byte unchanged (probe, 429-still-has-headers, expired
  login messaging).
- Codex `no_data`: dropdown/flyout line "No Codex sessions found — run codex
  once"; title shows `[blossom] –`.
- Malformed/truncated jsonl lines: skip backwards; never crash the refresh.
- Session file larger than tail window with no `rate_limits` in the last
  64 KB: fall back to next-newest file (covers long tool-only stretches).
- Clock skew / `resets_at` in the past: rollover zeroing already handles it.

## 9. Testing

- **macOS (pytest, existing importlib harness in `tests/`):** Codex reader
  against fixture jsonl files — happy path, rollover zeroing, malformed
  lines, empty dir, newest-file fallback; settings toggles round-trip
  (pattern exists in `test_selfheal.py`).
- **Windows (xUnit in `Core.Tests`):** same matrix for `CodexSessionReader`
  with fixture files; `TooltipFor`-style formatting tests for the Codex
  variants.
- **Manual smoke:** menu bar renders both glyphs light+dark; toggles hide
  title segment and dropdown section; Windows two-icon add/remove; flyout
  both sections; CLI sections on both platforms.

## 10. Rollout

Branch `feat/codex-provider`, macOS commits first, then Windows, then README
and version notes. Windows build/test round happens on the gaming PC.
