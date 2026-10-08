"""Endpoint breakdown and remediation-target context reach the model, bounded and untrusted."""

from __future__ import annotations

import json

from kairon.prompts import investigation_system_prompt, investigation_user_prompt
from kairon.schemas import EndpointSummary, EvidencePackage, RemediationTargetContext


def _payload(prompt: str) -> dict:
    start = prompt.index("BEGIN_UNTRUSTED_EVIDENCE_JSON") + len("BEGIN_UNTRUSTED_EVIDENCE_JSON")
    end = prompt.index("END_UNTRUSTED_EVIDENCE_JSON")
    return json.loads(prompt[start:end])


def test_schema_accepts_the_backend_breakdown_and_target_fields():
    package = EvidencePackage.model_validate(
        {
            "endpoint_breakdown": [
                {"endpoint": "/checkout", "method": "POST", "requests": 40, "server_errors": 12,
                 "client_errors": 0, "avg_duration_ms": 2100.5, "max_duration_ms": 4000,
                 "top_error_type": None}
            ],
            "remediation_target": {"windows_service": "OrdersSvc", "service_state": "Stopped",
                                   "telemetry_machine_scoped": True},
        }
    )
    assert package.endpoint_breakdown[0].server_errors == 12
    assert package.endpoint_breakdown[0].top_error_type is None
    assert package.remediation_target.service_state == "Stopped"


def test_older_backends_without_the_new_fields_still_validate():
    package = EvidencePackage.model_validate({})
    assert package.endpoint_breakdown == []
    assert package.remediation_target is None


def test_prompt_carries_breakdown_and_target_inside_the_untrusted_block(retry_storm_evidence):
    retry_storm_evidence.endpoint_breakdown = [
        EndpointSummary(endpoint=f"/e{i}", method="GET", requests=10, server_errors=i,
                        client_errors=0, avg_duration_ms=5.0, max_duration_ms=9)
        for i in range(15)
    ]
    retry_storm_evidence.remediation_target = RemediationTargetContext(
        windows_service="OrdersSvc", service_state="Running", telemetry_machine_scoped=True)

    payload = _payload(investigation_user_prompt(retry_storm_evidence))

    assert len(payload["endpoint_breakdown"]) == 10
    assert payload["endpoint_breakdown"][0]["endpoint"] == "/e0"
    assert payload["remediation_target"] == {
        "windows_service": "OrdersSvc", "service_state": "Running", "telemetry_machine_scoped": True}
    assert "endpoint_breakdown" in investigation_system_prompt()


def test_prompt_states_no_target_when_none_is_configured(retry_storm_evidence):
    payload = _payload(investigation_user_prompt(retry_storm_evidence))
    assert payload["remediation_target"] is None
    assert payload["endpoint_breakdown"] == []
