"""Kairon AI service.

FastAPI front end over the provider-independent intelligence layer in the `kairon` package.

Every endpoint the original service exposed is still here with the same route and the same
request/response shape (AI PRD section 21), plus the new structured `/analyze` contract the
Autonomous SRE backend consumes.
"""

from __future__ import annotations

import dataclasses
import ipaddress
import json
import logging
import os
import re
from urllib.parse import urlparse

import httpx
from dotenv import load_dotenv
from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse

from kairon.config import DEFAULT_ENDPOINTS, DEFAULT_MODELS, AiConfig, is_placeholder
from kairon.schemas import (
    ConfigureRequest,
    ContextReq,
    EvidencePackage,
    InvestigationResult,
    LogReq,
    MismatchReq,
    PredictReq,
)
from kairon.providers import PROVIDERS, ProviderError, available_providers, create_provider
from kairon.service import AiService, AiServiceError
from kairon.security import AiApiSecurityMiddleware

load_dotenv()

logging.basicConfig(
    level=os.getenv("LOG_LEVEL", "INFO").upper(),
    format="%(asctime)s %(levelname)s %(name)s %(message)s",
)
logger = logging.getLogger("kairon.api")

app = FastAPI(
    title="Kairon AI Service",
    description="Provider-independent AI intelligence layer for Kairon Autonomous AI SRE.",
    version="1.0.1",
    docs_url=None,
    redoc_url=None,
    openapi_url=None,
)
app.add_middleware(
    AiApiSecurityMiddleware,
    api_key=os.getenv("KAIRON_AI_API_KEY", "").strip(),
    max_request_bytes=int(os.getenv("KAIRON_AI_MAX_REQUEST_BYTES", "65536")),
    requests_per_minute=int(os.getenv("KAIRON_AI_REQUESTS_PER_MINUTE", "12")),
    requests_per_hour=int(os.getenv("KAIRON_AI_REQUESTS_PER_HOUR", "120")),
)

CONFIG = AiConfig.from_env()
SERVICE = AiService(CONFIG)

logger.info(
    "AI service starting: provider=%s effective=%s model=%s mock=%s",
    CONFIG.provider, CONFIG.effective_provider, CONFIG.model, CONFIG.effective_provider == "mock",
)


@app.exception_handler(AiServiceError)
async def ai_service_error_handler(_: Request, exc: AiServiceError) -> JSONResponse:
    """Turns a controlled AI failure into a clean, secret-free HTTP error."""
    return JSONResponse(
        status_code=exc.status_code,
        content={"error": str(exc), "code": exc.code},
    )


# --- Health and configuration ---


@app.get("/health")
async def health() -> dict:
    return {"status": "healthy", "service": "kairon-ai", "mode": SERVICE.mode}


@app.get("/providers")
async def providers() -> dict:
    """Describes provider configuration. Reports whether keys are present, never what they are."""
    return {
        "available": available_providers(),
        "configured": CONFIG.public_dict(),
    }


# --- Runtime provider configuration (KAIRON frontend AI Configuration panel) ---
#
# CONFIG/SERVICE are process-wide singletons built once at import time (see below `__main__`
# guard-free module scope). Every other endpoint reads them by closing over these same names, so
# reassigning them here - rather than constructing a new AiService - is what makes a saved change
# take effect for every subsequent request without restarting this process. `global` is required
# because these names are rebound, not just mutated.


def _is_loopback_host(host: str) -> bool:
    """localhost and the loopback ranges only - never a hostname that merely looks local."""
    if host.lower() == "localhost":
        return True
    try:
        return ipaddress.ip_address(host).is_loopback
    except ValueError:
        return False


def _validate_endpoint(endpoint: str) -> None:
    """Rejects an endpoint that would send the provider API key somewhere unsafe.

    This runs inside _merge_configured, before any provider is constructed or called, so a rejected
    endpoint never reaches an outbound request and the key is never transmitted. Plain HTTP is
    refused because the Authorization header carrying the key would cross the network in clear
    text; loopback is exempt so a local or self-hosted provider (an on-box proxy, a dev gateway)
    still works without demanding a certificate for 127.0.0.1.
    """
    parsed = urlparse(endpoint)

    if parsed.scheme not in ("http", "https") or not parsed.hostname:
        raise AiServiceError(
            "Endpoint must be an absolute http(s) URL, for example "
            "https://host/compatible-mode/v1/chat/completions.",
            status_code=400,
            code="invalid_endpoint",
        )

    if parsed.scheme == "http" and not _is_loopback_host(parsed.hostname):
        raise AiServiceError(
            "Endpoint must use HTTPS. Plain HTTP would send the provider API key in clear text; "
            "only loopback addresses (localhost, 127.0.0.1, ::1) may use HTTP.",
            status_code=400,
            code="insecure_endpoint",
        )


def _merge_configured(request: ConfigureRequest) -> AiConfig:
    """Applies a ConfigureRequest onto the current CONFIG, keeping any field the caller omitted
    (notably the API key, so changing just the model never requires resending a known-good key)."""
    provider = request.provider.strip().lower()
    if provider not in PROVIDERS:
        raise AiServiceError(
            f"Unknown provider '{provider}'. Available: {', '.join(available_providers())}",
            status_code=400,
            code="unknown_provider",
        )

    model = (request.model or "").strip() or DEFAULT_MODELS.get(provider, "")
    # None and "" mean different things here, and conflating them is what let a cleared endpoint
    # keep applying at runtime: the row said "no override" while this process carried on calling the
    # old URL until the next restart.
    if request.endpoint is None:
        # Omitted entirely - keep whatever this provider already had, so a caller changing only the
        # model never silently drops a working override. A custom endpoint only makes sense for the
        # provider it was set for, so switching provider still clears it.
        endpoint = "" if provider != CONFIG.provider else CONFIG.endpoint
    else:
        # Explicitly supplied - the caller is authoritative, including when it is blank, which
        # clears the override so the provider's own default endpoint applies again.
        endpoint = request.endpoint.strip()
        if endpoint:
            _validate_endpoint(endpoint)

    updated = dataclasses.replace(CONFIG, provider=provider, model=model, endpoint=endpoint)
    new_key = (request.api_key or "").strip()
    if new_key:
        key_field = {"qwen": "qwen_api_key", "gemini": "gemini_api_key", "groq": "groq_api_key"}.get(provider)
        if key_field:
            updated = dataclasses.replace(updated, **{key_field: new_key})
    return updated


