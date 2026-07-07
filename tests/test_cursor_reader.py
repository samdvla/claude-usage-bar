import importlib.machinery, importlib.util, json, os, pathlib, sqlite3, stat
import urllib.error
import pytest

_MOD = None

def _load():
    global _MOD
    if _MOD is None:
        p = pathlib.Path(__file__).resolve().parent.parent / "bin" / "ccc-cursor"
        loader = importlib.machinery.SourceFileLoader("ccccursor", str(p))
        spec = importlib.util.spec_from_loader("ccccursor", loader)
        _MOD = importlib.util.module_from_spec(spec)
        loader.exec_module(_MOD)
    return _MOD

NOW = 1783300000  # fixed 'now' for determinism


def _mkdb(tmp_path, token="x.y.z", name="state.vscdb"):
    """Real sqlite state.vscdb fixture with (optionally empty) ItemTable."""
    p = tmp_path / name
    con = sqlite3.connect(str(p))
    con.execute("CREATE TABLE ItemTable (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB)")
    if token is not None:
        con.execute("INSERT INTO ItemTable (key, value) VALUES (?, ?)",
                    ("cursorAuth/accessToken", token))
    con.commit()
    con.close()
    return str(p)


class _Resp:
    """Fake context-manager response object for a fake urlopen."""
    def __init__(self, payload):
        self._payload = payload

    def read(self):
        return json.dumps(self._payload).encode("utf-8")

    def __enter__(self):
        return self

    def __exit__(self, *a):
        return False


def _fake_urlopen(by_suffix, counter=None, count_suffix="GetCurrentPeriodUsage"):
    """Builds a urlopen(req, timeout=...) fake keyed by URL suffix.

    by_suffix: {"GetCurrentPeriodUsage": {...}, "GetPlanInfo": {...}}
    values may be an Exception instance to raise instead of returning.
    `counter`, if given, is bumped once per network *round* — i.e. only on
    the primary (GetCurrentPeriodUsage) call — since one cursor_usage() call
    that reaches the network makes two HTTP requests (usage + plan) but
    should count as a single "hit the network" event for cache tests.
    """
    def _urlopen(req, timeout=None):
        url = req.full_url if hasattr(req, "full_url") else req.get_full_url()
        if counter is not None and url.endswith(count_suffix):
            counter[0] += 1
        for suffix, payload in by_suffix.items():
            if url.endswith(suffix):
                if isinstance(payload, BaseException):
                    raise payload
                return _Resp(payload)
        raise AssertionError(f"unexpected URL: {url}")
    return _urlopen


def test_happy_path(tmp_path):
    m = _load()
    db = _mkdb(tmp_path)
    cache = str(tmp_path / "cache.json")
    urlopen = _fake_urlopen({
        "GetCurrentPeriodUsage": {
            "enabled": True,
            "planUsage": {"limit": 2000, "totalSpend": 900, "totalPercentUsed": 45.0},
            "billingCycleStart": 1780000000000,
            "billingCycleEnd": 1782600000000,
        },
        "GetPlanInfo": {"planInfo": {"planName": "pro"}},
    })
    rec = m.cursor_usage(state_db=db, cache_path=cache, now=NOW, urlopen=urlopen)
    assert rec["u5"] == 0.45
    assert rec["r5"] == 1782600000
    assert rec["plan"] == "Pro"
    assert rec["state"] == "ok"
    assert rec["as_of"] == NOW
    assert not rec.get("estimated")


def test_percent_fallback_from_cents(tmp_path):
    m = _load()
    db = _mkdb(tmp_path)
    cache = str(tmp_path / "cache.json")
    urlopen = _fake_urlopen({
        "GetCurrentPeriodUsage": {
            "enabled": True,
            "planUsage": {"limit": 2000, "totalSpend": 500},
            "billingCycleStart": 1780000000000,
            "billingCycleEnd": 1782600000000,
        },
        "GetPlanInfo": {"planInfo": {"planName": "pro"}},
    })
    rec = m.cursor_usage(state_db=db, cache_path=cache, now=NOW, urlopen=urlopen)
    assert rec["u5"] == 0.25
    assert rec["state"] == "ok"


