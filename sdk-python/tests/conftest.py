"""
Session-wide hermeticity guard: no test in this suite may ever read or write the developer's real
stored credential file (%LOCALAPPDATA%\\Kairon\\sdk\\credential.json on Windows, or
~/.config/kairon/credential.json elsewhere). A prior audit observed a real stored credential
leaking into test behavior (and pytest assertion output) because some tests constructed `Kairon()`
without an explicit `config_path`, which then silently fell back to
`_credential_store.default_config_path()` - the real, machine-wide file.

Rather than rely on every test remembering to pass `config_path=tmp_path/...` (easy to miss, and
unenforceable), this autouse fixture repoints `default_config_path()` itself at a per-test
temporary directory for the whole suite. A test that forgets `config_path` now fails safe into
isolation instead of silently touching real developer state.
"""

from __future__ import annotations

import pytest


@pytest.fixture(autouse=True)
def _never_touch_the_real_credential_store(tmp_path, monkeypatch):
    from kairon import _credential_store

    fake_default = tmp_path / "kairon-default-credential-store" / "credential.json"
    monkeypatch.setattr(_credential_store, "default_config_path", lambda: fake_default)
    yield
