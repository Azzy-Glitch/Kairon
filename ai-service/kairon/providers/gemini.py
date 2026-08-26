"""Google Gemini provider (AI PRD section 2: "Gemini must be added").

Gemini does not speak the OpenAI dialect, so it gets its own transport. Everything above this
class is unchanged by that - which is the point of the provider contract.
"""

from __future__ import annotations

import httpx

from ..config import DEFAULT_ENDPOINTS, DEFAULT_MODELS
from .base import AIProvider, ProviderError


class GeminiProvider(AIProvider):
    name = "gemini"

    def _endpoint(self) -> str:
        base = self.config.endpoint or DEFAULT_ENDPOINTS["gemini"]
        model = self.model or DEFAULT_MODELS["gemini"]

        # An endpoint that already names a model is used verbatim, so an override can point at a
        # proxy or a pinned deployment.
        if ":generateContent" in base:
            return base
        return f"{base.rstrip('/')}/{model}:generateContent"

    async def _invoke(self, system: str, user: str) -> str:
        api_key = self.config.gemini_api_key
        if not api_key:
            raise ProviderError("Gemini API key is not configured", transient=False)

        payload = {
            # Gemini takes the system prompt separately rather than as a message role.
            "systemInstruction": {"parts": [{"text": system}]},
            "contents": [{"role": "user", "parts": [{"text": user}]}],
            "generationConfig": {
                "temperature": self.config.temperature,
                # Asking for JSON at the API level is far more reliable than asking for it in prose.
                "responseMimeType": "application/json",
            },
        }

        try:
            async with httpx.AsyncClient(timeout=self.config.timeout_seconds) as client:
                response = await client.post(
                    self._endpoint(),
                    # The key goes in a header, not the query string: query strings end up in
                    # access logs and proxy logs (AI PRD section 4).
                    headers={
                        "x-goog-api-key": api_key,
                        "Content-Type": "application/json",
                    },
                    json=payload,
                )
        except httpx.TimeoutException as exc:
            raise ProviderError("Gemini request timed out", transient=True) from exc
        except httpx.HTTPError as exc:
            raise ProviderError(f"Gemini transport error: {type(exc).__name__}", transient=True) from exc

        if response.status_code >= 400:
            transient = response.status_code in (408, 409, 425, 429) or response.status_code >= 500
            raise ProviderError(
                f"Gemini returned HTTP {response.status_code}",
                transient=transient,
                status_code=response.status_code,
            )

        try:
            body = response.json()
            parts = body["candidates"][0]["content"]["parts"]
            return "".join(part.get("text", "") for part in parts)
        except (KeyError, IndexError, TypeError, ValueError) as exc:
            raise ProviderError("Gemini returned an unexpected response shape", transient=True) from exc
