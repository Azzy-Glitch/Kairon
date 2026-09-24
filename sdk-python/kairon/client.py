"""
Kairon telemetry client - the collector-only Python counterpart to the .NET Kairon.SDK
(sdk/Kairon.SDK/). Deliberately dependency-light: the core client uses only the standard
library, so a Python application never has to install or depend on anything Kairon-specific
beyond this package (fastapi/starlette are only required if you use kairon.middleware).

The contract this class exists to keep, mirroring sdk/Kairon.SDK/KaironTelemetryQueue.cs and
KaironTelemetryClient.cs exactly: never block the caller, never raise into host code, and a
bounded queue drops the OLDEST item on overflow rather than blocking or growing unbounded.
There is deliberately no AI credential, no database setting and no remediation option here -
this is a telemetry collector and nothing more, same boundary KaironOptions documents on the
.NET side.
"""

from __future__ import annotations

import asyncio
import json
import logging
import os
import queue
import random
import re
import socket
import sys
import threading
import time
import traceback
import urllib.error
import urllib.request
from contextlib import asynccontextmanager
from datetime import datetime, timezone
from pathlib import Path
from typing import Optional
from uuid import UUID, uuid4

from . import _credential_store
from ._endpoint_security import is_endpoint_allowed

_logger = logging.getLogger("kairon")
_logger.addHandler(logging.NullHandler())


class RedirectNotAllowedError(urllib.error.URLError):
    """A Kairon endpoint answered a pairing, confirmation or telemetry request with a redirect."""

    def __init__(self, status: int, location: object) -> None:
        # The Location value is attacker-controlled and may be absent or malformed; it is recorded
        # only as a repr for diagnostics and never parsed, resolved or requested.
        super().__init__(f"Refused to follow an HTTP {status} redirect to {location!r}")
        self.status = status
        self.location = location


class _RefuseRedirects(urllib.request.HTTPRedirectHandler):
    """Fails every 3xx instead of following it, for every status urllib would redirect on
    (301, 302, 303, 307, 308).

    308 needs a conditional, explicit handler here, not just an override of redirect_request:
    CPython's own HTTPRedirectHandler only grew a built-in http_error_308 in a later release
    (confirmed missing on 3.9.25 in CI - a 308 response there never reaches redirect_request at all
    and instead falls through to http_error_default, raising a bare urllib.error.HTTPError). The
    underlying security property still held even then - urllib never makes a second request
    without a handler for the status, so the redirect target is never contacted either way - but
    the exception type and message differed from every other refused status, a real cross-version
    inconsistency this SDK's own contract ("never follows a redirect", not "never follows a
    redirect except on Python 3.9") must not have.

    The fallback below is defined ONLY when the running Python's stdlib lacks http_error_308
    entirely (checked once, at class-definition time) - never as an unconditional override. An
    http_error_XXX hook's real signature omits newurl (six arguments, not redirect_request's
    seven) and is expected to derive it from the response headers itself before calling
    redirect_request, exactly like the stdlib's own http_error_302 already does for 301/302/303/
    307; aliasing http_error_308 directly to redirect_request would silently mismatch that
    signature and break real redirect handling on any Python version where the stdlib DOES already
    supply a (more careful, edge-case-handling) http_error_308 of its own.
    """

    def redirect_request(self, req, fp, code, msg, headers, newurl):  # noqa: D102 - urllib hook
        raise RedirectNotAllowedError(code, newurl)

    if not hasattr(urllib.request.HTTPRedirectHandler, "http_error_308"):
        def http_error_308(self, req, fp, code, msg, headers):  # noqa: D102 - urllib hook
            # The value is never resolved or requested - only ever repr()'d into the exception
            # message - so this deliberately does not replicate the stdlib's fuller relative-URL
            # resolution; it only needs to name the untrusted destination for diagnostics.
            newurl = headers.get("location") or headers.get("uri")
            return self.redirect_request(req, fp, code, msg, headers, newurl)


# A PRIVATE opener, never installed globally with urllib.request.install_opener(): this SDK's
# transport policy is its own business and must not change how the host application's own urllib
# calls behave.
#
# Redirects are refused outright rather than filtered, because following one is never safe here and
# never necessary. urllib's default opener replays the original request - including headers - at
# whatever address the response's Location names, so a compromised or misconfigured backend could
# harvest X-Kairon-API-Key simply by answering "302 Location: http://attacker/". Filtering by scheme
# or origin would still leave the credential one bug away from the wire; not following at all means
# the key can only ever reach the endpoint this SDK already validated. It also removes the
# downgrade path entirely: an HTTPS endpoint cannot be walked down to plaintext by redirect, and a
# same-origin redirect gets no more trust than a cross-origin one.
#
# Every Kairon endpoint answers these calls directly, so a redirect is a misconfiguration or an
# attack either way - reported as a delivery failure, never followed.
_opener = urllib.request.build_opener(_RefuseRedirects)


def _open(request: urllib.request.Request, timeout: float):
    """The single network entry point for this module. Goes through the private, non-redirecting
    opener above - never urllib.request.urlopen, which follows redirects by default."""
    return _opener.open(request, timeout=timeout)

DEFAULT_IGNORED_PATH_PREFIXES = ("/health", "/healthz", "/metrics", "/favicon.ico")

# Matches the .NET SDK's KaironOptions.Endpoint default exactly - the same address the installed
# desktop backend actually binds to (desktop/Kairon.Desktop/MainForm.cs).
DEFAULT_ENDPOINT = "http://localhost:8000"

# Module-level default instance, set by the most recent start() call. Lets
# `app.add_middleware(KaironMiddleware)` work with no explicit instance, mirroring how the
# .NET SDK's app.UseKairon() resolves everything from DI without the caller passing an instance.
_default_instance: Optional["Kairon"] = None


