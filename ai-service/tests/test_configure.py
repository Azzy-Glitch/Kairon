"""Runtime provider configuration: POST /configure, /configure/test and /models.

The KAIRON frontend's AI Configuration panel is the only caller of these routes - a user picks a
provider, pastes a key, and this is what applies it without editing .env or restarting anything.
"""

from __future__ import annotations

import dataclasses

import pytest
from fastapi.testclient import TestClient

import main
from kairon.config import DEFAULT_ENDPOINTS, AiConfig


HEADERS = {"X-Kairon-AI-Key": "kairon-ai-test-key-not-for-production"}


class _JsonResponse:
    def __init__(self, status_code: int, body: dict):
        self.status_code = status_code
        self._body = body
        self.content = b"{}"

    def json(self):
        return self._body


class _CapturingPostClient:
    """Mirrors tests/test_providers.py's _CapturingAsyncClient - intercepts the outbound POST a
    provider makes so no real network call ever happens in a test."""

    response = _JsonResponse(200, {"choices": [{"message": {"content": '{"ok": true}'}}]})

    def __init__(self, **_kwargs):
        pass

    async def __aenter__(self):
        return self

    async def __aexit__(self, *_args):
        return False

    async def post(self, _url, **_kwargs):
        return type(self).response


class _CapturingGetClient:
    """Same idea as _CapturingPostClient, for the GET a model-list lookup makes."""

    response = _JsonResponse(200, {"data": []})

    def __init__(self, **_kwargs):
        pass

    async def __aenter__(self):
        return self

    async def __aexit__(self, *_args):
        return False

    async def get(self, _url, **_kwargs):
        return type(self).response


@pytest.fixture
def client() -> TestClient:
    return TestClient(main.app, headers=HEADERS)


@pytest.fixture(autouse=True)
def _clean_config():
    """/configure mutates process-wide globals by design (that's the point), so every test needs
    a known-clean starting point - never whatever a developer's local ai-service/.env happens to
    contain, or these tests would pass/fail depending on ambient machine state instead of the code
    under test. Also restores the pre-test singleton afterwards so test order can't matter."""
    original_config = main.CONFIG
    original_provider = main.SERVICE.provider

    main.CONFIG = AiConfig(provider="qwen", model="qwen-plus", timeout_seconds=5, max_retries=1)
    main.SERVICE.config = main.CONFIG
    main.SERVICE.provider = main.SERVICE.provider  # rebuilt per-test by whichever test needs it

    yield

    main.CONFIG = original_config
    main.SERVICE.config = original_config
    main.SERVICE.provider = original_provider


class TestConfigureIsAuthenticated:
    def test_configure_requires_the_shared_secret(self):
        unauthenticated = TestClient(main.app)
        assert unauthenticated.post("/configure", json={"provider": "groq"}).status_code == 401

    def test_configure_test_requires_the_shared_secret(self):
        unauthenticated = TestClient(main.app)
        assert unauthenticated.post("/configure/test", json={"provider": "groq"}).status_code == 401

    def test_models_requires_the_shared_secret(self):
        unauthenticated = TestClient(main.app)
        assert unauthenticated.post("/models", json={"provider": "groq"}).status_code == 401


