"""HTTP contract tests.

Two things matter here: the new /analyze contract works, and every endpoint the original service
exposed still answers on the same route with the same shape (AI PRD section 21).
"""

from __future__ import annotations

import pytest
from fastapi.testclient import TestClient

import main


@pytest.fixture
def client() -> TestClient:
    return TestClient(
        main.app,
        headers={"X-Kairon-AI-Key": "kairon-ai-test-key-not-for-production"},
    )


class TestHealthAndProviders:
    def test_health(self, client):
        response = client.get("/health")

        assert response.status_code == 200
        assert response.json()["status"] == "healthy"
        assert response.json()["mode"] in {"mock", "live"}

    def test_health_is_the_only_credential_free_endpoint(self):
        unauthenticated = TestClient(main.app)

        assert unauthenticated.get("/health").status_code == 200
        assert unauthenticated.get("/providers").status_code == 401
        assert unauthenticated.post("/analyze", json={}).status_code == 401

    def test_wrong_credential_is_rejected(self):
        wrong = TestClient(main.app, headers={"X-Kairon-AI-Key": "wrong"})

        assert wrong.post("/analyze", json={}).status_code == 401

    def test_providers_lists_all_four(self, client):
        body = client.get("/providers").json()

        assert set(body["available"]) == {"qwen", "gemini", "groq", "mock"}

    def test_providers_never_returns_a_credential(self, client):
        raw = client.get("/providers").text.lower()

        assert "api_key" not in raw or "credentials_configured" in raw
        for leak in ("sk-", "aiza", "gsk_"):
            assert leak not in raw


class TestAnalyzeEndpoint:
    def _payload(self) -> dict:
        return {
            "incident": {
                "incident_key": "INC-0001",
                "title": "OrderProcessingService Service Degradation",
                "service": "OrderProcessingService",
                "environment": "Demo",
                "severity": "High",
                "affected_component": "OrderProcessingService",
                "affected_endpoint": "/api/orders/process",
                "symptoms": ["Retry rate 90/min", "CPU 94%"],
            },
            "correlated_signals": [
                {"rule": "retry-storm", "metric": "retries", "symptom": "Retry rate 90/min",
                 "observed": 90, "threshold": 30, "unit": "/min", "severity": "High"},
                {"rule": "cpu-threshold", "metric": "cpu", "symptom": "CPU 94%",
                 "observed": 94, "threshold": 80, "unit": "%", "severity": "High"},
            ],
            "recent_metrics": [
                {"cpu_percent": 94, "response_time_ms": 2400, "request_count": 20,
                 "error_count": 7, "retry_count": 30, "queue_depth": 85}
            ],
            "available_actions": [
                {"action": "DisableDemoRetryLoop", "description": "Disables the retry loop", "risk_level": "Low"}
            ],
        }

    def test_returns_a_structured_investigation(self, client):
        response = client.post("/analyze", json=self._payload())

        assert response.status_code == 200
        body = response.json()

        for field in (
            "summary", "root_cause", "contributing_factors", "evidence", "confidence",
            "severity", "affected_components", "predicted_failure", "estimated_risk",
            "recommendations",
        ):
            assert field in body, f"missing required field '{field}'"

    def test_confidence_is_within_range(self, client):
        body = client.post("/analyze", json=self._payload()).json()

        assert 0.0 <= body["confidence"] <= 1.0

    def test_recommendation_shape(self, client):
        body = client.post("/analyze", json=self._payload()).json()

        assert body["recommendations"], "the retry-storm scenario should produce a recommendation"
        rec = body["recommendations"][0]
        assert rec["action"] == "DisableDemoRetryLoop"
        assert rec["reason"]
        assert rec["expected_outcome"]
        assert rec["risk_level"] in {"low", "medium", "high", "critical"}

    def test_empty_evidence_still_returns_a_valid_shape(self, client):
        """A sparse package must not 500; it produces a low-confidence answer or a clean error."""
        response = client.post("/analyze", json={})

        assert response.status_code in (200, 502, 503)
        if response.status_code == 200:
            assert "root_cause" in response.json()

    def test_malformed_body_is_a_validation_error_not_a_crash(self, client):
        response = client.post("/analyze", json={"incident": "should be an object"})

        assert response.status_code == 422

    def test_oversized_request_is_rejected_before_model_execution(self, client):
        response = client.post("/analyze-error", json={"log": "x" * 70_000})

        assert response.status_code == 413


def test_rate_limiter_is_bounded():
    limiter = main.AiApiSecurityMiddleware.__module__
    assert limiter == "kairon.security"

    from kairon.security import FixedWindowRequestLimiter

    budget = FixedWindowRequestLimiter(permit_limit=2, window_seconds=60)
    assert budget.allow("backend", now=1)
    assert budget.allow("backend", now=2)
    assert not budget.allow("backend", now=3)
    assert budget.allow("backend", now=62)


class TestLegacyEndpointsPreserved:
    def test_analyze_error(self, client):
        response = client.post("/analyze-error", json={"log": "NullReferenceException at line 42"})

        assert response.status_code == 200
        body = response.json()
        assert set(body) >= {"root_cause", "severity", "severity_score", "fixes", "prevention"}

    def test_predict(self, client):
        response = client.post("/predict", json={"recent_logs": ["a", "b"], "current_log": "c"})

        assert response.status_code == 200
        assert set(response.json()) >= {"failure_risk_score", "risk_level", "reasoning"}

    def test_recommend(self, client):
        response = client.post("/recommend", json={"context": "a busy API"})

        assert response.status_code == 200
        assert "recommendations" in response.json()

    def test_suggest_fixes(self, client):
        response = client.post("/suggest-fixes", json={"mismatches": [{"path": "id", "issue": "type mismatch"}]})

        assert response.status_code == 200
        assert "suggestions" in response.json()

    def test_extract_json_is_still_importable_from_main(self):
        """Preserved re-export, so anything importing it from main keeps working."""
        assert main.extract_json('{"a": 1}') == {"a": 1}
