import importlib.machinery, importlib.util, json, os, pathlib, sqlite3, stat, time
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


def _fake_urlopen(by_suffix, counter=None, count_suffix="GetCurrentPeriodUsage",
                  check=None):
    """Builds a urlopen(req, timeout=...) fake keyed by URL suffix.

    by_suffix: {"GetCurrentPeriodUsage": {...}, "GetPlanInfo": {...}}
    values may be an Exception instance to raise instead of returning.
    `counter`, if given, is bumped once per network *round* — i.e. only on
    the primary (GetCurrentPeriodUsage) call — since one cursor_usage() call
    that reaches the network makes two HTTP requests (usage + plan) but
    should count as a single "hit the network" event for cache tests.
    `check`, if given, is called with every Request for contract assertions.
    """
    def _urlopen(req, timeout=None):
        url = req.full_url if hasattr(req, "full_url") else req.get_full_url()
        if check is not None:
            check(req)
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

    def _check_contract(req):
        # urllib capitalizes stored header keys: "Connect-Protocol-Version"
        # → "Connect-protocol-version" (verified empirically).
        assert req.get_header("Connect-protocol-version") == "1"
        assert req.get_header("Authorization").startswith("Bearer ")
        assert req.get_header("Content-type") == "application/json"
        assert req.data == b"{}"
        assert req.get_method() == "POST"

    urlopen = _fake_urlopen({
        "GetCurrentPeriodUsage": {
            "enabled": True,
            "planUsage": {"limit": 2000, "totalSpend": 900, "totalPercentUsed": 45.0},
            "billingCycleStart": 1780000000000,
            "billingCycleEnd": 1782600000000,
        },
        "GetPlanInfo": {"planInfo": {"planName": "pro"}},
    }, check=_check_contract)
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


def test_missing_enabled_key_is_active(tmp_path):
    """Real 2026-07 free-tier shape: no `enabled` key at all, valid planUsage.

    billingCycle* are strings here on purpose — Connect-RPC (protobuf JSON)
    encodes int64 fields as strings, observed on the live API.
    """
    m = _load()
    db = _mkdb(tmp_path)
    cache = str(tmp_path / "cache.json")
    urlopen = _fake_urlopen({
        "GetCurrentPeriodUsage": {
            "planUsage": {"totalPercentUsed": 0, "apiPercentUsed": 0,
                          "autoPercentUsed": 0, "remainingBonus": 0},
            "billingCycleStart": "1781509572704",
            "billingCycleEnd": "1784101572704",
            "displayThreshold": 200,
        },
        "GetPlanInfo": {"planInfo": {"planName": "Free"}},
    })
    rec = m.cursor_usage(state_db=db, cache_path=cache, now=NOW, urlopen=urlopen)
    assert rec["state"] == "ok"
    assert rec["u5"] == 0.0
    assert rec["r5"] == 1784101572
    assert rec["plan"] == "Free"