class TestConfigureApplies:
    def test_unknown_provider_is_rejected(self, client):
        response = client.post("/configure", json={"provider": "not-a-real-provider"})

        assert response.status_code == 400
        assert response.json()["code"] == "unknown_provider"

    def test_valid_provider_applies_immediately(self, client):
        response = client.post(
            "/configure", json={"provider": "groq", "api_key": "gsk_test_key_value", "model": "some-model"}
        )

        assert response.status_code == 200
        body = response.json()
        assert body["applied"] is True
        assert body["provider"] == "groq"
        assert body["model"] == "some-model"
        assert body["effective_provider"] == "groq"

        # Reflected everywhere else too - this is what "no restart required" means in practice.
        assert client.get("/providers").json()["configured"]["provider"] == "groq"

    def test_omitted_model_falls_back_to_the_verified_default(self, client):
        response = client.post("/configure", json={"provider": "groq", "api_key": "gsk_test_key_value"})

        assert response.json()["model"] == "openai/gpt-oss-120b"

    def test_omitted_api_key_keeps_the_previously_configured_key(self, client):
        client.post("/configure", json={"provider": "groq", "api_key": "gsk_real_key", "model": "model-a"})

        # Second call changes only the model - the key must survive, or "Test Connection" after a
        # model-only change would falsely report the provider as unconfigured.
        response = client.post("/configure", json={"provider": "groq", "model": "model-b"})

        assert response.json()["effective_provider"] == "groq"
        assert response.json()["model"] == "model-b"

    def test_response_never_contains_the_raw_key(self, client):
        response = client.post(
            "/configure", json={"provider": "groq", "api_key": "gsk_super_secret_value", "model": "m"}
        )

        assert "gsk_super_secret_value" not in response.text

    def test_switching_provider_clears_a_stale_endpoint_override(self, client):
        main.CONFIG = dataclasses.replace(main.CONFIG, provider="qwen", endpoint="https://proxy.example/qwen")
        main.SERVICE.config = main.CONFIG

        client.post("/configure", json={"provider": "groq", "api_key": "gsk_x"})

        assert main.CONFIG.endpoint == ""

    def test_staying_on_the_same_provider_keeps_an_endpoint_override(self, client):
        main.CONFIG = dataclasses.replace(
            main.CONFIG, provider="groq", groq_api_key="gsk_x", endpoint="https://proxy.example/groq"
        )
        main.SERVICE.config = main.CONFIG

        client.post("/configure", json={"provider": "groq", "model": "another-model"})

        assert main.CONFIG.endpoint == "https://proxy.example/groq"

    def test_an_explicit_endpoint_is_applied(self, client):
        dedicated = "https://ws-8s7id56fv8yt5bmm.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1/chat/completions"

        response = client.post(
            "/configure", json={"provider": "qwen", "api_key": "sk-ws-x", "endpoint": dedicated}
        )

        assert response.json()["endpoint"] == dedicated
        assert main.CONFIG.endpoint == dedicated
        assert client.get("/providers").json()["configured"]["endpoint"] == dedicated

    def test_an_explicit_endpoint_wins_even_when_switching_provider(self, client):
        # The provider-switch-clears-endpoint rule only exists to stop an old provider's override
        # silently leaking onto a new one; a caller deliberately supplying a new endpoint on the
        # same request must never be second-guessed by that rule.
        main.CONFIG = dataclasses.replace(main.CONFIG, provider="qwen", endpoint="https://stale.example/qwen")
        main.SERVICE.config = main.CONFIG
        dedicated = "https://ws-8s7id56fv8yt5bmm.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1/chat/completions"

        response = client.post(
            "/configure", json={"provider": "groq", "api_key": "gsk_x", "endpoint": dedicated}
        )

        assert response.json()["endpoint"] == dedicated

    def test_omitted_endpoint_falls_back_to_the_providers_default(self, client):
        response = client.post("/configure", json={"provider": "groq", "api_key": "gsk_x"})

        assert response.json()["endpoint"] == "https://api.groq.com/openai/v1/chat/completions"

    def test_a_blank_endpoint_clears_an_override_immediately(self, client):
        # The defect this covers: clearing the endpoint updated the stored row but left this
        # process still calling the old URL until it restarted, because blank was treated the same
        # as "not supplied". An explicit "" is the caller saying "remove the override", now.
        dedicated = "https://ws-8s7id56fv8yt5bmm.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1/chat/completions"
        client.post("/configure", json={"provider": "qwen", "api_key": "sk-ws-x", "endpoint": dedicated})
        assert main.CONFIG.endpoint == dedicated

        response = client.post("/configure", json={"provider": "qwen", "endpoint": ""})

        assert main.CONFIG.endpoint == ""
        assert response.json()["endpoint"] == DEFAULT_ENDPOINTS["qwen"]

    def test_an_omitted_endpoint_still_keeps_an_existing_override(self, client):
        # The other half of the same distinction: omitting the field entirely (e.g. a caller
        # changing only the model) must not drop a working override.
        dedicated = "https://ws-8s7id56fv8yt5bmm.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1/chat/completions"
        client.post("/configure", json={"provider": "qwen", "api_key": "sk-ws-x", "endpoint": dedicated})

        client.post("/configure", json={"provider": "qwen", "model": "qwen-plus"})

        assert main.CONFIG.endpoint == dedicated