def test_disabled_subscription(tmp_path):
    m = _load()
    db = _mkdb(tmp_path)
    cache = str(tmp_path / "cache.json")
    urlopen = _fake_urlopen({
        "GetCurrentPeriodUsage": {"enabled": False},
        "GetPlanInfo": {"planInfo": {"planName": "pro"}},
    })
    rec = m.cursor_usage(state_db=db, cache_path=cache, now=NOW, urlopen=urlopen)
    assert rec == {"state": "no_data"}


def test_401_expired(tmp_path):
    m = _load()
    db = _mkdb(tmp_path)
    cache = str(tmp_path / "cache.json")
    urlopen = _fake_urlopen({
        "GetCurrentPeriodUsage": urllib.error.HTTPError(
            "https://api2.cursor.sh/aiserver.v1.DashboardService/GetCurrentPeriodUsage",
            401, "Unauthorized", None, None),
        "GetPlanInfo": {"planInfo": {"planName": "pro"}},
    })
    rec = m.cursor_usage(state_db=db, cache_path=cache, now=NOW, urlopen=urlopen)
    assert rec["state"] == "expired"
    assert not os.path.exists(cache)


def test_cache_respected(tmp_path):
    m = _load()
    db = _mkdb(tmp_path)
    cache = str(tmp_path / "cache.json")
    counter = [0]
    urlopen = _fake_urlopen({
        "GetCurrentPeriodUsage": {
            "enabled": True,
            "planUsage": {"limit": 2000, "totalSpend": 900, "totalPercentUsed": 45.0},
            "billingCycleStart": 1780000000000,
            "billingCycleEnd": 1782600000000,
        },
        "GetPlanInfo": {"planInfo": {"planName": "pro"}},
    }, counter=counter)

    rec1 = m.cursor_usage(state_db=db, cache_path=cache, now=NOW, urlopen=urlopen)
    assert counter[0] == 1
    assert rec1["as_of"] == NOW

    rec2 = m.cursor_usage(state_db=db, cache_path=cache, now=NOW + 120, urlopen=urlopen)
    assert counter[0] == 1
    assert rec2["as_of"] == NOW  # served from cache, original as_of preserved

    rec3 = m.cursor_usage(state_db=db, cache_path=cache, now=NOW + 400, urlopen=urlopen)
    assert counter[0] == 2
    assert rec3["as_of"] == NOW + 400


def test_no_token(tmp_path):
    m = _load()
    db = _mkdb(tmp_path, token=None)
    cache = str(tmp_path / "cache.json")

    def _boom(req, timeout=None):
        raise AssertionError("urlopen should not be called with no token")

    rec = m.cursor_usage(state_db=db, cache_path=cache, now=NOW, urlopen=_boom)
    assert rec["state"] == "no_data"
    assert m.cursor_detected(state_db=db) is False

    db2 = _mkdb(tmp_path, token="x.y.z", name="state2.vscdb")
    assert m.cursor_detected(state_db=db2) is True


def test_network_error_keeps_cache(tmp_path):
    m = _load()
    db = _mkdb(tmp_path)
    cache_path = tmp_path / "cache.json"
    seeded = {"fetched_at": NOW - 1000, "record": {
        "state": "ok", "u5": 0.3, "u7": None, "r5": NOW + 5000, "r7": None,
        "plan": "Pro", "as_of": NOW - 1000,
    }}
    cache_path.write_text(json.dumps(seeded))

    urlopen = _fake_urlopen({
        "GetCurrentPeriodUsage": urllib.error.URLError("boom"),
        "GetPlanInfo": {"planInfo": {"planName": "pro"}},
    })
    rec = m.cursor_usage(state_db=db, cache_path=str(cache_path), now=NOW, urlopen=urlopen)
    assert rec["state"] == "ok"
    assert rec["u5"] == 0.3
    assert rec["stale"] is True
