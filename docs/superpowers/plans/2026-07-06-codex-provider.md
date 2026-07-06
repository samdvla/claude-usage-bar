# Codex Provider + Settings Toggles Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show OpenAI Codex usage next to Claude usage in the macOS menu bar (`[spark] 22%  [blossom] 20%`) and Windows tray (one icon per provider), with per-provider show/hide settings, on both apps and both CLIs.

**Architecture:** Every provider produces one normalized usage record (5h/7d fractions, resets, plan, data age, state). Claude's record comes from the existing keychain probe (unchanged). Codex's comes from a new session-log reader that tail-reads the newest `~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl` for the last `rate_limits` event. UI layers render whichever providers are enabled. The Codex reader is implemented exactly twice: once in Python (`bin/ccc-codex`, shared by ccc-bar and the bash CLI) and once in C# (`CodexSessionReader` in Core, shared by tray app and ccc.exe).

**Tech Stack:** Python 3 stdlib + rumps/PyObjC (macOS app), bash+jq (macOS CLI), .NET 8 WinForms (Windows tray), xUnit + pytest.

**Spec:** `docs/superpowers/specs/2026-07-06-codex-provider-design.md` — read it first.

## Global Constraints

- Branch: `feat/codex-provider` off `master`. Commit per task, no Claude/AI trailers in commit messages (repo convention).
- Claude probe path stays byte-for-byte unchanged (`probe_rate_headers`, `Probe.cs`, the bash `curl` — do not touch).
- `bin/ccc-codex` must be **stdlib-only** Python (no rumps/PyObjC/pip deps) — it runs under system `python3` from the bash CLI and is SourceFileLoader-imported by ccc-bar and pytest.
- Codex session roots: macOS `~/.codex/sessions`, Windows `%USERPROFILE%\.codex\sessions`. Event shape (Codex CLI 0.142.5): line JSON `{"timestamp":"...Z","type":"event_msg","payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":20.0,"window_minutes":300,"resets_at":<epoch>},"secondary":{"used_percent":25.0,"window_minutes":10080,"resets_at":<epoch>},"plan_type":"plus"}}}`.
- Tail-read window: 64 KB. Newest-file fallback: max 3 files. Rollover zeroing: if `now > resets_at` for a window → report 0.0 and drop that reset.
- Settings keys/defaults: macOS NSUserDefaults suite `com.claude-usage-bar`, keys `showClaude`/`showCodex`, default ON. Windows `HKCU\Software\ClaudeUsageBar`, DWORDs `ShowClaude`/`ShowCodex`, default 1.
- No dotnet SDK on the Mac. Windows compile/test/build steps run on the gaming PC over ssh (`ssh desktop`, authorized). macOS pytest runs locally with `~/.config/ccc/venv/bin/python -m pytest` (venv has pytest + PyObjC; reader tests need only stdlib but run under the same command).
- Health thresholds stay: ≥0.8 red, ≥0.5 orange, else green. 5h = provider primary window, 7d = secondary.

## File Map

| File | Change |
|---|---|
| `bin/ccc-codex` | **new** — stdlib Python Codex reader, importable + executable (prints JSON) |
| `tests/test_codex_reader.py` | **new** — pytest for the reader |
| `tests/test_settings.py` | **new** — provider-toggle round-trip tests |
| `bin/ccc-bar` | provider records, attributed title with glyphs, two-section dropdown, Settings submenu |
| `bin/ccc` | Codex section + `--json` addition |
| `assets/generate_glyphs.py` | **new** — fetch simple-icons SVGs, render template PNGs |
| `assets/claude-glyph.png`, `assets/codex-glyph.png` | **new** — committed 64 px template PNGs |
| `install.sh` | install `ccc-codex` + glyph PNGs |
| `windows/src/Core/RateUsage.cs` | add `Provider`, `AsOf`; add `Provider` enum; `UsageState.NoData` |
| `windows/src/Core/CodexSessionReader.cs` | **new** — C# reader |
| `windows/tests/Core.Tests/CodexSessionReaderTests.cs` | **new** — xUnit matrix |
| `windows/src/TrayApp/IconRenderer.cs` | per-provider badge colors |
| `windows/src/TrayApp/TrayController.cs` | second NotifyIcon, registry settings, menu toggles |
| `windows/src/TrayApp/FlyoutForm.cs` | second provider section |
| `windows/src/Cli/Program.cs` | Codex section |
| `README.md` | document Codex + settings |

---

### Task 0: Branch

- [ ] **Step 1:** `cd ~/Projects/claude-usage-bar && git checkout -b feat/codex-provider`
Expected: `Switched to a new branch 'feat/codex-provider'`

---

### Task 1: `bin/ccc-codex` — Codex session-log reader (Python, TDD)

**Files:**
- Create: `bin/ccc-codex`
- Create: `tests/test_codex_reader.py`
- Modify: `install.sh` (install line)

**Interfaces:**
- Produces: `codex_usage(root=None, now=None) -> dict` returning either
  `{"state":"ok","u5":float|None,"u7":float|None,"r5":int|None,"r7":int|None,"plan":str|None,"as_of":int|None}`
  or `{"state":"no_data"}`. Fractions are 0.0–1.0. Later tasks (ccc-bar Task 4, bash ccc Task 5) rely on exactly these key names.
- CLI behavior: `python3 bin/ccc-codex` prints the record as JSON to stdout, exit 0 even for no_data.

- [ ] **Step 1: Write the failing tests**

Create `tests/test_codex_reader.py`:

```python
import importlib.machinery, importlib.util, json, pathlib, time

_MOD = None

def _load():
    global _MOD
    if _MOD is None:
        p = pathlib.Path(__file__).resolve().parent.parent / "bin" / "ccc-codex"
        loader = importlib.machinery.SourceFileLoader("ccccodex", str(p))
        spec = importlib.util.spec_from_loader("ccccodex", loader)
        _MOD = importlib.util.module_from_spec(spec)
        loader.exec_module(_MOD)
    return _MOD

NOW = 1783300000  # fixed 'now' for determinism

def _event(u5=20.0, u7=25.0, r5=NOW + 9000, r7=NOW + 200000, plan="plus",
           ts="2026-07-06T14:41:05.555Z"):
    return json.dumps({
        "timestamp": ts, "type": "event_msg",
        "payload": {"type": "token_count", "info": {},
                    "rate_limits": {
                        "limit_id": "codex",
                        "primary": {"used_percent": u5, "window_minutes": 300, "resets_at": r5},
                        "secondary": {"used_percent": u7, "window_minutes": 10080, "resets_at": r7},
                        "plan_type": plan}}})

def _mkroot(tmp_path, files):
    """files: list of (relpath, [lines]) — creates sessions tree."""
    root = tmp_path / "sessions"
    for rel, lines in files:
        p = root / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text("\n".join(lines) + "\n")
    return str(root)

def test_happy_path(tmp_path):
    m = _load()
    root = _mkroot(tmp_path, [
        ("2026/07/06/rollout-2026-07-06T10-31-02-abc.jsonl",
         ['{"timestamp":"x","type":"event_msg","payload":{"type":"other"}}', _event()]),
    ])
    rec = m.codex_usage(root=root, now=NOW)
    assert rec["state"] == "ok"
    assert rec["u5"] == 0.20 and rec["u7"] == 0.25
    assert rec["r5"] == NOW + 9000 and rec["r7"] == NOW + 200000
    assert rec["plan"] == "Plus"
    assert rec["as_of"] == 1783348865  # 2026-07-06T14:41:05Z

def test_rollover_zeroing(tmp_path):
    m = _load()
    root = _mkroot(tmp_path, [
        ("2026/07/06/rollout-a.jsonl", [_event(r5=NOW - 100)]),  # 5h window expired
    ])
    rec = m.codex_usage(root=root, now=NOW)
    assert rec["u5"] == 0.0 and rec["r5"] is None
    assert rec["u7"] == 0.25  # 7d still live

def test_malformed_lines_skipped(tmp_path):
    m = _load()
    root = _mkroot(tmp_path, [
        ("2026/07/06/rollout-a.jsonl",
         [_event(u5=11.0), '{"broken rate_limits', 'not json "rate_limits"']),
    ])
    rec = m.codex_usage(root=root, now=NOW)
    assert rec["state"] == "ok" and rec["u5"] == 0.11

def test_fallback_to_older_file(tmp_path):
    import os
    m = _load()
    root = _mkroot(tmp_path, [
        ("2026/07/06/rollout-new.jsonl", ['{"timestamp":"x","type":"event_msg","payload":{}}']),
        ("2026/07/05/rollout-old.jsonl", [_event(u5=33.0)]),
    ])
    # make the no-rate_limits file the newest by mtime
    now = time.time()
    os.utime(f"{root}/2026/07/06/rollout-new.jsonl", (now, now))
    os.utime(f"{root}/2026/07/05/rollout-old.jsonl", (now - 60, now - 60))
    rec = m.codex_usage(root=root, now=NOW)
    assert rec["state"] == "ok" and rec["u5"] == 0.33

def test_no_data_when_missing(tmp_path):
    m = _load()
    assert m.codex_usage(root=str(tmp_path / "nope"), now=NOW) == {"state": "no_data"}
    root = _mkroot(tmp_path, [("2026/07/06/rollout-a.jsonl", ["{}"])])
    assert m.codex_usage(root=root, now=NOW)["state"] == "no_data"
```

