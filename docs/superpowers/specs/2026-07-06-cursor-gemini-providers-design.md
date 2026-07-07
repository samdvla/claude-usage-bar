# Cursor + Gemini providers, auto-detection — design

**Date:** 2026-07-06
**Status:** approved (pending spec review)
**Scope:** both platforms; builds on the provider architecture shipped in
`2026-07-06-codex-provider-design.md` (merged 4872280)
**Goal driver:** make the app shareable — a new user installs it and sees
items only for the AI tools they actually use, zero config.

## Goals

1. Cursor provider: first-class monthly-quota meter for any plan type.
2. Gemini CLI provider: best-effort **estimated** daily meter from local logs.
3. Auto-detection for ALL four providers: an item exists only when its tool
   is detected on the machine AND its Settings toggle allows it.

## Non-goals

- No Gemini live-quota API integration (its OAuth landscape is unstable —
  possible 2026-06-18 personal-tier discontinuation; the estimate degrades
  gracefully to "never detected" if the tool dies).
- No Cursor Enterprise Admin/Analytics API (needs org admin; irrelevant to
  individual users).
- No new refresh architecture: existing 2-min tick stays; Cursor rides it
  with an internal 5-min minimum probe interval.

## 1. Provider model extension

The provider record keeps its shape (`state,u5,u7,r5,r7,plan,as_of`) plus
one new optional field: `estimated` (bool, default false). The PROVIDERS
table (both platforms) gains per-provider:

| field | claude | codex | cursor | gemini |
|---|---|---|---|---|
| window labels | 5h / 7d | 5h / 7d | month / – | day / – |
| `detect()` | keychain entry exists | `~/.codex/sessions` dir | state.vscdb has `cursorAuth/accessToken` | `~/.gemini/tmp` dir |
| data source | live probe | local logs | live probe (5-min min) | local logs (estimate) |
| estimated | no | no | no | **yes** |

Single-window providers put their fraction in `u5`/`r5` and leave `u7`/`r7`
None; the UI renders only the windows named in the labels.

## 2. Auto-detection

- Display rule: item shown iff `detect()` AND `provider_enabled(key)`.
  Undetected → no item, no fetch, no probe, no dropdown section.
