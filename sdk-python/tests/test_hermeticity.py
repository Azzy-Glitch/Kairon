"""
Regression guard for the credential-store hermeticity fix (conftest.py's autouse fixture): proves
that constructing Kairon() with no config_path at all - the exact shape that previously fell
through to the real, machine-wide stored-credential file - never touches that real file, in this
process or any other test in the suite.
"""

from __future__ import annotations

import os
import sys
from pathlib import Path

from kairon import _credential_store
from kairon.client import Kairon


def _real_default_config_path() -> Path:
    # Independently reproduces _credential_store.default_config_path()'s own real logic, without
    # calling the (deliberately monkeypatched, for this whole suite) function itself - so this can
    # assert against the genuine real-world location the fixture is protecting.
    if sys.platform == "win32":
        base = os.environ.get("LOCALAPPDATA") or str(Path.home() / "AppData" / "Local")
        return Path(base) / "Kairon" / "sdk" / "credential.json"
    return Path.home() / ".config" / "kairon" / "credential.json"


def test_default_config_path_is_repointed_away_from_the_real_user_directory():
    # The autouse fixture in conftest.py must have replaced this with a per-test tmp_path location,
    # never the real path a genuine install would use.
    resolved = _credential_store.default_config_path()
    assert resolved != _real_default_config_path()
    assert "kairon-default-credential-store" in str(resolved)


def test_constructing_kairon_with_no_config_path_never_creates_the_real_stored_credential_file():
    real_default = _real_default_config_path()
    existed_before = real_default.exists()
    mtime_before = real_default.stat().st_mtime if existed_before else None

    kairon = Kairon(endpoint="http://127.0.0.1:9999", project_id="proj-hermetic", api_key="krn_hermetic")
    try:
        assert kairon.project_id == "proj-hermetic"
    finally:
        kairon.stop(timeout_seconds=1)

    # Never created if it didn't already exist, and never modified if it happened to (a developer's
    # own real, unrelated credential file on this machine) - this test only ever touches the fake,
    # conftest-patched default path.
    assert real_default.exists() == existed_before
    if existed_before:
        assert real_default.stat().st_mtime == mtime_before


def test_explicit_config_path_still_works_and_is_independent_of_the_default(tmp_path):
    explicit_path = tmp_path / "explicit" / "credential.json"
    _credential_store.save_stored_config("http://127.0.0.1:8000", "proj-explicit", "krn_explicit", explicit_path)

    loaded = _credential_store.load_stored_config(explicit_path)
    assert loaded["projectId"] == "proj-explicit"

    # Loading with NO path at all reads the (isolated, per-test) default - never this explicit one.
    assert _credential_store.load_stored_config(None) is None
    assert not Path(_credential_store.default_config_path()).exists()
