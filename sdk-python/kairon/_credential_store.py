"""
Persists the project credential obtained from a one-time pairing code, so a paired application
does not need to pair again on every restart - the Python counterpart to
sdk/Kairon.SDK/KaironCredentialStore.cs.

This SDK is deliberately dependency-light (see client.py's module docstring), so this uses no
third-party package. On Windows it calls the same OS-level DPAPI primitive KAIRON's own backend
wraps its Data Protection key ring with (backend/Program.cs's ProtectKeysWithDpapi()) via
ctypes - no new dependency, real encryption at rest. On other platforms there is no ctypes-free
equivalent to DPAPI in the standard library, so the file is written with owner-only permissions
(0600), the same "restrict by OS-level access, not by encrypting the bytes" approach
agent/Kairon.Agent's own AgentCredentialStore already relies on for its credential file.
"""

from __future__ import annotations

import json
import os
import sys
import time
import uuid
from pathlib import Path
from typing import Optional


def default_config_path() -> Path:
    if sys.platform == "win32":
        base = os.environ.get("LOCALAPPDATA") or str(Path.home() / "AppData" / "Local")
        return Path(base) / "Kairon" / "sdk" / "credential.json"
    return Path.home() / ".config" / "kairon" / "credential.json"


def _dpapi_protect(data: bytes) -> bytes:
    import ctypes
    from ctypes import wintypes

    class DATA_BLOB(ctypes.Structure):
        _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_char))]

    def _blob(raw: bytes) -> DATA_BLOB:
        buf = ctypes.create_string_buffer(raw, len(raw))
        return DATA_BLOB(len(raw), ctypes.cast(buf, ctypes.POINTER(ctypes.c_char)))

    in_blob = _blob(data)
    out_blob = DATA_BLOB()
    if not ctypes.windll.crypt32.CryptProtectData(
        ctypes.byref(in_blob), None, None, None, None, 0, ctypes.byref(out_blob)
    ):
        raise OSError("CryptProtectData failed")
    try:
        return ctypes.string_at(out_blob.pbData, out_blob.cbData)
    finally:
        ctypes.windll.kernel32.LocalFree(out_blob.pbData)


def _dpapi_unprotect(data: bytes) -> bytes:
    import ctypes
    from ctypes import wintypes

    class DATA_BLOB(ctypes.Structure):
        _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_char))]

    buf = ctypes.create_string_buffer(data, len(data))
    in_blob = DATA_BLOB(len(data), ctypes.cast(buf, ctypes.POINTER(ctypes.c_char)))
    out_blob = DATA_BLOB()
    if not ctypes.windll.crypt32.CryptUnprotectData(
        ctypes.byref(in_blob), None, None, None, None, 0, ctypes.byref(out_blob)
    ):
        raise OSError("CryptUnprotectData failed")
    try:
        return ctypes.string_at(out_blob.pbData, out_blob.cbData)
    finally:
        ctypes.windll.kernel32.LocalFree(out_blob.pbData)


def load_stored_config(path: Optional[Path] = None) -> Optional[dict]:
    """Returns {"endpoint", "projectId", "apiKey", "pendingConfirmationPairingId"} from a previous
    successful pairing, or None if there is nothing stored, or if the file cannot be read/decrypted
    (a foreign machine's DPAPI key, a corrupted file) - callers treat that exactly like "not paired
    yet", never as a fatal error, since re-pairing is always the safe fallback.
    pendingConfirmationPairingId is present only while a redeemed credential has not yet been
    confirmed with the backend (the confirmation response was lost, or the process exited before
    sending it) - non-secret (a plain session id, not a credential), kept alongside the credential
    it describes purely so a later run can retry confirming it without needing a new pairing code."""
    target = path or default_config_path()
    try:
        if not target.exists():
            return None
        raw = target.read_bytes()
        plaintext = _dpapi_unprotect(raw) if sys.platform == "win32" else raw
        data = json.loads(plaintext.decode("utf-8"))
        if not isinstance(data, dict):
            return None
        if not all(isinstance(data.get(k), str) and data.get(k) for k in ("endpoint", "projectId", "apiKey")):
            return None
        return data
    except Exception:
        return None


def save_stored_config(
    endpoint: str, project_id: str, api_key: str, path: Optional[Path] = None,
    pending_confirmation_pairing_id: Optional[str] = None,
) -> None:
    """Raises on any failure rather than returning a status - a caller must never report a
    successful pairing when the credential could not actually be persisted.

    Writes to a per-call-uniquely-named temporary file before the final atomic os.replace, rather
    than a single fixed ".tmp" name - two SDK instances (or two overlapping calls in one process)
    saving at the same moment must never read or clobber each other's still-being-written temp
    file; os.replace itself remains the sole atomic publish step either way, so whichever finishes
    last still wins cleanly rather than corrupting the target.
    """
    target = path or default_config_path()
    target.parent.mkdir(parents=True, exist_ok=True)
    data = {"endpoint": endpoint, "projectId": project_id, "apiKey": api_key}
    if pending_confirmation_pairing_id:
        data["pendingConfirmationPairingId"] = pending_confirmation_pairing_id
    plaintext = json.dumps(data).encode("utf-8")
    payload = _dpapi_protect(plaintext) if sys.platform == "win32" else plaintext

    temp_path = target.with_suffix(target.suffix + f".{os.getpid()}.{uuid.uuid4().hex}.tmp")
    try:
        temp_path.write_bytes(payload)
        if sys.platform != "win32":
            os.chmod(temp_path, 0o600)
        # On Windows, os.replace can transiently fail with PermissionError/OSError when another
        # thread or process replaces the SAME destination at nearly the same instant (or a virus
        # scanner briefly holds it open) - POSIX rename has no such window, but Windows' does. A
        # short bounded retry is the standard way to make the replace itself robust to that; it is
        # still the single atomic publish step, never a partial/torn write either way.
        last_error: Optional[OSError] = None
        for attempt in range(5):
            try:
                os.replace(temp_path, target)
                last_error = None
                break
            except OSError as exc:
                last_error = exc
                time.sleep(0.05 * (attempt + 1))
        if last_error is not None:
            raise last_error
    finally:
        try:
            if temp_path.exists():
                temp_path.unlink()
        except OSError:
            pass
