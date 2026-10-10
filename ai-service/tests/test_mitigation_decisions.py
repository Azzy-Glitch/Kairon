"""Mitigation vs root-cause decisions.

The model is told that an HTTP 500 alone neither establishes the cause nor rules a restart out, to
separate temporary mitigation from root-cause correction, and to treat a problem that came back after
a restart as unresolved. The deterministic mock provider applies the same rules, so offline/test mode
exercises the real recommendation flow. Validation keeps the new structured fields honest.
"""

from __future__ import annotations

import json

from kairon.prompts import GROUND_RULES, investigation_system_prompt, investigation_user_prompt
from kairon.schemas import (
    AvailableAction,
    CorrelatedSignal,
    EndpointSummary,
    EvidencePackage,
    IncidentContext,
    RecurrenceContext,
    RelatedError,
    RemediationTargetContext,
    RiskForecast,
)
from kairon.service import AiService
from kairon.validation import validate_investigation


def _evidence(metrics, offered, error_type=None, error_message=None, recurred=False, forecast=None):
    evidence = EvidencePackage(
        incident=IncidentContext(service="Checkout", severity="high", symptoms=["Error rate 60%"]),
        correlated_signals=[CorrelatedSignal(metric=m, symptom=m, observed=60, threshold=10, unit="%") for m in metrics],
        available_actions=[AvailableAction(action=a, description="", risk_level="medium") for a in offered],
        recurrence=RecurrenceContext(recurred_after_restart=recurred, minutes_since_restart=3 if recurred else None),
        remediation_target=RemediationTargetContext(kind="AppProcess", telemetry_machine_scoped=True),
        risk_forecast=forecast,
    )
    if error_type or error_message:
        evidence.related_errors.append(RelatedError(endpoint="/checkout", status_code=500, error_type=error_type, error_message=error_message))
        evidence.endpoint_breakdown.append(EndpointSummary(endpoint="/checkout", requests=10, server_errors=6, top_error_type=error_type))
    return evidence


# --- Instructions --------------------------------------------------------------------------------


def test_the_instructions_no_longer_treat_an_exception_as_proof_that_a_restart_cannot_help():
    rules = GROUND_RULES.lower()
    assert "does not prove a restart cannot help" in rules
    assert "temporary mitigation" in rules
    assert "recurred_after_restart" in rules
    assert "never present a code bug" in rules
    # The old blanket rule is gone.
    assert "errors with an application exception type point to application code" not in rules


def test_the_schema_asks_for_certainty_next_steps_and_every_considered_action():
    system = investigation_system_prompt()
    for field in ("root_cause_certainty", "next_steps", "considered_actions"):
        assert field in system


def test_forecast_recurrence_and_target_kind_reach_the_model_inside_the_untrusted_block():
    forecast = RiskForecast(outcome="elevated", risk_level="high", failure_mode="Requests keep failing with server errors.")
    prompt = investigation_user_prompt(_evidence(["errorRate"], ["RestartApplication"], recurred=True, forecast=forecast))
    payload = json.loads(prompt[prompt.index("{"): prompt.rindex("}") + 1])

    assert payload["risk_forecast"]["outcome"] == "elevated"
    assert payload["recurrence"]["recurred_after_restart"] is True
    assert payload["remediation_target"]["kind"] == "AppProcess"


# --- Deterministic (mock) decisions --------------------------------------------------------------


async def test_a_sudden_server_error_spike_gets_the_offered_restart_as_a_mitigation(mock_config):
    result = await AiService(mock_config).investigate(
        _evidence(["errorRate"], ["RestartApplication"], error_type="HTTPException", error_message="Payment provider rejected the charge"))

    assert [r.action for r in result.recommendations] == ["RestartApplication"]
    assert "temporary mitigation" in result.recommendations[0].reason
    assert "cannot fix a code defect" in result.recommendations[0].reason
    assert result.root_cause_certainty == "possible"
    assert any(c.action == "RestartApplication" and c.verdict == "recommended" for c in result.considered_actions)


async def test_a_database_outage_gets_a_dependency_answer_not_a_restart(mock_config):
    result = await AiService(mock_config).investigate(
        _evidence(["errorRate"], ["RestartService", "RunHealthCheck"], error_type="OperationalError",
                  error_message="could not connect to database: Connection refused"))

    assert [r.action for r in result.recommendations] == ["RunHealthCheck"]
    restart = next(c for c in result.considered_actions if c.action == "RestartService")
    assert restart.verdict == "not_recommended" and "dependency" in restart.reason
    assert any("dependency" in s for s in result.next_steps)


async def test_errors_that_returned_after_a_restart_are_investigated_not_restarted_again(mock_config):
    result = await AiService(mock_config).investigate(
        _evidence(["errorRate"], ["RestartApplication"], error_type="KeyError", recurred=True))

    assert result.recommendations == []
    assert any(c.action == "RestartApplication" and c.verdict == "not_recommended" for c in result.considered_actions)
    assert any("KeyError" in s for s in result.next_steps)
    assert result.root_cause_certainty != "confirmed"


async def test_cpu_pressure_inside_the_app_gets_a_restart_mitigation(mock_config):
    result = await AiService(mock_config).investigate(_evidence(["cpu", "latency"], ["RestartApplication"]))

    assert [r.action for r in result.recommendations] == ["RestartApplication"]
    assert result.root_cause_certainty == "likely"


async def test_an_elevated_forecast_becomes_the_prediction_and_no_data_is_inconclusive(mock_config):
    forecast = RiskForecast(outcome="elevated", risk_level="critical", failure_mode="Requests keep failing with server errors.")
    service = AiService(mock_config)

    predicted = await service.investigate(_evidence(["errorRate"], ["RestartApplication"], forecast=forecast))
    unknown = await service.investigate(_evidence(["errorRate"], ["RestartApplication"]))

    assert predicted.predicted_failure == "Requests keep failing with server errors."
    assert predicted.estimated_risk == "critical"
    assert unknown.predicted_failure.startswith("Prediction inconclusive")


async def test_nothing_is_recommended_or_considered_that_kairon_did_not_offer(mock_config):
    result = await AiService(mock_config).investigate(_evidence(["errorRate"], []))

    assert result.recommendations == []
    assert result.considered_actions == []


# --- Validation ----------------------------------------------------------------------------------


def test_validation_keeps_only_offered_considered_actions_and_normalizes_certainty():
    evidence = _evidence(["errorRate"], ["RestartApplication"])
    result = validate_investigation(
        {
            "root_cause": "Unknown server errors",
            "root_cause_certainty": "Probably",
            "next_steps": ["Check the payment client", "", "Review recent deploys"],
            "considered_actions": [
                {"action": "RestartApplication", "verdict": "Not_Recommended", "reason": "Dependency is down"},
                {"action": "rm -rf /", "verdict": "recommended", "reason": "invented"},
            ],
        },
        evidence,
    )

    assert result.root_cause_certainty == "unknown"
    assert [c.action for c in result.considered_actions] == ["RestartApplication"]
    assert result.considered_actions[0].verdict == "not_recommended"
    assert "Check the payment client" in result.next_steps
