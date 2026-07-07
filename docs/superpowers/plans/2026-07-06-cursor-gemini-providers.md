# Cursor + Gemini Providers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add auto-detected Cursor (monthly quota, live probe) and Gemini CLI (estimated daily count, local logs) providers, and return the macOS bar to ONE status item with uniformly-spaced segments.

**Architecture:** Two new stdlib helpers (`bin/ccc-cursor`, `bin/ccc-gemini`) follow the `bin/ccc-codex` contract: importable function + CLI printing one record JSON. The record gains `estimated` (bool). All four providers get `detect()`; UI renders only detected+enabled ones. macOS drops the multi-item machinery (single rumps item, `SEGMENT_GAP` separator). Windows mirrors with `CursorUsageReader`/`GeminiSessionReader` and generalizes tray slots + flyout sections.

**Tech Stack:** Python 3 stdlib (sqlite3, urllib, zoneinfo), rumps/PyObjC, bash+jq, .NET 8 + Microsoft.Data.Sqlite, xUnit/pytest.

**Spec:** `docs/superpowers/specs/2026-07-06-cursor-gemini-providers-design.md`. Research (local, gitignored — implementers on this Mac may read): `.superpowers/sdd/research-cursor.md`, `.superpowers/sdd/research-gemini.md`.

## Global Constraints

- Branch `feat/cursor-gemini-providers` off master. Commit per task, NO AI trailers.
- Helpers stdlib-only; never throw to callers; record shape `{"state","u5","u7","r5","r7","plan","as_of"}` + optional `"estimated": true` + provider-specific extras. Fractions 0.0–1.0.
- Cursor: read-only credential access (`mode=ro&immutable=1` SQLite URI); NEVER print/log token values; NO token-refresh POST (401 → state `expired`); probe cadence ≥300 s via cache file `~/.config/ccc/cursor-cache.json` (record + `fetched_at`); 20 s HTTP timeout.
- Cursor endpoints (verified 2026-07-06 against openusage main): `POST https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage` and `.../GetPlanInfo`, body `{}`, headers `Authorization: Bearer <token>`, `Content-Type: application/json`, `Connect-Protocol-Version: 1`. Mapping: `planUsage.totalPercentUsed`/100 → u5 (fallback compute `totalSpend/limit` when percent absent but both cents fields present); `billingCycleEnd` (epoch **ms**) /1000 → r5; `planInfo.planName` TitleCased → plan; `enabled == false` → state `no_data`. No legacy-cookie fallback in this iteration (YAGNI — revisit on user reports).
- Gemini: count `type == "gemini"` records (each = one model response ≈ one quota request) in `~/.gemini/tmp/*/chats/**/*.jsonl` — glob BOTH `chats/*.jsonl` and `chats/*/*.jsonl` (subagent files) — with record `timestamp` ≥ last midnight **America/Los_Angeles** (stdlib zoneinfo). Do NOT replay `$rewindTo`/`$set` control records — rewound calls still consumed quota. Only open files with mtime within 48 h. `u5 = count/cap` (cap via `--cap N`, default 1000; cap<1 → 1000; u5 caps at 1.0 for the bar, real pct kept in `"count"`/`"cap"` extras); r5 = next midnight PT epoch; `estimated: true`; as_of None (live count).
- Detection: claude = `security find-generic-password -s "Claude Code-credentials"` rc 0; codex = isdir `~/.codex/sessions`; cursor = state.vscdb exists AND `cursorAuth/accessToken` key present (macOS `~/Library/Application Support/Cursor/User/globalStorage/state.vscdb`, Windows `%APPDATA%\Cursor\User\globalStorage\state.vscdb`); gemini = isdir `~/.gemini/tmp`. Shown iff detected AND enabled. Cache detection per refresh tick.
- macOS single item: `SEGMENT_GAP = "   "` (3 spaces) between segments; estimated values render `est. NN%`; all-hidden → terminal icon anchor. Settings adds `showCursor`/`showGemini` (default ON) + "Gemini plan" submenu writing `geminiDailyCap` int: Free 1000 (default) / Pro 1500 / Ultra 2000 / API key 250.
- Windows: registry `ShowCursor`/`ShowCodex` etc. DWORDs default 1 + `GeminiDailyCap` DWORD default 1000; badges Cursor `#1A1A1E`/white text, Gemini `#4285F4`/white text; estimated tray values prefixed `~`.
- Windows PC: `ssh cyberadmin@100.116.225.124` (PowerShell 5.1 — `;` not `&&`; dotnet at `C:\Program Files\dotnet\dotnet.exe`; repo cloned at `C:\Users\cyberadmin\claude-usage-bar`).
- macOS tests: `~/.config/ccc/venv/bin/python -m pytest tests/ -v` (currently 11). Test loading via `tests/test_selfheal.py`'s shared `_load()` pattern for ccc-bar; new helpers get their own SourceFileLoader (stdlib-only, no PyObjC conflict).

