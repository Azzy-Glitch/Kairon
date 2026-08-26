"""The AI intelligence layer (AI PRD section 5).

This is where evidence becomes a validated, structured investigation. It owns prompting,
provider invocation, validation and normalization - and nothing else. It never persists anything,
never decides an incident's lifecycle, and never executes a remediation (AI PRD section 17).
"""

from __future__ import annotations

import asyncio
import logging
import time
from typing import Any

from .config import AiConfig
from .prompts import (
    ERROR_ANALYSIS_SYSTEM,
    FIX_SUGGESTION_SYSTEM,
    PREDICTION_SYSTEM,
    RECOMMENDATION_SYSTEM,
    investigation_system_prompt,
    investigation_user_prompt,
)
from .providers import AIProvider, ProviderError, create_provider
from .schemas import EvidencePackage, InvestigationResult
from .validation import (
    AiResponseError,
    validate_error_analysis,
    validate_fix_suggestions,
    validate_investigation,
    validate_prediction,
    validate_recommendations,
)

logger = logging.getLogger("kairon.service")


class AiServiceError(RuntimeError):
    """A controlled failure the API layer turns into a clean HTTP error.

    Distinct from a provider exception so the caller can tell "the model misbehaved" apart from
    "the service is broken".
    """

    def __init__(self, message: str, *, status_code: int = 502, code: str = "ai_error"):
        super().__init__(message)
        self.status_code = status_code
        self.code = code


class AiService:
    def __init__(self, config: AiConfig | None = None, provider: AIProvider | None = None):
        self.config = config or AiConfig.from_env()
        # An injected provider is how tests exercise the full pipeline without any network.
        self.provider = provider or create_provider(self.config)

        # Caps concurrent model calls so a burst of incidents cannot open unbounded connections
        # (AI PRD section 20).
        self._gate = asyncio.Semaphore(4)

    @property
    def mode(self) -> str:
        return "mock" if self.provider.name == "mock" else "live"

    async def investigate(self, evidence: EvidencePackage) -> InvestigationResult:
        """Full investigation: evidence in, validated structured diagnosis out."""
        system = investigation_system_prompt()
        user = investigation_user_prompt(evidence)

        raw = await self._complete(system, user, task="investigate")

        try:
            result = validate_investigation(
                raw,
                evidence=evidence,
                provider=self.provider.name,
                model=self.provider.model,
            )
        except AiResponseError as exc:
            # Refusing the response is the correct outcome. Writing unvalidated model text into an
            # incident would be worse than having no diagnosis at all (AI PRD section 8).
            logger.warning(
                "provider=%s model=%s task=investigate status=rejected reason=%s incident=%s",
                self.provider.name, self.provider.model, exc, evidence.incident.incident_key,
            )
            raise AiServiceError(
                f"AI response rejected: {exc}", status_code=502, code="ai_response_rejected"
            ) from exc

        logger.info(
            "provider=%s model=%s task=investigate status=ok incident=%s confidence=%.2f recommendations=%d",
            self.provider.name, self.provider.model, evidence.incident.incident_key,
            result.confidence, len(result.recommendations),
        )

        return result

    async def analyze_error(self, log: str) -> dict:
        raw = await self._complete(ERROR_ANALYSIS_SYSTEM, f"Analyze this error:{log}", task="analyze-error")
        return self._validate(validate_error_analysis, raw, "analyze-error")

    async def predict(self, recent_logs: list, current_log: str) -> dict:
        user = "Recent logs:" + "".join(str(x) for x in recent_logs) + f"Current:{current_log}"
        raw = await self._complete(PREDICTION_SYSTEM, user, task="predict")
        return self._validate(validate_prediction, raw, "predict")

    async def recommend(self, context: str) -> dict:
        raw = await self._complete(RECOMMENDATION_SYSTEM, f"Context:{context}", task="recommend")
        return self._validate(validate_recommendations, raw, "recommend")

    async def suggest_fixes(self, mismatches: list) -> dict:
        import json

        user = f"Fix these mismatches:{json.dumps(mismatches, default=str)}"
        raw = await self._complete(FIX_SUGGESTION_SYSTEM, user, task="suggest-fixes")
        return self._validate(validate_fix_suggestions, raw, "suggest-fixes")

    async def _complete(self, system: str, user: str, *, task: str) -> Any:
        started = time.monotonic()

        async with self._gate:
            try:
                return await self.provider.complete_json(system, user)
            except asyncio.CancelledError:
                logger.info("provider=%s task=%s status=cancelled", self.provider.name, task)
                raise
            except ProviderError as exc:
                elapsed_ms = int((time.monotonic() - started) * 1000)
                logger.warning(
                    "provider=%s model=%s task=%s status=failed duration_ms=%d transient=%s",
                    self.provider.name, self.provider.model, task, elapsed_ms, exc.transient,
                )
                # Provider failures are isolated here: they become a controlled 503 rather than an
                # unhandled exception (AI PRD section 12).
                raise AiServiceError(
                    f"AI provider '{self.provider.name}' is unavailable: {exc}",
                    status_code=503,
                    code="ai_provider_unavailable",
                ) from exc

    @staticmethod
    def _validate(validator, raw: Any, task: str) -> dict:
        try:
            return validator(raw)
        except AiResponseError as exc:
            logger.warning("task=%s status=rejected reason=%s", task, exc)
            raise AiServiceError(
                f"AI response rejected: {exc}", status_code=502, code="ai_response_rejected"
            ) from exc
