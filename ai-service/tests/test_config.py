"""RB-003: endpoint validation must apply consistently everywhere an AI provider endpoint
override can enter this service - not just the runtime /configure endpoint (see
test_configure.py's TestEndpointMustBeSafe for that path)."""

from __future__ import annotations

import pytest

from kairon.config import AiConfig, EndpointNotAllowedError, is_endpoint_allowed
from kairon.providers import create_provider
from kairon.providers.unconfigured import UnconfiguredProvider


class TestIsEndpointAllowed:
    def test_blank_endpoint_is_allowed(self):
        assert is_endpoint_allowed("") is True

    def test_https_remote_is_allowed(self):
        assert is_endpoint_allowed("https://example.com/v1/chat/completions") is True

    @pytest.mark.parametrize(
        "endpoint",
        ["http://localhost:8080/v1", "http://127.0.0.1:8080/v1", "http://[::1]:8080/v1"],
    )
    def test_loopback_http_is_allowed(self, endpoint):
        assert is_endpoint_allowed(endpoint) is True

    def test_remote_http_is_rejected(self):
        assert is_endpoint_allowed("http://remote-host/v1") is False

    @pytest.mark.parametrize(
        "endpoint",
        [
            "https://user:pass@example.com/v1",
            "http://user:pass@127.0.0.1/v1",  # embedded userinfo is rejected on either scheme
        ],
    )
    def test_embedded_userinfo_is_rejected(self, endpoint):
        assert is_endpoint_allowed(endpoint) is False

    @pytest.mark.parametrize("endpoint", ["not-a-url", "ftp://example.com/v1", "file:///etc/passwd"])
    def test_malformed_or_unsupported_scheme_is_rejected(self, endpoint):
        assert is_endpoint_allowed(endpoint) is False


class TestStartupEndpointValidation:
    """AiConfig.from_env() must apply the exact same policy as the runtime /configure endpoint -
    an operator setting AI__Endpoint in the environment is not a second, unvalidated path to a
    remote plaintext endpoint or one carrying embedded credentials."""

    def test_remote_plaintext_endpoint_env_var_fails_closed_at_startup(self, monkeypatch):
        monkeypatch.setenv("AI__Provider", "qwen")
        monkeypatch.setenv("AI__Endpoint", "http://remote-host/v1")

        with pytest.raises(EndpointNotAllowedError):
            AiConfig.from_env()

    def test_userinfo_embedded_endpoint_env_var_fails_closed_at_startup(self, monkeypatch):
        monkeypatch.setenv("AI__Provider", "qwen")
        monkeypatch.setenv("AI__Endpoint", "https://user:password@example.com/v1")

        with pytest.raises(EndpointNotAllowedError):
            AiConfig.from_env()

    def test_https_remote_endpoint_env_var_is_accepted_at_startup(self, monkeypatch):
        monkeypatch.setenv("AI__Provider", "qwen")
        monkeypatch.setenv("AI__Endpoint", "https://ws-dedicated.example/v1")

        config = AiConfig.from_env()

        assert config.endpoint == "https://ws-dedicated.example/v1"

    def test_loopback_http_endpoint_env_var_is_accepted_at_startup(self, monkeypatch):
        monkeypatch.setenv("AI__Provider", "qwen")
        monkeypatch.setenv("AI__Endpoint", "http://127.0.0.1:9000/v1")

        config = AiConfig.from_env()

        assert config.endpoint == "http://127.0.0.1:9000/v1"

    def test_no_endpoint_override_falls_back_to_the_providers_default(self, monkeypatch):
        monkeypatch.delenv("AI__Endpoint", raising=False)
        monkeypatch.delenv("AI_ENDPOINT", raising=False)
        monkeypatch.setenv("AI__Provider", "qwen")

        config = AiConfig.from_env()

        assert config.endpoint.startswith("https://dashscope.aliyuncs.com")


class TestCreateProviderDefenseInDepth:
    """create_provider is the actual point a real outbound-calling provider gets built - even if
    some future code path constructed an AiConfig directly (bypassing from_env()/`/configure`'s
    own validation), this must not hand back a provider that would call an unsafe endpoint."""

    def test_a_config_with_a_disallowed_endpoint_never_yields_a_real_provider(self):
        config = AiConfig(
            provider="qwen",
            endpoint="http://remote-host/v1",
            qwen_api_key="sk-real-looking-key",
        )

        provider = create_provider(config)

        assert isinstance(provider, UnconfiguredProvider)

    def test_a_config_with_an_allowed_endpoint_yields_the_real_provider(self):
        config = AiConfig(
            provider="qwen",
            endpoint="https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions",
            qwen_api_key="sk-real-looking-key",
        )

        provider = create_provider(config)

        assert not isinstance(provider, UnconfiguredProvider)
