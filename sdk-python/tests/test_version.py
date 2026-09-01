"""Release-version consistency checks for the Python SDK."""

from __future__ import annotations

import inspect
import re
from pathlib import Path

import kairon
from kairon.client import pair


EXPECTED_RELEASE_VERSION = "1.0.1"
SDK_ROOT = Path(__file__).resolve().parents[1]
REPOSITORY_ROOT = SDK_ROOT.parent


def _distribution_version() -> str:
    pyproject = (SDK_ROOT / "pyproject.toml").read_text(encoding="utf-8")
    match = re.search(r'^version\s*=\s*"([^"]+)"\s*$', pyproject, re.MULTILINE)
    assert match is not None, "sdk-python/pyproject.toml does not define a static project version"
    return match.group(1)


def test_runtime_distribution_and_release_versions_agree():
    release_version = (REPOSITORY_ROOT / "VERSION").read_text(encoding="utf-8").strip()

    assert release_version == EXPECTED_RELEASE_VERSION
    assert _distribution_version() == EXPECTED_RELEASE_VERSION
    assert kairon.__version__ == EXPECTED_RELEASE_VERSION


def test_pairing_default_uses_the_release_version():
    pairing_default = inspect.signature(pair).parameters["version"].default

    assert pairing_default == EXPECTED_RELEASE_VERSION
    assert pairing_default == kairon.__version__
