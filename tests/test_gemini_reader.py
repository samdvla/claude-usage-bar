import importlib.machinery, importlib.util, json, os, pathlib, time
from datetime import datetime, timedelta, timezone
from zoneinfo import ZoneInfo

import pytest

_MOD = None
PT = ZoneInfo("America/Los_Angeles")


def _load():
    global _MOD
    if _MOD is None:
        p = pathlib.Path(__file__).resolve().parent.parent / "bin" / "ccc-gemini"
        loader = importlib.machinery.SourceFileLoader("cccgemini", str(p))
        spec = importlib.util.spec_from_loader("cccgemini", loader)
        _MOD = importlib.util.module_from_spec(spec)
        loader.exec_module(_MOD)
    return _MOD


# Fixed 'now': 2026-07-06T18:00:00-07:00 (PDT), computed via zoneinfo rather
# than a hand-derived UTC literal, for determinism.
NOW = int(datetime(2026, 7, 6, 18, 0, 0, tzinfo=PT).timestamp())


def _iso(epoch):
    return datetime.fromtimestamp(epoch, timezone.utc).isoformat().replace("+00:00", "Z")


def _gemini_rec(ts_epoch, id_="i"):
    return {"id": id_, "timestamp": _iso(ts_epoch), "type": "gemini", "content": "hi",
            "model": "gemini-2.5-pro", "tokens": {"total": 100}}


def _user_rec(ts_epoch, id_="u"):
    return {"id": id_, "timestamp": _iso(ts_epoch), "type": "user", "content": "hi"}


def _line(obj_or_str):
    return obj_or_str if isinstance(obj_or_str, str) else json.dumps(obj_or_str)


def _mkroot(tmp_path, files, mtime=None):
    """files: list of (relpath, [lines]) rooted at a fresh '<root>' dir.

    relpaths are relative to the gemini root itself, e.g.
    'tmp/<hash>/chats/session.jsonl'. All created files get their mtime
    pinned to `mtime` (defaults to NOW) so the 48h staleness gate never
    depends on wall-clock test-run time.
    """
    root = tmp_path / "gemini_root"
    stamp = NOW if mtime is None else mtime
    for rel, lines in files:
        p = root / rel
        p.parent.mkdir(parents=True, exist_ok=True)
        p.write_text("\n".join(_line(l) for l in lines) + "\n")
        os.utime(str(p), (stamp, stamp))
    return str(root)


def test_happy_count_two_projects_and_subagent(tmp_path):
    m = _load()
    today = NOW - 3600  # 1h ago, safely within "today" PT
    root = _mkroot(tmp_path, [
        ("tmp/hash1/chats/session-a.jsonl", [
            {"sessionId": "session-a", "projectHash": "hash1"},  # metadata line
            _user_rec(today, "u1"),
            _gemini_rec(today, "g1"),
            _gemini_rec(today, "g2"),
        ]),
        ("tmp/hash2/chats/session-b.jsonl", [
            _gemini_rec(today, "g3"),
        ]),
        ("tmp/hash1/chats/parent123/sub456.jsonl", [
            _gemini_rec(today, "g4"),
            _user_rec(today, "u2"),
            {"$rewindTo": "g4"},
            _gemini_rec(today, "g5"),
        ]),
    ])
    rec = m.gemini_usage(root=root, now=NOW)
    assert rec["state"] == "ok"
    assert rec["count"] == 5  # g1..g5; rewind control line does NOT reduce it
    assert rec["estimated"] is True
    assert rec["u7"] is None and rec["r7"] is None
    assert rec["plan"] == "Free"  # default cap (1000) maps to the Free tier label
    assert rec["as_of"] is None

    expected_midnight = datetime.fromtimestamp(NOW, PT).replace(
        hour=0, minute=0, second=0, microsecond=0)
    expected_r5 = int((expected_midnight + timedelta(days=1)).timestamp())
    assert rec["r5"] == expected_r5


