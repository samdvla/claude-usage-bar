import importlib.util, importlib.machinery, pathlib

_MOD = None

def _load():
    # bin/ccc-bar has no .py extension, so use an explicit source loader.
    # Load once and cache: re-executing the module would redefine its PyObjC
    # classes (_MenuDelegate), which PyObjC forbids within one process.
    global _MOD
    if _MOD is None:
        p = pathlib.Path(__file__).resolve().parent.parent / "bin" / "ccc-bar"
        loader = importlib.machinery.SourceFileLoader("cccbar", str(p))
        spec = importlib.util.spec_from_loader("cccbar", loader)
        _MOD = importlib.util.module_from_spec(spec)
        loader.exec_module(_MOD)
    return _MOD

def test_toggle_roundtrip():
    m = _load()
    m.set_self_heal(False)
    assert m.self_heal_enabled() is False
    m.set_self_heal(True)
    assert m.self_heal_enabled() is True

def test_default_is_on_when_unset():
    m = _load()
    # Clear the key, then it should default to True.
    d = m._defaults()
    d.removeObjectForKey_(m.SELFHEAL_KEY)
    assert m.self_heal_enabled() is True

if __name__ == "__main__":
    test_toggle_roundtrip()
    test_default_is_on_when_unset()
    # leave the real setting ON (its default) so we don't change app behavior
    _load().set_self_heal(True)
    print("OK test_selfheal toggle")
