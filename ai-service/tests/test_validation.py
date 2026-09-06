"""Response parsing and validation (AI PRD sections 8 and 18).

The rule these tests encode: never trust raw model output. Malformed output is rejected with a
controlled error rather than written into incident state.
"""

from __future__ import annotations

import pytest

from kairon.schemas import AvailableAction, EvidencePackage
from kairon.validation import (
    AiResponseError,
    extract_json,
    validate_error_analysis,
    validate_fix_suggestions,
    validate_investigation,
    validate_prediction,
    validate_recommendations,
)


class TestExtractJson:
    def test_plain_json(self):
        assert extract_json('{"a": 1}') == {"a": 1}

    def test_fenced_json(self):
        assert extract_json('```json\n{"a": 1}\n```') == {"a": 1}

    def test_bare_fence(self):
        assert extract_json('```\n{"a": 1}\n```') == {"a": 1}

    def test_json_wrapped_in_prose(self):
        text = 'Here is my analysis:\n{"a": 1}\nHope that helps!'
        assert extract_json(text) == {"a": 1}

    @pytest.mark.parametrize("text", ["", "   ", None, "no json here at all"])
    def test_unusable_content_is_rejected(self, text):
        with pytest.raises(AiResponseError):
            extract_json(text)

    def test_invalid_json_is_rejected(self):
        with pytest.raises(AiResponseError):
            extract_json('{"a": 1,,,}')


class TestInvestigationValidation:
    def _valid(self) -> dict:
        return {
            "summary": "Service degraded",
            "root_cause": "Retry loop",
            "contributing_factors": ["cpu", "latency"],
            "evidence": ["retries 90/min"],
            "confidence": 0.9,
            "severity": "high",
            "affected_components": ["OrderProcessingService"],
            "predicted_failure": "Backlog grows",
            "estimated_risk": "high",
            "recommendations": [
                {
                    "action": "RunHealthCheck",
                    "reason": "leading signal",
                    "expected_outcome": "retries drop",
                    "risk_level": "low",
                }
            ],
        }

    def test_valid_response(self, retry_storm_evidence):
        result = validate_investigation(self._valid(), evidence=retry_storm_evidence)

        assert result.root_cause == "Retry loop"
        assert result.confidence == 0.9
        assert len(result.recommendations) == 1
        assert result.recommendations[0].action == "RunHealthCheck"

    def test_missing_root_cause_is_fatal(self):
        payload = self._valid()
        payload.pop("root_cause")

        with pytest.raises(AiResponseError):
            validate_investigation(payload)

    def test_empty_root_cause_is_fatal(self):
        payload = self._valid()
        payload["root_cause"] = "   "

        with pytest.raises(AiResponseError):
            validate_investigation(payload)

    def test_non_object_response_is_rejected(self):
        for payload in ([], "a string", 42, None):
            with pytest.raises(AiResponseError):
                validate_investigation(payload)

    def test_camel_case_keys_are_accepted(self):
        payload = {
            "root_cause": "Retry loop",
            "contributingFactors": ["a"],
            "predictedFailure": "backlog",
            "estimatedRisk": "high",
            "affectedComponents": ["svc"],
        }
        result = validate_investigation(payload)

        assert result.contributing_factors == ["a"]
        assert result.predicted_failure == "backlog"
        assert result.estimated_risk == "high"

    @pytest.mark.parametrize(
        "raw,expected",
        [(1.5, 1.0), (-3, 0.0), (95, 0.95), ("0.8", 0.8), ("nonsense", 0.0), (None, 0.0)],
    )
    def test_confidence_is_clamped_and_coerced(self, raw, expected):
        payload = {"root_cause": "x", "confidence": raw}
        assert validate_investigation(payload).confidence == pytest.approx(expected)

    @pytest.mark.parametrize(
        "raw,expected",
        [("HIGH", "high"), ("moderate", "medium"), ("unknown-value", "medium"), (None, "medium")],
    )
    def test_severity_is_normalized(self, raw, expected):
        payload = {"root_cause": "x", "severity": raw}
        assert validate_investigation(payload).severity == expected

    def test_unregistered_action_is_dropped(self, retry_storm_evidence):
        """A model naming a tool the backend never offered gets that recommendation dropped."""
        payload = self._valid()
        payload["recommendations"] = [
            {"action": "rm -rf /", "reason": "no", "expected_outcome": "no", "risk_level": "low"},
            {"action": "DeleteProductionDatabase", "reason": "no", "expected_outcome": "no", "risk_level": "low"},
        ]

        result = validate_investigation(payload, evidence=retry_storm_evidence)

        assert result.recommendations == []

    def test_registered_action_survives_case_differences(self, retry_storm_evidence):
        payload = self._valid()
        payload["recommendations"][0]["action"] = "runhealthcheck"

        result = validate_investigation(payload, evidence=retry_storm_evidence)

        assert len(result.recommendations) == 1

    def test_recommendation_without_an_action_is_dropped(self, retry_storm_evidence):
        payload = self._valid()
        payload["recommendations"] = [{"reason": "something", "action": ""}]

        assert validate_investigation(payload, evidence=retry_storm_evidence).recommendations == []

    def test_malformed_recommendation_entries_are_skipped(self, retry_storm_evidence):
        payload = self._valid()
        payload["recommendations"] = ["a string", 42, None, payload["recommendations"][0]]

        result = validate_investigation(payload, evidence=retry_storm_evidence)

        assert len(result.recommendations) == 1

    def test_scalar_list_fields_are_coerced(self):
        payload = {"root_cause": "x", "contributing_factors": "just one", "evidence": None}
        result = validate_investigation(payload)

        assert result.contributing_factors == ["just one"]
        assert result.evidence == []

    def test_no_available_actions_means_no_recommendations(self):
        """An explicitly empty tool boundary must fail closed, not allow arbitrary actions."""
        payload = {"root_cause": "x", "recommendations": [{"action": "Anything"}]}
        result = validate_investigation(payload, evidence=EvidencePackage())

        assert result.recommendations == []

    def test_model_output_text_lists_and_parameters_are_bounded(self, retry_storm_evidence):
        payload = self._valid()
        payload["root_cause"] = "x" * 10_000
        payload["contributing_factors"] = ["y" * 2_000] * 30
        payload["recommendations"] = [
            {
                "action": "RunHealthCheck",
                "reason": "z" * 10_000,
                "parameters": {f"key-{i}": "v" * 2_000 for i in range(50)},
            }
        ] * 20

        result = validate_investigation(payload, evidence=retry_storm_evidence)

        assert len(result.root_cause) == 4_000
        assert len(result.contributing_factors) == 12
        assert all(len(value) <= 1_000 for value in result.contributing_factors)
        assert len(result.recommendations) == 10
        assert all(len(item.reason) <= 4_000 for item in result.recommendations)
        assert all(len(item.parameters or {}) <= 20 for item in result.recommendations)

    def test_provider_metadata_is_attached(self, retry_storm_evidence):
        result = validate_investigation(
            self._valid(), evidence=retry_storm_evidence, provider="gemini", model="gemini-2.5-flash-lite"
        )

        assert result.provider == "gemini"
        assert result.model == "gemini-2.5-flash-lite"


