"""Never trust raw model output (AI PRD section 8).

Every model response goes through: extract -> parse -> validate -> normalize -> reject. A response
that cannot survive that is refused with a controlled error rather than allowed to write arbitrary
text into incident state.
"""

from __future__ import annotations

import json
import re
from typing import Any, Dict, List

from .schemas import EvidencePackage, InvestigationResult, Recommendation

SEVERITIES = {"info", "low", "medium", "high", "critical"}
RISKS = {"low", "medium", "high", "critical"}
MAX_TEXT_CHARS = 4000
MAX_SHORT_TEXT_CHARS = 500
MAX_LIST_ITEMS = 12
MAX_LIST_ITEM_CHARS = 1000
MAX_RECOMMENDATIONS = 10
MAX_PARAMETERS = 20

# Models routinely wrap JSON in prose or fences; these are the shapes worth recovering from before
# giving up on a response.
_FENCE = re.compile(r"^```(?:json)?\s*|\s*```$", re.MULTILINE)


class AiResponseError(ValueError):
    """Raised when a model response cannot be turned into a valid structured result."""


def extract_json(text: str) -> Any:
    """Pulls a JSON document out of a model response.

    Kept tolerant on purpose: a fenced or prose-wrapped answer is a formatting slip, not a reason
    to throw away an otherwise correct diagnosis. What is *not* tolerated is invalid JSON.
    """
    if text is None:
        raise AiResponseError("Model returned no content")

    candidate = str(text).strip()
    if not candidate:
        raise AiResponseError("Model returned empty content")

    if candidate.startswith("```"):
        candidate = _FENCE.sub("", candidate).strip()

    try:
        return json.loads(candidate)
    except json.JSONDecodeError:
        pass

    # Last resort: the outermost {...} in the response.
    start = candidate.find("{")
    end = candidate.rfind("}")
    if start != -1 and end > start:
        try:
            return json.loads(candidate[start : end + 1])
        except json.JSONDecodeError as exc:
            raise AiResponseError(f"Model response was not valid JSON: {exc.msg}") from exc

    raise AiResponseError("Model response contained no JSON object")


def _text(value: Any, limit: int = MAX_TEXT_CHARS) -> str:
    return str(value or "").strip()[:limit]


def _as_list_of_str(value: Any, limit: int = MAX_LIST_ITEMS) -> List[str]:
    if value is None:
        return []
    if isinstance(value, str):
        return [_text(value, MAX_LIST_ITEM_CHARS)][:limit]
    if isinstance(value, (list, tuple)):
        return [_text(v, MAX_LIST_ITEM_CHARS) for v in value if v is not None][:limit]
    return [_text(value, MAX_LIST_ITEM_CHARS)][:limit]


def _as_float(value: Any, default: float = 0.0) -> float:
    try:
        return float(value)
    except (TypeError, ValueError):
        return default


def _normalize_choice(value: Any, allowed: set[str], default: str) -> str:
    text = str(value or "").strip().lower()
    if text in allowed:
        return text
    # A few synonyms models reach for that map cleanly onto the allowed set.
    aliases = {"moderate": "medium", "med": "medium", "sev1": "critical", "warning": "medium"}
    return aliases.get(text, default)


