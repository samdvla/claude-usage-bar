"""Cross-provider contract: every helper (ccc-codex, ccc-cursor, ccc-gemini)
normalizes to the same record shape so ccc-bar/ccc can treat them uniformly.
See the PROVIDERS table in docs/superpowers/specs/2026-07-06-cursor-gemini-
providers-design.md §1 — the record keeps `state,u5,u7,r5,r7,plan,as_of` plus
the optional `estimated` flag; single-window providers just leave u7/r7 None.
"""
import importlib.machinery, importlib.util, json, pathlib, sqlite3
from datetime import datetime, timezone
from zoneinfo import ZoneInfo

import pytest

BIN = pathlib.Path(__file__).resolve().parent.parent / "bin"
PT = ZoneInfo("America/Los_Angeles")

SHARED_KEYS = {"state", "u5", "u7", "r5", "r7", "plan", "as_of"}


def _load(name, modname):
    p = BIN / name
    loader = importlib.machinery.SourceFileLoader(modname, str(p))
    spec = importlib.util.spec_from_loader(modname, loader)
    mod = importlib.util.module_from_spec(spec)
    loader.exec_module(mod)
    return mod


# -- per-provider minimal ok-record fixtures --------------------------------
# Each mirrors the fixture-building pattern already used in that provider's
# own reader test (test_codex_reader.py / test_cursor_reader.py /
# test_gemini_reader.py), trimmed to the minimum needed for one "ok" record.

def _codex_ok_record(tmp_path):
    m = _load("ccc-codex", "cccodex_contract")
    now = 1783300000
    event = json.dumps({
        "timestamp": "2026-07-06T14:41:05.555Z", "type": "event_msg",
        "payload": {"type": "token_count", "info": {},
                    "rate_limits": {
                        "limit_id": "codex",
                        "primary": {"used_percent": 20.0, "window_minutes": 300,
                                    "resets_at": now + 9000},
                        "secondary": {"used_percent": 25.0, "window_minutes": 10080,
                                      "resets_at": now + 200000},
                        "plan_type": "plus"}}})
    root = tmp_path / "sessions"
    day_dir = root / "2026" / "07" / "06"
    day_dir.mkdir(parents=True)
    (day_dir / "rollout-2026-07-06T10-31-02-abc.jsonl").write_text(event + "\n")
    return m.codex_usage(root=str(root), now=now)


def _cursor_ok_record(tmp_path):
    m = _load("ccc-cursor", "ccccursor_contract")

    db = tmp_path / "state.vscdb"
    con = sqlite3.connect(str(db))
    con.execute("CREATE TABLE ItemTable (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB)")
    con.execute("INSERT INTO ItemTable (key, value) VALUES (?, ?)",
                ("cursorAuth/accessToken", "x.y.z"))
    con.commit()
    con.close()

    class _Resp:
        def __init__(self, payload):
            self._payload = payload

        def read(self):
            return json.dumps(self._payload).encode("utf-8")

        def __enter__(self):
            return self

        def __exit__(self, *a):
            return False

    by_suffix = {
        "GetCurrentPeriodUsage": {
            "enabled": True,
            "planUsage": {"limit": 2000, "totalSpend": 900, "totalPercentUsed": 45.0},
            "billingCycleEnd": 1782600000000,
        },
        "GetPlanInfo": {"planInfo": {"planName": "pro"}},
    }

    def _urlopen(req, timeout=None):
        url = req.full_url if hasattr(req, "full_url") else req.get_full_url()
        for suffix, payload in by_suffix.items():
            if url.endswith(suffix):
                return _Resp(payload)
        raise AssertionError(f"unexpected URL: {url}")

    cache = tmp_path / "cursor-cache.json"
    return m.cursor_usage(state_db=str(db), cache_path=str(cache), now=1783300000,
                           urlopen=_urlopen)


def _gemini_ok_record(tmp_path):
    m = _load("ccc-gemini", "cccgemini_contract")
    now = int(datetime(2026, 7, 6, 18, 0, 0, tzinfo=PT).timestamp())
    today = now - 3600
    ts = datetime.fromtimestamp(today, timezone.utc).isoformat().replace("+00:00", "Z")
    root = tmp_path / "gemini_root"
    session = root / "tmp" / "hash1" / "chats" / "session.jsonl"
    session.parent.mkdir(parents=True)
    session.write_text(json.dumps({"id": "g1", "timestamp": ts, "type": "gemini",
                                    "content": "hi"}) + "\n")
    return m.gemini_usage(root=str(root), now=now)


PROVIDERS = {
    "codex": _codex_ok_record,
    "cursor": _cursor_ok_record,
    "gemini": _gemini_ok_record,
}


@pytest.mark.parametrize("name", sorted(PROVIDERS))
def test_shared_key_set_present(name, tmp_path):
    rec = PROVIDERS[name](tmp_path)
    assert rec["state"] == "ok", f"{name} fixture did not produce an ok record: {rec}"
    missing = SHARED_KEYS - rec.keys()
    assert not missing, f"{name} record missing shared keys: {missing}"


@pytest.mark.parametrize("name", sorted(PROVIDERS))
def test_ok_record_field_types_are_sane(name, tmp_path):
    rec = PROVIDERS[name](tmp_path)
    assert rec["state"] == "ok"

    u5 = rec["u5"]
    assert u5 is None or (isinstance(u5, (int, float)) and not isinstance(u5, bool)
                           and 0.0 <= float(u5) <= 1.0), f"{name} u5={u5!r}"

    r5 = rec["r5"]
    assert r5 is None or (isinstance(r5, int) and not isinstance(r5, bool)), \
        f"{name} r5={r5!r}"

    # u7/r7 follow the same shape when present (single-window providers
    # leave both None, per spec §1).
    u7 = rec["u7"]
    assert u7 is None or (isinstance(u7, (int, float)) and not isinstance(u7, bool)
                           and 0.0 <= float(u7) <= 1.0), f"{name} u7={u7!r}"
    r7 = rec["r7"]
    assert r7 is None or (isinstance(r7, int) and not isinstance(r7, bool)), \
        f"{name} r7={r7!r}"

    assert rec["plan"] is None or isinstance(rec["plan"], str)
    assert rec["as_of"] is None or (isinstance(rec["as_of"], int)
                                     and not isinstance(rec["as_of"], bool))
