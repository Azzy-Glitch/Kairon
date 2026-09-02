"""Kairon AI service.

FastAPI front end over the provider-independent intelligence layer in the `kairon` package.

Every endpoint the original service exposed is still here with the same route and the same
request/response shape (AI PRD section 21), plus the new structured `/analyze` contract the
Autonomous SRE backend consumes.
"""

from __future__ import annotations

import json
import logging
import os
import re

from dotenv import load_dotenv
from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse

from kairon.config import AiConfig
from kairon.schemas import (
    ContextReq,
    EvidencePackage,
    InvestigationResult,
    LogReq,
    MismatchReq,
    PredictReq,
)
from kairon.providers import available_providers
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