def get_default_instance() -> Optional["Kairon"]:
    return _default_instance


def _utcnow_iso() -> str:
    # .isoformat() on a UTC-aware datetime renders the offset as "+00:00", not "Z". The backend's
    # DateTime fields (not DateTimeOffset) round-trip a "Z"-suffixed UTC instant as-is - the same
    # format the .NET SDK's DateTime.UtcNow serializes to - but System.Text.Json's default
    # DateTime converter treats an explicit "+00:00" offset as needing conversion to the server's
    # local time zone. On a server not itself running in UTC, "+00:00" silently lands as a
    # wrong-by-the-server's-UTC-offset timestamp instead of the instant actually reported.
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def format_exception(exc: BaseException) -> Optional[str]:
    try:
        return _scrub_text("".join(traceback.format_exception(type(exc), exc, exc.__traceback__)))
    except Exception:
        return None


def _scrub_text(value: Optional[str]) -> Optional[str]:
    """Mask common credential forms before telemetry enters the in-memory queue."""
    if value is None:
        return None
    value = re.sub(r"(?i)bearer\s+[A-Za-z0-9._~+/-]+=*", "Bearer [redacted]", value)
    value = re.sub(
        r"(?i)\b(api[_-]?key|authorization|token|secret|password|pwd|pairing[_-]?code)\b\s*[:=]\s*[^\s,;'\"]+",
        lambda match: match.group(1) + "=[redacted]", value,
    )
    value = re.sub(r"(?i)\b(?:krn|ksi|pair)_[A-Za-z0-9_-]{8,}\b", "[redacted]", value)
    return value


def pair(endpoint: str, code: str, version: str = "1.1.0", timeout_seconds: float = 10.0) -> Optional[dict]:
    """Redeems a one-time pairing code (minted by an operator in the Kairon UI) for a
    persistent project API key - the Python counterpart to Kairon.SDK's KaironPairingClient
    (docs/DESKTOP_SHELL.md). Returns a dict with "apiKey"/"projectId"/"endpoint"/"pairingId" on
    success, or None on any failure (rejected code, malformed response, unreachable backend) -
    contained here rather than raised, matching every other network path in this client.
    """
    try:
        # Checked before this SDK ever sends a pairing code anywhere - a caller-supplied endpoint
        # is exactly as untrusted as one returned in a response (checked again below), and the
        # pairing code itself is a secret worth protecting in transit.
        if not is_endpoint_allowed(endpoint):
            return None
        url = endpoint.rstrip("/") + "/api/v1/sdk/pair"
        body = json.dumps({"code": code, "sdkType": "python", "version": version}).encode("utf-8")
        request = urllib.request.Request(url, data=body, method="POST", headers={"Content-Type": "application/json"})
        with _open(request, timeout=max(1.0, timeout_seconds)) as response:
            if not 200 <= response.status < 300:
                return None
            raw = response.read(65537)
            if len(raw) > 65536:
                return None
            result = json.loads(raw)
            if not isinstance(result, dict) or not isinstance(result.get("apiKey"), str) or not result["apiKey"]:
                return None
            if not isinstance(result.get("projectId"), str) or UUID(result["projectId"]).int == 0:
                return None
            if not isinstance(result.get("pairingId"), str) or UUID(result["pairingId"]).int == 0:
                return None
            # The returned endpoint is exactly as untrusted as any other network input - a
            # compromised or misconfigured backend must never be able to redirect this SDK onto a
            # remote plaintext address merely by including one in a pairing response.
            if not is_endpoint_allowed(result.get("endpoint")):
                return None
            return result
    except Exception:
        return None


