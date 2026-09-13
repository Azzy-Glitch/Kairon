"""Fallback for a selected-but-unusable configuration (AI PRD sections 13/18).

Reached only when the selected provider is either unknown or lacks a real credential, and mock
was never explicitly requested (see AiConfig.effective_provider/is_configured). Never fabricates
an answer: every call fails immediately with a clear, non-transient ProviderError, which
AiService._complete already turns into a clean 503 "unavailable" response through the exact same
path a real provider's own failure would take - no new error-handling code needed anywhere else.
"""

from __future__ import annotations

from .base import AIProvider, ProviderError


class UnconfiguredProvider(AIProvider):
    """Reports "not configured" honestly instead of silently answering with mock output."""

    name = "unconfigured"

    @property
    def requires_credentials(self) -> bool:
        return False  # nothing here ever calls out, so there is no credential to require

    async def _invoke(self, system: str, user: str) -> str:
        raise ProviderError(
            f"No usable credential is configured for provider '{self.config.provider}'. "
            "AI is unavailable until a provider is configured.",
            transient=False,
        )