class TestLegacyValidation:
    def test_error_analysis(self):
        result = validate_error_analysis(
            {"root_cause": "npe", "severity": "HIGH", "severity_score": 150, "fixes": ["a"], "prevention": "b"}
        )

        assert result["severity"] == "high"
        assert result["severity_score"] == 100, "score is clamped to 0..100"

    def test_error_analysis_requires_root_cause(self):
        with pytest.raises(AiResponseError):
            validate_error_analysis({"severity": "high"})

    def test_prediction_clamps_and_normalizes(self):
        result = validate_prediction({"failure_risk_score": -5, "risk_level": "MODERATE", "reasoning": "x"})

        assert result["failure_risk_score"] == 0
        assert result["risk_level"] == "moderate"

    def test_recommendations_drop_empty_suggestions(self):
        result = validate_recommendations(
            {"recommendations": [{"category": "perf", "suggestion": ""}, {"category": "perf", "suggestion": "ok"}]}
        )

        assert len(result["recommendations"]) == 1

    def test_fix_suggestions_drop_empty_explanations(self):
        result = validate_fix_suggestions(
            {"suggestions": [{"path": "a", "explanation": ""}, {"path": "b", "explanation": "ok"}]}
        )

        assert len(result["suggestions"]) == 1

    @pytest.mark.parametrize(
        "validator", [validate_error_analysis, validate_prediction, validate_recommendations, validate_fix_suggestions]
    )
    def test_every_legacy_validator_rejects_non_objects(self, validator):
        with pytest.raises(AiResponseError):
            validator(["not", "an", "object"])
