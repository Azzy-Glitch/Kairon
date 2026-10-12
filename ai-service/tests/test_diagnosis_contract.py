"""The shared diagnosis contract (tests/fixtures/diagnosis-scenarios.json).

The backend's DeterministicInvestigation runs the same file (tests/Kairon.Backend.Tests/
DiagnosisContractTests.cs), so the AI service's mock provider and the backend's offline rules
cannot drift apart unnoticed.
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from kairon.schemas import EvidencePackage
from kairon.service import AiService

_FIXTURE = Path(__file__).resolve().parents[2] / "tests" / "fixtures" / "diagnosis-scenarios.json"
_SCENARIOS = json.loads(_FIXTURE.read_text(encoding="utf-8"))["scenarios"]


@pytest.mark.parametrize("scenario", _SCENARIOS, ids=[s["name"] for s in _SCENARIOS])
async def test_the_ai_service_satisfies_the_shared_scenario(mock_config, scenario):
    result = await AiService(mock_config).investigate(EvidencePackage.model_validate(scenario["evidence"]))
    expect = scenario["expect"]

    if "certainty" in expect:
        assert result.root_cause_certainty == expect["certainty"]
    if "recommends" in expect:
        assert [r.action for r in result.recommendations] == expect["recommends"]
    for text in expect.get("root_contains", []):
        assert text in result.root_cause
    for text in expect.get("root_excludes", []):
        assert text not in result.root_cause
    for text in expect.get("alternatives_contain", []):
        assert any(text.lower() in a.lower() for a in result.alternative_explanations)
    for text in expect.get("next_steps_contain", []):
        assert any(text.lower() in s.lower() for s in result.next_steps)
    for text in expect.get("evidence_excludes", []):
        assert not any(text.lower() in e.lower() for e in result.evidence)
    if "restart_verdict" in expect:
        assert next(c for c in result.considered_actions if c.action == "RestartApplication").verdict == expect["restart_verdict"]