def test_missing_plan_usage_no_data(tmp_path):
    m = _load()
    db = _mkdb(tmp_path)
    cache = str(tmp_path / "cache.json")
    urlopen = _fake_urlopen({
        "GetCurrentPeriodUsage": {"billingCycleEnd": 1784101572704},
        "GetPlanInfo": {"planInfo": {"planName": "Free"}},
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
    assert m.cursor_detected(state_db=db2) is True  # fresh mtime + token


def test_detected_stale_state_db(tmp_path):
    m = _load()
    db = _mkdb(tmp_path)
    assert m.cursor_detected(state_db=db) is True  # freshly written → active
    stale = time.time() - 20 * 86400  # outside the 14-day activity window
    os.utime(db, (stale, stale))
    assert m.cursor_detected(state_db=db) is False  # token present but inactive


def test_detected_fresh_mtime_without_token(tmp_path):
    m = _load()
    db = _mkdb(tmp_path, token=None)  # fresh mtime, no login token
    assert m.cursor_detected(state_db=db) is False
    assert m.cursor_detected(state_db=str(tmp_path / "missing.vscdb")) is False


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


def test_list_response_error_without_cache(tmp_path):
    """Valid JSON but not an object (a list) → error when no cache exists."""
    m = _load()
    db = _mkdb(tmp_path)
    cache = str(tmp_path / "cache.json")
    urlopen = _fake_urlopen({
        "GetCurrentPeriodUsage": [1, 2, 3],
        "GetPlanInfo": {"planInfo": {"planName": "pro"}},
    })
    rec = m.cursor_usage(state_db=db, cache_path=cache, now=NOW, urlopen=urlopen)
    assert rec == {"state": "error"}


def test_list_response_stale_with_cache(tmp_path):
    """Valid JSON but not an object → stale cached record when cache exists."""
    m = _load()
    db = _mkdb(tmp_path)
    cache_path = tmp_path / "cache.json"
    seeded = {"fetched_at": NOW - 1000, "record": {
        "state": "ok", "u5": 0.3, "u7": None, "r5": NOW + 5000, "r7": None,
        "plan": "Pro", "as_of": NOW - 1000,
    }}
    cache_path.write_text(json.dumps(seeded))

    urlopen = _fake_urlopen({
        "GetCurrentPeriodUsage": [1, 2, 3],
        "GetPlanInfo": {"planInfo": {"planName": "pro"}},
    })
    rec = m.cursor_usage(state_db=db, cache_path=str(cache_path), now=NOW, urlopen=urlopen)
    assert rec["state"] == "ok"
    assert rec["u5"] == 0.3
    assert rec["stale"] is True


def test_consecutive_failures_flip_to_error(tmp_path):
    """Spec: each network/shape failure with a cache present bumps a
    `fail_count` kept in the cache FILE and still serves stale data while
    it's under 3; the 3rd consecutive failure flips to a hard error instead
    (counter keeps climbing past 3 too); a subsequent success resets it to 0
    and rewrites the cache."""
    m = _load()
    db = _mkdb(tmp_path)
    cache_path = tmp_path / "cache.json"
    seeded = {"fetched_at": NOW - 1000, "record": {
        "state": "ok", "u5": 0.3, "u7": None, "r5": NOW + 5000, "r7": None,
        "plan": "Pro", "as_of": NOW - 1000,
    }}
    cache_path.write_text(json.dumps(seeded))

    bad_urlopen = _fake_urlopen({
        "GetCurrentPeriodUsage": urllib.error.URLError("boom"),
        "GetPlanInfo": {"planInfo": {"planName": "pro"}},
    })

    rec1 = m.cursor_usage(state_db=db, cache_path=str(cache_path), now=NOW, urlopen=bad_urlopen)
    assert rec1["state"] == "ok" and rec1["stale"] is True
    assert json.loads(cache_path.read_text())["fail_count"] == 1

    rec2 = m.cursor_usage(state_db=db, cache_path=str(cache_path), now=NOW + 400, urlopen=bad_urlopen)
    assert rec2["state"] == "ok" and rec2["stale"] is True
    assert json.loads(cache_path.read_text())["fail_count"] == 2

    rec3 = m.cursor_usage(state_db=db, cache_path=str(cache_path), now=NOW + 800, urlopen=bad_urlopen)
    assert rec3 == {"state": "error"}
    assert json.loads(cache_path.read_text())["fail_count"] == 3

    rec4 = m.cursor_usage(state_db=db, cache_path=str(cache_path), now=NOW + 1200, urlopen=bad_urlopen)
    assert rec4 == {"state": "error"}  # stays in error, counter keeps climbing
    assert json.loads(cache_path.read_text())["fail_count"] == 4

    good_urlopen = _fake_urlopen({
        "GetCurrentPeriodUsage": {
            "enabled": True,
            "planUsage": {"limit": 2000, "totalSpend": 900, "totalPercentUsed": 45.0},
            "billingCycleEnd": 1782600000000,
        },
        "GetPlanInfo": {"planInfo": {"planName": "pro"}},
    })
    rec5 = m.cursor_usage(state_db=db, cache_path=str(cache_path), now=NOW + 1600, urlopen=good_urlopen)
    assert rec5["state"] == "ok"
    assert json.loads(cache_path.read_text())["fail_count"] == 0


def test_corrupt_record_cache_ignored(tmp_path):
    """A cache file whose 'record' value isn't a dict (corrupted/foreign
    write) is treated as NO cache at all, not merely an empty stale answer —
    so a failure with nothing valid to fall back on is a hard error."""
    m = _load()
    db = _mkdb(tmp_path)
    cache_path = tmp_path / "cache.json"
    cache_path.write_text(json.dumps({"fetched_at": NOW - 1000, "record": "not-a-dict"}))
    urlopen = _fake_urlopen({
        "GetCurrentPeriodUsage": urllib.error.URLError("boom"),
        "GetPlanInfo": {"planInfo": {"planName": "pro"}},
    })
    rec = m.cursor_usage(state_db=db, cache_path=str(cache_path), now=NOW, urlopen=urlopen)
    assert rec == {"state": "error"}


def test_num_never_raises_on_huge_int_literal():
    """A >308-digit int (arbitrary-precision Python int from json.loads of a
    huge JSON integer literal) overflows float() with OverflowError, not
    ValueError — _num must swallow that too, per its never-raise docstring."""
    m = _load()
    assert m._num(int("9" * 400)) is None


def test_huge_billing_cycle_end_field_none_state_ok(tmp_path):
    """End-to-end: a billingCycleEnd this large must not crash cursor_usage —
    r5 becomes None while the rest of the record stays state 'ok'."""
    m = _load()
    db = _mkdb(tmp_path)
    cache = str(tmp_path / "cache.json")
    huge = int("9" * 400)
    urlopen = _fake_urlopen({
        "GetCurrentPeriodUsage": {
            "enabled": True,
            "planUsage": {"totalPercentUsed": 10.0},
            "billingCycleEnd": huge,
        },
        "GetPlanInfo": {"planInfo": {"planName": "Free"}},
    })
    rec = m.cursor_usage(state_db=db, cache_path=cache, now=NOW, urlopen=urlopen)
    assert rec["state"] == "ok"
    assert rec["r5"] is None
    assert rec["u5"] == 0.1


def test_nonfinite_cycle_end_and_string_percent(tmp_path):
    """billingCycleEnd overflowing float ("1e400" → inf) must not crash — r5
    becomes None; totalPercentUsed as a STRING pins _num on the percent path."""
    m = _load()
    db = _mkdb(tmp_path)
    cache = str(tmp_path / "cache.json")
    urlopen = _fake_urlopen({
        "GetCurrentPeriodUsage": {
            "planUsage": {"totalPercentUsed": "45.0"},
            "billingCycleEnd": "1e400",
        },
        "GetPlanInfo": {"planInfo": {"planName": "Free"}},
    })
    rec = m.cursor_usage(state_db=db, cache_path=cache, now=NOW, urlopen=urlopen)
    assert rec["state"] == "ok"
    assert rec["u5"] == 0.45
    assert rec["r5"] is None
