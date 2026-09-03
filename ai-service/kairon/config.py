"""Configuration for the Kairon AI service.

Every provider setting is read from the environment. Nothing here has a real credential baked in,
and nothing here is ever returned by an endpoint or written to a log (AI PRD sections 4 and 14).
"""

from __future__ import annotations

import os
from dataclasses import dataclass, field


def _env(name: str, default: str = "") -> str:
    return os.getenv(name, default).strip()


def _env_int(name: str, default: int) -> int:
    try:
        return int(os.getenv(name, str(default)))
    except (TypeError, ValueError):
        return default


def _env_float(name: str, default: float) -> float:
    try:
        return float(os.getenv(name, str(default)))
    except (TypeError, ValueError):
        return default


# Placeholder values that people leave in .env files. Treating these as "no key" is what keeps a
# half-configured environment in deterministic mock mode instead of firing doomed HTTP requests.
PLACEHOLDER_KEYS = {"", "your-key", "your-api-key", "changeme", "none", "null", "todo"}


def is_placeholder(key: str | None) -> bool:
    return key is None or key.strip().lower() in PLACEHOLDER_KEYS


# Default model per provider, so AI__Provider alone is a complete configuration.
DEFAULT_MODELS = {
    "qwen": "qwen-plus",
    "gemini": "gemini-2.5-flash-lite",
    "groq": "openai/gpt-oss-120b",
    "mock": "deterministic-mock",
}

DEFAULT_ENDPOINTS = {
    "qwen": "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions",
    "gemini": "https://generativelanguage.googleapis.com/v1beta/models",
    "groq": "https://api.groq.com/openai/v1/chat/completions",
}


@dataclass
class AiConfig:
    """Resolved AI configuration. Provider selection is configuration-driven (AI PRD section 3)."""

    provider: str = "qwen"
    model: str = ""
    endpoint: str = ""
    timeout_seconds: float = 30.0
    max_retries: int = 2
    temperature: float = 0.2
    max_output_tokens: int = 2048
    max_response_bytes: int = 65536
    force_mock: bool = False

    # Credentials. Server-side only; never serialized, never logged.
    qwen_api_key: str = field(default="", repr=False)
    gemini_api_key: str = field(default="", repr=False)
    groq_api_key: str = field(default="", repr=False)

    @classmethod
    def from_env(cls) -> "AiConfig":
        # Both AI__Provider (the .NET configuration convention the PRD uses) and a plain
        # AI_PROVIDER are accepted, because the same value gets set both ways in practice.
        provider = (_env("AI__Provider") or _env("AI_PROVIDER") or "qwen").lower()
        model = _env("AI__Model") or _env("AI_MODEL") or DEFAULT_MODELS.get(provider, "")
        endpoint = _env("AI__Endpoint") or _env("AI_ENDPOINT") or DEFAULT_ENDPOINTS.get(provider, "")

        force_mock = (_env("AI__MockMode") or _env("AI_MOCK_MODE") or "").lower() in {"1", "true", "yes"}

        return cls(
            provider=provider,
            model=model,
            endpoint=endpoint,
            timeout_seconds=_env_float("AI__TimeoutSeconds", _env_float("AI_TIMEOUT_SECONDS", 30.0)),
            max_retries=_env_int("AI__MaxRetries", _env_int("AI_MAX_RETRIES", 2)),
            temperature=_env_float("AI__Temperature", 0.2),
            max_output_tokens=_env_int("AI__MaxOutputTokens", 2048),
            max_response_bytes=_env_int("AI__MaxResponseBytes", 65536),
            force_mock=force_mock,
            qwen_api_key=_env("QWEN_API_KEY"),
            gemini_api_key=_env("GEMINI_API_KEY"),
            groq_api_key=_env("GROQ_API_KEY"),
        )

    def key_for(self, provider: str) -> str:
        return {
            "qwen": self.qwen_api_key,
            "gemini": self.gemini_api_key,
            "groq": self.groq_api_key,
        }.get(provider, "")

    @property
    def has_usable_key(self) -> bool:
        return not is_placeholder(self.key_for(self.provider))

    @property
    def effective_provider(self) -> str:
        """The provider that will actually serve requests.

        Falling back to mock rather than erroring is what makes the PRD's "tests and demonstrations
        must work without paid API credentials" true by construction.
        """
        if self.force_mock or self.provider == "mock":
            return "mock"
        return self.provider if self.has_usable_key else "mock"

    def public_dict(self) -> dict:
        """Safe to return from an endpoint: describes configuration, exposes no secret."""
        return {
            "provider": self.provider,
            "effective_provider": self.effective_provider,
            "model": self.model,
            "timeout_seconds": self.timeout_seconds,
            "max_retries": self.max_retries,
            "max_output_tokens": self.max_output_tokens,
            "mock_mode": self.effective_provider == "mock",
            "credentials_configured": {
                "qwen": not is_placeholder(self.qwen_api_key),
                "gemini": not is_placeholder(self.gemini_api_key),
                "groq": not is_placeholder(self.groq_api_key),
            },
        }