class TestEndpointMustBeSafe:
    """Plain HTTP would put the provider API key on the wire in clear text. Loopback is exempt so a
    local/self-hosted provider still works without a certificate."""

    REMOTE_HTTPS = "https://ws-example.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1/chat/completions"

    def test_https_remote_is_accepted(self, client):
        response = client.post(
            "/configure", json={"provider": "qwen", "api_key": "sk-x", "endpoint": self.REMOTE_HTTPS}
        )

        assert response.status_code == 200
        assert response.json()["endpoint"] == self.REMOTE_HTTPS

    def test_http_remote_is_rejected(self, client):
        response = client.post(
            "/configure",
            json={"provider": "qwen", "api_key": "sk-x", "endpoint": "http://example.com/v1/chat/completions"},
        )

        assert response.status_code == 400
        assert response.json()["code"] == "insecure_endpoint"

    @pytest.mark.parametrize(
        "endpoint",
        [
            "http://localhost:8080/v1/chat/completions",
            "http://127.0.0.1:8080/v1/chat/completions",
            "http://[::1]:8080/v1/chat/completions",
        ],
    )
    def test_loopback_http_is_accepted(self, client, endpoint):
        response = client.post(
            "/configure", json={"provider": "qwen", "api_key": "sk-x", "endpoint": endpoint}
        )

        assert response.status_code == 200
        assert response.json()["endpoint"] == endpoint

    @pytest.mark.parametrize(
        "endpoint",
        [
            "not-a-url",
            "://missing-scheme",
            "ftp://example.com/v1",
            "file:///etc/passwd",
            "https://",
        ],
    )
    def test_malformed_or_unsupported_urls_are_rejected(self, client, endpoint):
        response = client.post(
            "/configure", json={"provider": "qwen", "api_key": "sk-x", "endpoint": endpoint}
        )

        assert response.status_code == 400
        assert response.json()["code"] in ("invalid_endpoint", "insecure_endpoint")

    def test_a_rejected_endpoint_never_sends_the_api_key_anywhere(self, client, monkeypatch):
        """The point of validating inside _merge_configured: rejection happens before any provider
        is built or called, so the key cannot reach the attacker-supplied host."""
        import kairon.providers.openai_compatible as oc

        attempted = []

        class _RecordingClient(_CapturingPostClient):
            async def post(self, url, **kwargs):
                attempted.append((url, kwargs.get("headers", {})))
                return await super().post(url, **kwargs)

        monkeypatch.setattr(oc.httpx, "AsyncClient", _RecordingClient)

        response = client.post(
            "/configure/test",
            json={
                "provider": "qwen",
                "api_key": "sk-super-secret-value",
                "endpoint": "http://attacker.example/collect",
            },
        )

        assert response.status_code == 400
        assert attempted == []                              # no outbound request at all
        assert "sk-super-secret-value" not in response.text  # and nothing echoed back

    def test_a_rejected_endpoint_leaves_the_live_configuration_untouched(self, client):
        client.post("/configure", json={"provider": "qwen", "api_key": "sk-x", "endpoint": self.REMOTE_HTTPS})

        client.post("/configure", json={"provider": "qwen", "endpoint": "http://attacker.example/collect"})

        assert main.CONFIG.endpoint == self.REMOTE_HTTPS


