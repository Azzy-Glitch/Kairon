"""Provider selection, credentials and failure isolation (AI PRD section 18)."""

from __future__ import annotations

import asyncio

import pytest

from kairon.config import AiConfig, is_placeholder
from kairon.providers import PROVIDERS, available_providers, create_provider
from kairon.providers.base import AIProvider, ProviderError
from kairon.providers.gemini import GeminiProvider
from kairon.providers.mock import MockProvider
from kairon.providers.openai_compatible import GroqProvider, QwenProvider


class _HttpResponse:
    def __init__(self, body: dict, content: bytes = b"{}"):
        self.status_code = 200
        self.content = content
        self._body = body

    def json(self):
        return self._body


class _CapturingAsyncClient:
    response = _HttpResponse({})
    last_json = None

    def __init__(self, **_kwargs):
        pass

    async def __aenter__(self):
        return self

    async def __aexit__(self, *_args):
        return False

    async def post(self, _url, **kwargs):
        type(self).last_json = kwargs["json"]
        return type(self).response


class TestProviderSelection:
    def test_every_required_provider_is_registered(self):
        assert set(available_providers()) == {"qwen", "gemini", "groq", "mock"}

    @pytest.mark.parametrize(
        "provider,expected",
        [("qwen", QwenProvider), ("gemini", GeminiProvider), ("groq", GroqProvider)],
    )
    def test_provider_selected_by_configuration(self, provider, expected):
        config = AiConfig(provider=provider, **{f"{provider}_api_key": "real-looking-key-value"})
        assert isinstance(create_provider(config), expected)

    def test_missing_credentials_fall_back_to_mock(self):
        config = AiConfig(provider="gemini", gemini_api_key="")
        assert config.effective_provider == "mock"
        assert isinstance(create_provider(config), MockProvider)

    def test_placeholder_credentials_fall_back_to_mock(self):
        config = AiConfig(provider="qwen", qwen_api_key="your-key")
        assert config.effective_provider == "mock"

    def test_force_mock_overrides_a_real_key(self):
        config = AiConfig(provider="qwen", qwen_api_key="real-looking-key-value", force_mock=True)
        assert config.effective_provider == "mock"

    def test_unknown_provider_falls_back_to_mock_rather_than_crashing(self):
        config = AiConfig(provider="not-a-provider")
        assert isinstance(create_provider(config), MockProvider)

    def test_switching_provider_needs_no_code_change(self):
        """The same config object, one field changed, produces a different provider."""
        config = AiConfig(provider="qwen", qwen_api_key="k1", gemini_api_key="k2")
        assert isinstance(create_provider(config), QwenProvider)

        config.provider = "gemini"
        assert isinstance(create_provider(config), GeminiProvider)


class TestCredentialSafety:
    def test_public_dict_never_contains_a_key(self):
        config = AiConfig(
            provider="qwen",
            qwen_api_key="sk-super-secret-value",
            gemini_api_key="AIzaSuperSecretValue",
            groq_api_key="gsk-secret",
        )
        rendered = str(config.public_dict())

        assert "sk-super-secret-value" not in rendered
        assert "AIzaSuperSecretValue" not in rendered
        assert "gsk-secret" not in rendered
        assert config.public_dict()["credentials_configured"]["qwen"] is True

    def test_repr_does_not_leak_keys(self):
        config = AiConfig(provider="qwen", qwen_api_key="sk-super-secret-value")
        assert "sk-super-secret-value" not in repr(config)

    @pytest.mark.parametrize("value", ["", "your-key", "CHANGEME", "none", None])
    def test_placeholder_detection(self, value):
        assert is_placeholder(value) is True

    def test_real_key_is_not_a_placeholder(self):
        assert is_placeholder("sk-abcdef123456") is False


class _ScriptedProvider(AIProvider):
    """Provider that replays a fixed script of outcomes, for resilience tests."""

    name = "scripted"

    def __init__(self, config, outcomes):
        super().__init__(config)
        self.outcomes = list(outcomes)
        self.calls = 0

    async def _invoke(self, system: str, user: str) -> str:
        self.calls += 1
        outcome = self.outcomes.pop(0)
        # BaseException, not Exception: asyncio.CancelledError does not derive from Exception
        # in Python 3.8+, and cancellation is exactly what these tests need to script.
        if isinstance(outcome, BaseException):
            raise outcome
        return outcome


