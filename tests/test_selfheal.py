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

class _Win: pass
class _Button:
    def __init__(self, win): self._w = win
    def window(self): return self._w
class _Item:
    def __init__(self, button): self._b = button
    def button(self): return self._b
class _Raises:
    def button(self): raise RuntimeError("boom")

def test_status_item_alive():
    m = _load()
    assert m.status_item_alive(None) is False
    assert m.status_item_alive(_Item(None)) is False           # no button
    assert m.status_item_alive(_Item(_Button(None))) is False  # button not in a window
    assert m.status_item_alive(_Item(_Button(_Win()))) is True # in a window
    assert m.status_item_alive(_Raises()) is True              # unknown -> assume alive

if __name__ == "__main__":
    test_toggle_roundtrip()
    test_default_is_on_when_unset()
    test_status_item_alive()
    # leave the real setting ON (its default) so we don't change app behavior
    _load().set_self_heal(True)
    print("OK test_selfheal toggle")