def _resolve_configuration(
    endpoint: Optional[str],
    project_id: Optional[str],
    api_key: Optional[str],
    pairing_code: Optional[str],
    config_path: Optional[str],
) -> tuple:
    """Implements the required configuration precedence:

        1. explicit pairing_code - always wins, redeeming (or re-pairing) immediately, before
           anything else below is even consulted.
        2. a previously stored credential, if one exists - preferred as a whole over ordinary
           configuration. A stored credential represents a real, completed pairing event; treating
           it as the strongest available signal of intended identity (once no pairing_code is
           given) means a stray or inherited KAIRON_PROJECT_ID/KAIRON_API_KEY - or even an explicit
           project_id/api_key left over in code - can never silently override, or be silently
           mixed field-by-field with, an application's own already-paired identity. endpoint/
           project_id/api_key are always taken from the SAME source together: there is no
           per-field merge between "stored" and "explicit/env" anywhere in this function, which is
           what makes a mixed configuration (one project's id with another's key) structurally
           impossible rather than merely unlikely.
        3. ordinary explicit arguments, then KAIRON_ENDPOINT/KAIRON_PROJECT_ID/KAIRON_API_KEY
           environment variables - consulted only when neither of the above applies (first-time
           onboarding, or a fresh config_path with nothing stored yet).

    Raises ValueError/RuntimeError (no new exception hierarchy) rather than ever continuing with
    an incomplete credential.

    Pairing is always explicit, never automatic: nothing in this SDK ever supplies pairing_code
    on the caller's behalf (not on HTTP 401, not on startup with a still-valid credential) - it is
    consulted here only because the caller passed it in this exact call.
    """
    path = Path(config_path) if config_path else None

    if pairing_code:
        resolved_endpoint = endpoint or os.environ.get("KAIRON_ENDPOINT") or DEFAULT_ENDPOINT
        paired = pair(resolved_endpoint, pairing_code)
        if paired is None:
            raise RuntimeError(
                "Kairon pairing failed: the pairing code is invalid, expired, already used, or "
                "Kairon is unavailable. Generate a new pairing code from Kairon and try again."
            )
        # Persisted (with the pairing id recorded as still-pending-confirmation) before being used
        # - if this raises, __init__ never completes, so pairing is never reported as successful
        # without a durable credential. The freshly redeemed values win outright, replacing
        # whatever explicit args/env vars/stored file resolved above - an explicit pairing_code is
        # a direct instruction to (re)pair now, not a fallback.
        _credential_store.save_stored_config(
            paired["endpoint"], paired["projectId"], paired["apiKey"], path,
            pending_confirmation_pairing_id=paired["pairingId"],
        )
        if _confirm_pairing(paired["endpoint"], paired["pairingId"], paired["apiKey"]):
            # Clears the pending marker now that confirmation actually succeeded - leaving it set
            # after a successful confirm would cause every future run to keep retrying pointlessly.
            _credential_store.save_stored_config(paired["endpoint"], paired["projectId"], paired["apiKey"], path)
        # If confirmation did not succeed, the pending marker stays recorded on disk (set just
        # above) so a LATER run - even with no pairing_code at all - can retry it. Never treated as
        # a fatal error here: the credential itself is already valid and saved either way.
        return paired["endpoint"], paired["projectId"], paired["apiKey"]

    stored = _credential_store.load_stored_config(path)
    if stored and stored.get("projectId"):
        # Atomic with projectId/apiKey below, not merely "closest available value": a stored
        # connection is (endpoint, projectId, apiKey) together, from the one pairing that produced
        # it. Letting an explicit endpoint argument or an ambient KAIRON_ENDPOINT redirect traffic
        # for this project/key pair to a DIFFERENT backend than the one it was actually paired
        # against - while still authenticating as this project - is exactly the "hybrid" precedence
        # this SDK must not have. To point an already-paired application at a different KAIRON
        # backend, pass a fresh pairing_code (re-pairing) - the one explicit, documented way to
        # relocate a stored connection - rather than an environment variable or constructor
        # argument silently overriding half of it.
        resolved_endpoint = stored.get("endpoint") or DEFAULT_ENDPOINT
        # Defense in depth: a credential file predating this security policy, or one edited/
        # corrupted on disk, must fail closed here rather than silently resume sending telemetry
        # (and API-key-bearing requests) to a remote plaintext address.
        if not is_endpoint_allowed(resolved_endpoint):
            raise RuntimeError(
                "Kairon endpoint rejected: plain HTTP is only allowed to localhost/127.0.0.0/8/::1. "
                "Use HTTPS for any non-local KAIRON backend."
            )
        pending_pairing_id = stored.get("pendingConfirmationPairingId")
        if pending_pairing_id and stored.get("apiKey"):
            # Recovers a confirmation whose earlier attempt was lost (network failure, or the
            # process exited before it could run) - never re-redeems a code, never generates one:
            # the stored credential's own presence is what makes retrying confirmation safe here.
            if _confirm_pairing(stored["endpoint"], pending_pairing_id, stored["apiKey"]):
                _credential_store.save_stored_config(stored["endpoint"], stored["projectId"], stored["apiKey"], path)
        return resolved_endpoint, stored["projectId"], stored.get("apiKey")

    # First-time onboarding: no pairing code, nothing stored yet - project_id has always been
    # required; api_key has always been optional (some deployments run with no authentication at
    # all), which is the existing, still-supported "unauthenticated" configuration.
    endpoint = endpoint or os.environ.get("KAIRON_ENDPOINT")
    project_id = project_id or os.environ.get("KAIRON_PROJECT_ID")
    api_key = api_key or os.environ.get("KAIRON_API_KEY")

    if not project_id:
        raise ValueError(
            "Kairon needs a project_id. Provide it directly, set KAIRON_PROJECT_ID, pass "
            "pairing_code from a Kairon-generated pairing code, or pair once so the stored "
            "configuration can be reused."
        )

    resolved_endpoint = endpoint or DEFAULT_ENDPOINT
    if not is_endpoint_allowed(resolved_endpoint):
        raise RuntimeError(
            "Kairon endpoint rejected: plain HTTP is only allowed to localhost/127.0.0.0/8/::1. "
            "Use HTTPS for any non-local KAIRON backend."
        )
    return resolved_endpoint, project_id, api_key


def _confirm_pairing(
    endpoint: str, pairing_id: str, api_key: str, timeout_seconds: float = 10.0, attempts: int = 2,
) -> bool:
    """Proves to the backend that this SDK actually received and is about to persist the newly
    issued credential - the only trustworthy signal of that, distinct from the backend merely
    having ISSUED the credential (a redeem response can be lost in transit, or this process could
    crash between receiving it and writing it to disk). A confirmation failure must never block
    onboarding or raise - the credential is already saved and fully usable either way - so this
    always returns a bool rather than propagating an exception. It only means an operator-triggered
    re-pair completion (which revokes the credential being replaced) keeps waiting until a retry -
    either one of these attempts, or a later run recovering a still-pending confirmation from the
    stored credential file - lets this catch up; never that the old credential gets revoked before
    the new one is confirmed working.

    Retries a small, fixed number of times with a short linear backoff before giving up for THIS
    call - bounded, never an unbounded loop - so a single transient network blip does not need a
    full process restart to recover from; a longer-lived outage is instead recovered by the pending
    marker _resolve_configuration persists, picked up on a later run.
    """
    # Never validated only once at the top of the flow - a stored/recovered endpoint reaches this
    # call independently of pair() (e.g. retrying a pending confirmation on a later run), so it is
    # re-checked here too rather than trusted because it was checked somewhere earlier.
    if not is_endpoint_allowed(endpoint):
        return False

    body = json.dumps({"apiKey": api_key}).encode("utf-8")
    url = endpoint.rstrip("/") + f"/api/v1/sdk/pair/{pairing_id}/confirm"
    for attempt in range(max(1, attempts)):
        try:
            request = urllib.request.Request(url, data=body, method="POST", headers={"Content-Type": "application/json"})
            with _open(request, timeout=max(1.0, timeout_seconds)):
                return True
        except Exception:
            if attempt + 1 < attempts:
                time.sleep(0.5 * (attempt + 1))
    return False


