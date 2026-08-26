"""Provider registry and factory (AI PRD section 3).

Provider selection is a configuration lookup here, so adding a provider means adding a class and
one registry entry - never touching the service, the prompts, or the backend.
"""

from __future__ import annotations

from typing import Dict, Type

from ..config import AiConfig
from .base import AIProvider, ProviderError
from .gemini import GeminiProvider
from .mock import MockProvider
from .openai_compatible import GroqProvider, QwenProvider

PROVIDERS: Dict[str, Type[AIProvider]] = {
    "qwen": QwenProvider,
    "gemini": GeminiProvider,
    "groq": GroqProvider,
    "mock": MockProvider,
}


def available_providers() -> list[str]:
    return sorted(PROVIDERS.keys())


def create_provider(config: AiConfig) -> AIProvider:
    """Builds the configured provider.

    Falls back to the deterministic mock whenever the selected provider has no usable credential.
    That is what keeps the service runnable, testable and demonstrable without a paid key, rather
    than failing at startup (AI PRD section 13 and 18).
    """
    name = config.effective_provider

    provider_class = PROVIDERS.get(name)
    if provider_class is None:
        # An unknown provider name is a configuration mistake, not a reason to crash the service.
        provider_class = MockProvider
        name = "mock"

    return provider_class(config)


__all__ = [
    "AIProvider",
    "ProviderError",
    "PROVIDERS",
    "available_providers",
    "create_provider",
]