class TestResilience:
    async def test_transient_failure_is_retried_then_succeeds(self, mock_config):
        provider = _ScriptedProvider(
            mock_config,
            [ProviderError("boom", transient=True), '{"root_cause":"recovered"}'],
        )

        result = await provider.complete_json("sys", "user")

        assert result["root_cause"] == "recovered"
        assert provider.calls == 2

    async def test_permanent_failure_is_not_retried(self, mock_config):
        provider = _ScriptedProvider(
            mock_config,
            [ProviderError("bad credentials", transient=False), '{"root_cause":"never reached"}'],
        )

        with pytest.raises(ProviderError):
            await provider.complete_json("sys", "user")

        assert provider.calls == 1, "a permanent failure must not consume a retry"

    async def test_retries_are_bounded(self, mock_config):
        mock_config.max_retries = 2
        provider = _ScriptedProvider(mock_config, [ProviderError("boom", transient=True)] * 10)

        with pytest.raises(ProviderError):
            await provider.complete_json("sys", "user")

        assert provider.calls == 3, "max_retries=2 means exactly three attempts"

    async def test_malformed_json_is_retried_then_rejected(self, mock_config):
        mock_config.max_retries = 1
        provider = _ScriptedProvider(mock_config, ["not json at all", "still not json"])

        with pytest.raises(ProviderError):
            await provider.complete_json("sys", "user")

        assert provider.calls == 2

    async def test_cancellation_propagates_and_is_not_retried(self, mock_config):
        provider = _ScriptedProvider(mock_config, [asyncio.CancelledError()])

        with pytest.raises(asyncio.CancelledError):
            await provider.complete_json("sys", "user")

        assert provider.calls == 1

    async def test_timeout_surfaces_as_a_provider_error(self, mock_config):
        mock_config.max_retries = 0
        provider = _ScriptedProvider(mock_config, [ProviderError("timed out", transient=True)])

        with pytest.raises(ProviderError) as exc:
            await provider.complete_json("sys", "user")

        assert "timed out" in str(exc.value)


class TestNoPaidCredentialsRequired:
    def test_full_provider_registry_instantiates_without_keys(self):
        """Every provider class can be constructed with no credentials at all."""
        config = AiConfig(provider="mock")

        for name, provider_class in PROVIDERS.items():
            provider = provider_class(config)
            assert provider.name == name or name == "mock"


class TestProviderBounds:
    async def test_openai_compatible_provider_sends_output_token_limit(self, monkeypatch):
        config = AiConfig(provider="qwen", qwen_api_key="synthetic", max_output_tokens=321)
        _CapturingAsyncClient.response = _HttpResponse(
            {"choices": [{"message": {"content": "{}"}}]}
        )
        monkeypatch.setattr("kairon.providers.openai_compatible.httpx.AsyncClient", _CapturingAsyncClient)

        await QwenProvider(config)._invoke("system", "user")

        assert _CapturingAsyncClient.last_json["max_tokens"] == 321

    async def test_gemini_provider_sends_output_token_limit(self, monkeypatch):
        config = AiConfig(provider="gemini", gemini_api_key="synthetic", max_output_tokens=654)
        _CapturingAsyncClient.response = _HttpResponse(
            {"candidates": [{"content": {"parts": [{"text": "{}"}]}}]}
        )
        monkeypatch.setattr("kairon.providers.gemini.httpx.AsyncClient", _CapturingAsyncClient)

        await GeminiProvider(config)._invoke("system", "user")

        assert _CapturingAsyncClient.last_json["generationConfig"]["maxOutputTokens"] == 654

    @pytest.mark.parametrize(
        "provider,module,response",
        [
            (
                lambda config: QwenProvider(config),
                "kairon.providers.openai_compatible.httpx.AsyncClient",
                {"choices": [{"message": {"content": "{}"}}]},
            ),
            (
                lambda config: GeminiProvider(config),
                "kairon.providers.gemini.httpx.AsyncClient",
                {"candidates": [{"content": {"parts": [{"text": "{}"}]}}]},
            ),
        ],
    )
    async def test_provider_rejects_oversized_response(self, monkeypatch, provider, module, response):
        config = AiConfig(
            provider="qwen",
            qwen_api_key="synthetic",
            gemini_api_key="synthetic",
            max_retries=0,
            max_response_bytes=1024,
        )
        _CapturingAsyncClient.response = _HttpResponse(response, content=b"x" * 1025)
        monkeypatch.setattr(module, _CapturingAsyncClient)

        with pytest.raises(ProviderError, match="size limit"):
            await provider(config)._invoke("system", "user")