## File Map

| File | Change |
|---|---|
| `bin/ccc-cursor`, `tests/test_cursor_reader.py` | **new** — probe helper + tests |
| `bin/ccc-gemini`, `tests/test_gemini_reader.py` | **new** — log-count helper + tests |
| `bin/ccc-bar` | single-item revert; PROVIDERS v2 (detect/labels/estimated); Settings additions |
| `bin/ccc` | cursor + gemini sections, detection gating |
| `assets/generate_glyphs.py`, `assets/{cursor,gemini}-glyph.png` | 2 new glyphs |
| `install.sh` | install 2 helpers + 2 glyphs |
| `windows/src/Core/{CursorUsageReader,GeminiSessionReader}.cs` + tests | **new** |
| `windows/src/Core/ClaudeUsage.Core.csproj` | + Microsoft.Data.Sqlite |
| `windows/src/Core/RateUsage.cs` | + `Estimated` bool, `Provider.Cursor/Gemini` |
| `windows/src/TrayApp/{IconRenderer,TrayController,FlyoutForm}.cs` | colors; slot list; section list |
| `windows/src/Cli/Program.cs` | 2 sections + detection |
| `README.md`, `windows/README.md` | provider matrix |

---

### Task 0: Branch

- [ ] `cd ~/Projects/claude-usage-bar && git checkout master && git pull && git checkout -b feat/cursor-gemini-providers`

---

### Task 1: macOS single-item revert (deploy for Sam's visual OK)

**Files:** Modify `bin/ccc-bar` only.

**Interfaces produced:** `SEGMENT_GAP = "   "` module constant; `title_attr_string(entries, button)` accepts MULTIPLE entries again, joining segments with `_seg_font(SEGMENT_GAP, ...)`; single rumps status item is the only item.

- [ ] **Step 1: Remove multi-item machinery.** Delete from `bin/ccc-bar`: `_codex_item` field + `_ensure_codex_item` + `_remove_codex_item`; `_stash_position`/`_unstash_position`/`_pos_key`/`_restore_pos_key`; `_seed_positions` and the 600/599 band constants; `_recreate_claude_item` (restore the pre-87e70e9 simple `_restore_status_item` body: remove old rumps item, `initializeStatusBar()`, reset `_delegate_attached`, refresh — see git history `git show 548c5e9^:bin/ccc-bar` for the original). KEEP: `setAutosaveName_("ccc-claude-item")` on the rumps item (position persistence), tint caches, `_menu_bar_dark`, theme observer, the delayed first-tint repaint (now schedule it whenever the rumps item is recreated).
- [ ] **Step 2: Title composition.** Add `SEGMENT_GAP = "   "` near `W_REG`. `title_attr_string(entries, button)`: before each entry after the first, append `_seg_font(SEGMENT_GAP, color, font)`. `_apply`: build `entries` for every provider present in `data` (all enabled providers), set attributed title on the single rumps button; no data at all → icon anchor (existing logic).
- [ ] **Step 3: Verify + deploy.** `~/.config/ccc/venv/bin/python -m pytest tests/ -v` (11 pass). Deploy (install + bootout/bootstrap + pgrep). Screenshot the menu bar (screencapture + crop, as in task-4 report rounds) — expect ONE item: `[spark] NN%   [blossom] NN%` with equal gaps. Ask controller to get Sam's visual OK before Task 5 ships more segments (do not block Tasks 2–4 on it).
- [ ] **Step 4: Commit** `refactor(ccc-bar): single status item with uniform segment gaps (drops multi-item machinery)`