class TestConfigureTestNeverMutatesLiveConfig:
    def test_test_endpoint_leaves_the_active_provider_untouched(self, client):
        client.post("/configure", json={"provider": "gemini", "api_key": "AIza_original", "model": "m1"})

        client.post("/configure/test", json={"provider": "groq", "api_key": "gsk_probe", "model": "m2"})

        still_active = client.get("/providers").json()["configured"]
        assert still_active["provider"] == "gemini"
        assert still_active["model"] == "m1"

    def test_explicit_endpoint_is_used_for_the_probe_without_touching_live_config(self, client, monkeypatch):
        import kairon.providers.openai_compatible as oc

        client.post("/configure", json={"provider": "qwen", "api_key": "sk-ws-original", "model": "qwen-plus"})
        captured_urls = []

        class _RecordingClient(_CapturingPostClient):
            async def post(self, url, **kwargs):
                captured_urls.append(url)
                return await super().post(url, **kwargs)

        monkeypatch.setattr(oc.httpx, "AsyncClient", _RecordingClient)
        dedicated = "https://ws-8s7id56fv8yt5bmm.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1/chat/completions"

        response = client.post(
            "/configure/test", json={"provider": "qwen", "api_key": "sk-ws-x", "endpoint": dedicated}
        )

        assert response.json()["endpoint"] == dedicated
        assert captured_urls == [dedicated]
        # The probe must never leak into the live, already-applied configuration.
        assert main.CONFIG.endpoint == ""

    def test_no_usable_key_honestly_reports_mock(self, client):
        response = client.post("/configure/test", json={"provider": "groq"})

        body = response.json()
        assert body["effective_provider"] == "mock"
        assert body["success"] is True

    def test_successful_call_reports_success(self, client, monkeypatch):
        import kairon.providers.openai_compatible as oc

        monkeypatch.setattr(oc.httpx, "AsyncClient", _CapturingPostClient)

        response = client.post(
            "/configure/test", json={"provider": "groq", "api_key": "gsk_probe", "model": "m"}
        )

        body = response.json()
        assert body["success"] is True
        assert body["error"] is None
        assert body["provider"] == "groq"

    def test_provider_failure_reports_failure_without_leaking_the_key(self, client, monkeypatch):
        import kairon.providers.openai_compatible as oc

        _CapturingPostClient.response = _JsonResponse(401, {})
        monkeypatch.setattr(oc.httpx, "AsyncClient", _CapturingPostClient)

        response = client.post(
            "/configure/test", json={"provider": "groq", "api_key": "gsk_probe_secret", "model": "m"}
        )

        body = response.json()
        assert body["success"] is False
        assert body["error"]
        assert "gsk_probe_secret" not in response.text

        _CapturingPostClient.response = _JsonResponse(
            200, {"choices": [{"message": {"content": '{"ok": true}'}}]}
        )


class TestModelDiscovery:
    def test_non_groq_provider_reports_unsupported_not_an_error(self, client):
        for provider in ("qwen", "gemini"):
            response = client.post("/models", json={"provider": provider})
            body = response.json()
            assert body["supported"] is False
            assert body["models"] == []

    def test_groq_without_a_key_reports_no_models(self, client):
        response = client.post("/models", json={"provider": "groq"})

        body = response.json()
        assert body["supported"] is True
        assert body["models"] == []
        assert body.get("error")

    def test_groq_filters_to_active_text_models_only(self, client, monkeypatch):
        _CapturingGetClient.response = _JsonResponse(
            200,
            {
                "data": [
                    {"id": "openai/gpt-oss-120b", "active": True, "output_modalities": ["text"]},
                    {"id": "whisper-large-v3", "active": True, "output_modalities": ["transcription"]},
                    {"id": "retired-model", "active": False, "output_modalities": ["text"]},
                ]
            },
        )
        monkeypatch.setattr(main.httpx, "AsyncClient", _CapturingGetClient)

        response = client.post("/models", json={"provider": "groq", "api_key": "gsk_probe"})

        body = response.json()
        assert body["supported"] is True
        assert body["models"] == ["openai/gpt-oss-120b"]
