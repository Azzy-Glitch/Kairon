"""Configuration for the Kairon AI service.

Every provider setting is read from the environment. Nothing here has a real credential baked in,
and nothing here is ever returned by an endpoint or written to a log (AI PRD sections 4 and 14).
"""

from __future__ import annotations

import ipaddress
import os
from dataclasses import dataclass, field
from urllib.parse import urlparse


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


def _is_loopback_host(host: str) -> bool:
    """localhost and the loopback ranges only - never a hostname that merely looks local."""
    if host.lower() == "localhost":
        return True
    try:
        return ipaddress.ip_address(host).is_loopback
    except ValueError:
        return False


def is_endpoint_allowed(endpoint: str) -> bool:
    """The ONE endpoint-validation policy for this service, applied identically wherever a custom
    AI provider endpoint can enter it: startup environment configuration (AiConfig.from_env,
    below), the runtime /configure endpoint (main.py), and provider construction. A blank endpoint
    is always allowed - it means "use this provider's own default", not an override to validate.

    Plain HTTP is refused unless the host is a genuine loopback address (the Authorization/API-key
    header would otherwise cross a real network in clear text); HTTPS is accepted for any host.
    Embedded userinfo (https://user:pass@host) is refused on either scheme - it is a credential
    leak vector of its own (logged URLs, proxies, browser/shell history) that this service never
    needs.
    """
    if not endpoint:
        return True
    parsed = urlparse(endpoint)
    if parsed.scheme not in ("http", "https") or not parsed.hostname:
        return False
    if parsed.username or parsed.password:
        return False
    if parsed.scheme == "http" and not _is_loopback_host(parsed.hostname):
        return False
    return True


class EndpointNotAllowedError(ValueError):
    """A supplied AI provider endpoint failed is_endpoint_allowed. Raised directly by
    AiConfig.from_env() (crashing startup with a clear, actionable message rather than silently
    starting with an insecure override); main.py's runtime /configure endpoint catches this and
    reports it as an ordinary 400 AiServiceError instead."""


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

        # An explicit override (AI__Endpoint/AI_ENDPOINT) is exactly as untrusted as one supplied
        # through the runtime /configure endpoint - startup environment configuration must not be
        # a second, unvalidated path to a remote plaintext endpoint or one carrying embedded
        # credentials. A provider's own DEFAULT_ENDPOINTS entry is never checked: it is a fixed,
        # known-safe HTTPS URL baked into this module, not external input.
        endpoint_override = _env("AI__Endpoint") or _env("AI_ENDPOINT")
        if endpoint_override and not is_endpoint_allowed(endpoint_override):
            raise EndpointNotAllowedError(
                f"AI__Endpoint/AI_ENDPOINT {endpoint_override!r} is not allowed: plain HTTP is "
                "only allowed to localhost/loopback addresses, and an endpoint embedding "
                "credentials (user:pass@host) is never allowed. Use an HTTPS endpoint for any "
                "non-local AI provider."
            )
        endpoint = endpoint_override or DEFAULT_ENDPOINTS.get(provider, "")

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
        """The provider name that will actually serve requests.

        Mock is selected ONLY when explicitly requested - force_mock (AI__MockMode) or an explicit
        provider="mock" choice. It is deliberately NOT the answer whenever the selected real
        provider merely lacks a usable credential: silently substituting mock there is exactly the
        production-safety gap this property used to have (a fresh install with no provider
        configured would fabricate AI output with no visible sign anything was wrong). See
        is_configured for whether the resolved provider can actually be reached - create_provider
        (kairon.providers) is what turns "selected but not configured" into a provider that fails
        honestly instead of one that answers with fabricated data.
        """
        if self.force_mock or self.provider == "mock":
            return "mock"
        return self.provider

    @property
    def is_configured(self) -> bool:
        """Whether effective_provider can actually be reached.

        An explicit mock choice is always considered configured - that is an intentional test/
        development setup, not a gap to report. A real provider is configured only when it is a
        known, registered provider (DEFAULT_ENDPOINTS' keys - "mock" and any typo/unknown name are
        excluded on purpose) AND has a real, non-placeholder credential. False is the signal the
        rest of the stack (create_provider, AiService.mode, the /health endpoint, the .NET backend
        consuming it) uses to report AI as unavailable/not-configured rather than quietly answering
        with mock output.
        """
        if self.force_mock or self.provider == "mock":
            return True
        return self.provider in DEFAULT_ENDPOINTS and self.has_usable_key

    def public_dict(self) -> dict:
        """Safe to return from an endpoint: describes configuration, exposes no secret."""
        return {
            "provider": self.provider,
            "effective_provider": self.effective_provider,
            "is_configured": self.is_configured,
            "model": self.model,
            "endpoint": self.endpoint or DEFAULT_ENDPOINTS.get(self.provider, ""),
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
