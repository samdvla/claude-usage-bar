from test_selfheal import _load

def test_provider_toggle_roundtrip():
    m = _load()
    for key in (m.SHOW_CLAUDE_KEY, m.SHOW_CODEX_KEY, m.SHOW_CURSOR_KEY, m.SHOW_GEMINI_KEY):
        m.set_provider_enabled(key, False)
        assert m.provider_enabled(key) is False
        m.set_provider_enabled(key, True)
        assert m.provider_enabled(key) is True

def test_provider_default_on():
    m = _load()
    d = m._defaults()
    d.removeObjectForKey_(m.SHOW_CLAUDE_KEY)
    d.removeObjectForKey_(m.SHOW_CODEX_KEY)
    d.removeObjectForKey_(m.SHOW_CURSOR_KEY)
    d.removeObjectForKey_(m.SHOW_GEMINI_KEY)
    assert m.provider_enabled(m.SHOW_CLAUDE_KEY) is True
    assert m.provider_enabled(m.SHOW_CODEX_KEY) is True
    assert m.provider_enabled(m.SHOW_CURSOR_KEY) is True
    assert m.provider_enabled(m.SHOW_GEMINI_KEY) is True

def test_gemini_cap_default_1000():
    m = _load()
    d = m._defaults()
    d.removeObjectForKey_(m.GEMINI_CAP_KEY)
    assert m.gemini_cap() == 1000

def test_gemini_cap_roundtrip():
    m = _load()
    for cap in (1500, 2000, 250, 1000):
        m.set_gemini_cap(cap)
        assert m.gemini_cap() == cap
    # leave the real setting at its default so we don't change app behavior
    m.set_gemini_cap(1000)
