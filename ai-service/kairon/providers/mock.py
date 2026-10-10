"""Deterministic mock provider (AI PRD section 13).

Mock mode is not a stub that returns a fixed string: it reads the actual evidence and produces a
plausible, self-consistent investigation from it. That is what makes the whole demo and the whole
test suite runnable without a paid API key, while still exercising the real validation path.
"""

from __future__ import annotations

import json
import re
from typing import Any

from .base import AIProvider

# Markers used to route legacy prompts to the right canned shape, matching the original service.
_ERROR_MARKER = "root_cause"
_PREDICT_MARKER = "risk_level"
_RECOMMEND_MARKER = "recommendations"

# Error text that points at a dependency rather than at the app's own process. Mirrors the backend's
# DeterministicInvestigation.DependencyPattern.
_DEPENDENCY = re.compile(
    r"connection|connect\b|timed? ?out|timeout|refused|unreachable|database|\bdb\b|operationalerror|sql|dns|socket|econn|reset by peer|service unavailable|\b503\b|\b504\b",
    re.IGNORECASE,
)


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

        It applies the same evidence-based decision rules the real model is instructed to follow
        (and the backend's DeterministicInvestigation applies in its own mock mode), so test mode
        exercises the real recommendation -> policy -> approval flow:

        - a stopped process is started again (StartService) when that is offered;
        - a dependency failure (connection/timeout/database errors) is not fixed by a restart;
        - a problem that came back soon after a restart is a persistent defect, not a transient one;
        - a slowdown with no CPU pressure and no errors points at a slow dependency;
        - otherwise a sudden fault in a previously healthy process is mitigated by the offered
          restart, recommended as a temporary mitigation and never claimed as the fix.
        """
        evidence = self._parse_evidence(user)

        signals = [s for s in (evidence.get("correlated_signals") or []) if isinstance(s, dict)]
        metrics = {s.get("metric") for s in signals}
        incident = evidence.get("incident") or {}
        available = [a.get("action") for a in (evidence.get("available_actions") or []) if isinstance(a, dict) and a.get("action")]
        recurrence = evidence.get("recurrence") or {}
        forecast = evidence.get("risk_forecast") or {}

        has_errors = bool(metrics & {"errorRate", "errors"})
        has_cpu = "cpu" in metrics
        has_memory = "memory" in metrics
        has_latency = "latency" in metrics
        # KAIRON Agent-sourced signals (docs/OBSERVABILITY_MIGRATION.md).
        has_process_crash = "processCrash" in metrics
        has_log_pattern = "logPattern" in metrics

        error_texts = [
            f"{e.get('error_type') or ''} {e.get('error_message') or ''} "
            f"{e.get('status_code') if e.get('status_code') in (503, 504) else ''}"
            for e in (evidence.get("related_errors") or []) if isinstance(e, dict)
        ] + [str(e.get("top_error_type") or "") for e in (evidence.get("endpoint_breakdown") or []) if isinstance(e, dict)]
        # Dependency error text only counts when the incident itself is about errors or slowness.
        dependency = bool(metrics & {"errorRate", "errors", "latency", "retries"}) and any(_DEPENDENCY.search(t) for t in error_texts)
        recurred = bool(recurrence.get("recurred_after_restart"))
        failing = sorted(
            (e for e in (evidence.get("endpoint_breakdown") or []) if isinstance(e, dict) and (e.get("server_errors") or 0) > 0),
            key=lambda e: e.get("server_errors") or 0, reverse=True,
        )
        top_error_type = next((e.get("top_error_type") for e in failing if e.get("top_error_type")), None)

        restart = "RestartApplication" if "RestartApplication" in available else ("RestartService" if "RestartService" in available else None)
        health_check = "RunHealthCheck" if "RunHealthCheck" in available else None
        start = "StartService" if "StartService" in available else None

        next_steps: list = []
        restart_fits = False
        other: list = []  # (action, verdict, reason, recommendation-or-None)

        has_retries = "retries" in metrics
        confidence = 0.3
        if not signals:
            root_cause = "The evidence is not sufficient to identify a cause."
            certainty = "unknown"
            restart_reason = "There is not enough evidence to justify changing anything yet."
            next_steps.append("Wait for more telemetry, then re-investigate.")
        elif has_process_crash:
            # A process that has stopped running is the most unambiguous evidence there is.
            root_cause = "The monitored process stopped running unexpectedly."
            certainty, confidence = "confirmed", 0.95
            restart_reason = "The process is not running, so there is nothing to restart; it needs starting."
            if start:
                other.append((start, "recommended", "The process has stopped; starting it restores the service.", {
                    "action": start,
                    "reason": "The monitored process stopped running; starting it restores availability.",
                    "expected_outcome": "The service runs again; KAIRON verifies recovery afterwards.",
                    "risk_level": "medium",
                }))
            next_steps.append("Check the application's logs for why the process exited.")
        elif recurred:
            minutes = recurrence.get("minutes_since_restart")
            when = f"{minutes:.0f} minute(s) " if isinstance(minutes, (int, float)) else ""
            root_cause = f"The problem returned {when}after a restart, so it is not a one-off transient fault; the underlying cause is still present."
            certainty, confidence = "possible", 0.6
            restart_reason = "A restart already cleared this once and the problem came back; another restart would only hide it briefly."
            next_steps.append("Investigate the application: recent deployments or configuration changes, and the exceptions in the evidence.")
            if top_error_type:
                next_steps.append(f"Start with the most frequent exception, {top_error_type}, and the code path that raises it.")
        elif has_retries:
            root_cause = "Repeated retries may be amplifying a downstream failure; the cause is not yet confirmed."
            certainty, confidence = "possible", 0.92 if dependency else 0.6
            restart_reason = "Retries point at a failing downstream call; restarting the app would reset the retry loop only until it builds up again."
            next_steps.append("Find the downstream call being retried and check that dependency.")
            next_steps.append("Add retry limits with backoff so a failing dependency cannot cause a retry storm.")
        elif has_cpu or has_memory:
            root_cause = ("CPU saturation in the app's own process is driving request latency up." if has_cpu and has_latency
                          else "The app's own process is saturating the CPU." if has_cpu
                          else "The app's own process is using an abnormal amount of memory.")
            certainty, confidence = "likely", 0.78
            restart_fits = True
            restart_reason = ("The pressure is inside the app's own process, which was healthy before; a restart clears the runaway work "
                              "or memory and should restore service as a temporary mitigation. It will build up again if the cause is in the code.")
            next_steps.append("Find the CPU- or memory-heavy work (background jobs, hot loops, caches) so it does not build up again.")
        elif dependency:
            root_cause = "Requests are failing or slowing while calling a dependency (connection, timeout or database errors in the evidence)."
            certainty, confidence = "likely", 0.72
            restart_reason = "The errors point at a dependency the app calls; restarting the app would not bring that dependency back."
            next_steps.append("Check the dependency named in the errors (database, downstream API, network) and restore it.")
            next_steps.append("Consider timeouts and a circuit breaker so the app fails fast while the dependency is down.")
        elif has_latency and not has_errors:
            root_cause = "Requests are slow without CPU pressure or errors, which usually means waiting on something slow (a dependency or lock)."
            certainty, confidence = "possible", 0.55
            restart_reason = "Nothing in the evidence suggests the slowdown is inside the app's own process, so a restart is unlikely to help."
            next_steps.append("Check the latency of the dependencies the slow endpoint calls.")
        else:
            restart_fits = True
            if has_log_pattern and not has_errors:
                root_cause = "Application logs show a repeated error pattern, most likely an unhandled exception; the evidence does not show its cause."
                certainty, confidence = "possible", 0.68
            else:
                root_cause = ("The app started returning server errors; the evidence does not show why." if not top_error_type
                              else f"The app started returning server errors ({top_error_type}); the evidence does not show whether this is bad in-process state or a code defect.")
                certainty, confidence = "possible", 0.6
            restart_reason = ("The fault began in a previously healthy process with no sign of a failing dependency; a restart clears "
                              "in-process state and should restore service as a temporary mitigation. It cannot fix a code defect - if the "
                              "problem returns after the restart, the code needs investigating.")
            next_steps.append("After the restart, watch whether the problem returns; if it does, investigate the application code.")
            if top_error_type:
                next_steps.append(f"Review the code path that raises {top_error_type}.")

        considered = []
        recommendations = []
        for action, verdict, reason, rec in other:
            considered.append({"action": action, "verdict": verdict, "reason": reason})
            if rec:
                recommendations.append(rec)
        if restart:
            fits = restart_fits and not has_process_crash
            considered.append({"action": restart, "verdict": "recommended" if fits else "not_recommended", "reason": restart_reason})
            if fits:
                recommendations.append({
                    "action": restart,
                    "reason": restart_reason,
                    "expected_outcome": "The process starts fresh and the symptoms clear, if they were caused by in-process state. KAIRON verifies recovery afterwards.",
                    "risk_level": "medium",
                })
        if health_check and not recommendations and signals:
            considered.append({"action": health_check, "verdict": "recommended", "reason": "A read-only check of the target's state, useful while the cause is investigated."})
            recommendations.append({
                "action": health_check,
                "reason": "Confirm the target's current state while the cause is investigated.",
                "expected_outcome": "Current service state; this read-only check does not repair anything.",
                "risk_level": "low",
            })

        has_forecast = bool(forecast) and forecast.get("outcome") not in (None, "inconclusive")
        symptoms = incident.get("symptoms") or []

        return {
            "summary": f"{incident.get('service', 'Service')} is degraded: " + "; ".join(str(s) for s in symptoms[:3]),
            "root_cause": root_cause,
            "root_cause_certainty": certainty,
            "contributing_factors": [str(s.get("symptom", "")) for s in signals[:5]],
            "evidence": [
                f"{s.get('metric')} {s.get('observed')}{s.get('unit', '')} vs threshold "
                f"{s.get('threshold')}{s.get('unit', '')}"
                for s in signals[:6]
            ],
            "confidence": confidence,
            "severity": str(incident.get("severity", "medium")).lower(),
            "affected_components": [
                c for c in [incident.get("affected_component"), incident.get("service")] if c
            ],
            "predicted_failure": forecast.get("failure_mode", "") if has_forecast else "Prediction inconclusive: not enough trend data.",
            "estimated_risk": forecast.get("risk_level", "medium") if has_forecast else "medium",
            "recommendations": recommendations,
            "next_steps": next_steps,
            "considered_actions": considered,
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