- [ ] **Step 2: Run tests, verify they fail**

Run: `cd ~/Projects/claude-usage-bar && ~/.config/ccc/venv/bin/python -m pytest tests/test_codex_reader.py -v`
Expected: FAIL — `FileNotFoundError` loading `bin/ccc-codex` (doesn't exist yet). If pytest itself is missing: `~/.config/ccc/venv/bin/pip install pytest` first.

- [ ] **Step 3: Implement `bin/ccc-codex`**

```python
#!/usr/bin/env python3
"""ccc-codex — read OpenAI Codex usage from its local session logs.

Codex CLI writes a `rate_limits` snapshot into
~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl on every turn. This module finds
the newest snapshot and normalizes it to the same record shape ccc uses for
Claude: fractions for the 5h (primary) and 7d (secondary) windows, reset
epochs, plan, and the snapshot's age. Stdlib only — imported by ccc-bar and
invoked as `python3 ccc-codex` by the ccc CLI (prints the record as JSON).

If a window's resets_at is already in the past the snapshot predates the
current window, so that window reports 0.0 (usage since the reset is unknown
but starts from zero) and its reset is dropped.
"""
import json
import os
import time
from datetime import datetime

CODEX_SESSIONS = os.path.expanduser("~/.codex/sessions")
TAIL_BYTES = 64 * 1024
MAX_FILES = 3


def _session_files(root, limit=MAX_FILES):
    """Newest rollout-*.jsonl paths, newest day first, mtime-desc within a day."""
    out = []
    try:
        years = sorted((d for d in os.listdir(root) if d.isdigit()), reverse=True)
    except OSError:
        return out
    for y in years:
        ydir = os.path.join(root, y)
        try:
            months = sorted(os.listdir(ydir), reverse=True)
        except OSError:
            continue
        for mo in months:
            mdir = os.path.join(ydir, mo)
            if not os.path.isdir(mdir):
                continue
            for day in sorted(os.listdir(mdir), reverse=True):
                ddir = os.path.join(mdir, day)
                if not os.path.isdir(ddir):
                    continue
                try:
                    fs = [os.path.join(ddir, f) for f in os.listdir(ddir)
                          if f.startswith("rollout-") and f.endswith(".jsonl")]
                except OSError:
                    continue
                fs.sort(key=lambda p: os.path.getmtime(p), reverse=True)
                out.extend(fs)
                if len(out) >= limit:
                    return out[:limit]
    return out[:limit]


def _last_rate_limits(path):
    """(rate_limits dict, iso timestamp) from the last parseable event, or None."""
    try:
        size = os.path.getsize(path)
        with open(path, "rb") as fh:
            fh.seek(max(0, size - TAIL_BYTES))
            chunk = fh.read().decode("utf-8", "replace")
    except OSError:
        return None
    for line in reversed(chunk.splitlines()):
        if '"rate_limits"' not in line:
            continue
        try:
            ev = json.loads(line)
            rl = ev["payload"]["rate_limits"]
            if not isinstance(rl, dict) or "primary" not in rl:
                continue
            return rl, ev.get("timestamp")
        except (ValueError, KeyError, TypeError):
            continue
    return None


def _parse_iso(ts):
    try:
        return int(datetime.fromisoformat(ts.replace("Z", "+00:00")).timestamp())
    except (AttributeError, ValueError, TypeError):
        return None


def codex_usage(root=None, now=None):
    """Normalized Codex usage record; see module docstring for semantics."""
    root = CODEX_SESSIONS if root is None else root
    now = int(time.time()) if now is None else now
    for path in _session_files(root):
        hit = _last_rate_limits(path)
        if hit is None:
            continue
        rl, ts = hit
        rec = {"state": "ok",
               "plan": (rl.get("plan_type") or "").title() or None,
               "as_of": _parse_iso(ts)}
        for wkey, ukey, rkey in (("primary", "u5", "r5"), ("secondary", "u7", "r7")):
            w = rl.get(wkey) or {}
            u, r = w.get("used_percent"), w.get("resets_at")
            if r is not None and now > r:
                u, r = 0.0, None
            rec[ukey] = None if u is None else round(u / 100.0, 4)
            rec[rkey] = r
        return rec
    return {"state": "no_data"}


if __name__ == "__main__":
    print(json.dumps(codex_usage()))
```

- [ ] **Step 4: Run tests, verify they pass**

Run: `~/.config/ccc/venv/bin/python -m pytest tests/test_codex_reader.py -v`
Expected: 5 passed. Also confirm existing suite still green: `~/.config/ccc/venv/bin/python -m pytest tests/ -v`

- [ ] **Step 5: Live sanity check + install line**

Run: `chmod +x bin/ccc-codex && python3 bin/ccc-codex`
Expected: one JSON line with `"state": "ok"` and real numbers (Sam uses Codex; if it prints no_data, investigate before continuing).

In `install.sh`, after the line `install -m 0755 "$REPO/bin/ccc-bar" "$BIN/ccc-bar"` add:

```bash
install -m 0755 "$REPO/bin/ccc-codex" "$BIN/ccc-codex"
```

- [ ] **Step 6: Commit**

```bash
git add bin/ccc-codex tests/test_codex_reader.py install.sh
git commit -m "feat: ccc-codex — Codex session-log usage reader (stdlib, tested)"
```

---

### Task 2: Menu bar glyphs (Claude spark, OpenAI blossom)

**Files:**
- Create: `assets/generate_glyphs.py`, `assets/claude-glyph.png`, `assets/codex-glyph.png`
- Modify: `install.sh`

**Interfaces:**
- Produces: 64×64 black-on-transparent PNGs at `assets/claude-glyph.png` / `assets/codex-glyph.png`, installed to `~/.config/ccc/`. Task 4 loads them as template NSImages (a single high-res PNG downscaled by NSImage.setSize_ — no @1x/@2x pair needed).

- [ ] **Step 1: Write the generator**

Create `assets/generate_glyphs.py`:

```python
#!/usr/bin/env python3
"""Generate menu-bar template glyphs for the providers ccc tracks.

Fetches the monochrome logomarks from simple-icons (CC0-licensed icon set;
the marks themselves remain their owners' trademarks — used nominatively to
label each provider's own usage number) and rasterizes them to 64x64
black-on-transparent PNGs, which macOS template-image mode recolors for
light/dark menu bars automatically.

Usage: python3 generate_glyphs.py        (needs: pip install cairosvg)
"""
import os
import urllib.request

import cairosvg

HERE = os.path.dirname(os.path.abspath(__file__))
ICONS = {"claude-glyph.png": "anthropic", "codex-glyph.png": "openai"}
URL = "https://raw.githubusercontent.com/simple-icons/simple-icons/develop/icons/{}.svg"

for out, slug in ICONS.items():
    svg = urllib.request.urlopen(URL.format(slug), timeout=30).read()
    path = os.path.join(HERE, out)
    cairosvg.svg2png(bytestring=svg, write_to=path,
                     output_width=64, output_height=64)
    print("saved", path)
```

- [ ] **Step 2: Run it and verify output**

```bash
python3 -m venv /tmp/glyphs-venv && /tmp/glyphs-venv/bin/pip -q install cairosvg
/tmp/glyphs-venv/bin/python assets/generate_glyphs.py
file assets/claude-glyph.png assets/codex-glyph.png
```
Expected: both report `PNG image data, 64 x 64`. Visually check both (Read the PNGs): claude-glyph = Anthropic spark/asterisk mark, codex-glyph = OpenAI blossom knot, black on transparent. If simple-icons renamed a slug (404), check https://simpleicons.org for "anthropic"/"openai" and update the slug.

- [ ] **Step 3: Install line**

In `install.sh`, after `cp "$REPO/assets/icon.png" "$CFGDIR/icon.png"` add:

```bash
cp "$REPO/assets/claude-glyph.png" "$CFGDIR/claude-glyph.png"
cp "$REPO/assets/codex-glyph.png" "$CFGDIR/codex-glyph.png"
```

- [ ] **Step 4: Commit**

```bash
git add assets/generate_glyphs.py assets/claude-glyph.png assets/codex-glyph.png install.sh
git commit -m "feat: provider glyph assets (Anthropic spark, OpenAI blossom) as template images"
```

---

### Task 3: ccc-bar provider settings (TDD)

**Files:**
- Modify: `bin/ccc-bar` (settings section, ~line 45-62)
- Create: `tests/test_settings.py`

**Interfaces:**
- Produces: `provider_enabled(key) -> bool` (default True) and `set_provider_enabled(key, on)`, plus constants `SHOW_CLAUDE_KEY = "showClaude"`, `SHOW_CODEX_KEY = "showCodex"` in ccc-bar. Task 4 gates fetching/rendering on these.

- [ ] **Step 1: Write the failing test**

Create `tests/test_settings.py` (same loader pattern as `tests/test_selfheal.py` — copy the `_load()` helper from there verbatim):

```python
import importlib.util, importlib.machinery, pathlib

_MOD = None

def _load():
    global _MOD
    if _MOD is None:
        p = pathlib.Path(__file__).resolve().parent.parent / "bin" / "ccc-bar"
        loader = importlib.machinery.SourceFileLoader("cccbar", str(p))
        spec = importlib.util.spec_from_loader("cccbar", loader)
        _MOD = importlib.util.module_from_spec(spec)
        loader.exec_module(_MOD)
    return _MOD

def test_provider_toggle_roundtrip():
    m = _load()
    for key in (m.SHOW_CLAUDE_KEY, m.SHOW_CODEX_KEY):
        m.set_provider_enabled(key, False)
        assert m.provider_enabled(key) is False
        m.set_provider_enabled(key, True)
        assert m.provider_enabled(key) is True

def test_provider_default_on():
    m = _load()
    d = m._defaults()
    d.removeObjectForKey_(m.SHOW_CLAUDE_KEY)
    d.removeObjectForKey_(m.SHOW_CODEX_KEY)
    assert m.provider_enabled(m.SHOW_CLAUDE_KEY) is True
    assert m.provider_enabled(m.SHOW_CODEX_KEY) is True
```

- [ ] **Step 2: Run test, verify it fails**

Run: `~/.config/ccc/venv/bin/python -m pytest tests/test_settings.py -v`
Expected: FAIL — `AttributeError: module 'cccbar' has no attribute 'SHOW_CLAUDE_KEY'`

- [ ] **Step 3: Implement**

In `bin/ccc-bar`, below `SELFHEAL_KEY = "selfHeal"` add:

```python
SHOW_CLAUDE_KEY = "showClaude"
SHOW_CODEX_KEY = "showCodex"
```

Below `set_self_heal(...)` add (mirrors the selfHeal accessors):

```python
def provider_enabled(key):
    d = _defaults()
    if d.objectForKey_(key) is None:
        return True  # both providers default ON
    return bool(d.boolForKey_(key))


def set_provider_enabled(key, on):
    _defaults().setBool_forKey_(bool(on), key)
```

- [ ] **Step 4: Run tests, verify pass**

Run: `~/.config/ccc/venv/bin/python -m pytest tests/ -v`
Expected: all pass (new + selfheal + codex reader).

- [ ] **Step 5: Commit**

```bash
git add bin/ccc-bar tests/test_settings.py
git commit -m "feat(ccc-bar): per-provider show/hide settings, default on"
```

---

### Task 4: ccc-bar — provider records, dual-glyph title, two-section dropdown, Settings submenu

**Files:**
- Modify: `bin/ccc-bar` (imports, data fetch, `_apply`, `_show`, `_menu_tail`)

This is the big UI task. All Claude probe internals (`claude_code_creds`, `probe_rate_headers`, `fetch`) stay untouched — we wrap `fetch()`.

**Interfaces:**
- Consumes: `codex_usage()` record from Task 1 (ccc-codex loaded via SourceFileLoader), `provider_enabled`/`set_provider_enabled` from Task 3, glyph PNGs from Task 2.
- Produces: n/a (top of the food chain).

- [ ] **Step 1: Load ccc-codex + record normalizers**

In `bin/ccc-bar` imports section add `import importlib.machinery, importlib.util` to the stdlib imports, and extend the AppKit/Foundation imports:

```python
from AppKit import (NSApplication, NSColor, NSFont, NSFontAttributeName,
                    NSForegroundColorAttributeName, NSImage, NSStatusBar,
                    NSTextAttachment)
from Foundation import (NSMakeRect, NSMakeSize, NSMutableAttributedString,
                        NSAttributedString, NSObject, NSUserDefaults)
```

Below the `KEYCHAIN_SERVICE`/header-key constants add:

```python
GLYPH_CLAUDE = os.path.expanduser("~/.config/ccc/claude-glyph.png")
GLYPH_CODEX = os.path.expanduser("~/.config/ccc/codex-glyph.png")


def _load_ccc_codex():
    """ccc-codex sits next to this file (repo bin/ or ~/.local/bin)."""
    p = os.path.join(os.path.dirname(os.path.abspath(__file__)), "ccc-codex")
    try:
        loader = importlib.machinery.SourceFileLoader("ccccodex", p)
        spec = importlib.util.spec_from_loader("ccccodex", loader)
        mod = importlib.util.module_from_spec(spec)
        loader.exec_module(mod)
        return mod
    except Exception:
        return None


_CODEX = _load_ccc_codex()
```

Below `fetch()` add the two record providers (same record shape as ccc-codex):

```python
def claude_usage():
    """Normalize the probe result to the shared provider-record shape."""
    data = fetch()
    if data is None:
        return {"state": "no_login"}
    if data.get("error"):
        return {"state": "expired" if data["error"] == "expired" else "error"}
    rh = data["rate_headers"]

    def _i(v):
        try:
            return int(v)
        except (TypeError, ValueError):
            return None

    return {"state": "ok", "u5": f(rh.get(R5_KEY)), "u7": f(rh.get(R7_KEY)),
            "r5": _i(rh.get(RESET5_KEY)), "r7": _i(rh.get(RESET7_KEY)),
            "plan": (data.get("plan") or "").title() or None, "as_of": None}


def codex_usage():
    if _CODEX is None:
        return {"state": "no_data"}
    try:
        return _CODEX.codex_usage()
    except Exception:
        return {"state": "no_data"}


# Ordered: (record key, defaults key, glyph path, fetcher, text fallback)
PROVIDERS = (
    ("claude", SHOW_CLAUDE_KEY, GLYPH_CLAUDE, claude_usage, "C:"),
    ("codex", SHOW_CODEX_KEY, GLYPH_CODEX, codex_usage, "X:"),
)
```

- [ ] **Step 2: Title composition**

Add below the formatting helpers (`_seg` etc.):

```python
_GLYPH_CACHE = {}


def _glyph(path):
    """Template NSImage for the menu-bar title, or None (text fallback)."""
    if path not in _GLYPH_CACHE:
        img = NSImage.alloc().initWithContentsOfFile_(path)
        if img is not None:
            img.setTemplate_(True)
            img.setSize_(NSMakeSize(15, 15))
        _GLYPH_CACHE[path] = img
    return _GLYPH_CACHE[path]


def _title_value(rec):
    if rec["state"] == "ok":
        return pct(rec.get("u5"))
    return "⚠" if rec["state"] in ("expired", "error") else "–"


def title_attr_string(entries):
    """entries: [(glyph_path, text_fallback, rec)] → attributed title.

    Glyph images ride in NSTextAttachments (bounds y-offset centers them on
    the menu-bar text baseline); values use the same monospaced font as the
    old plain-text title.
    """
    s = NSMutableAttributedString.alloc().init()
    color = NSColor.labelColor()
    for i, (glyph_path, fallback, rec) in enumerate(entries):
        if i:
            s.appendAttributedString_(_seg("  ", color, 12, W_REG))
        img = _glyph(glyph_path)
        if img is not None:
            att = NSTextAttachment.alloc().init()
            att.setImage_(img)
            att.setBounds_(NSMakeRect(0, -3, 15, 15))
            s.appendAttributedString_(
                NSAttributedString.attributedStringWithAttachment_(att))
            s.appendAttributedString_(_seg(" " + _title_value(rec), color, 12, W_SEMI))
        else:
            s.appendAttributedString_(_seg(fallback + _title_value(rec), color, 12, W_SEMI))
    return s
```

- [ ] **Step 3: Fetch both providers in the worker**

Replace `_refresh_worker`:

```python
    def _refresh_worker(self):
        try:
            # Fetch only enabled providers — a hidden Claude means no probe
            # call at all (saves the token + the rate-limit ping).
            data = {}
            for key, setting, _glyph_path, fetcher, _fb in PROVIDERS:
                if provider_enabled(setting):
                    data[key] = fetcher()
        finally:
            self._refreshing = False
        AppHelper.callAfter(self._apply, data)
```

- [ ] **Step 4: Rewrite `_apply` + `_show` for sections**

Replace `_apply` and `_show` with:

```python
    def _apply(self, data):
        rebuild = self._rebuild_menu_next
        entries = [(g, fb, data[k]) for k, s, g, fetch_, fb in PROVIDERS
                   if k in data]
        if not entries:
            # Both providers hidden: show the app icon so the item stays
            # clickable, clear the title.
            self.icon = ICON if os.path.exists(ICON) else None
            self.title = ""
        else:
            self.icon = None
            self.title = ""  # rumps clears the plain title...
            try:              # ...then the attributed one paints over it.
                btn = self._nsapp.nsstatusitem.button()
                btn.setAttributedTitle_(title_attr_string(entries))
            except Exception:
                # No button API (very old macOS) — plain-text fallback.
                self.title = " ".join(
                    fb + _title_value(rec) for _g, fb, rec in entries)
        if rebuild:
            self._show(data)

    def _section_rows(self, name, rec):
        rows = []
        head = name + (f" · {rec['plan']}" if rec.get("plan") else "")
        as_of = rec.get("as_of")
        if as_of and time.time() - as_of > REFRESH_SECONDS:
            head += f"   · as of {_age(as_of)} ago"
        rows.append(self._attr_item(name + "_hdr", info_row(head)))
        state = rec["state"]
        if state == "ok":
            rows.append(self._attr_item(name + "_5h", bar_row(rec.get("u5"), "  5h", "", True)))
            rows.append(self._attr_item(name + "_7d", bar_row(rec.get("u7"), "  7d", "", True)))
            resets = []
            if rec.get("r5"):
                resets.append(f"5h in {countdown(rec['r5'])}")
            if rec.get("r7"):
                resets.append(f"7d in {countdown(rec['r7'])}")
            if resets:
                rows.append(self._attr_item(name + "_rst",
                                            info_row("  resets " + " · ".join(resets))))
        else:
            msg = {
                "no_login": "  No Claude Code login found — open Claude Code",
                "expired": "  Login expired — open Claude Code to refresh",
                "no_data": "  No Codex sessions found — run codex once",
            }.get(state, "  Couldn't read usage — try again shortly")
            rows.append(self._attr_item(name + "_err", info_row(msg)))
        return rows

    def _show(self, data):
        items = [self._attr_item("hdr", info_row("usage")), None]
        first = True
        for key, setting, _g, _f, _fb in PROVIDERS:
            if key not in data:
                continue
            if not first:
                items.append(None)
            first = False
            items += self._section_rows(key.title(), data[key])
        if first:  # nothing enabled
            items.append(self._attr_item("none", info_row("All providers hidden — Settings ▸")))
        items += self._menu_tail()
        self.menu.clear()
        self.menu = items
        self._attach_open_delegate()
```

Add the `_age` helper next to `countdown`:

```python
def _age(ts):
    d = max(0, int(time.time()) - int(ts))
    if d >= 86400:
        return f"{d // 86400}d"
    if d >= 3600:
        return f"{d // 3600}h {(d % 3600) // 60}m"
    return f"{d // 60}m"
```

Delete `_show_message` (its callers are gone — error text now renders per section).

- [ ] **Step 5: Settings submenu**

Replace `_menu_tail`:

```python
    def _menu_tail(self):
        settings = rumps.MenuItem("Settings")
        show_c = rumps.MenuItem("Show Claude",
                                callback=lambda s: self._toggle_provider(s, SHOW_CLAUDE_KEY))
        show_c.state = 1 if provider_enabled(SHOW_CLAUDE_KEY) else 0
        show_x = rumps.MenuItem("Show Codex",
                                callback=lambda s: self._toggle_provider(s, SHOW_CODEX_KEY))
        show_x.state = 1 if provider_enabled(SHOW_CODEX_KEY) else 0
        heal = rumps.MenuItem("Auto-restore icon", callback=self.toggle_self_heal)
        heal.state = 1 if self_heal_enabled() else 0
        settings.update([show_c, show_x, heal])
        return [
            None,
            rumps.MenuItem("Refresh now", callback=self.refresh),
            settings,
            rumps.MenuItem("Open in Terminal", callback=self.open_terminal),
            rumps.MenuItem("Quit", callback=rumps.quit_application),
        ]
```

Add next to `toggle_self_heal`:

```python
    def _toggle_provider(self, sender, key):
        new = not provider_enabled(key)
        set_provider_enabled(key, new)
        sender.state = 1 if new else 0
        self.refresh(None)  # repaint title + dropdown immediately
```

Also update the module docstring's first paragraph to mention Codex (it renders in `-h` nowhere, but keep the file honest): change the title line to `"""ccc-bar — Claude + Codex usage in the macOS menu bar.`

- [ ] **Step 6: Tests + import smoke**

Run: `~/.config/ccc/venv/bin/python -m pytest tests/ -v`
Expected: all pass (loader executes the module — catches syntax/name errors).

Run a 10-second live smoke (starts the real app on a second status item — fine, singleton guard port differs only if the LaunchAgent copy is running; stop it first):

```bash
launchctl bootout gui/$(id -u)/com.claude-usage-bar.menubar 2>/dev/null || true
cp assets/claude-glyph.png assets/codex-glyph.png ~/.config/ccc/
timeout 12 ~/.config/ccc/venv/bin/python bin/ccc-bar || true
```
Expected: menu bar briefly shows `[spark] NN%  [blossom] NN%`. (Visual check by Sam or screenshot; the command exiting without traceback is the automated gate. Restart the agent after: `launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/com.claude-usage-bar.menubar.plist`.)

- [ ] **Step 7: Commit**

```bash
git add bin/ccc-bar
git commit -m "feat(ccc-bar): Codex provider — dual-glyph title, sectioned dropdown, Settings submenu"
```

---

### Task 5: bash `ccc` — Codex section

**Files:**
- Modify: `bin/ccc`

**Interfaces:**
- Consumes: `~/.local/bin/ccc-codex` (falls back to repo-sibling path) via `python3`, output record from Task 1.

- [ ] **Step 1: Add the Codex renderer**

In `bin/ccc`, after the `render()` function add:

```bash
codex_helper() {
  if [ -x "$HOME/.local/bin/ccc-codex" ]; then echo "$HOME/.local/bin/ccc-codex"
  else echo "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/ccc-codex"; fi
}

render_codex() {
  local rec state u5 u7 r5 asof c5 c7 p5 p7 agestr=""
  rec=$(python3 "$(codex_helper)" 2>/dev/null) || return 0
  state=$(echo "$rec" | jq -r '.state // empty')
  if [ "$state" != "ok" ]; then
    echo "  ${C_BOLD}${C_CYN}codex usage${C_RST}   ${C_DIM}· no session data — run codex once${C_RST}"
    echo
    return 0
  fi
  u5=$(echo "$rec" | jq -r '.u5 // 0'); u7=$(echo "$rec" | jq -r '.u7 // 0')
  r5=$(echo "$rec" | jq -r '.r5 // empty'); asof=$(echo "$rec" | jq -r '.as_of // empty')
  if [ -n "$asof" ]; then
    local age=$(( $(date +%s) - asof ))
    [ "$age" -gt 120 ] && agestr=" · as of $((age/60))m ago"
  fi
  c5=$(color_for "$u5"); c7=$(color_for "$u7")
  p5=$(awk -v u="$u5" 'BEGIN{printf "%d",u*100+0.5}'); p7=$(awk -v u="$u7" 'BEGIN{printf "%d",u*100+0.5}')
  echo "  ${C_BOLD}${C_CYN}codex usage${C_RST}  ${C_DIM}· via session log${agestr}${C_RST}"
  echo
  printf "  5h  %s %s%3d%%%s\n" "$(bar "$u5" "$c5")" "$(colcode "$c5")" "$p5" "$C_RST"
  printf "  7d  %s %s%3d%%%s\n" "$(bar "$u7" "$c7")" "$(colcode "$c7")" "$p7" "$C_RST"
  echo
  [ -n "$r5" ] && printf "  ${C_DIM}5h resets %s${C_RST}\n" "$(countdown "$r5")" && echo
}
```

Wire it in by replacing the entire bottom `if [ "$WATCH" = "1" ]...` block (currently the last 6 lines of the script) with this single version — `render` keeps returning early on its own errors, so codex renders regardless via `|| true`:

```bash
if [ "$WATCH" = "1" ] && [ "$JSON" != "1" ]; then
  trap 'tput cnorm 2>/dev/null; exit 0' INT TERM
  tput civis 2>/dev/null || true
  while true; do clear; render || true; render_codex || true
    printf "  ${C_DIM}refreshing every %ss · ctrl-c to quit${C_RST}\n" "$INTERVAL"; sleep "$INTERVAL"; done
else
  render || true
  if [ "$JSON" = "1" ]; then python3 "$(codex_helper)" 2>/dev/null || true
  else render_codex; fi
fi
```

- [ ] **Step 2: Verify**

Run: `bash bin/ccc` — expected: Claude block (unchanged) followed by `codex usage · via session log` block with real bars.
Run: `bash bin/ccc --json` — expected: the anthropic header lines then one codex JSON record line.
Run: `bash -n bin/ccc` — expected: no syntax errors.

- [ ] **Step 3: Commit**

```bash
git add bin/ccc
git commit -m "feat(ccc): codex usage section in CLI + --json record"
```

---

### Task 6: macOS reinstall + end-to-end smoke

**Files:** none (runs install.sh)

- [ ] **Step 1:** `./install.sh`
Expected: completes; LaunchAgent bootstraps; `>_`-less title appears as `[spark] NN%  [blossom] NN%`.
- [ ] **Step 2:** Verify `ccc` on PATH shows both sections; `python3 ~/.local/bin/ccc-codex` prints ok-record.
- [ ] **Step 3:** Ask Sam to visually confirm: glyphs legible in light + dark menu bar; Settings ▸ toggles hide/show each segment immediately; "Auto-restore icon" still toggles; Quit + relaunch works.
- [ ] **Step 4:** Commit anything install-found (usually nothing): `git status` clean or fix + commit.

---

### Task 7: Windows Core — `RateUsage` provider fields + `NoData` state

Run all Windows build/test commands on the gaming PC: `ssh desktop`, repo path discovered in Step 1. Author code on the Mac, push branch, pull on PC.

**Files:**
- Modify: `windows/src/Core/RateUsage.cs`, `windows/src/Core/UsageState.cs`

**Interfaces:**
- Produces: `enum Provider { Claude, Codex }`; `RateUsage` gains `Provider Provider = Provider.Claude`, `long? AsOf = null` (trailing optional params — `Probe.cs` and existing call sites compile untouched); `UsageState.NoData`; factory `RateUsage.NoData(Provider p)`.

- [ ] **Step 1: PC repo sync check**

```bash
ssh desktop "cd claude-usage-bar 2>/dev/null && git rev-parse --show-toplevel || dir /b C:\\Users"
```
If the repo isn't at `~/claude-usage-bar` on the PC, locate it (`ssh desktop "where /r C:\Users claude-usage-bar 2>nul"`) or clone it (`ssh desktop "git clone https://github.com/samdvla/claude-usage-bar"`). Record the path; all later `ssh desktop` commands `cd` there first. Then `git fetch && git checkout feat/codex-provider && git pull`.

- [ ] **Step 2: Edit the records**

`windows/src/Core/UsageState.cs`:

```csharp
namespace ClaudeUsage.Core;

public enum UsageState
{
    Ok,        // usage numbers present (includes 429 = maxed out)
    NoLogin,   // no Claude Code credential found
    Expired,   // 401/403
    Transient, // network error or non-429 missing headers
    NoData,    // provider has no local data yet (Codex: no session logs)
}
```

`windows/src/Core/RateUsage.cs`:

```csharp
namespace ClaudeUsage.Core;

public enum Provider { Claude, Codex }

public record RateUsage(
    UsageState State,
    double? Util5h = null,
    double? Util7d = null,
    long? Reset5h = null,
    long? Reset7d = null,
    string? Plan = null,
    Provider Provider = Provider.Claude,
    long? AsOf = null)
{
    public static RateUsage NoLogin() => new(UsageState.NoLogin);
    public static RateUsage Expired() => new(UsageState.Expired);
    public static RateUsage Transient() => new(UsageState.Transient);
    public static RateUsage NoData(Provider p) => new(UsageState.NoData, Provider: p);
}
```

- [ ] **Step 3: Build + existing tests on PC**

```bash
git add -A && git commit -m "feat(windows): Provider/AsOf on RateUsage, NoData state" && git push -u origin feat/codex-provider
ssh desktop "cd <REPO> && git pull && dotnet test windows/tests/Core.Tests"
```
Expected: build succeeds, all existing tests pass (records are additive).

---

### Task 8: Windows Core — `CodexSessionReader` (TDD)

**Files:**
- Create: `windows/src/Core/CodexSessionReader.cs`
- Create: `windows/tests/Core.Tests/CodexSessionReaderTests.cs`

**Interfaces:**
- Consumes: `Provider`, `UsageState.NoData`, `RateUsage` from Task 7.
- Produces: `class CodexSessionReader { CodexSessionReader(string? root = null); RateUsage Read(long nowEpoch); }` — mirrors the Python semantics exactly (64 KB tail, 3-file fallback, rollover zeroing, `used_percent/100`, plan TitleCased, AsOf from event timestamp).

- [ ] **Step 1: Write the failing tests**

`windows/tests/Core.Tests/CodexSessionReaderTests.cs`:

```csharp
using ClaudeUsage.Core;

namespace Core.Tests;

public class CodexSessionReaderTests : IDisposable
{
    private const long Now = 1783300000;
    private readonly string _root;

    public CodexSessionReaderTests()
        => _root = Directory.CreateTempSubdirectory("codex-sessions-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string rel, params string[] lines)
    {
        var p = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllLines(p, lines);
        return p;
    }

    private static string Event(double u5 = 20.0, double u7 = 25.0,
        long r5 = Now + 9000, long r7 = Now + 200000,
        string plan = "plus", string ts = "2026-07-06T14:41:05.555Z") =>
        $"{{\"timestamp\":\"{ts}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"token_count\",\"info\":{{}}," +
        $"\"rate_limits\":{{\"limit_id\":\"codex\"," +
        $"\"primary\":{{\"used_percent\":{u5},\"window_minutes\":300,\"resets_at\":{r5}}}," +
        $"\"secondary\":{{\"used_percent\":{u7},\"window_minutes\":10080,\"resets_at\":{r7}}}," +
        $"\"plan_type\":\"{plan}\"}}}}}}";

    [Fact]
    public void HappyPath()
    {
        Write("2026/07/06/rollout-a.jsonl",
            "{\"timestamp\":\"x\",\"type\":\"event_msg\",\"payload\":{\"type\":\"other\"}}",
            Event());
        var u = new CodexSessionReader(_root).Read(Now);
        Assert.Equal(UsageState.Ok, u.State);
        Assert.Equal(0.20, u.Util5h!.Value, 3);
        Assert.Equal(0.25, u.Util7d!.Value, 3);
        Assert.Equal(Now + 9000, u.Reset5h);
        Assert.Equal("Plus", u.Plan);
        Assert.Equal(Provider.Codex, u.Provider);
        Assert.Equal(1783348865, u.AsOf);
    }

    [Fact]
    public void RolloverZeroing()
    {
        Write("2026/07/06/rollout-a.jsonl", Event(r5: Now - 100));
        var u = new CodexSessionReader(_root).Read(Now);
        Assert.Equal(0.0, u.Util5h!.Value, 3);
        Assert.Null(u.Reset5h);
        Assert.Equal(0.25, u.Util7d!.Value, 3);
    }

    [Fact]
    public void MalformedLinesSkipped()
    {
        Write("2026/07/06/rollout-a.jsonl",
            Event(u5: 11.0), "{\"broken rate_limits", "not json rate_limits");
        var u = new CodexSessionReader(_root).Read(Now);
        Assert.Equal(UsageState.Ok, u.State);
        Assert.Equal(0.11, u.Util5h!.Value, 3);
    }

    [Fact]
    public void FallsBackToOlderFile()
    {
        var newer = Write("2026/07/06/rollout-new.jsonl",
            "{\"timestamp\":\"x\",\"type\":\"event_msg\",\"payload\":{}}");
        var older = Write("2026/07/05/rollout-old.jsonl", Event(u5: 33.0));
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddMinutes(-5));
        File.SetLastWriteTimeUtc(newer, DateTime.UtcNow);
        var u = new CodexSessionReader(_root).Read(Now);
        Assert.Equal(0.33, u.Util5h!.Value, 3);
    }

    [Fact]
    public void NoDataWhenMissing()
    {
        Assert.Equal(UsageState.NoData,
            new CodexSessionReader(Path.Combine(_root, "nope")).Read(Now).State);
        Write("2026/07/06/rollout-a.jsonl", "{}");
        Assert.Equal(UsageState.NoData, new CodexSessionReader(_root).Read(Now).State);
    }
}
```

- [ ] **Step 2: Run on PC, verify fail**

`ssh desktop "cd <REPO> && git pull && dotnet test windows/tests/Core.Tests"` (after committing+pushing the test file)
Expected: FAIL — `CodexSessionReader` not found.

- [ ] **Step 3: Implement**

`windows/src/Core/CodexSessionReader.cs`:

```csharp
using System.Text;
using System.Text.Json;

namespace ClaudeUsage.Core;

// Reads OpenAI Codex usage from its local session logs. Codex CLI appends a
// rate_limits snapshot to ~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl on
// every turn; the newest snapshot is normalized into RateUsage. A window whose
// resets_at is already past reports 0.0 (snapshot predates the current
// window) and drops its reset.
public sealed class CodexSessionReader
{
    private const int TailBytes = 64 * 1024;
    private const int MaxFiles = 3;
    private readonly string _root;

    public CodexSessionReader(string? root = null) =>
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex", "sessions");

    public RateUsage Read(long nowEpoch)
    {
        foreach (var file in SessionFiles())
        {
            var parsed = LastRateLimits(file);
            if (parsed is null) continue;
            var (rl, ts) = parsed.Value;

            (double? util, long? reset) Window(JsonElement rlEl, string key)
            {
                if (!rlEl.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object)
                    return (null, null);
                double? u = w.TryGetProperty("used_percent", out var up) &&
                            up.ValueKind == JsonValueKind.Number ? up.GetDouble() : null;
                long? r = w.TryGetProperty("resets_at", out var ra) &&
                          ra.ValueKind == JsonValueKind.Number ? ra.GetInt64() : null;
                if (r is not null && nowEpoch > r) { u = 0.0; r = null; }
                return (u is null ? null : Math.Round(u.Value / 100.0, 4), r);
            }

            var (u5, r5) = Window(rl, "primary");
            var (u7, r7) = Window(rl, "secondary");
            string? plan = rl.TryGetProperty("plan_type", out var p) &&
                           p.ValueKind == JsonValueKind.String && p.GetString()!.Length > 0
                ? char.ToUpper(p.GetString()![0]) + p.GetString()![1..] : null;
            long? asOf = ParseIso(ts);
            return new RateUsage(UsageState.Ok, u5, u7, r5, r7, plan,
                                 Provider.Codex, asOf);
        }
        return RateUsage.NoData(Provider.Codex);
    }

    private IEnumerable<string> SessionFiles()
    {
        if (!Directory.Exists(_root)) yield break;
        int yielded = 0;
        foreach (var y in Directory.GetDirectories(_root).OrderDescending())
        foreach (var m in Directory.GetDirectories(y).OrderDescending())
        foreach (var d in Directory.GetDirectories(m).OrderDescending())
        {
            var files = Directory.GetFiles(d, "rollout-*.jsonl")
                .OrderByDescending(File.GetLastWriteTimeUtc);
            foreach (var f in files)
            {
                yield return f;
                if (++yielded >= MaxFiles) yield break;
            }
        }
    }

    private static (JsonElement, string?)? LastRateLimits(string path)
    {
        string chunk;
        try
        {
            using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(Math.Max(0, fs.Length - TailBytes), SeekOrigin.Begin);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            chunk = sr.ReadToEnd();
        }
        catch (IOException) { return null; }

        var lines = chunk.Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (!lines[i].Contains("\"rate_limits\"")) continue;
            try
            {
                using var doc = JsonDocument.Parse(lines[i]);
                var rootEl = doc.RootElement;
                if (!rootEl.TryGetProperty("payload", out var payload) ||
                    !payload.TryGetProperty("rate_limits", out var rl) ||
                    rl.ValueKind != JsonValueKind.Object ||
                    !rl.TryGetProperty("primary", out _))
                    continue;
                string? ts = rootEl.TryGetProperty("timestamp", out var t) &&
                             t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                return (rl.Clone(), ts);
            }
            catch (JsonException) { continue; }
        }
        return null;
    }

    private static long? ParseIso(string? ts)
    {
        if (ts is null) return null;
        return DateTimeOffset.TryParse(ts, null,
            System.Globalization.DateTimeStyles.AdjustToUniversal, out var dto)
            ? dto.ToUnixTimeSeconds() : null;
    }
}
```

- [ ] **Step 4: Run on PC, verify pass**

Commit + push, then `ssh desktop "cd <REPO> && git pull && dotnet test windows/tests/Core.Tests"`
Expected: all tests pass (5 new + existing).

- [ ] **Step 5: Commit** (already committed in step 4 flow; squash-fix if needed)

```bash
git add windows/src/Core/CodexSessionReader.cs windows/tests/Core.Tests/CodexSessionReaderTests.cs
git commit -m "feat(windows): CodexSessionReader — session-log usage with tests"
git push
```

---

### Task 9: Windows `IconRenderer` — per-provider badges

**Files:**
- Modify: `windows/src/TrayApp/IconRenderer.cs`

**Interfaces:**
- Produces: `IconRenderer.Render(double? util, bool dark, Provider provider = Provider.Claude, int size = 64)` — Claude: existing orange badge/white text; Codex: light-gray badge (`#E8E8EC`) with near-black text (`#1A1A1E`) and a matching dark shadow disabled (shadow only on Claude). Null util stays the neutral grey badge with "—".

- [ ] **Step 1: Edit**

In `IconRenderer.cs` add after `ClaudeOrange`:

```csharp
    private static readonly Color CodexGray = Color.FromArgb(0xE8, 0xE8, 0xEC);
    private static readonly Color CodexText = Color.FromArgb(0x1A, 0x1A, 0x1E);
```

Change the signature and color selection:

```csharp
    public static Icon Render(double? util, bool dark, Provider provider = Provider.Claude, int size = 64)
```

```csharp
        Color baseColor = util is null
            ? Color.FromArgb(120, 120, 128)                      // neutral grey when no data
            : provider == Provider.Codex ? CodexGray : ClaudeOrange;
        Color textColor = util is not null && provider == Provider.Codex
            ? CodexText : Color.White;
```

Then use `textColor` for the number and the maxed "+" (replace both `new SolidBrush(Color.White)` usages that draw text with `new SolidBrush(textColor)`), and draw the drop shadow only when `textColor == Color.White` (wrap the shadow `DrawString` in `if (textColor == Color.White) { ... }` — a dark shadow under dark text on a light badge just smears).

- [ ] **Step 2: Compile on PC**

`ssh desktop "cd <REPO> && git pull && dotnet build windows/src/TrayApp"` after commit+push.
Expected: builds clean (default parameter keeps existing call sites).

- [ ] **Step 3: Commit**

```bash
git add windows/src/TrayApp/IconRenderer.cs
git commit -m "feat(windows-tray): per-provider badge — Codex neutral gray" && git push
```

---

### Task 10: Windows `FlyoutForm` — two provider sections

**Files:**
- Modify: `windows/src/TrayApp/FlyoutForm.cs`

**Interfaces:**
- Consumes: `RateUsage` with `Provider`/`AsOf` (Task 7).
- Produces: `Render(RateUsage claude, RateUsage? codex)` — codex null = hidden (toggle off). Old single-arg `Render(RateUsage)` is removed; TrayController (Task 11) is the only caller.

- [ ] **Step 1: Edit**

Add fields after `_reset7`:

```csharp
    private readonly Label _headerCodex = new();
    private readonly BarControl _bar5x;
    private readonly BarControl _bar7x;
    private readonly Label _resetX = new();
    private readonly Label _asOfX = new();
```

In the constructor after `_bar7 = ...`:

```csharp
        _bar5x = new BarControl(theme) { BarLabel = "5h" };
        _bar7x = new BarControl(theme) { BarLabel = "7d" };
```

Change `Height = 168;` to `Height = 320;` and style the new controls alongside the old (extend the existing loops):

```csharp
        foreach (var b in new[] { _bar5, _bar7, _bar5x, _bar7x }) b.Dock = DockStyle.Top;

        foreach (var l in new[] { _reset5, _reset7, _resetX, _asOfX })
        {
            l.Dock = DockStyle.Top;
            l.Height = 18;
            l.Font = F("Segoe UI", 9f);
            l.ForeColor = theme.Secondary;
        }

        _headerCodex.AutoSize = false;
        _headerCodex.Dock = DockStyle.Top;
        _headerCodex.Height = 30;                 // extra top gap between sections
        _headerCodex.TextAlign = ContentAlignment.BottomLeft;
        _headerCodex.Font = F("Segoe UI", 11f, FontStyle.Bold);
        _headerCodex.ForeColor = theme.Primary;
```

Replace the `Controls.Add` block (reverse stacking order — bottom-most control added first within DockStyle.Top means LAST added is TOP; keep existing convention: add in reverse):

```csharp
        Controls.Add(_asOfX);
        Controls.Add(_resetX);
        Controls.Add(_bar7x);
        Controls.Add(_bar5x);
        Controls.Add(_headerCodex);
        Controls.Add(_reset7);
        Controls.Add(_reset5);
        Controls.Add(_bar7);
        Controls.Add(_bar5);
        Controls.Add(_header);
        Controls.Add(footer);
```

Replace `Render` and `SetMessage`:

```csharp
    public void Render(RateUsage claude, RateUsage? codex)
    {
        _header.Text = "Claude usage" + PlanSuffix(claude);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        switch (claude.State)
        {
            case UsageState.NoLogin:
                ClaudeMessage("No Claude Code login found — open Claude Code."); break;
            case UsageState.Expired:
                ClaudeMessage("Login expired — open Claude Code to refresh."); break;
            case UsageState.Transient:
                ClaudeMessage("Couldn't read usage — try again shortly."); break;
            default:
                _bar5.Visible = _bar7.Visible = _reset5.Visible = _reset7.Visible = true;
                _bar5.Util = claude.Util5h; _bar7.Util = claude.Util7d;
                _bar5.Invalidate(); _bar7.Invalidate();
                _reset5.Text = $"5h resets in {Countdown.Format(claude.Reset5h, now)}";
                _reset7.Text = $"7d resets in {Countdown.Format(claude.Reset7d, now)}";
                break;
        }

        bool showCodex = codex is not null;
        _headerCodex.Visible = _bar5x.Visible = _bar7x.Visible =
            _resetX.Visible = _asOfX.Visible = showCodex;
        Height = showCodex ? 320 : 168;
        if (!showCodex) return;

        _headerCodex.Text = "Codex usage" + PlanSuffix(codex!);
        if (codex!.State == UsageState.NoData)
        {
            _bar5x.Visible = _bar7x.Visible = _asOfX.Visible = false;
            _resetX.Text = "No Codex sessions found — run codex once.";
            return;
        }
        _bar5x.Util = codex.Util5h; _bar7x.Util = codex.Util7d;
        _bar5x.Invalidate(); _bar7x.Invalidate();
        _resetX.Text = $"5h resets in {Countdown.Format(codex.Reset5h, now)}";
        long age = codex.AsOf is null ? 0 : now - codex.AsOf.Value;
        _asOfX.Text = age > 120 ? $"as of {age / 60}m ago" : "";
    }

    private void ClaudeMessage(string msg)
    {
        _bar5.Visible = _bar7.Visible = _reset7.Visible = false;
        _reset5.Visible = true;
        _reset5.Text = msg;
    }

    private static string PlanSuffix(RateUsage u) =>
        string.IsNullOrEmpty(u.Plan) ? "" : "  ·  " + Capitalize(u.Plan!);
```

(`Capitalize` already exists; the old `SetMessage` is replaced by `ClaudeMessage`.)

- [ ] **Step 2: Compile on PC** (will fail until Task 11 updates the caller — expected; do Tasks 10+11 in one push if compiling between them is noisy, but keep separate commits)

- [ ] **Step 3: Commit**

```bash
git add windows/src/TrayApp/FlyoutForm.cs
git commit -m "feat(windows-tray): flyout renders Claude + Codex sections"
```

---

### Task 11: Windows `TrayController` — second icon + settings

**Files:**
- Modify: `windows/src/TrayApp/TrayController.cs`

**Interfaces:**
- Consumes: `CodexSessionReader` (Task 8), `IconRenderer.Render(..., Provider)` (Task 9), `FlyoutForm.Render(claude, codex)` (Task 10).
- Produces: registry settings `HKCU\Software\ClaudeUsageBar` → `ShowClaude`/`ShowCodex` DWORD (default 1).

- [ ] **Step 1: Edit**

Add fields after `_tray`:

```csharp
    private const string SettingsKey = @"Software\ClaudeUsageBar";
    private NotifyIcon? _trayCodex;
    private readonly CodexSessionReader _codexReader = new();
    private RateUsage _lastCodex = RateUsage.NoData(Provider.Codex);
    private Icon? _currentCodexIcon;
    private ToolStripMenuItem _showClaude = null!;
    private ToolStripMenuItem _showCodex = null!;
```

Settings helpers (add near `IsAutostartEnabled`):

```csharp
    private static bool GetShow(string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(SettingsKey);
        return (k?.GetValue(name) as int?) != 0;   // absent → 1 (on)
    }

    private static void SetShow(string name, bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(SettingsKey);
        k.SetValue(name, on ? 1 : 0, RegistryValueKind.DWord);
    }
```

In the constructor, replace the fixed `_tray.Visible = true;` block start with visibility from settings, add the toggle items to the menu after `_autostart`, and create the codex icon:

```csharp
        _tray.Visible = GetShow("ShowClaude");
        _tray.Text = "Claude usage";
        ApplyIcon(null);

        _showClaude = new ToolStripMenuItem("Show Claude icon", null, (_, _) => ToggleShow(Provider.Claude))
        { Checked = GetShow("ShowClaude") };
        _showCodex = new ToolStripMenuItem("Show Codex icon", null, (_, _) => ToggleShow(Provider.Codex))
        { Checked = GetShow("ShowCodex") };
```

and add to the menu between `_autostart` and "Open in Terminal":

```csharp
        _menu.Items.Add(_showClaude);
        _menu.Items.Add(_showCodex);
```

Codex icon setup at the end of the constructor (before the timer block):

```csharp
        _trayCodex = new NotifyIcon { Visible = GetShow("ShowCodex"), Text = "Codex usage" };
        _trayCodex.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ToggleFlyout();
            else if (e.Button == MouseButtons.Right) _menu.Show(Cursor.Position);
        };
        ApplyCodexIcon(RateUsage.NoData(Provider.Codex));
```

Toggle logic (add after `ToggleAutostart`) — refuse hiding the last visible icon so the app never becomes unreachable:

```csharp
    private void ToggleShow(Provider p)
    {
        bool claude = GetShow("ShowClaude"), codex = GetShow("ShowCodex");
        if (p == Provider.Claude)
        {
            if (claude && !codex) return;          // would hide the last icon
            claude = !claude; SetShow("ShowClaude", claude);
        }
        else
        {
            if (codex && !claude) return;
            codex = !codex; SetShow("ShowCodex", codex);
        }
        _showClaude.Checked = claude; _showCodex.Checked = codex;
        _tray.Visible = claude;
        if (_trayCodex is not null) _trayCodex.Visible = codex;
        Refresh();
    }
```

`Refresh()` — read codex (cheap, sync) alongside the probe and pass both to the flyout:

```csharp
    private async void Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        RateUsage u;
        try { u = await _probe.FetchAsync(); }
        catch { u = RateUsage.Transient(); }
        finally { _refreshing = false; }
        _last = u;
        try { _lastCodex = _codexReader.Read(DateTimeOffset.UtcNow.ToUnixTimeSeconds()); }
        catch { _lastCodex = RateUsage.NoData(Provider.Codex); }
        ApplyIcon(u);
        ApplyCodexIcon(_lastCodex);
        if (_flyout is { Visible: true }) RenderFlyout();
    }

    private void RenderFlyout() =>
        _flyout!.Render(_last, GetShow("ShowCodex") ? _lastCodex : null);
```

`ApplyCodexIcon` next to `ApplyIcon`:

```csharp
    private void ApplyCodexIcon(RateUsage u)
    {
        if (_trayCodex is null) return;
        double? util = u.State == UsageState.Ok ? u.Util5h : null;
        var icon = IconRenderer.Render(util, _theme.Dark, Provider.Codex);
        _trayCodex.Icon = icon;
        _currentCodexIcon?.Dispose();
        _currentCodexIcon = icon;
        _trayCodex.Text = u.State == UsageState.Ok
            ? $"Codex usage{(string.IsNullOrEmpty(u.Plan) ? "" : " · " + u.Plan)} — 5h {Pct(u.Util5h)} · 7d {Pct(u.Util7d)}"
            : "Codex usage — no session data";
    }
```

Update `ToggleFlyout`'s render call from `_flyout.Render(_last)` to `RenderFlyout()` (after `_flyout ??= BuildFlyout();`).

In `PromoteToTaskbar`, the `changed` branch blind-toggles `_tray.Visible = false; _tray.Visible = true;` — with settings that could resurrect a hidden icon. Replace those two lines with:

```csharp
                _tray.Visible = false;
                _tray.Visible = GetShow("ShowClaude");
                if (_trayCodex is not null)
                {
                    _trayCodex.Visible = false;
                    _trayCodex.Visible = GetShow("ShowCodex");
                }
```

In `Dispose()`, add:

```csharp
        _trayCodex?.Dispose();
        _currentCodexIcon?.Dispose();
```

- [ ] **Step 2: Full build + tests on PC**

```bash
git add windows/src/TrayApp/TrayController.cs && git commit -m "feat(windows-tray): Codex tray icon + show/hide settings" && git push
ssh desktop "cd <REPO> && git pull && dotnet build windows/src/TrayApp && dotnet test windows/tests/Core.Tests"
```
Expected: build clean, tests pass.

---

### Task 12: Windows CLI — Codex section

**Files:**
- Modify: `windows/src/Cli/Program.cs`

**Interfaces:**
- Consumes: `CodexSessionReader` (Task 8).

- [ ] **Step 1: Edit**

In `RenderOnce`, before `return 0;` at the end of the non-json path, add:

```csharp
    RenderCodex(json: false);
```

In the `if (json)` block, before its `return 0;`, add:

```csharp
        RenderCodex(json: true);
```

Add the helper below `RenderOnce`:

```csharp
static void RenderCodex(bool json)
{
    RateUsage u;
    try { u = new CodexSessionReader().Read(DateTimeOffset.UtcNow.ToUnixTimeSeconds()); }
    catch { u = RateUsage.NoData(Provider.Codex); }

    if (json)
    {
        Console.WriteLine($"codex-5h-utilization: {u.Util5h}");
        Console.WriteLine($"codex-7d-utilization: {u.Util7d}");
        Console.WriteLine($"codex-5h-reset: {u.Reset5h}");
        Console.WriteLine($"codex-as-of: {u.AsOf}");
        return;
    }

    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    if (u.State != UsageState.Ok)
    {
        Console.WriteLine("  \x1b[1;36mcodex usage\x1b[0m  \x1b[2m· no session data — run codex once\x1b[0m\n");
        return;
    }
    long age = u.AsOf is null ? 0 : now - u.AsOf.Value;
    string ageStr = age > 120 ? $" · as of {age / 60}m ago" : "";
    string plan = string.IsNullOrEmpty(u.Plan) ? "" : $"  \x1b[2m· {u.Plan}\x1b[0m";
    Console.WriteLine($"  \x1b[1;36mcodex usage\x1b[0m{plan}  \x1b[2m· via session log{ageStr}\x1b[0m\n");
    Console.WriteLine("  5h  " + Bar(u.Util5h) + "  " + Pct(u.Util5h));
    Console.WriteLine("  7d  " + Bar(u.Util7d) + "  " + Pct(u.Util7d));
    Console.WriteLine($"\n  \x1b[2m5h resets {Countdown.Format(u.Reset5h, now)}\x1b[0m\n");
}
```

- [ ] **Step 2: Build + run on PC**

```bash
git add windows/src/Cli/Program.cs && git commit -m "feat(windows-cli): codex usage section" && git push
ssh desktop "cd <REPO> && git pull && dotnet run --project windows/src/Cli"
```
Expected: Claude block then codex block (or "no session data" if Codex isn't installed on the PC — both are correct output).

---

### Task 13: Windows build + smoke on the PC

- [ ] **Step 1:** `ssh desktop "cd <REPO> && powershell -File windows/build.ps1"`
Expected: single-file exe produced as before (~63 MB).
- [ ] **Step 2:** Launch the built exe on the PC; ask Sam (or check via screenshot if a remote-view path exists) to confirm: two tray icons (orange Claude, gray Codex), both promoted to the taskbar (IsPromoted applied to both NotifyIconSettings entries), flyout shows both sections, right-click toggles hide/show each icon, unchecking the second-to-last is allowed but the last visible refuses.
- [ ] **Step 3:** Fix anything found, commit.

---

### Task 14: README + wrap-up

**Files:**
- Modify: `README.md`

- [ ] **Step 1:** Update README: title/intro mentions Claude **and Codex**; new "Codex support" section (data source = local session logs, zero API calls, staleness semantics, settings toggles per platform); screenshot TODOs removed — describe menu `Settings ▸ Show Claude / Show Codex`; Windows section notes one tray icon per provider.
- [ ] **Step 2:** `~/.config/ccc/venv/bin/python -m pytest tests/ -v` one last time; `bash -n bin/ccc`.
- [ ] **Step 3:** Commit: `git add README.md && git commit -m "docs: Codex provider + settings"`
- [ ] **Step 4:** Merge/push per Sam's call (default: `git checkout master && git merge feat/codex-provider && git push`), reinstall via `./install.sh`, restart LaunchAgent.

---

## Self-Review Notes

- Spec §2 (reader semantics), §3 (title/dropdown), §4 (settings), §5 (Windows), §6 (CLIs), §7 (icons), §8 (errors), §9 (tests) → Tasks 1, 4, 3, 7–11, 5+12, 2+9, 1+8, 1+3+8. §10 rollout → Tasks 0, 13, 14.
- Deviation from spec §7 noted in Task 2: single 64 px PNG per glyph instead of @1x/@2x pair (NSImage downscales from one high-res template cleanly; fewer files).
- Fixture epoch 1783348865 verified by computation (`datetime.fromisoformat`), used identically in the Python and C# tests.
