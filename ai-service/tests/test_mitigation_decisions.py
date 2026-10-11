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
    DependencySummary,
    RemediationOutcome,
    SignalOnset,
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


async def test_server_errors_with_normal_resources_are_investigated_not_restarted(mock_config):
    result = await AiService(mock_config).investigate(_evidence(["errorRate"], ["RestartApplication"], error_type="KeyError", error_message="'sku'"))

    assert result.recommendations == []
    restart = next(c for c in result.considered_actions if c.action == "RestartApplication")
    assert restart.verdict == "not_recommended" and "code path" in restart.reason
    assert any("KeyError" in s for s in result.next_steps)
    assert result.alternative_explanations and result.verification_steps


async def test_errors_from_the_processs_own_state_get_a_restart_as_a_temporary_mitigation(mock_config):
    result = await AiService(mock_config).investigate(
        _evidence(["errorRate"], ["RestartApplication"], error_type="OSError", error_message="[Errno 24] Too many open files"))

    assert [r.action for r in result.recommendations] == ["RestartApplication"]
    rec = result.recommendations[0]
    assert rec.kind == "temporary_mitigation" and "temporary mitigation" in rec.reason
    assert rec.risks and rec.verification


async def test_a_failing_upstream_api_with_retries_points_at_the_upstream(mock_config):
    evidence = _evidence(["errorRate", "retries"], ["RestartApplication"], error_type="HTTPException",
                         error_message="Payment provider rejected the charge")
    evidence.dependencies.append(DependencySummary(target="payments.example:443/charge", calls=90, failures=63, avg_duration_ms=120))

    result = await AiService(mock_config).investigate(evidence)

    assert result.recommendations == []
    assert "payments.example:443/charge" in result.root_cause and "retries" in result.root_cause
    assert any("rate limiting" in s for s in result.next_steps)


async def test_database_latency_before_errors_points_at_the_database(mock_config):
    evidence = _evidence(["errorRate", "latency"], ["RestartApplication"])
    evidence.dependencies.append(DependencySummary(target="orders-db:5432", calls=200, failures=12, avg_duration_ms=2400))
    evidence.signal_onsets += [SignalOnset(metric="latency", label="Latency", seconds_before_detection=90),
                               SignalOnset(metric="errorRate", label="Error rate", seconds_before_detection=20)]

    result = await AiService(mock_config).investigate(evidence)

    assert result.recommendations == []
    assert "orders-db:5432" in result.root_cause and "latency rose before the errors" in result.root_cause
    assert any("Only add database capacity if" in s for s in result.next_steps)


async def test_a_restart_that_failed_for_the_same_fault_is_not_recommended_again(mock_config):
    evidence = _evidence(["cpu"], ["RestartApplication"])
    evidence.remediation_history.append(RemediationOutcome(incident_key="INC-0003", action="RestartApplication",
                                                           status="Executed", verification="Failed", same_fault=True))

    result = await AiService(mock_config).investigate(evidence)

    assert result.recommendations == []
    assert "INC-0003" in result.root_cause


async def test_missing_evidence_is_reported_not_guessed(mock_config):
    evidence = _evidence(["errorRate"], [])
    evidence.evidence_gaps.append("Server errors without stack traces - the failing code location is not visible.")

    result = await AiService(mock_config).investigate(evidence)

    assert result.root_cause_certainty == "unknown"
    assert any("stack traces" in m for m in result.missing_evidence)


def test_the_new_evidence_reaches_the_model_and_the_instructions_put_explanation_first():
    evidence = _evidence(["errorRate"], ["RestartApplication"], error_type="KeyError")
    evidence.related_errors[0].stack_excerpt = "File app.py, line 42\nKeyError: 'sku'"
    evidence.dependencies.append(DependencySummary(target="orders-db:5432", calls=10, failures=2))
    evidence.signal_onsets.append(SignalOnset(metric="latency", label="Latency", seconds_before_detection=60))
    evidence.remediation_history.append(RemediationOutcome(action="RestartApplication", status="Executed", verification="Failed"))
    evidence.evidence_gaps.append("No memory measurements.")
    prompt = investigation_user_prompt(evidence)
    payload = json.loads(prompt[prompt.index("{"): prompt.rindex("}") + 1])

    assert payload["related_errors"][0]["stack_excerpt"].endswith("KeyError: 'sku'")
    assert payload["dependencies"][0]["target"] == "orders-db:5432"
    assert payload["signal_onsets"][0]["metric"] == "latency"
    assert payload["remediation_history"][0]["verification"] == "Failed"
    assert payload["evidence_gaps"] == ["No memory measurements."]
    rules = GROUND_RULES.lower()
    assert "explain before you act" in rules and "correlation is not causation" in rules
    assert "recommend no action rather than forcing one" in rules
    for field in ("alternative_explanations", "missing_evidence", "verification_steps", "temporary_mitigation|root_cause_fix|diagnostic"):
        assert field in investigation_system_prompt()


async def test_a_database_outage_gets_a_dependency_answer_not_a_restart(mock_config):
    result = await AiService(mock_config).investigate(
        _evidence(["errorRate"], ["RestartService", "RunHealthCheck"], error_type="OperationalError",
                  error_message="could not connect to database: Connection refused"))

    assert [r.action for r in result.recommendations] == ["RunHealthCheck"]
    restart = next(c for c in result.considered_actions if c.action == "RestartService")
    assert restart.verdict == "not_recommended" and "database" in restart.reason
    assert any("database" in s for s in result.next_steps)


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
