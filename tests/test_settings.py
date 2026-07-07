from test_selfheal import _load

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
