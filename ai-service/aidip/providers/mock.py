"""Deterministic mock provider (AI PRD section 13).

Mock mode is not a stub that returns a fixed string: it reads the actual evidence and produces a
plausible, self-consistent investigation from it. That is what makes the whole demo and the whole
test suite runnable without a paid API key, while still exercising the real validation path.
"""

from __future__ import annotations

import json
from typing import Any

from .base import AIProvider

# Markers used to route legacy prompts to the right canned shape, matching the original service.
_ERROR_MARKER = "root_cause"
_PREDICT_MARKER = "risk_level"
_RECOMMEND_MARKER = "recommendations"


class MockProvider(AIProvider):
    name = "mock"

    @property
    def requires_credentials(self) -> bool:
        return False

    def __init__(self, config):
        super().__init__(config)
        self.model = "deterministic-mock"

    async def _invoke(self, system: str, user: str) -> str:
        return json.dumps(self._respond(system, user))

    def _respond(self, system: str, user: str) -> Any:
        if "site reliability engineer" in system:
            return self._investigation(user)

        # Legacy task routing, preserved exactly so the original screens behave identically in
        # mock mode as they did before.
        if _ERROR_MARKER in system:
            return {
                "root_cause": "Mock: Null reference before initialization",
                "severity": "high",
                "severity_score": 78,
                "fixes": ["Add null check", "Initialize default value"],
                "prevention": "Use TypeScript interfaces",
            }

        if _PREDICT_MARKER in system:
            return {
                "failure_risk_score": 65,
                "risk_level": "moderate",
                "reasoning": "Mock: Timeout cascade pattern suggests downstream service degradation",
            }

        if _RECOMMEND_MARKER in system:
            return {
                "recommendations": [
                    {"category": "performance", "suggestion": "Mock: Add Redis caching for session store"},
                    {"category": "security", "suggestion": "Mock: Add rate limiting to /login"},
                ]
            }

        return {"suggestions": [{"path": "mock", "explanation": "Mock: Cast string to integer using parseInt()"}]}

    def _investigation(self, user: str) -> dict:
        """Builds an investigation from the evidence embedded in the user prompt.

        Reading the evidence rather than ignoring it is what makes the mock useful: the demo shows
        the real numbers, and a test can assert that the diagnosis actually tracks the signals.
        """
        evidence = self._parse_evidence(user)

        signals = evidence.get("correlated_signals") or []
        metrics = {s.get("metric") for s in signals if isinstance(s, dict)}
        incident = evidence.get("incident") or {}
        available = [a.get("action") for a in (evidence.get("available_actions") or []) if a.get("action")]

        has_retries = "retries" in metrics
        has_cpu = "cpu" in metrics
        has_latency = "latency" in metrics
        has_errors = bool(metrics & {"errorRate", "errors"})

        if has_retries:
            root_cause = "Controlled retry loop causing repeated downstream requests, saturating worker threads."
            confidence = 0.92
            preferred = "DisableDemoRetryLoop"
            predicted = (
                "Order processing latency will continue increasing and the request backlog will keep growing."
            )
        elif has_cpu and has_latency:
            root_cause = "CPU saturation is driving request latency above the configured threshold."
            confidence = 0.78
            preferred = "ReduceDemoWorkerConcurrency"
            predicted = "Latency will continue rising and begin affecting dependent endpoints."
        elif has_errors:
            root_cause = "A repeating downstream failure is driving the error rate above threshold."
            confidence = 0.71
            preferred = "RestartDemoService"
            predicted = "The error rate will remain elevated and failed requests will accumulate."
        else:
            root_cause = "Resource pressure on the affected service."
            confidence = 0.55
            preferred = "RunHealthCheck"
            predicted = "Degradation is likely to continue without intervention."

        # Only ever recommend something the backend actually offered. If the preferred tool is not
        # on the list, fall back to the first available one rather than naming a tool that does not
        # exist.
        action = preferred if preferred in available else (available[0] if available else "")

        recommendations = []
        if action:
            recommendations.append(
                {
                    "action": action,
                    "reason": "The leading signal in the supplied evidence points to this as the controllable cause.",
                    "expected_outcome": "The breached metrics return toward their baseline.",
                    "risk_level": "low",
                }
            )

        symptoms = incident.get("symptoms") or []

        return {
            "summary": f"{incident.get('service', 'Service')} is degraded: " + "; ".join(str(s) for s in symptoms[:3]),
            "root_cause": root_cause,
            "contributing_factors": [str(s.get("symptom", "")) for s in signals[:5] if isinstance(s, dict)],
            "evidence": [
                f"{s.get('metric')} {s.get('observed')}{s.get('unit', '')} vs threshold "
                f"{s.get('threshold')}{s.get('unit', '')}"
                for s in signals[:6]
                if isinstance(s, dict)
            ],
            "confidence": confidence,
            "severity": str(incident.get("severity", "medium")).lower(),
            "affected_components": [
                c for c in [incident.get("affected_component"), incident.get("service")] if c
            ],
            "predicted_failure": predicted,
            "estimated_risk": "high" if str(incident.get("severity", "")).lower() in {"high", "critical"} else "medium",
            "recommendations": recommendations,
        }

    @staticmethod
    def _parse_evidence(user: str) -> dict:
        """Recovers the JSON evidence block from the prompt, tolerating a missing one."""
        start = user.find("{")
        end = user.rfind("}")
        if start == -1 or end <= start:
            return {}

        try:
            parsed = json.loads(user[start : end + 1])
            return parsed if isinstance(parsed, dict) else {}
        except json.JSONDecodeError:
            return {}