class Kairon:
    def __init__(
        self,
        endpoint: Optional[str] = None,
        project_id: Optional[str] = None,
        pairing_code: Optional[str] = None,
        service: Optional[str] = None,
        application: Optional[str] = None,
        environment: Optional[str] = None,
        api_key: Optional[str] = None,
        enabled: bool = True,
        timeout_seconds: float = 5.0,
        queue_capacity: int = 1000,
        success_sample_rate: float = 1.0,
        ignored_path_prefixes: tuple = DEFAULT_IGNORED_PATH_PREFIXES,
        enable_metrics: bool = True,
        metrics_interval_seconds: float = 5.0,
        machine_id: Optional[str] = None,
        config_path: Optional[str] = None,
        normalized_telemetry: bool = False,
        batch_size: int = 25,
        delivery_attempts: int = 3,
    ) -> None:
        endpoint, project_id, api_key = _resolve_configuration(
            endpoint, project_id, api_key, pairing_code, config_path
        )
        self.endpoint = endpoint.rstrip("/")
        self.project_id = project_id
        self.machine_id = machine_id
        self.application = application or service or "python-app"
        self.service = service or self.application
        self.environment = environment or os.environ.get("KAIRON_ENVIRONMENT") or "Production"
        self.api_key = api_key
        self.enabled = enabled
        # Kept short on purpose: a slow collector must not hold the caller's thread, and a
        # dropped telemetry item is always cheaper than a delayed one.
        self.timeout_seconds = max(1.0, timeout_seconds)
        # Errors are always sent; only successful requests are sampled.
        self.success_sample_rate = min(1.0, max(0.0, success_sample_rate))
        self.ignored_path_prefixes = tuple(ignored_path_prefixes)
        self.enable_metrics = enable_metrics
        self.metrics_interval_seconds = max(1.0, float(metrics_interval_seconds))

        self._queue: "queue.Queue[tuple]" = queue.Queue(maxsize=max(1, queue_capacity))
        self._queue_lock = threading.Lock()
        self._start_lock = threading.Lock()
        self._dropped_count = 0
        self._delivered_count = 0
        self._failed_count = 0
        self.last_delivery_error: Optional[str] = None
        self._outstanding = 0
        self._delivery = threading.Condition()
        self._closed = False
        self._sender_stop = threading.Event()
        self._thread: Optional[threading.Thread] = None
        self._metrics_thread: Optional[threading.Thread] = None
        self._stop_event = threading.Event()
        self._sampler = random.Random()
        self._metrics_lock = threading.Lock()
        self._metric_requests = 0
        self._metric_errors = 0
        self._metric_duration_ms = 0
        self.last_shutdown_drained: Optional[bool] = None
        # Existing direct Kairon(...) callers keep the legacy wire contract. The one-call web
        # integrations opt into the backend's idempotent normalized batch contract below.
        self.normalized_telemetry = bool(normalized_telemetry and machine_id is None)
        self.batch_size = min(200, max(1, int(batch_size)))
        self.delivery_attempts = min(5, max(1, int(delivery_attempts)))

    @classmethod
    def attach(
        cls,
        app,
        *,
        pairing_code: Optional[str] = None,
        endpoint: Optional[str] = None,
        project_id: Optional[str] = None,
        service: Optional[str] = None,
        application: Optional[str] = None,
        environment: Optional[str] = None,
        api_key: Optional[str] = None,
        enabled: bool = True,
        timeout_seconds: float = 5.0,
        queue_capacity: int = 1000,
        success_sample_rate: float = 1.0,
        ignored_path_prefixes: tuple = DEFAULT_IGNORED_PATH_PREFIXES,
        enable_metrics: bool = True,
        metrics_interval_seconds: float = 5.0,
        machine_id: Optional[str] = None,
        config_path: Optional[str] = None,
        shutdown_timeout_seconds: float = 5.0,
        batch_size: int = 25,
        delivery_attempts: int = 3,
    ) -> "Kairon":
        """Attach Kairon to a supported FastAPI/Starlette or Flask application.

        FastAPI/Starlette retain their lifespan and middleware. Flask wraps its real WSGI app;
        because WSGI defines no portable shutdown hook, use ``app.wsgi_app.close()`` during
        server shutdown for a bounded drain (a best-effort process-exit fallback is registered).
        The returned collector remains available for advanced/manual recording.

        A pairing code is still an explicit security decision. When omitted, the same credential
        resolver used by the manual API reuses a completed stored connection. Remote first contact
        still requires an explicit HTTPS endpoint (argument or ``KAIRON_ENDPOINT``).
        """
        from .adapters import attach_application

        return attach_application(
            cls, app, pairing_code=pairing_code, endpoint=endpoint,
            project_id=project_id, service=service, application=application,
            environment=environment, api_key=api_key, enabled=enabled,
            timeout_seconds=timeout_seconds, queue_capacity=queue_capacity,
            success_sample_rate=success_sample_rate,
            ignored_path_prefixes=ignored_path_prefixes,
            enable_metrics=enable_metrics,
            metrics_interval_seconds=metrics_interval_seconds,
            machine_id=machine_id, config_path=config_path,
            shutdown_timeout_seconds=shutdown_timeout_seconds,
            normalized_telemetry=True,
            batch_size=batch_size, delivery_attempts=delivery_attempts,
        )

    @classmethod
    def wrap_asgi(cls, app, *, shutdown_timeout_seconds: float = 5.0, **kwargs):
        """Wrap an ASGI 3 application; preserve its HTTP and lifespan messages."""
        from .adapters import wrap_asgi
        kwargs.setdefault("normalized_telemetry", True)
        return wrap_asgi(cls, app, shutdown_timeout_seconds=shutdown_timeout_seconds, **kwargs)

    @classmethod
    def wrap_wsgi(cls, app, *, shutdown_timeout_seconds: float = 5.0, **kwargs):
        """Wrap a WSGI application; call ``close()`` for a bounded final drain."""
        from .adapters import wrap_wsgi
        kwargs.setdefault("normalized_telemetry", True)
        return wrap_wsgi(cls, app, shutdown_timeout_seconds=shutdown_timeout_seconds, **kwargs)

    @classmethod
    def _attach_starlette(
        cls, app, *, pairing_code, endpoint, project_id, service, application,
        environment, api_key, enabled, timeout_seconds, queue_capacity,
        success_sample_rate, ignored_path_prefixes, enable_metrics,
        metrics_interval_seconds, machine_id, config_path, shutdown_timeout_seconds,
        normalized_telemetry, batch_size, delivery_attempts,
    ):
        from .middleware import KaironMiddleware

        if getattr(getattr(app, "state", None), "_kairon_sdk_attached", False):
            raise RuntimeError("Kairon is already attached to this application.")
        if getattr(app, "middleware_stack", None) is not None:
            raise RuntimeError("Call Kairon.attach(app) before the application starts.")
        if (
            not hasattr(app, "add_middleware")
            or not hasattr(app, "state")
            or not hasattr(getattr(app, "router", None), "lifespan_context")
        ):
            raise TypeError("Kairon.attach requires a FastAPI or Starlette application.")

        inferred_application = application
        if inferred_application is None:
            title = getattr(app, "title", None)
            if isinstance(title, str) and title.strip():
                inferred_application = title.strip()

        collector = cls(
            endpoint=endpoint,
            project_id=project_id,
            pairing_code=pairing_code,
            service=service,
            application=inferred_application,
            environment=environment,
            api_key=api_key,
            enabled=enabled,
            timeout_seconds=timeout_seconds,
            queue_capacity=queue_capacity,
            success_sample_rate=success_sample_rate,
            ignored_path_prefixes=ignored_path_prefixes,
            enable_metrics=enable_metrics,
            metrics_interval_seconds=metrics_interval_seconds,
            machine_id=machine_id,
            config_path=config_path,
            normalized_telemetry=normalized_telemetry,
            batch_size=batch_size, delivery_attempts=delivery_attempts,
        )

        original_lifespan = app.router.lifespan_context

        @asynccontextmanager
        async def kairon_lifespan(lifespan_app):
            collector.start()
            try:
                async with original_lifespan(lifespan_app) as state:
                    yield state
            finally:
                try:
                    collector.last_shutdown_drained = await asyncio.to_thread(
                        collector.stop, shutdown_timeout_seconds
                    )
                except Exception:
                    collector.last_shutdown_drained = False
                    # Telemetry cleanup remains fail-open, just like request delivery. Never emit
                    # exception text here: it could include host/application data.
                    _logger.warning("kairon: shutdown cleanup failed")

        app.add_middleware(KaironMiddleware, kairon=collector)
        app.router.lifespan_context = kairon_lifespan
        app.state._kairon_sdk_attached = True
        return collector

    # --- lifecycle -------------------------------------------------------------------

    def start(self) -> "Kairon":
        """Starts the background sender thread and registers this as the process-wide default
        instance for KaironMiddleware() called with no explicit instance."""
        with self._start_lock:
            if self._closed:
                return self  # A stopped collector is closed; create a new instance.
            if self._thread is None:
                self._stop_event.clear()
                self._thread = threading.Thread(target=self._run, name="kairon-sender", daemon=True)
                self._thread.start()
            if self.enabled and self.enable_metrics and self._metrics_thread is None:
                self._metrics_thread = threading.Thread(
                    target=self._run_metrics, name="kairon-metrics", daemon=True
                )
                self._metrics_thread.start()

            global _default_instance
            _default_instance = self
        return self

    def flush(self, timeout_seconds: float = 5.0) -> bool:
        """Wait for queued AND in-flight sends. False on timeout or any lifetime loss.
        Call after producers stop for a final delivery result. Does not retry requests.
        """
        deadline = time.monotonic() + max(0.0, timeout_seconds)
        with self._delivery:
            while self._outstanding:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    return False
                self._delivery.wait(remaining)
            return self._failed_count == 0 and self._dropped_count == 0

    def stop(self, timeout_seconds: float = 5.0) -> bool:
        """Stop producers and attempt a bounded drain; never guarantee delivery on timeout."""
        with self._start_lock:
            with self._queue_lock:
                self._closed = True
        self._stop_event.set()
        drained = self.flush(timeout_seconds)
        self._sender_stop.set()
        # Account for queued work that can no longer be delivered after the deadline.
        # A request already in flight finishes independently under its transport timeout.
        with self._queue_lock:
            while True:
                try:
                    self._queue.get_nowait()
                except queue.Empty:
                    break
                self._dropped_count += 1
                self._finish()
        global _default_instance
        if _default_instance is self:
            _default_instance = None
        return drained

    @property
    def delivered_count(self) -> int:
        return self._delivered_count

    @property
    def is_started(self) -> bool:
        """Whether this collector is currently accepting and delivering telemetry."""
        return not self._closed and self._thread is not None and self._thread.is_alive()

    @property
    def failed_count(self) -> int:
        return self._failed_count

    def _finish(self, delivered=None):
        with self._delivery:
            if delivered is True:
                self._delivered_count += 1
            elif delivered is False:
                self._failed_count += 1
            self._outstanding -= 1
            self._delivery.notify_all()

    @property
    def dropped_count(self) -> int:
        return self._dropped_count

    @property
    def pending_count(self) -> int:
        return self._queue.qsize()

    # --- public recording API ---------------------------------------------------------

    def capture_exception(
        self,
        exc: BaseException,
        endpoint: str = "",
        method: str = "",
        status_code: int = 500,
        duration_ms: int = 0,
    ) -> None:
        """Manually records an exception as an incident. Framework middleware (see
        kairon.middleware) calls the same underlying enqueue automatically for HTTP apps; this is
        for background workers, scheduled jobs, or anywhere there is no request to instrument."""
        if not self.enabled:
            return
        self._enqueue_telemetry(
            {
                "ApplicationName": self.application,
                "Environment": self.environment,
                "Service": self.service,
                "Endpoint": _scrub_text(endpoint),
                "Method": method,
                "StatusCode": status_code,
                "Duration": duration_ms,
                "Error": _scrub_text(str(exc)),
                "ExceptionType": type(exc).__name__,
                "StackTrace": format_exception(exc),
                "Timestamp": _utcnow_iso(),
            }
        )

    def record_metric(
        self,
        cpu_percent: Optional[float] = None,
        memory_percent: Optional[float] = None,
        response_time_ms: Optional[float] = None,
        request_count: int = 0,
        error_count: int = 0,
        retry_count: Optional[int] = None,
        queue_depth: Optional[int] = None,
        component: Optional[str] = None,
    ) -> None:
        if not self.enabled:
            return
        self._enqueue_metric(
            {
                "Timestamp": _utcnow_iso(),
                "CpuPercent": cpu_percent,
                "MemoryPercent": memory_percent,
                "ResponseTimeMs": response_time_ms,
                "RequestCount": request_count,
                "ErrorCount": error_count,
                "RetryCount": retry_count,
                "QueueDepth": queue_depth,
                "Environment": self.environment,
                "Application": self.application,
                "Service": self.service,
                "Component": component,
            }
        )

    def should_sample(self) -> bool:
        if self.success_sample_rate >= 1.0:
            return True
        if self.success_sample_rate <= 0:
            return False
        return self._sampler.random() < self.success_sample_rate

    def is_ignored(self, path: str) -> bool:
        return any(path.startswith(prefix) for prefix in self.ignored_path_prefixes)

    def record_http_request(
        self, method: str, path: str, status_code: int, duration_ms: int,
        exception: Optional[BaseException] = None, request_id: Optional[str] = None,
    ) -> None:
        """The framework-independent HTTP observation boundary.

        Adapters supply only safe metadata, never headers, query strings or bodies. Request IDs
        are accepted only as short UUIDs to avoid forwarding arbitrary header contents.
        """
        try:
            if not self.enabled or self.is_ignored(path):
                return
            is_error = exception is not None or status_code >= 500
            self._record_request(duration_ms, is_error)
            if not is_error and not self.should_sample():
                return
            safe_request_id = None
            if request_id:
                try:
                    safe_request_id = str(UUID(request_id))
                except (ValueError, TypeError, AttributeError):
                    pass
            self._enqueue_telemetry({
                "ApplicationName": self.application,
                "Environment": self.environment,
                "Service": self.service,
                "Endpoint": _scrub_text(path),
                "Method": method,
                "StatusCode": status_code,
                "Duration": duration_ms,
                "Error": _scrub_text(str(exception)) if exception else None,
                "ExceptionType": type(exception).__name__ if exception else None,
                "StackTrace": format_exception(exception) if exception else None,
                "RequestId": safe_request_id,
                "Timestamp": _utcnow_iso(),
            })
        except Exception:
            # Observation never changes the host response or exception.
            pass

    def _record_request(self, duration_ms: int, is_error: bool) -> None:
        """Accumulates middleware request measurements for the automatic metrics sample."""
        if not self.enabled or not self.enable_metrics:
            return
        with self._metrics_lock:
            self._metric_requests += 1
            self._metric_duration_ms += max(0, int(duration_ms))
            if is_error:
                self._metric_errors += 1

    def _drain_request_metrics(self) -> tuple[int, int, Optional[float]]:
        with self._metrics_lock:
            requests = self._metric_requests
            errors = self._metric_errors
            duration = self._metric_duration_ms
            self._metric_requests = 0
            self._metric_errors = 0
            self._metric_duration_ms = 0
        latency = round(duration / requests, 1) if requests else None
        return requests, errors, latency

    # --- internal enqueue (also used by kairon.middleware) -----------------------------

    def _enqueue_telemetry(self, payload: dict) -> bool:
        payload["ProjectId"] = self.project_id
        payload["MachineId"] = self.machine_id
        if self.normalized_telemetry:
            payload["_EventId"] = str(uuid4())
        return self._enqueue(("api/telemetry/incidents", payload))

    def _enqueue_metric(self, payload: dict) -> bool:
        payload["ProjectId"] = self.project_id
        payload["MachineId"] = self.machine_id
        if self.normalized_telemetry:
            payload["_EventId"] = str(uuid4())
        return self._enqueue(("api/telemetry/metrics", payload))

    def _enqueue(self, item: tuple) -> bool:
        try:
            with self._queue_lock:
                if self._closed:
                    self._dropped_count += 1
                    return False
                if self._queue.full():
                    try:
                        self._queue.get_nowait()
                        self._dropped_count += 1
                        self._finish()
                    except queue.Empty:
                        pass
                with self._delivery:
                    self._outstanding += 1
                try:
                    self._queue.put_nowait(item)
                except Exception:
                    self._dropped_count += 1
                    self._finish()
                    return False
            return True
        except Exception:
            # Even an unexpected queue fault remains visible without affecting the host.
            self._dropped_count += 1
            return False

    # --- background send loop -----------------------------------------------------------

    def _run(self) -> None:
        while not self._sender_stop.is_set():
            try:
                path, payload = self._queue.get(timeout=0.5)
            except queue.Empty:
                continue

            if self.normalized_telemetry:
                items = [(path, payload)]
                while len(items) < self.batch_size and not self._sender_stop.is_set():
                    try:
                        items.append(self._queue.get(timeout=0.02))
                    except queue.Empty:
                        break
                if self._sender_stop.is_set():
                    for _ in items:
                        self._dropped_count += 1
                        self._finish()
                    continue
                delivered = False
                try:
                    delivered = self._send_normalized(items)
                except Exception:
                    self._delivery_failure("Transport or serialization failure")
                finally:
                    if isinstance(delivered, list) and len(delivered) == len(items):
                        for result in delivered:
                            self._finish(result is True)
                    else:
                        for _ in items:
                            self._finish(delivered is True)
                continue

            if self._sender_stop.is_set():
                self._dropped_count += 1
                self._finish()
                continue
            delivered = False
            try:
                delivered = self._send(path, payload)
            except Exception:
                pass
            finally:
                self._finish(delivered is True)

    def _normalized_event(self, path: str, payload: dict) -> dict:
        metric = path.endswith("/metrics")
        application = (payload.get("ApplicationName") or payload.get("Application") or self.application)[:200]
        event = {
            "EventId": payload["_EventId"], "ProjectId": self.project_id,
            "Timestamp": payload.get("Timestamp") or _utcnow_iso(),
            "EventType": "metric" if metric else "http",
            "Severity": "Error" if payload.get("ExceptionType") or (payload.get("StatusCode") or 0) >= 500 else "Information",
            "Source": "python-sdk", "Application": application,
            "Service": (payload.get("Service") or self.service)[:200],
            "Environment": (payload.get("Environment") or self.environment)[:100],
            "Runtime": "Python " + ".".join(map(str, sys.version_info[:2])),
            "SourceVersion": "1.1.0", "RequestId": payload.get("RequestId"),
        }
        if metric:
            event["ResourceMetrics"] = {
                "CpuPercent": payload.get("CpuPercent"),
                "MemoryPercent": payload.get("MemoryPercent"),
                "ResponseTimeMs": payload.get("ResponseTimeMs"),
                "RequestCount": payload.get("RequestCount") or 0,
                "ErrorCount": payload.get("ErrorCount") or 0,
                "RetryCount": payload.get("RetryCount"),
                "QueueDepth": payload.get("QueueDepth"),
            }
        else:
            event["Message"] = (payload.get("Error") or "")[:4000] or None
            event["ExceptionType"] = (payload.get("ExceptionType") or "")[:500] or None
            event["StackTrace"] = (payload.get("StackTrace") or "")[:8000] or None
            event["HttpContext"] = {
                "Endpoint": (payload.get("Endpoint") or "")[:500],
                "Method": (payload.get("Method") or "GET")[:10],
                "StatusCode": payload.get("StatusCode") or 0,
                "DurationMs": payload.get("Duration") or 0,
            }
        return event

    def _send_normalized(self, items: list[tuple]):
        if not is_endpoint_allowed(self.endpoint):
            return self._delivery_failure("Endpoint rejected: insecure transport")
        try:
            events = [self._normalized_event(path, payload) for path, payload in items]
            body = json.dumps({"events": events}).encode("utf-8")
        except (ValueError, TypeError, KeyError):
            return self._delivery_failure("Serialization failure")
        if len(body) > 1_048_576:
            return self._delivery_failure("Telemetry batch exceeds backend size limit")
        for attempt in range(self.delivery_attempts):
            request = urllib.request.Request(
                self.endpoint + "/api/v1/telemetry/events", data=body, method="POST",
                headers={"Content-Type": "application/json"},
            )
            if self.api_key:
                request.add_header("X-Kairon-API-Key", self.api_key)
            try:
                with _open(request, timeout=self.timeout_seconds) as response:
                    result = json.loads(response.read(65537))
                    if (isinstance(result, dict)
                            and result.get("accepted", 0) + result.get("duplicates", 0) == len(events)
                            and result.get("rejected") == 0):
                        self.last_delivery_error = None
                        return True
                    if (isinstance(result, dict) and len(items) > 1
                            and all(isinstance(result.get(key), int) and result[key] >= 0
                                    for key in ("accepted", "duplicates", "rejected"))
                            and result["accepted"] + result["duplicates"] + result["rejected"] == len(items)
                            and result["rejected"] > 0):
                        # Recheck members individually. Previously accepted members come back as
                        # duplicates by EventId; invalid members fail alone. This preserves
                        # accurate counters without replaying an unsafe legacy request.
                        member_results = []
                        first_error = None
                        for item in items:
                            ok = self._send_normalized([item]) is True
                            member_results.append(ok)
                            if not ok and first_error is None:
                                first_error = self.last_delivery_error
                        self.last_delivery_error = first_error
                        return member_results
                    return self._delivery_failure("Collector rejected telemetry batch")
            except RedirectNotAllowedError as exc:
                return self._delivery_failure("Endpoint rejected: HTTP " + str(exc.status) + " redirect not followed")
            except urllib.error.HTTPError as exc:
                status = exc.code
                exc.close()
                if status not in (429, 500, 502, 503, 504) or attempt + 1 == self.delivery_attempts:
                    return self._delivery_failure("HTTP " + str(status))
            except (urllib.error.URLError, TimeoutError, OSError):
                if attempt + 1 == self.delivery_attempts:
                    return self._delivery_failure("Transport failure")
            except (ValueError, TypeError):
                return self._delivery_failure("Malformed collector response")
            time.sleep(min(0.1 * (attempt + 1), 0.5))
        return False

    def _run_metrics(self) -> None:
        last_wall = time.perf_counter()
        last_cpu = time.process_time()

        while not self._stop_event.wait(self.metrics_interval_seconds):
            try:
                now_wall = time.perf_counter()
                now_cpu = time.process_time()
                elapsed = max(now_wall - last_wall, 0.001)
                cpu_percent = min(
                    100.0,
                    max(0.0, ((now_cpu - last_cpu) / elapsed) * 100 / (os.cpu_count() or 1)),
                )
                last_wall = now_wall
                last_cpu = now_cpu

                requests, errors, latency = self._drain_request_metrics()
                self.record_metric(
                    cpu_percent=round(cpu_percent, 1),
                    memory_percent=_process_memory_percent(),
                    response_time_ms=latency,
                    request_count=requests,
                    error_count=errors,
                    component=self.service,
                )
            except Exception:
                # Automatic metrics are best-effort and must never affect the host application.
                _logger.debug("kairon: failed to collect process metrics", exc_info=True)

    def _send(self, path: str, payload: dict) -> bool:
        if not self.enabled:
            return False
        # Final defense-in-depth gate, right at actual network egress: self.endpoint is a plain
        # public attribute (nothing stops host code from reassigning it after construction), so
        # every send re-checks it rather than trusting whatever validation ran once at __init__.
        if not is_endpoint_allowed(self.endpoint):
            return self._delivery_failure("Endpoint rejected: insecure transport")
        try:
            request = urllib.request.Request(
                f"{self.endpoint}/{path}", data=json.dumps(payload, default=str).encode("utf-8"),
                method="POST", headers={"Content-Type": "application/json"})
            if self.api_key:
                request.add_header("X-Kairon-API-Key", self.api_key)
            with _open(request, timeout=self.timeout_seconds) as response:
                if not 200 <= response.status < 300:
                    return self._delivery_failure("HTTP " + str(response.status))
                try:
                    body = json.loads(response.read(65536))
                    if isinstance(body, dict) and body.get("success") is False:
                        return self._delivery_failure("Collector rejected telemetry")
                    self.last_delivery_error = None
                    return True
                except (ValueError, UnicodeError):
                    self.last_delivery_error = None
                    return True  # HTTP acceptance; informational body is not database proof.
        except RedirectNotAllowedError as exc:
            # Fixed category only - the redirect target is attacker-controlled and never recorded
            # here, exactly like every other value kept out of last_delivery_error.
            return self._delivery_failure("Endpoint rejected: HTTP " + str(exc.status) + " redirect not followed")
        except urllib.error.HTTPError as exc:
            status = exc.code
            exc.close()
            return self._delivery_failure("HTTP " + str(status) + (" (project authentication rejected)" if status in (401, 403) else ""))
        except Exception:
            return self._delivery_failure("Transport or serialization failure")

    def _delivery_failure(self, message: str) -> bool:
        # Only fixed categories/status codes; never URL, payload, credential or exception text.
        self.last_delivery_error = message
        return False