def test_pt_midnight_boundary(tmp_path):
    m = _load()
    midnight = datetime.fromtimestamp(NOW, PT).replace(
        hour=0, minute=0, second=0, microsecond=0)
    midnight_epoch = int(midnight.timestamp())

    before = midnight_epoch - 1   # 06:59:59Z on the fixed date — belongs to yesterday PT
    after = midnight_epoch + 1    # 07:00:01Z on the fixed date — belongs to today PT

    root = _mkroot(tmp_path, [
        ("tmp/hash1/chats/session.jsonl", [
            _gemini_rec(before, "before"),
            _gemini_rec(after, "after"),
        ]),
    ])
    rec = m.gemini_usage(root=root, now=NOW)
    assert rec["count"] == 1
    assert rec["r5"] == midnight_epoch + 24 * 3600  # no DST change on this date


def test_dst_transition_day(tmp_path):
    """2026-03-08: US spring-forward. Wall-clock midnight-to-midnight spans
    only 23 real hours that day — assert the property via zoneinfo (not a
    hardcoded epoch), then confirm the implementation matches."""
    m = _load()
    now_dt = datetime(2026, 3, 8, 12, 0, 0, tzinfo=PT)  # noon, after the 2am jump
    now = int(now_dt.timestamp())

    today_midnight = datetime.fromtimestamp(now, PT).replace(
        hour=0, minute=0, second=0, microsecond=0)
    next_midnight = today_midnight + timedelta(days=1)
    expected_r5 = int(next_midnight.timestamp())

    # Confirm this really is a spring-forward day per zoneinfo (not asserting
    # a hardcoded r5 literal, just the well-known DST fact for this date).
    assert expected_r5 - int(today_midnight.timestamp()) == 23 * 3600

    root = _mkroot(tmp_path, [("tmp/hash1/chats/session.jsonl", [])], mtime=now)
    rec = m.gemini_usage(root=root, now=now)
    assert rec["r5"] == expected_r5


def test_cap_math_quarter(tmp_path):
    m = _load()
    today = NOW - 3600
    files = [("tmp/hash1/chats/session.jsonl",
              [_gemini_rec(today, f"g{i}") for i in range(250)])]
    root = _mkroot(tmp_path, files)
    rec = m.gemini_usage(root=root, cap=1000, now=NOW)
    assert rec["count"] == 250
    assert rec["cap"] == 1000
    assert rec["u5"] == 0.25


def test_cap_math_overflow_clamped(tmp_path):
    m = _load()
    today = NOW - 3600
    files = [("tmp/hash1/chats/session.jsonl",
              [_gemini_rec(today, f"g{i}") for i in range(30)])]
    root = _mkroot(tmp_path, files)
    rec = m.gemini_usage(root=root, cap=10, now=NOW)
    assert rec["u5"] == 1.0
    assert rec["count"] == 30  # true count preserved in extras
    assert rec["cap"] == 10


def test_pct_unclamped_over_cap(tmp_path):
    # Spec §8: the bar (u5) caps at 100%, but "pct" carries the real,
    # unclamped fraction so the UI can show e.g. "est. 120%".
    m = _load()
    today = NOW - 3600
    files = [("tmp/hash1/chats/session.jsonl",
              [_gemini_rec(today, f"g{i}") for i in range(1200)])]
    root = _mkroot(tmp_path, files)
    rec = m.gemini_usage(root=root, cap=1000, now=NOW)
    assert rec["u5"] == 1.0
    assert rec["pct"] == 1.2
    # under-cap: pct matches u5
    rec2 = m.gemini_usage(root=root, cap=4800, now=NOW)
    assert rec2["u5"] == 0.25
    assert rec2["pct"] == 0.25


def test_cap_below_one_defaults_to_1000(tmp_path):
    m = _load()
    today = NOW - 3600
    files = [("tmp/hash1/chats/session.jsonl",
              [_gemini_rec(today, f"g{i}") for i in range(250)])]
    root = _mkroot(tmp_path, files)
    rec = m.gemini_usage(root=root, cap=0, now=NOW)
    assert rec["cap"] == 1000
    assert rec["u5"] == 0.25


