"""Providers that speak the OpenAI chat-completions dialect.

Qwen (DashScope compatible-mode) and Groq both expose that shape, so they share one transport and
differ only in endpoint, credential and default model. That is the whole reason the abstraction
exists: adding Groq cost an endpoint constant, not an integration.
"""

from __future__ import annotations

import httpx

from ..config import DEFAULT_ENDPOINTS, DEFAULT_MODELS
from .base import AIProvider, ProviderError


class OpenAICompatibleProvider(AIProvider):
    """Shared transport for OpenAI-style /chat/completions endpoints."""

    def _api_key(self) -> str:
        return self.config.key_for(self.name)

    def _endpoint(self) -> str:
        return self.config.endpoint or DEFAULT_ENDPOINTS.get(self.name, "")

    async def _invoke(self, system: str, user: str) -> str:
        api_key = self._api_key()
        if not api_key:
            raise ProviderError(f"{self.name} API key is not configured", transient=False)

        payload = {
            "model": self.model or DEFAULT_MODELS.get(self.name, ""),
            "messages": [
                {"role": "system", "content": system},
                {"role": "user", "content": user},
            ],
            "temperature": self.config.temperature,
            "max_tokens": max(1, self.config.max_output_tokens),
        }

        try:
            async with httpx.AsyncClient(timeout=self.config.timeout_seconds) as client:
                response = await client.post(
                    self._endpoint(),
                    headers={
                        "Authorization": f"Bearer {api_key}",
                        "Content-Type": "application/json",
                    },
                    json=payload,
                )
        except httpx.TimeoutException as exc:
            raise ProviderError(f"{self.name} request timed out", transient=True) from exc
        except httpx.HTTPError as exc:
            raise ProviderError(f"{self.name} transport error: {type(exc).__name__}", transient=True) from exc

        if response.status_code >= 400:
            # 408/429/5xx are worth another attempt; 401/403/404 are configuration problems that
            # will fail identically every time.
            transient = response.status_code in (408, 409, 425, 429) or response.status_code >= 500
            raise ProviderError(
                f"{self.name} returned HTTP {response.status_code}",
                transient=transient,
                status_code=response.status_code,
            )

        if len(response.content) > max(1024, self.config.max_response_bytes):
            raise ProviderError(f"{self.name} response exceeded the configured size limit", transient=False)

        try:
            body = response.json()
            return body["choices"][0]["message"]["content"]
        except (KeyError, IndexError, TypeError, ValueError) as exc:
            raise ProviderError(f"{self.name} returned an unexpected response shape", transient=True) from exc


class QwenProvider(OpenAICompatibleProvider):
    name = "qwen"


class GroqProvider(OpenAICompatibleProvider):
    name = "groq"
