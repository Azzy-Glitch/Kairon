"""Provider abstraction (AI PRD section 3).

Every provider implements the same contract, so provider selection is a configuration value and
never a code change. Nothing above this layer knows which model is answering.
"""

from __future__ import annotations

import abc
import asyncio
import logging
import time
from typing import Any

from ..config import AiConfig
from ..validation import AiResponseError, extract_json

logger = logging.getLogger("kairon.providers")


class ProviderError(RuntimeError):
    """A provider failed to produce a usable response."""

    def __init__(self, message: str, *, transient: bool = False, status_code: int | None = None):
        super().__init__(message)
        # Transient failures (timeouts, 5xx, rate limits) are worth retrying; permanent ones
        # (bad credentials, unknown model) are not, and retrying them just burns the budget.
        self.transient = transient
        self.status_code = status_code


class AIProvider(abc.ABC):
    """One model provider behind a uniform interface."""

    name: str = "base"

    def __init__(self, config: AiConfig):
        self.config = config
        self.model = config.model or ""

    @abc.abstractmethod
    async def _invoke(self, system: str, user: str) -> str:
        """Sends one request and returns the model's raw text response."""

    @property
    def requires_credentials(self) -> bool:
        return True

    async def complete_json(self, system: str, user: str) -> Any:
        """Runs one prompt with bounded retries and returns parsed JSON.

        Resilience lives here rather than in each provider so every provider inherits identical
        timeout, retry and logging behaviour (AI PRD section 12).
        """
        attempts = max(1, self.config.max_retries + 1)
        last_error: Exception | None = None

        for attempt in range(1, attempts + 1):
            started = time.monotonic()

            try:
                raw = await self._invoke(system, user)
                elapsed_ms = int((time.monotonic() - started) * 1000)

                parsed = extract_json(raw)

                # Operational metadata only. Prompts and evidence are deliberately never logged
                # (AI PRD section 19).
                logger.info(
                    "provider=%s model=%s status=ok duration_ms=%d attempt=%d",
                    self.name,
                    self.model,
                    elapsed_ms,
                    attempt,
                )
                return parsed

            except asyncio.CancelledError:
                # Cancellation is the caller's decision, never a provider failure.
                raise

            except AiResponseError as exc:
                # Malformed output is worth one more try: the same prompt often parses on a retry.
                last_error = exc
                logger.warning(
                    "provider=%s model=%s status=malformed attempt=%d/%d error=%s",
                    self.name, self.model, attempt, attempts, exc,
                )

            except ProviderError as exc:
                last_error = exc
                logger.warning(
                    "provider=%s model=%s status=error attempt=%d/%d transient=%s http_status=%s",
                    self.name, self.model, attempt, attempts, exc.transient, exc.status_code,
                )
                if not exc.transient:
                    break

            except Exception as exc:  # noqa: BLE001 - provider failures must stay isolated
                last_error = exc
                logger.warning(
                    "provider=%s model=%s status=unexpected attempt=%d/%d error=%s",
                    self.name, self.model, attempt, attempts, type(exc).__name__,
                )

            if attempt < attempts:
                # Bounded linear backoff. Never retries indefinitely.
                await asyncio.sleep(0.25 * attempt)

        raise ProviderError(
            f"{self.name} failed after {attempts} attempt(s): {last_error}",
            transient=isinstance(last_error, (AiResponseError, ProviderError)),
        )