def _process_memory_percent() -> Optional[float]:
    """Returns this process's resident-memory share using only the Python standard library."""
    try:
        if os.name == "nt":
            import ctypes

            class MemoryStatusEx(ctypes.Structure):
                _fields_ = [
                    ("length", ctypes.c_ulong),
                    ("memory_load", ctypes.c_ulong),
                    ("total_physical", ctypes.c_ulonglong),
                    ("available_physical", ctypes.c_ulonglong),
                    ("total_page_file", ctypes.c_ulonglong),
                    ("available_page_file", ctypes.c_ulonglong),
                    ("total_virtual", ctypes.c_ulonglong),
                    ("available_virtual", ctypes.c_ulonglong),
                    ("available_extended_virtual", ctypes.c_ulonglong),
                ]

            class ProcessMemoryCounters(ctypes.Structure):
                _fields_ = [
                    ("cb", ctypes.c_ulong),
                    ("page_fault_count", ctypes.c_ulong),
                    ("peak_working_set_size", ctypes.c_size_t),
                    ("working_set_size", ctypes.c_size_t),
                    ("quota_peak_paged_pool_usage", ctypes.c_size_t),
                    ("quota_paged_pool_usage", ctypes.c_size_t),
                    ("quota_peak_non_paged_pool_usage", ctypes.c_size_t),
                    ("quota_non_paged_pool_usage", ctypes.c_size_t),
                    ("pagefile_usage", ctypes.c_size_t),
                    ("peak_pagefile_usage", ctypes.c_size_t),
                ]

            kernel32 = ctypes.windll.kernel32
            psapi = ctypes.windll.psapi
            kernel32.GetCurrentProcess.restype = ctypes.c_void_p
            kernel32.GlobalMemoryStatusEx.argtypes = [ctypes.POINTER(MemoryStatusEx)]
            kernel32.GlobalMemoryStatusEx.restype = ctypes.c_int
            psapi.GetProcessMemoryInfo.argtypes = [
                ctypes.c_void_p,
                ctypes.POINTER(ProcessMemoryCounters),
                ctypes.c_ulong,
            ]
            psapi.GetProcessMemoryInfo.restype = ctypes.c_int

            system = MemoryStatusEx()
            system.length = ctypes.sizeof(system)
            process = ProcessMemoryCounters()
            process.cb = ctypes.sizeof(process)
            if not kernel32.GlobalMemoryStatusEx(ctypes.byref(system)):
                return None
            if not psapi.GetProcessMemoryInfo(
                kernel32.GetCurrentProcess(), ctypes.byref(process), process.cb
            ):
                return None
            return round((process.working_set_size / system.total_physical) * 100, 1)

        if os.path.exists("/proc/self/statm"):
            with open("/proc/self/statm", encoding="ascii") as statm:
                resident_pages = int(statm.read().split()[1])
            total_pages = int(os.sysconf("SC_PHYS_PAGES"))
            return round((resident_pages / total_pages) * 100, 1) if total_pages > 0 else None
    except (AttributeError, IndexError, OSError, TypeError, ValueError):
        pass
    return None