def test_plan_label_from_cap(tmp_path):
    """Spec §4: the cap (Settings ▸ Gemini plan picker) maps to a tier label
    so the dropdown header can show "Gemini · Free" etc. An unrecognized cap
    (a stray custom value) falls back to no label rather than guessing."""
    m = _load()
    root = _mkroot(tmp_path, [("tmp/hash1/chats/session.jsonl", [])])
    expected = {250: "API key", 1000: "Free", 1500: "AI Pro", 2000: "AI Ultra"}
    for cap, label in expected.items():
        rec = m.gemini_usage(root=root, cap=cap, now=NOW)
        assert rec["plan"] == label, f"cap={cap}"
    assert m.gemini_usage(root=root, cap=777, now=NOW)["plan"] is None


def test_mtime_filter_excludes_stale_file(tmp_path):
    m = _load()
    today = NOW - 3600
    root = _mkroot(tmp_path, [
        ("tmp/hash1/chats/fresh.jsonl", [_gemini_rec(today, "g1")]),
        ("tmp/hash1/chats/stale.jsonl", [_gemini_rec(today, "g2"), _gemini_rec(today, "g3")]),
    ])
    stale_path = os.path.join(root, "tmp", "hash1", "chats", "stale.jsonl")
    old = NOW - 72 * 3600
    os.utime(stale_path, (old, old))

    rec = m.gemini_usage(root=root, now=NOW)
    assert rec["count"] == 1  # only fresh.jsonl counted


def test_malformed_lines_skipped(tmp_path):
    m = _load()
    today = NOW - 3600
    root = _mkroot(tmp_path, [
        ("tmp/hash1/chats/session.jsonl", [
            _gemini_rec(today, "g1"),
            "not json at all {{{",
            "[1, 2, 3]",           # valid JSON, not a dict
            '"just a string"',     # valid JSON, not a dict
            json.dumps({"type": "gemini", "id": "g2"}),  # missing timestamp
            json.dumps({"type": "gemini", "id": "g3", "timestamp": "not-a-date"}),
            _gemini_rec(today, "g4"),
        ]),
    ])
    rec = m.gemini_usage(root=root, now=NOW)
    assert rec["state"] == "ok"
    assert rec["count"] == 2  # only g1 and g4


def test_missing_root_no_data(tmp_path):
    m = _load()
    missing = str(tmp_path / "does-not-exist")
    assert m.gemini_usage(root=missing, now=NOW) == {"state": "no_data"}
    assert m.gemini_detected(root=missing) is False


def test_missing_tmp_dir_no_data(tmp_path):
    m = _load()
    root = tmp_path / "gemini_root"
    root.mkdir()
    assert m.gemini_usage(root=str(root), now=NOW) == {"state": "no_data"}
    assert m.gemini_detected(root=str(root)) is False


def test_detected_recent_chat_activity(tmp_path):
    m = _load()
    # newest activity lives in a SUBAGENT file (second glob level) — both
    # levels must feed detection.
    root = _mkroot(tmp_path, [
        ("tmp/hash1/chats/parent123/sub456.jsonl", [_gemini_rec(NOW - 3600)]),
    ], mtime=time.time() - 3600)
    assert m.gemini_detected(root=root) is True


def test_detected_false_when_stale_or_empty(tmp_path):
    m = _load()
    stale = time.time() - 20 * 86400  # outside the 14-day activity window
    root = _mkroot(tmp_path, [
        ("tmp/hash1/chats/session.jsonl", [_gemini_rec(NOW - 3600)]),
    ], mtime=stale)
    assert m.gemini_detected(root=root) is False
    # bare tmp dir, no chat files at all → merely installed, not active
    root2 = tmp_path / "gemini_root2"
    (root2 / "tmp").mkdir(parents=True)
    assert m.gemini_detected(root=str(root2)) is False
    # ...but gemini_usage still treats an existing tmp dir as a live source
    # (zero is a valid count) — recency gating is only the display rule.
    assert m.gemini_usage(root=str(root2), now=NOW)["state"] == "ok"