- **Activity window (added 2026-07-06, user feedback):** `detect()` means
  *recently active*, not merely installed — the provider's data source must
  show activity within `ACTIVE_DAYS = 14`: Codex/Gemini = newest session
  file mtime; Cursor = state.vscdb mtime (plus token present). Claude stays
  presence-based (Claude Code refreshes its keychain entry continuously and
  is the app's core provider). An installed-but-idle tool disappears from
  the bar; the Settings toggle remains a manual override for *hiding* only
  (it cannot force-show an undetected provider).
- `detect()` runs at launch and on every refresh tick (all checks are a
  stat/keychain call; cache within a tick). A tool installed mid-session
  appears within one tick.
- Claude/Codex adopt the same rule (behavior change: a machine without
  Claude Code no longer shows a `–` Claude item). Settings toggles keep
  their meaning (user veto) and defaults (ON).
- All four hidden/undetected → the Claude rumps item shows the bare
  terminal icon as the reachability anchor (existing behavior, now the
  general rule).

## 3. Cursor provider

- **Helper:** `bin/ccc-cursor` (stdlib Python: `sqlite3` + `urllib`), same
  contract as `ccc-codex`: importable `cursor_usage(...)` + CLI printing the
  record JSON. Loaded by ccc-bar via SourceFileLoader; called by bash `ccc`.
- **Credential:** read-only SQLite open (`file:...?mode=ro&immutable=1`) of
  `~/Library/Application Support/Cursor/User/globalStorage/state.vscdb`
  (`%APPDATA%\Cursor\User\globalStorage\state.vscdb` on Windows), ItemTable
  keys `cursorAuth/accessToken` (+ `cursorAuth/refreshToken` presence only).
  Never log token values.
- **Endpoints** (verified against actively-maintained OSS, 2026): POST
  `https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage`
  and `.../GetPlanInfo`, JSON bodies, `Authorization: Bearer <accessToken>`.
  Exact request/response field mapping lives in the implementation plan
  (source: `.superpowers/sdd/research-cursor.md`, local). Map to: `u5` =
  included-quota consumed fraction, `r5` = period end epoch, `plan` = plan
  name. Legacy `GET cursor.com/api/usage` is the documented fallback.
- **Cadence:** minimum 300 s between real probes; the 2-min tick serves the
  cached record (`as_of` = probe time; dropdown shows "as of Xm ago" past
  5 min, reusing the staleness rule). Errors: 401/403 → state `expired`
  ("Open Cursor to refresh login"); network/5xx → keep cached record, flip
  to `error` after 3 consecutive failures.
- **Windows:** `CursorUsageReader` in Core. Reading state.vscdb adds the
  `Microsoft.Data.Sqlite` NuGet package to Core — accepted dependency.

## 4. Gemini provider (estimated)

- **Helper:** `bin/ccc-gemini` (stdlib: `glob`, `json`, `zoneinfo`): count
  request records (`type:"gemini"`) in `~/.gemini/tmp/*/chats/**/*.jsonl`
  with timestamp ≥ last midnight **America/Los_Angeles** (documented quota
  reset boundary), across all project hashes; only files with mtime within
  48 h are opened. `u5 = count / cap`, `r5` = next midnight PT, `plan` =
  tier label, `estimated = true`.
- **Cap / tier picker:** Settings ▸ "Gemini plan" — Free 1000/day
  (default), Pro 1500/day, Ultra 2000/day, API-key free 250/day. Stored
  per-platform (NSUserDefaults key `geminiDailyCap` / registry
  `GeminiDailyCap` DWORD); helpers receive it as an argument (`--cap N`),
  defaulting to 1000.
- **UI labeling:** value renders `est. NN%`; dropdown section footnote
  "estimated from local session logs". README documents the limits (single
  machine, tier not auto-detected, retries counted).

## 5. macOS UI: back to ONE status item (user decision 2026-07-06)

Reverses the v1 separate-items experiment (548c5e9/87e70e9): all providers
render in a SINGLE status item (the rumps item), as `[glyph] NN%` segments
separated by a uniform fixed separator (`SEGMENT_GAP = "   "`, three
spaces in the title font — identical between every pair, tunable in one
place). Order: claude, codex, cursor, gemini.

- Toggling/detection removes a segment (and its dropdown section); the gap
  count adjusts automatically. All providers hidden/undetected → terminal
  icon only (existing anchor behavior).
- **Removal:** the multi-item machinery is deleted — `_codex_item`
  management, position seeding, `_stash_position`/`_unstash_position`,
  `_recreate_claude_item`, the 600/599 band constants. The single rumps
  item keeps its `autosaveName` (position persistence is still useful) and
  the existing wake-restore recreate. Net code reduction.
- Dropdown: sections iterate detected+enabled providers; window labels
  come from the PROVIDERS table; single-window providers render one bar.
  Estimated providers show `est.` in the value and the footnote line.
- Settings: Show <Provider> toggle per provider (existing two + two new
  keys `showCursor`/`showGemini`, default ON) + "Gemini plan ▸" submenu
  (radio-style checkmarks).
- The first implementation task is this single-item revert (Claude+Codex
  only), deployed immediately so Sam validates the look before the new
  providers land on top of it.

## 5b. In-bar reset countdown (user pick 2026-07-06: mockup variation C)

Dropdown/flyout bars become drawn bars with the reset countdown INSIDE the
track, adaptive battery-style; the separate per-section "resets …" line is
removed (one line saved per provider).

- **Swap (Sam 2026-07-07): the PERCENTAGE lives inside the bar; the
  COUNTDOWN takes the outside-right column** (health-colored semibold,
  monospacedDigit — the row's key figure slot). Adaptive placement rule now
  applies to the % text: inside the fill (dark `#1A1A1E` ~72 %, semibold)
  when fill ≥ 50 % and it fits + padding; otherwise right-aligned in the
  dotted region (secondary label). Estimated values render compactly
  INSIDE as `~NN%` (unclamped, e.g. `~120%` — the `est.` long form never
  fits reliably in-bar; the section footnote carries the "estimated"
  disclosure). Countdown text: existing `countdown()` formatting.
- **macOS (visual style revised 2026-07-06 per Sam's live feedback):**
  dropdown rows are custom NSViews via `NSMenuItem.setView_`, but the bar
  reproduces the ORIGINAL dotted text-bar aesthetic, not smooth rounded
  bars: track = near-black rect (3 pt radius) textured with a dot grid
  (~1.5 pt dots at ~4.5 pt pitch, dim ~30 %-white) exactly like the old
  `░` region; fill = health-color block with faint vertical segment
  separators every ~8 pt (slightly darker shade) like the old `█` runs;
  window label left, `NN%` right-aligned OUTSIDE in the health color.
  Countdown placement keeps the design-C adaptive rule (inside fill when
  ≥50 % and it fits, else in the dotted region). Estimated providers show
  `est. NN%` outside and keep the footnote row. Rows non-interactive.
  Acceptance = side-by-side render match against Sam's original dropdown
  screenshot texture, verified by the controller before deploy.
- **Windows:** `BarControl` (already a drawn control) gains the same
  adaptive in-bar countdown; flyout reset labels are removed (section =
  header + bars + optional footnote).
- Reference mockup: `.superpowers/sdd/bar-mockups.html` variation C.

## 6. Windows UI generalization

- `TrayController`: per-provider slot list (icon + last record + visibility
  from registry `Show<Provider>` DWORDs). Last-visible-icon guard covers
  the whole set. Hidden/undetected providers: no NotifyIcon, no probe.
- `IconRenderer`: badge colors — Claude orange (existing), Codex light
  gray (existing), Cursor near-black `#1A1A1E` badge with white text,
  Gemini Google-blue `#4285F4` badge with white text. Estimated values
  render with a leading `~` (tray tooltip says "est.").
- `FlyoutForm`: refactor the two hardcoded sections into a section list
  with dynamic height (also removes the fixed-320 dead space flagged in
  the v1 final review). Estimated sections get the footnote line.
- CLI: sections for each detected provider, same order.

## 7. Glyphs

`assets/generate_glyphs.py` adds `cursor-glyph.png` and `gemini-glyph.png`
from simple-icons (slugs verified at implementation time; new pinned REF
that contains all four icons). Same 64 px black-on-transparent output,
runtime-tinted. Text fallbacks: `R:` (Cursor), `G:` (Gemini). install.sh
copies all four glyphs.

## 8. Error handling

- Helpers never throw to callers (established contract): all fs/sqlite/
  network/parse failures degrade to `no_data` (or cached record for
  Cursor). All jq→bash values integer-validated as in v1.
- Cursor probe failures never block the tick (probe runs in the existing
  worker thread; 20 s timeout).
- Gemini cap=0 or missing cap → treat as 1000; count overflow (>cap) caps
  the bar at 100% but shows the real `est.` percentage.

## 9. Testing

- **pytest:** `ccc-cursor` — fixture state.vscdb (sqlite3-built in test),
  injectable `urlopen` (no real network in tests), 401/500/timeout paths,
  cadence gate honored. `ccc-gemini` — fixture chats tree, midnight-PT
  bucketing with fixed `now` (both sides of the boundary), 48 h mtime
  filter, cap math, malformed lines.
- **xUnit:** same matrices for `CursorUsageReader` (HttpClient injectable
  handler) and `GeminiSessionReader`; FlyoutForm section-list formatting.
- **Live verification:** Cursor end-to-end on Sam's Mac (real token; even
  a free plan proves the pipeline). Gemini stays fixture-verified —
  README labels it community-verifiable; flagged, not blocking.
- Windows compile/test/build on the gaming PC (SDK now installed, repo
  cloned under cyberadmin from the previous round).

## 10. Rollout

Branch `feat/cursor-gemini-providers` off master (v1 merged). macOS first,
then Windows, then README provider matrix (what's measured, how, exact vs
estimated). Windows visual tray check remains an open item from v1 —
verify both rounds together when Sam is at the PC.