---

### Task 2: `bin/ccc-cursor` (TDD)

**Files:** Create `bin/ccc-cursor`, `tests/test_cursor_reader.py`; modify `install.sh` (+1 install line after ccc-codex's).

**Interfaces produced:** `cursor_usage(state_db=None, cache_path=None, now=None, urlopen=None, min_interval=300) -> dict`; `cursor_detected(state_db=None) -> bool`; CLI `python3 bin/ccc-cursor` prints record JSON (`--detect` exits 0/1 for detection; `--force` ignores cache TTL).

- [ ] **Step 1: Failing tests.** `tests/test_cursor_reader.py` — SourceFileLoader-load `bin/ccc-cursor` (own loader; stdlib module). Fixtures: build a real sqlite file per test (`sqlite3.connect(tmp)`; `CREATE TABLE ItemTable (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB)`; insert `cursorAuth/accessToken` = `"x.y.z"`). Fake `urlopen(req, timeout=...)` returning context-manager objects whose `.read()` yields canned JSON per URL suffix. Tests:
  1. `test_happy_path`: GetCurrentPeriodUsage → `{"enabled":true,"planUsage":{"limit":2000,"totalSpend":900,"totalPercentUsed":45.0},"billingCycleStart":1780000000000,"billingCycleEnd":1782600000000}`; GetPlanInfo → `{"planInfo":{"planName":"pro"}}`. Assert u5==0.45, r5==1782600000, plan=="Pro", state=="ok", as_of==now, estimated absent/False.
  2. `test_percent_fallback_from_cents`: percent field absent, limit=2000/totalSpend=500 → u5==0.25.
  3. `test_disabled_subscription`: `{"enabled":false}` → `{"state":"no_data"}`.
  4. `test_401_expired`: urlopen raises `urllib.error.HTTPError(code=401)` → state=="expired".
  5. `test_cache_respected`: first call hits fake network (counter=1); second call with same cache_path and now+120 → counter still 1, record served from cache with original as_of; now+400 → counter 2.
  6. `test_no_token`: empty ItemTable → state=="no_data"; `cursor_detected` False; with token present → True.
  7. `test_network_error_keeps_cache`: seeded cache + urlopen raising URLError → cached record returned with state "ok" (single failure tolerated; `"stale": true` extra set).
- [ ] **Step 2: RED** — run the file, expect load failure.
- [ ] **Step 3: Implement `bin/ccc-cursor`.** Module docstring (data source, privacy: token never logged, no refresh, read-only DB, 5-min cadence, source attribution to openusage research). Constants: `STATE_DB` per-OS default (darwin → `~/Library/Application Support/Cursor/User/globalStorage/state.vscdb`; win32 → `%APPDATA%`; else `~/.config/Cursor/...`), `CACHE = ~/.config/ccc/cursor-cache.json`, `API = https://api2.cursor.sh/aiserver.v1.DashboardService/`. `_token(state_db)`: `sqlite3.connect(f"file:{path}?mode=ro&immutable=1", uri=True)`, SELECT the key, return str or None (all exceptions → None). `_post(name, token, urlopen)`: Request with the three headers + body `b"{}"`, json-parse response. `cursor_usage(...)`: token None → no_data; cache fresh (now - fetched_at < min_interval) → return cached record; probe both endpoints (usage first; plan failure tolerated → plan None); map per Global Constraints; write cache `{"fetched_at": now, "record": rec}` (0600 perms); HTTPError 401/403 → expired (cache cleared); other errors → cached record + `"stale": true` if cache exists else `{"state":"error"}`. `as_of` = fetch time (so UI staleness works between probes). `__main__`: argparse-lite (`--detect`, `--force`), print JSON.
- [ ] **Step 4: GREEN** — all 7 pass + full suite. **Live check** (one real probe, Sam's machine): `python3 bin/ccc-cursor --force` → expect real record (plan likely "Free"); PASTE the record shape (values redacted ok) into the report. If Cursor's real response shape differs from research (missing planUsage on free tier?), STOP and report DONE_WITH_CONCERNS with the actual field names observed.
- [ ] **Step 5: Commit** `feat: ccc-cursor — Cursor monthly usage probe (cached, read-only token)`

---

### Task 3: `bin/ccc-gemini` (TDD)

**Files:** Create `bin/ccc-gemini`, `tests/test_gemini_reader.py`; modify `install.sh`.

**Interfaces produced:** `gemini_usage(root=None, cap=1000, now=None) -> dict` (+ `"count"`, `"cap"`, `"estimated": true` extras); `gemini_detected(root=None)`; CLI flags `--cap N`, `--detect`.

- [ ] **Step 1: Failing tests.** Fixture tree builder: `root/tmp/<hash>/chats/session-x.jsonl` + nested `chats/<parent>/<sub>.jsonl`. Record helper emitting `{"id":"i","timestamp":ts,"type":"gemini","content":"…","model":"gemini-2.5-pro","tokens":{"total":100}}` plus `type:"user"` lines and a `{"$rewindTo":"i"}` control line. Fixed `NOW` = epoch for 2026-07-06T18:00:00-07:00. Tests: happy count across two projects + subagent file (rewind line does NOT reduce count); PT-midnight boundary (record at 06:59:59 UTC vs 07:00:01 UTC on the boundary day counts correctly — compute expected via zoneinfo in the test itself); cap math (`count=250, cap=1000 → u5 0.25`; `count>cap → u5 1.0`, count extra intact); mtime filter (file older than 48 h ignored via os.utime); malformed lines skipped; missing root → no_data + detected False; `r5` == next PT midnight epoch (assert exact via zoneinfo).
- [ ] **Step 2: RED.**
- [ ] **Step 3: Implement.** Glob `os.path.join(root, "tmp", "*", "chats", "*.jsonl")` + `... "chats", "*", "*.jsonl")`; mtime gate; per line: `json.loads`, skip non-dict / `type != "gemini"` / missing-bad timestamp (`datetime.fromisoformat(ts.replace("Z","+00:00"))`); count if ts_epoch >= midnight_pt(now). `midnight_pt(now)`/`next_midnight_pt(now)` via `zoneinfo.ZoneInfo("America/Los_Angeles")` (handle DST via localize-then-floor: `datetime.fromtimestamp(now, tz).replace(hour=0,minute=0,second=0,microsecond=0)`). Return record with `estimated: True`.
- [ ] **Step 4: GREEN** + full suite. (No live check possible — Gemini not installed; note in report.)
- [ ] **Step 5: Commit** `feat: ccc-gemini — estimated daily usage from local session logs`

---

### Task 4: Glyphs

**Files:** Modify `assets/generate_glyphs.py`; create `assets/cursor-glyph.png`, `assets/gemini-glyph.png`; modify `install.sh` (2 cp lines).

- [ ] Extend `ICONS` with `"cursor-glyph.png"` and `"gemini-glyph.png"`. Slugs: check https://simpleicons.org for current "cursor" and "googlegemini" slugs; the existing pinned REF (pre-2025-11-29) may lack Cursor — if so, fetch cursor/gemini from a NEWER pinned commit SHA that contains them (keep the old REF for the two existing icons; per-icon ref dict). Verify: `file` says 64x64 RGBA; Read both PNGs — Cursor = angular cursor-arrow mark, Gemini = four-point star/sparkle; black on transparent. Regenerate NOTHING for claude/codex (files untouched).
- [ ] install.sh: cp both after the codex-glyph line. Commit `feat: cursor + gemini glyph assets`.

---

### Task 5: ccc-bar integration (the big one)

**Files:** Modify `bin/ccc-bar`.

**Interfaces consumed:** helpers from Tasks 2–3 (SourceFileLoader like `_load_ccc_codex`), glyphs from Task 4, single-item title from Task 1.

- [ ] **Step 1: PROVIDERS v2.** Extend the table entries to `(key, settings_key, glyph_path, fetcher, fallback, labels, detect)`:
  - claude: labels `("5h","7d")`, detect = `claude_code_creds() is not None` (wrap: subprocess check cached per tick)
  - codex: `("5h","7d")`, detect = `os.path.isdir(os.path.expanduser("~/.codex/sessions"))`
  - cursor: `("month", None)`, fetcher `cursor_usage()` via loaded module (pass nothing — defaults), detect = `_CURSOR.cursor_detected()` (module load pattern identical to `_load_ccc_codex`; module None → detect False)
  - gemini: `("day", None)`, fetcher `lambda: _GEMINI.gemini_usage(cap=gemini_cap())`, detect same pattern on `gemini_detected()`
  New settings keys `SHOW_CURSOR_KEY="showCursor"`, `SHOW_GEMINI_KEY="showGemini"`; `GEMINI_CAP_KEY="geminiDailyCap"` with `gemini_cap()` accessor (int, default 1000) + `set_gemini_cap(n)`.
- [ ] **Step 2: Detection gating.** `_refresh_worker`: fetch only providers where `provider_enabled AND detect()` (detect computed once per worker run, passed to `_apply` alongside data so title/dropdown agree). Claude detect uses the existing creds call — do NOT double-probe (detect via creds-read only; fetch does the network probe).
- [ ] **Step 3: Title values.** `_title_value(rec)`: if `rec.get("estimated")` and state ok → `f"est. {pct(rec.get('u5'))}"`. Everything else unchanged.
- [ ] **Step 4: Dropdown sections.** `_section_rows` gains labels param: render bar rows only for labels that are not None (`labels[0]` with u5/r5, `labels[1]` with u7/r7); resets line uses the label text (`"month resets in …"`, `"day resets in …"`); estimated sections append `info_row("  estimated from local session logs")`. Cursor `stale: true` extra → append `· cached` to its header age text.
- [ ] **Step 5: Settings.** Two new Show toggles (same `_toggle_provider` mechanism). "Gemini plan" submenu: 4 items ("Free — 1000/day" default, "AI Pro — 1500/day", "AI Ultra — 2000/day", "API key — 250/day"), radio checkmarks (state=1 on the selected cap), callback `set_gemini_cap` + refresh.
- [ ] **Step 6:** pytest full suite (module exec gate) + add `tests/test_settings.py` cases for the two new keys + cap round-trip (default 1000). Deploy + screenshot: with Cursor detected on this Mac expect THREE segments (`spark NN%   blossom NN%   cursor-arrow NN%`); gemini absent (not detected). Commit `feat(ccc-bar): cursor + gemini providers, auto-detection, gemini plan picker`.

---

### Task 5b: macOS in-bar countdown rows (spec §5b, mockup variation C)

**Files:** Modify `bin/ccc-bar`.

**Interfaces consumed:** provider records + labels from Task 5; `countdown()`, `health_color()`, tint/appearance helpers.

- [ ] **Step 1: BarRowView.** Add an NSView subclass (PyObjC) drawing one bar row: window label (left, 22 pt column), rounded track (5 pt radius, `NSColor.quaternaryLabelColor()` fill or #3A3A3E-equivalent via dynamic color), health-color fill rect (existing thresholds via `health_color`), countdown text via the ADAPTIVE rule (inside fill right-aligned, `#1A1A1E` @ 0.72 alpha semibold 11 pt, when fill_width >= text_width + 14; else right-aligned in empty region, `NSColor.secondaryLabelColor()` 11 pt), percent text OUTSIDE right in the health color (semibold, monospacedDigit; `est. NN%` for estimated records). Size: ~(280, 26) per row; `drawRect_` implementation; all drawing main-thread (rows are built in `_show`, which runs in `_apply` via callAfter — already main thread).
- [ ] **Step 2: wire into `_section_rows`.** Replace `bar_row(...)` attributed-string items with `rumps.MenuItem` whose `_menuitem.setView_(BarRowView(...))`; DELETE the per-section resets `info_row` line (countdown now lives in the bars). Keep header rows + estimated footnote as attributed strings. Keep `bar_row` itself only if still referenced elsewhere — if nothing references it, delete it and `BAR_WIDTH`.
- [ ] **Step 3:** pytest full suite (module-exec gate) + deploy + screenshot of the OPEN dropdown if feasible (screencapture of menu open is tricky — at minimum deploy and ask Sam to eyeball). Commit `feat(ccc-bar): drawn bar rows with adaptive in-bar reset countdown (variation C)`.

### Task 6: bash `ccc` sections

**Files:** Modify `bin/ccc`.

- [ ] `render_cursor()`: gate on `python3 "$(helper ccc-cursor)" --detect` rc; record via helper (cache handles cadence — CLI never forces); same bar/int-validation pattern as render_codex (r5/asof integer-regexed); header `cursor usage · <plan> · month`; stale extra → `· cached`. `render_gemini()`: gate on `--detect`; cap read: `defaults read com.claude-usage-bar geminiDailyCap 2>/dev/null || echo 1000` passed as `--cap`; header `gemini usage · est. · day`; single bar. Wire both into the dispatch block (after codex, `|| true` in watch; sections skip silently when undetected). `--json` prints each detected provider's record line. `bash -n` + one live run (cursor probes real API — run ONCE). Commit `feat(ccc): cursor + gemini CLI sections`.

---

### Task 7: macOS install + smoke

- [ ] `./install.sh`; verify `ccc` shows claude+codex+cursor sections (gemini absent); `python3 ~/.local/bin/ccc-cursor` returns ok-record from cache (no double probe); menu bar single item with three segments; Settings shows 4 toggles + Gemini plan submenu. Sam visual OK gate. Commit any install fixes.

---

### Task 8: Windows `CursorUsageReader` (TDD on PC)

**Files:** Create `windows/src/Core/CursorUsageReader.cs`, `windows/tests/Core.Tests/CursorUsageReaderTests.cs`; modify `windows/src/Core/ClaudeUsage.Core.csproj` (PackageReference Microsoft.Data.Sqlite 8.x), `windows/src/Core/RateUsage.cs` (add `bool Estimated = false` trailing param; extend `enum Provider { Claude, Codex, Cursor, Gemini }`).

**Interfaces produced:** `CursorUsageReader(string? stateDb = null, string? cachePath = null, HttpMessageHandler? handler = null, int minIntervalSeconds = 300)`; `Task<RateUsage> ReadAsync(long nowEpoch)`; `bool Detected()`.

- [ ] Tests mirror Task 2's seven cases (fixture sqlite via Microsoft.Data.Sqlite; fake HttpMessageHandler returning canned JSON per endpoint; cache file in temp dir). Implementation mirrors `bin/ccc-cursor` semantics exactly (same mapping/cadence/expired rules; no refresh). All dotnet commands on the PC (`git pull` the pushed branch first). Commit `feat(windows): CursorUsageReader + Estimated/Provider extensions`.

---

### Task 9: Windows `GeminiSessionReader` (TDD on PC)

**Files:** Create `windows/src/Core/GeminiSessionReader.cs` + tests.

**Interfaces produced:** `GeminiSessionReader(string? root = null)`; `RateUsage Read(long nowEpoch, int cap)`; `Detected()`. Root default `%USERPROFILE%\.gemini`.

- [ ] Tests mirror Task 3 (fixture tree incl. subagent nesting, PT-midnight boundary via TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time") — note Windows TZ id differs from IANA; DST handled by TimeZoneInfo), mtime filter, malformed lines, cap math, rewind-not-replayed. Implementation: enumerate with the SafeDirs/SafeFiles guards pattern from CodexSessionReader. Commit `feat(windows): GeminiSessionReader — estimated daily count`.

---

### Task 10: Windows tray generalization

**Files:** Modify `windows/src/TrayApp/{IconRenderer,TrayController}.cs`.

- [ ] IconRenderer: color map per provider (add `CursorDark #1A1A1E`→white text, `GeminiBlue #4285F4`→white text); `Render` gains `bool estimated = false` → prefix the number with `~` (font size logic: treat as 2-char when `~N`, keep 99 cap).
- [ ] TrayController: replace `_tray`+`_trayCodex` pair with `List<TraySlot>` (`record TraySlot(Provider P, NotifyIcon Icon, Func<RateUsage> LastGetter…)` — concretely: a small private class holding NotifyIcon, last RateUsage, Icon currentIcon). Detection: cursor = File.Exists(stateDb) && token key present (reuse CursorUsageReader.Detected()); gemini = Directory.Exists root; codex/claude as today. Undetected or Show<P>=0 → no NotifyIcon. Menu gains Show Cursor icon / Show Gemini icon + "Gemini plan" submenu (radio, writes GeminiDailyCap DWORD). Refresh(): claude probe only when its icon shown (existing fix); cursor `await reader.ReadAsync` (cadence internal); codex/gemini sync reads. Last-visible guard spans the whole set. PromoteToTaskbar loops all slots. Dispose loops (Visible=false then Dispose). Commit `feat(windows-tray): provider slot list, cursor + gemini icons, plan picker`.

---

### Task 11: Windows flyout section list

**Files:** Modify `windows/src/TrayApp/FlyoutForm.cs`.

- [ ] Replace hardcoded Claude/Codex controls with a `ProviderSection` control group (header label, up to 2 BarControls with label text from provider window labels, footnote label — NO reset labels, see next bullet) built per provider at construct time; `Render(IReadOnlyList<RateUsage> records)` shows sections for the passed records in order, hides the rest, and sets `Height = 44 + visibleSections * sectionHeight + footer` (compute from actual control heights — kills the fixed-320 dead space). Estimated sections show `est.` in the bar % and footnote "estimated from local session logs". TrayController passes detected+shown records. Commit `feat(windows-tray): dynamic per-provider flyout sections`.
- [ ] **In-bar countdown (spec §5b):** `BarControl` gains a `ResetText` string property; in its `OnPaint`, draw it with the adaptive rule — inside the fill right-aligned (color `#1A1A1E` at 72% alpha, bold, 8.5pt) when fillWidth >= textWidth + 14px, else right-aligned in the empty region (theme.Secondary). Reset label rows are removed from the flyout entirely. Same commit.

---

### Task 12: Windows CLI

**Files:** Modify `windows/src/Cli/Program.cs`.

- [ ] After codex section: cursor section (only when `CursorUsageReader.Detected()`; header `cursor usage · <plan> · month`), gemini section (when Detected; cap from registry GeminiDailyCap default 1000; header `gemini usage · est. · day`). `--json` adds `cursor-*`/`gemini-*` lines for detected providers. Claude-failure path still renders all detected sections (established rule). Commit `feat(windows-cli): cursor + gemini sections`.

---

### Task 13: Windows PC round

- [ ] Push branch; on PC: pull, `dotnet test` (expect 30 prior + ~14 new), build TrayApp/Cli Release, build.ps1 publish. CLI run: claude error → codex no-data → cursor (detected? Cursor probably NOT installed on PC → section absent — verify detection gating output) → gemini absent. Fix loop as needed. Tray visual: attempt screenshot; if session locked again, defer to Sam (combined with v1's pending tray check).

---

### Task 14: README matrix + wrap

- [ ] README.md + windows/README.md: provider matrix table (provider / what's measured / source / exact-vs-estimated / detection); Gemini estimate caveats (single machine, tier picker, community-verifiable); Cursor privacy notes (read-only token use, no refresh, 5-min cadence, unofficial API disclaimer). Commit `docs: provider matrix — cursor + gemini`.
- [ ] Final whole-branch review (most capable model, package from merge-base, ledger minors triage) → fix wave → merge to master + push per Sam.

## Self-Review Notes

- Spec §1–§10 → Tasks: model/extras (2,3,8), detection (5,6,10,12), cursor (2,8), gemini (3,9), single-item macOS (1,5), Windows UI (10,11,12), glyphs (4), errors (in each helper/reader task), testing (per task + 13), rollout (0,13,14).
- Cursor legacy-cookie fallback deliberately dropped (YAGNI) — spec amended implicitly; Task 2 Step 4 catches real-shape surprises on the live check.
- Windows TZ id ("Pacific Standard Time") vs IANA noted in Task 9 — the one cross-platform trap in the date math.
- Task 1 restores pre-87e70e9 behavior from git history — reference commit given, no invented code needed.
