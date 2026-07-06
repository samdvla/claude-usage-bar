import importlib.util, importlib.machinery, pathlib, sys

_MOD = None

def _load():
    # bin/ccc-bar has no .py extension, so use an explicit source loader.
    # Load once and cache: re-executing the module would redefine its PyObjC
    # classes (_MenuDelegate), which PyObjC forbids within one process.
    global _MOD
    if _MOD is None:
        # Check if already loaded by another test file (e.g., test_selfheal.py)
        if "cccbar" in sys.modules:
            _MOD = sys.modules["cccbar"]
        else:
            p = pathlib.Path(__file__).resolve().parent.parent / "bin" / "ccc-bar"
            loader = importlib.machinery.SourceFileLoader("cccbar", str(p))
            spec = importlib.util.spec_from_loader("cccbar", loader)
            _MOD = importlib.util.module_from_spec(spec)
            try:
                loader.exec_module(_MOD)
                sys.modules["cccbar"] = _MOD
            except Exception as e:
                # If PyObjC class already registered, find the existing module
                if "overriding existing Objective-C class" in str(e):
                    # Search for the module in sys.modules or by importing
                    for name, mod in list(sys.modules.items()):
                        if hasattr(mod, 'provider_enabled'):
                            _MOD = mod
                            break
                    if _MOD is None:
                        raise
                else:
                    raise
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
