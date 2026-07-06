import importlib.machinery, importlib.util, json, pathlib, time
import pytest

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

def test_unreadable_dir_degrades_gracefully(tmp_path):
    import os
    m = _load()
    if os.geteuid() == 0:
        pytest.skip("running as root; chmod 000 doesn't block root")
    root = _mkroot(tmp_path, [
        ("2026/07/06/rollout-a.jsonl", [_event(u5=50.0)]),
    ])
    # make the day directory unreadable
    ddir = pathlib.Path(root) / "2026" / "07" / "06"
    try:
        os.chmod(str(ddir), 0o000)
        rec = m.codex_usage(root=root, now=NOW)
        assert rec == {"state": "no_data"}
    finally:
        os.chmod(str(ddir), 0o700)