def validate_investigation(
    raw: Any,
    evidence: EvidencePackage | None = None,
    provider: str | None = None,
    model: str | None = None,
) -> InvestigationResult:
    """Validates and normalizes a model investigation response.

    Raises AiResponseError when the response is unusable. A missing root cause is the one field
    treated as fatal: an investigation with no explanation is not an investigation.
    """
    if not isinstance(raw, dict):
        raise AiResponseError("Model response was not a JSON object")

    root_cause = _text(raw.get("root_cause") or raw.get("rootCause"))
    if not root_cause:
        raise AiResponseError("Model response is missing a root cause")

    confidence = _as_float(raw.get("confidence"), 0.0)
    # Models sometimes express confidence as a percentage; 0..1 is the contract. Only values of 2
    # or more are read as a percentage - a 1.x value is a botched 0..1 number, not "150%", and
    # dividing it by 100 would silently turn high confidence into near-zero.
    if confidence >= 2.0:
        confidence = confidence / 100.0
    confidence = max(0.0, min(1.0, confidence))

    allowed_actions = (
        {a.action.strip().lower() for a in evidence.available_actions if a.action}
        if evidence is not None
        else set()
    )

    recommendations: List[Recommendation] = []
    for item in raw.get("recommendations") or []:
        if not isinstance(item, dict):
            continue

        action = _text(item.get("action"), MAX_SHORT_TEXT_CHARS)
        if not action:
            continue

        # A recommendation naming a tool the backend never offered is dropped here rather than
        # forwarded. Backend policy would refuse it anyway; refusing it at the source keeps the
        # operator's recommendation list honest.
        if evidence is not None and action.lower() not in allowed_actions:
            continue

        parameters = item.get("parameters")
        if not isinstance(parameters, dict):
            parameters = None
        else:
            parameters = {
                _text(k, 100): _text(v, MAX_LIST_ITEM_CHARS)
                for k, v in list(parameters.items())[:MAX_PARAMETERS]
            }

        recommendations.append(
            Recommendation(
                action=action,
                reason=_text(item.get("reason")),
                expected_outcome=_text(item.get("expected_outcome") or item.get("expectedOutcome")),
                risk_level=_normalize_choice(item.get("risk_level") or item.get("riskLevel"), RISKS, "medium"),
                parameters=parameters,
            )
        )
        if len(recommendations) >= MAX_RECOMMENDATIONS:
            break

    return InvestigationResult(
        summary=_text(raw.get("summary")),
        root_cause=root_cause,
        contributing_factors=_as_list_of_str(raw.get("contributing_factors") or raw.get("contributingFactors")),
        evidence=_as_list_of_str(raw.get("evidence")),
        confidence=confidence,
        severity=_normalize_choice(raw.get("severity"), SEVERITIES, "medium"),
        affected_components=_as_list_of_str(raw.get("affected_components") or raw.get("affectedComponents")),
        predicted_failure=_text(raw.get("predicted_failure") or raw.get("predictedFailure")),
        estimated_risk=_normalize_choice(raw.get("estimated_risk") or raw.get("estimatedRisk"), RISKS, "medium"),
        recommendations=recommendations,
        provider=provider,
        model=model,
    )


def validate_error_analysis(raw: Any) -> Dict[str, Any]:
    """Validates the legacy /analyze-error response shape."""
    if not isinstance(raw, dict):
        raise AiResponseError("Model response was not a JSON object")

    root_cause = _text(raw.get("root_cause"))
    if not root_cause:
        raise AiResponseError("Model response is missing a root cause")

    score = _as_float(raw.get("severity_score"), 0.0)

    return {
        "root_cause": root_cause,
        "severity": _normalize_choice(raw.get("severity"), SEVERITIES, "medium"),
        "severity_score": int(max(0, min(100, score))),
        "fixes": _as_list_of_str(raw.get("fixes")),
        "prevention": _text(raw.get("prevention")),
    }


def validate_prediction(raw: Any) -> Dict[str, Any]:
    """Validates the legacy /predict response shape."""
    if not isinstance(raw, dict):
        raise AiResponseError("Model response was not a JSON object")

    score = _as_float(raw.get("failure_risk_score"), 0.0)

    return {
        "failure_risk_score": int(max(0, min(100, score))),
        "risk_level": _normalize_choice(raw.get("risk_level"), {"low", "moderate", "high"}, "moderate"),
        "reasoning": _text(raw.get("reasoning")),
    }


def validate_recommendations(raw: Any) -> Dict[str, Any]:
    """Validates the legacy /recommend response shape."""
    if not isinstance(raw, dict):
        raise AiResponseError("Model response was not a JSON object")

    items = []
    candidates = raw.get("recommendations") or []
    if not isinstance(candidates, (list, tuple)):
        candidates = []
    for item in candidates[:MAX_RECOMMENDATIONS]:
        if not isinstance(item, dict):
            continue
        suggestion = _text(item.get("suggestion"))
        if not suggestion:
            continue
        items.append(
            {
                "category": _text(item.get("category") or "general", MAX_SHORT_TEXT_CHARS),
                "suggestion": suggestion,
            }
        )

    return {"recommendations": items}


def validate_fix_suggestions(raw: Any) -> Dict[str, Any]:
    """Validates the legacy /suggest-fixes response shape."""
    if not isinstance(raw, dict):
        raise AiResponseError("Model response was not a JSON object")

    items = []
    candidates = raw.get("suggestions") or []
    if not isinstance(candidates, (list, tuple)):
        candidates = []
    for item in candidates[:MAX_RECOMMENDATIONS]:
        if not isinstance(item, dict):
            continue
        explanation = _text(item.get("explanation"))
        if not explanation:
            continue
        items.append(
            {
                "path": _text(item.get("path"), MAX_LIST_ITEM_CHARS),
                "explanation": explanation,
            }
        )

    return {"suggestions": items}