@app.post("/configure")
async def configure(request: ConfigureRequest) -> dict:
    """Applies a provider/model/key change immediately - no restart, no file edit. The previous
    key for the provider is kept when `api_key` is omitted or blank."""
    global CONFIG, SERVICE

    updated = _merge_configured(request)
    CONFIG = updated
    SERVICE.config = CONFIG
    SERVICE.provider = create_provider(CONFIG)

    logger.info(
        "AI provider reconfigured: provider=%s effective=%s model=%s",
        CONFIG.provider, CONFIG.effective_provider, CONFIG.model,
    )
    return {"applied": True, **CONFIG.public_dict()}


@app.post("/configure/test")
async def configure_test(request: ConfigureRequest) -> dict:
    """Validates a provider/key/model combination with one real call, without touching the live
    configuration - a failed test must never disturb whatever is currently serving requests."""
    candidate = _merge_configured(request)
    # A snappy, bounded check: no point retrying a bad key three times before reporting failure.
    candidate = dataclasses.replace(candidate, max_retries=0, timeout_seconds=min(candidate.timeout_seconds, 15.0))

    result = {
        "provider": candidate.provider,
        "effective_provider": candidate.effective_provider,
        "model": candidate.model,
        "endpoint": candidate.endpoint or DEFAULT_ENDPOINTS.get(candidate.provider, ""),
    }

    if candidate.effective_provider == "mock":
        # Same fallback logic every other call path uses (AI PRD section 13): no usable key means
        # this "succeeds" against the deterministic mock, and the response says so honestly rather
        # than implying a real provider was reached.
        result.update(success=True, error=None)
        return result

    try:
        provider = create_provider(candidate)
        await provider.complete_json(
            "Respond with only this exact JSON object, nothing else: {\"ok\": true}",
            "Connectivity check.",
        )
        result.update(success=True, error=None)
    except ProviderError as exc:
        result.update(success=False, error=str(exc))
    except Exception as exc:  # noqa: BLE001 - a bad test result must never crash the request
        result.update(success=False, error=f"{type(exc).__name__}: {exc}")

    return result


@app.post("/models")
async def list_models(request: ConfigureRequest) -> dict:
    """Live model discovery, Groq only (its /v1/models is simple and OpenAI-compatible; Qwen and
    Gemini don't have an equivalently trivial equivalent wired up here - the frontend falls back
    to manual model entry for those, per the brief's own guidance not to force this everywhere)."""
    provider = request.provider.strip().lower()
    if provider != "groq":
        return {"provider": provider, "supported": False, "models": []}

    api_key = (request.api_key or "").strip() or CONFIG.groq_api_key
    if is_placeholder(api_key):
        return {"provider": provider, "supported": True, "models": [], "error": "No Groq API key configured."}

    try:
        async with httpx.AsyncClient(timeout=10.0) as client:
            response = await client.get(
                "https://api.groq.com/openai/v1/models",
                headers={"Authorization": f"Bearer {api_key}"},
            )
    except httpx.HTTPError as exc:
        return {"provider": provider, "supported": True, "models": [], "error": f"{type(exc).__name__}: {exc}"}

    if response.status_code >= 400:
        return {
            "provider": provider,
            "supported": True,
            "models": [],
            "error": f"Groq returned HTTP {response.status_code}",
        }

    models = [
        m["id"]
        for m in response.json().get("data", [])
        if m.get("active") and "text" in (m.get("output_modalities") or [])
    ]
    return {"provider": provider, "supported": True, "models": sorted(models)}


# --- Autonomous SRE investigation (AI PRD section 15) ---


@app.post("/analyze", response_model=InvestigationResult)
async def analyze(evidence: EvidencePackage) -> InvestigationResult:
    """Evidence package in, validated structured investigation out.

    The result is advisory. Any recommendation it contains names a tool the backend supplied and
    still has to pass backend policy and human approval before anything runs.
    """
    return await SERVICE.investigate(evidence)


# --- Existing endpoints. Routes and payload shapes unchanged. ---


@app.post("/analyze-error")
async def analyze_error(req: LogReq) -> dict:
    return await SERVICE.analyze_error(req.log)


@app.post("/predict")
async def predict(req: PredictReq) -> dict:
    return await SERVICE.predict(req.recent_logs, req.current_log)


@app.post("/recommend")
async def recommend(req: ContextReq) -> dict:
    return await SERVICE.recommend(req.context)


@app.post("/suggest-fixes")
async def suggest_fixes(req: MismatchReq) -> dict:
    return await SERVICE.suggest_fixes(req.mismatches)


# --- Backwards-compatible helper ---
#
# The original module exposed extract_json at module scope. It is re-exported so anything that
# imported it from here keeps working; the implementation now lives in kairon.validation.

from kairon.validation import extract_json  # noqa: E402  (re-export, must follow app setup)

__all__ = ["app", "extract_json", "SERVICE", "CONFIG"]
