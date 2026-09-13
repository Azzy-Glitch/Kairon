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
from .unconfigured import UnconfiguredProvider

# "unconfigured" is deliberately NOT registered here: it is never a provider a caller selects (via
# AI__Provider or the AI Configuration panel) - it is create_provider's own internal fallback for a
# selected-but-unusable configuration. Keeping it out of PROVIDERS is what keeps available_providers()
# (and the /providers endpoint's "available" list) an honest list of real, choosable providers.
PROVIDERS: Dict[str, Type[AIProvider]] = {
    "qwen": QwenProvider,
    "gemini": GeminiProvider,
    "groq": GroqProvider,
    "mock": MockProvider,
}


def available_providers() -> list[str]:
    return sorted(PROVIDERS.keys())


def create_provider(config: AiConfig) -> AIProvider:
    """Builds the provider that will actually serve requests.

    Mock is built ONLY for an explicit choice (config.effective_provider == "mock" - force_mock or
    provider="mock"); that is what keeps the service testable/demonstrable without a paid key
    (AI PRD section 13 and 18) while remaining an intentional, configured choice rather than a
    silent one. An unusable REAL selection - an unknown provider name, or a known one with no
    usable credential (config.is_configured is False) - builds UnconfiguredProvider instead: it
    never crashes the service and never fabricates an answer, it just fails clearly the moment it
    is actually invoked (see its own docstring).
    """
    name = config.effective_provider

    if name == "mock":
        return MockProvider(config)

    if not config.is_configured:
        return UnconfiguredProvider(config)

    return PROVIDERS[name](config)


__all__ = [
    "AIProvider",
    "ProviderError",
    "PROVIDERS",
    "UnconfiguredProvider",
    "available_providers",
    "create_provider",
]
