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

def test_providers_arity():
    m = _load()
    for row in m.PROVIDERS:
        assert len(row) == 7
        key, setting_key, glyph_path, fetcher, fallback, labels, detect = row
        assert callable(fetcher)
        assert callable(detect)

def test_title_value_real_estimated_pct():
    m = _load()
    # over cap: text shows the real unclamped estimate, not the capped bar value
    assert m._title_value({"state": "ok", "estimated": True,
                           "u5": 1.0, "pct": 1.2}) == "est. 120%"
    # no pct field: falls back to u5
    assert m._title_value({"state": "ok", "estimated": True,
                           "u5": 0.12}) == "est. 12%"
    # non-estimated records ignore pct entirely
    assert m._title_value({"state": "ok", "u5": 0.79}) == "79%"


def test_claude_plan_label_from_tier():
    m = _load()
    assert m._claude_plan_label("default_claude_max_20x", "max") == "Max 20x"
    assert m._claude_plan_label("default_claude_max_5x", "max") == "Max 5x"
    assert m._claude_plan_label("default_claude_pro", "pro") == "Pro"


def test_claude_plan_label_fallbacks():
    m = _load()
    assert m._claude_plan_label(None, "max") == "Max"
    assert m._claude_plan_label("something_weird", "max") == "Max"
    assert m._claude_plan_label(None, None) is None


# ------------------------------------------------------ settings window (§5c)

def test_display_mode_default_and_roundtrip():
    m = _load()
    d = m._defaults()
    d.removeObjectForKey_(m.DISPLAY_MODE_KEY)
    assert m.display_mode() == "full"
    m.set_display_mode("glyph")
    assert m.display_mode() == "glyph"
    m.set_display_mode("full")
    assert m.display_mode() == "full"
    m.set_display_mode("nonsense")  # anything unknown coerces to full
    assert m.display_mode() == "full"
    d.removeObjectForKey_(m.DISPLAY_MODE_KEY)


def test_health_color_serialize_parse_roundtrip():
    from AppKit import NSColor
    m = _load()
    d = m._defaults()
    m.set_health_color(
        m.HEALTH_GREEN_KEY,
        NSColor.colorWithSRGBRed_green_blue_alpha_(0.1, 0.8, 0.3, 1.0))
    c = m.health_custom_color(m.HEALTH_GREEN_KEY)
    assert c is not None
    assert abs(c.redComponent() - 0.1) < 1e-6
    assert abs(c.greenComponent() - 0.8) < 1e-6
    assert abs(c.blueComponent() - 0.3) < 1e-6
    d.removeObjectForKey_(m.HEALTH_GREEN_KEY)
    assert m.health_custom_color(m.HEALTH_GREEN_KEY) is None


def test_health_color_garbage_strings_none():
    m = _load()
    d = m._defaults()
    for bad in ("", "x,y,z", "1,2", "0.1,0.2,0.3,0.4", "2,0,0", "-0.1,0,0"):
        d.setObject_forKey_(bad, m.HEALTH_ORANGE_KEY)
        assert m.health_custom_color(m.HEALTH_ORANGE_KEY) is None, bad
    d.removeObjectForKey_(m.HEALTH_ORANGE_KEY)


def test_thresholds_defaults_and_clamps():
    m = _load()
    m.reset_bar_defaults()
    assert (m.warn_threshold(), m.crit_threshold()) == (0.5, 0.8)
    m.set_warn_threshold(0.4)
    m.set_crit_threshold(0.9)
    assert (m.warn_threshold(), m.crit_threshold()) == (0.4, 0.9)
    # invalid pairs → both degrade to defaults (validated together)
    m.set_warn_threshold(0.9)              # warn >= crit
    assert (m.warn_threshold(), m.crit_threshold()) == (0.5, 0.8)
    m.set_warn_threshold(0.4)
    m.set_crit_threshold(1.5)              # crit > 1
    assert (m.warn_threshold(), m.crit_threshold()) == (0.5, 0.8)
    m._defaults().setObject_forKey_("garbage", m.WARN_THRESHOLD_KEY)
    assert (m.warn_threshold(), m.crit_threshold()) == (0.5, 0.8)
    m.reset_bar_defaults()


def test_health_color_honors_custom_and_thresholds():
    from AppKit import NSColor, NSColorSpace
    m = _load()
    m.reset_bar_defaults()
    # custom green applies below warn
    m.set_health_color(
        m.HEALTH_GREEN_KEY,
        NSColor.colorWithSRGBRed_green_blue_alpha_(0.0, 0.2, 1.0, 1.0))
    c = m.health_color(0.1).colorUsingColorSpace_(NSColorSpace.sRGBColorSpace())
    assert abs(c.blueComponent() - 1.0) < 1e-6
    m._defaults().removeObjectForKey_(m.HEALTH_GREEN_KEY)
    # threshold shift: warn 0.3 makes 0.4 warning-colored (system orange)
    m.set_warn_threshold(0.3)
    m.set_crit_threshold(0.8)
    assert m.health_color(0.4).isEqual_(NSColor.systemOrangeColor())
    assert m.health_color(0.85).isEqual_(NSColor.systemRedColor())
    m.reset_bar_defaults()
    assert m.health_color(0.4).isEqual_(NSColor.systemGreenColor())


def test_reset_bar_defaults_removes_all_keys():
    from AppKit import NSColor
    m = _load()
    m.set_health_color(m.HEALTH_RED_KEY,
                       NSColor.colorWithSRGBRed_green_blue_alpha_(1, 0, 0, 1))
    m.set_warn_threshold(0.3)
    m.set_crit_threshold(0.9)
    m.reset_bar_defaults()
    d = m._defaults()
    for key in m.BAR_SETTINGS_KEYS:
        assert d.objectForKey_(key) is None, key
    assert m.health_custom_color(m.HEALTH_RED_KEY) is None
    assert (m.warn_threshold(), m.crit_threshold()) == (0.5, 0.8)
