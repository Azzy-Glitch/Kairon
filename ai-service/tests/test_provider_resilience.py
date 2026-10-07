"""Rate-limit backoff, JSON mode and truncation handling for OpenAI-compatible providers (Groq)."""

from __future__ import annotations

import pytest

from kairon.config import AiConfig
from kairon.providers.base import MAX_RETRY_WAIT_SECONDS, ProviderError, retry_delay
from kairon.providers.openai_compatible import GroqProvider, QwenProvider


class _Response:
    def __init__(self, status_code=200, body=None, headers=None):
        self.status_code = status_code
        self._body = body if body is not None else {}
        self.headers = headers or {}
        self.content = b"{}"

    def json(self):
        return self._body


class _Client:
    response = _Response()
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


@pytest.fixture(autouse=True)
def _transport(monkeypatch):
    monkeypatch.setattr("kairon.providers.openai_compatible.httpx.AsyncClient", _Client)


def _groq(**overrides):
    return GroqProvider(AiConfig(provider="groq", groq_api_key="synthetic-groq-key", **overrides))


async def test_groq_requests_a_json_object_and_short_reasoning_for_gpt_oss():
    _Client.response = _Response(body={"choices": [{"message": {"content": "{}"}, "finish_reason": "stop"}]})
    await _groq()._invoke("system: respond in JSON", "user")
    assert _Client.last_json["response_format"] == {"type": "json_object"}
    assert _Client.last_json["reasoning_effort"] == "low"


async def test_providers_without_documented_json_mode_are_unchanged():
    _Client.response = _Response(body={"choices": [{"message": {"content": "{}"}}]})
    await QwenProvider(AiConfig(provider="qwen", qwen_api_key="synthetic"))._invoke("s", "u")
    assert "response_format" not in _Client.last_json


async def test_truncated_answer_is_a_clear_permanent_error_not_malformed_json():
    _Client.response = _Response(body={"choices": [{"message": {"content": '{"summary": "cut'}, "finish_reason": "length"}]})
    with pytest.raises(ProviderError) as error:
        await _groq(max_output_tokens=64)._invoke("s", "u")
    assert "truncated" in str(error.value)
    assert error.value.transient is False


async def test_rate_limit_carries_retry_after_and_stays_transient():
    _Client.response = _Response(status_code=429, headers={"retry-after": "3"})
    with pytest.raises(ProviderError) as error:
        await _groq()._invoke("s", "u")
    assert error.value.status_code == 429
    assert error.value.transient is True
    assert error.value.retry_after == 3.0


async def test_invalid_key_is_permanent():
    _Client.response = _Response(status_code=401)
    with pytest.raises(ProviderError) as error:
        await _groq()._invoke("s", "u")
    assert error.value.transient is False


def test_retry_delay_honours_rate_limits_within_the_budget():
    assert retry_delay(1, ProviderError("x", transient=True, status_code=429, retry_after=3)) == 3
    assert retry_delay(1, ProviderError("x", transient=True, status_code=429, retry_after=600)) == MAX_RETRY_WAIT_SECONDS
    assert retry_delay(2, ProviderError("x", transient=True, status_code=429)) == 4.0
    assert retry_delay(1, ProviderError("x", transient=True, status_code=503)) == 0.25
    assert retry_delay(2, None) == 0.5
