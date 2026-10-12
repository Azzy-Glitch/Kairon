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

# The same evidence patterns the backend's DeterministicInvestigation uses.
_DATABASE = re.compile(
    r"database|\bsql\b|sqlalchemy|psycopg|operationalerror|mysql|postgres|sqlite|mongo|redis|\bdb\b|query timeout|deadlock|connection pool",
    re.IGNORECASE,
)
_EXTERNAL = re.compile(
    r"timed? ?out|timeout|refused|unreachable|dns|socket|econn|reset by peer|service unavailable|bad gateway|gateway|upstream|provider|rate limit|too many requests|\b429\b|\b502\b|\b503\b|\b504\b|connection",
    re.IGNORECASE,
)
# Failures that come from the process's own state - the cases a restart can clear.
_IN_PROCESS = re.compile(
    r"out ?of ?memory|memoryerror|too many open files|cannot allocate|resource temporarily unavailable|thread ?pool|event loop|pool (is )?exhausted|stack ?overflow",
    re.IGNORECASE,
)
_DATABASE_PORT = re.compile(r":(5432|3306|1433|1521|27017|6379)\b")


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

        It applies the reasoning the real model is instructed to follow - and the backend's
        DeterministicInvestigation applies the same rules - so test mode exercises the real
        recommendation -> policy -> approval flow. It explains first and acts second: what happened,
        the signals behind it, how certain that is, alternatives, missing evidence, next steps and how
        to verify a fix. A restart is only recommended when the fault is inside the process, and only
        ever as a temporary mitigation. It never names an action KAIRON did not offer.
        """
        e = self._parse_evidence(user)
        signals = [x for x in (e.get("correlated_signals") or []) if isinstance(x, dict)]
        metrics = {x.get("metric") for x in signals}
        has = lambda *names: any(n in metrics for n in names)  # noqa: E731
        has_errors, has_latency = has("errorRate", "errors"), has("latency")
        has_cpu, has_memory = has("cpu", "processHighResource"), has("memory")
        has_retries, has_queue = has("retries"), has("queue")
        has_crash, has_log = has("processCrash"), has("logPattern")
        incident = e.get("incident") or {}
        related = [x for x in (e.get("related_errors") or []) if isinstance(x, dict)]
        breakdown = [x for x in (e.get("endpoint_breakdown") or []) if isinstance(x, dict)]
        dependencies = [x for x in (e.get("dependencies") or []) if isinstance(x, dict)]
        onsets = [x for x in (e.get("signal_onsets") or []) if isinstance(x, dict)]
        history = [x for x in (e.get("remediation_history") or []) if isinstance(x, dict)]
        trends = ((e.get("risk_forecast") or {}).get("trends")) or []
        recurrence = e.get("recurrence") or {}
        forecast = e.get("risk_forecast") or {}
        gaps = e.get("evidence_gaps") or []

        texts = [f"{x.get('error_type') or ''} {x.get('error_message') or ''} {x.get('stack_excerpt') or ''} "
                 f"{x.get('status_code') if x.get('status_code') in (429, 502, 503, 504) else ''}" for x in related]
        texts += [str(x.get("top_error_type") or "") for x in breakdown]
        failing = [d for d in dependencies if (d.get("failures") or 0) > 0 or (d.get("avg_duration_ms") or 0) >= 1000]
        db_dep = next((d for d in failing if _DATABASE.search(str(d.get("target"))) or _DATABASE_PORT.search(str(d.get("target")))), None)
        ext_dep = next((d for d in failing if d is not db_dep), None)
        db_text = any(_DATABASE.search(t) for t in texts)
        ext_text = any(_EXTERNAL.search(t) for t in texts)
        in_process = any(_IN_PROCESS.search(t) for t in texts)
        rising = lambda m: any(t.get("metric") == m and t.get("direction") == "rising" for t in trends if isinstance(t, dict))  # noqa: E731
        # Only onsets inside the evidence window have a known time; one already over its threshold at
        # the window's first sample cannot be said to have moved first.
        known = [o for o in onsets if not o.get("breached_before_window")]
        censored = [o for o in onsets if o.get("breached_before_window")]
        first = known[0] if len(known) > 1 else None
        order = [o.get("metric") for o in known]
        latency_led = "latency" in order and "errorRate" in order and order.index("latency") < order.index("errorRate")
        top = max((x for x in breakdown if (x.get("server_errors") or 0) > 0), key=lambda x: x.get("server_errors") or 0, default=None)
        top_type = (top or {}).get("top_error_type") or next((x.get("error_type") for x in related if x.get("error_type")), None)
        top_message = next((x.get("error_message") for x in related if x.get("error_message")), None)
        stack = next((x.get("stack_excerpt") for x in related if x.get("stack_excerpt")), None)
        failed_restart = next((h for h in history if h.get("same_fault") and str(h.get("action", "")).startswith("Restart")
                               and (h.get("status") == "Failed" or h.get("verification") == "Failed")), None)
        error_rate = next((f"error rate {x.get('observed')}{x.get('unit', '')}" for x in signals if x.get("metric") == "errorRate"), "server errors")
        onset_text = (" " + ", then ".join(f"{str(o.get('label', '')).lower()} ({float(o.get('seconds_before_detection') or 0):.0f}s before detection)"
                                           for o in known[:3]) + " crossed their thresholds in that order.") if len(known) > 1 else ""
        if censored and known:
            labels = " and ".join(str(o.get("label", "")) for o in censored)
            onset_text += (f" {labels} {'was' if len(censored) == 1 else 'were'} already over threshold when the evidence window starts, "
                           f"so when {'it' if len(censored) == 1 else 'they'} began is unknown.")
        summary = e.get("metric_summary") or {}

        def resource_context():
            normal = [name for name, key, breached in (("CPU", "cpu_percent", has_cpu), ("memory", "memory_percent", has_memory))
                      if not breached and key in summary]
            parts = []
            if normal:
                parts.append(f"{' and '.join(normal)} {'is' if len(normal) == 1 else 'are'} within normal range")
            parts.append("dependency calls are not measured for this app" if not dependencies else "no dependency failures were measured")
            return "; " + "; ".join(parts)
        where = f" on {top.get('method')} {top.get('endpoint')}" if top else ""

        # Each explanation is scored by the strength of what supports it, not by a fixed ranking:
        # measured evidence outweighs inference, moving first adds a little (only when that ordering is
        # known), and a close runner-up is a genuine conflict that qualifies the diagnosis. Mirrors the
        # backend's DeterministicInvestigation; tests/fixtures/diagnosis-scenarios.json holds both to it.
        dependency_text = db_text or ext_text
        scores = {}

        def score(hypothesis, value):
            if value > 0 and value > scores.get(hypothesis, 0):
                scores[hypothesis] = value

        if db_dep:
            score("database", 3)
        if ext_dep:
            score("external", 3)
        if has_retries and dependency_text:
            score("database" if db_text else "external", 2)
        if has_errors and db_text:
            score("database", 1.5)
        if has_errors and ext_text:
            score("external", 1.5)
        if has_memory:
            score("memory", 3 if rising("memory") else 2.5)
        if has_cpu:
            score("cpu", 3)
        if has_queue:
            score("queue", 1.5)
        if has_retries and not dependency_text:
            score("queue", 1)
        if has_latency and not has_errors and not has_cpu:
            score("latency", 1)
        if has_errors or has_log:
            score("errors", 1 + (0.5 if (top_type or stack) else 0) + (1 if in_process else 0)
                  - (0.5 if dependency_text and not in_process else 0))
        moved_first = {
            "cpu": "cpu", "memory": "memory", "queue": "queue", "errorRate": "errors",
            "retries": "external" if "external" in scores else "database" if "database" in scores else "queue",
            "latency": "database" if "database" in scores else "external" if "external" in scores else "latency",
        }.get((first or {}).get("metric"))
        if moved_first in scores:
            scores[moved_first] += 0.5
        tie_order = ["database", "external", "cpu", "memory", "queue", "errors", "latency"]
        ranked = sorted(scores.items(), key=lambda kv: (-kv[1], tie_order.index(kv[0])))

        if not signals:
            kind = "none"
        elif has_crash:
            kind = "crash"
        elif recurrence.get("recurred_after_restart") or failed_restart:
            kind = "recurred"
        else:
            kind = ranked[0][0] if ranked else "errors"
        competitor = (ranked[1][0] if kind not in ("none", "crash", "recurred") and len(ranked) > 1
                      and ranked[1][1] >= ranked[0][1] - 1 else None)

        f = {"root": "", "certainty": "unknown", "confidence": 0.3, "restart": False, "start": False,
             "restart_reason": "", "next": [], "alt": [], "verify": []}
        if kind == "none":
            f.update(root="The evidence is not sufficient to identify a cause.",
                     restart_reason="There is not enough evidence to justify changing anything.")
            f["next"].append("Wait for more telemetry from the app, then re-investigate.")
        elif kind == "crash":
            f.update(root="The monitored process stopped running unexpectedly.", certainty="confirmed", confidence=0.95, start=True,
                     restart_reason="The process is not running, so there is nothing to restart; it needs starting.")
            f["next"].append("Check the application's logs for why the process exited (unhandled exception, out-of-memory kill, configuration).")
            f["verify"].append("The process stays running and its health checks pass for several minutes.")
        elif kind == "recurred":
            if failed_restart:
                root = (f"A restart for this same fault ({failed_restart.get('incident_key')}) did not resolve it (status "
                        f"{failed_restart.get('status')}, verification {failed_restart.get('verification') or 'not run'}), so the cause is not transient process state.")
            else:
                minutes = recurrence.get("minutes_since_restart")
                when = f"{minutes:.0f} minute(s) " if isinstance(minutes, (int, float)) else ""
                root = f"The same kind of fault returned {when}after a restart, so the underlying cause is still present."
            f.update(root=root, certainty="possible", confidence=0.6,
                     restart_reason="A restart already failed to make this fault go away; repeating it would only hide the problem briefly.")
            f["next"].append("Investigate what persists across restarts: recent deployments or configuration changes, data the app reads, and its dependencies.")
            if top_type:
                f["next"].append(f"Start with the most frequent exception, {top_type}{where}.")
            f["alt"].append("A dependency that keeps failing would also make the fault return after every restart.")
            f["verify"].append("The fault does not return within an hour after the underlying change is made.")
        elif kind == "database":
            root = (f"Calls to the database ({db_dep.get('target')}) are failing or slow ({db_dep.get('failures')} of {db_dep.get('calls')} failed, "
                    f"avg {float(db_dep.get('avg_duration_ms') or 0):.0f} ms)" if db_dep else
                    "The errors are database errors" + (f" ({top_type})" if top_type else ""))
            root += (", and latency rose before the errors did." if latency_led else ".") + onset_text
            if not db_dep:
                root = root.rstrip(".") + " - inferred from the error text; database calls are not measured for this app."
            f.update(root=root, certainty="likely" if db_dep else "possible", confidence=0.72 if db_dep else 0.55,
                     restart_reason="The evidence points at the database; restarting the app does not make the database faster or available, and reconnecting many clients at once can add load.")
            f["next"] += ["Check the database: query latency and slow-query log, connection-pool usage and waits, locks, and server CPU, memory and disk I/O.",
                          "Only add database capacity if those measurements show it running near its limits."]
            f["alt"].append("Connection-pool exhaustion in the app (too few connections for the load) can look like a slow database.")
            f["verify"].append(f"The {error_rate} and request latency return under threshold and database call latency is back to normal.")
        elif kind == "external":
            if ext_dep:
                root = (f"Calls to the downstream service {ext_dep.get('target')} are failing or slow ({ext_dep.get('failures')} of "
                        f"{ext_dep.get('calls')} failed, avg {float(ext_dep.get('avg_duration_ms') or 0):.0f} ms)")
            else:
                root = ("The errors suggest a downstream call is failing" + (f' ("{top_message}")' if top_message else "") +
                        " - the dependency itself is not measured, so this is inferred from the error text")
            root += (", and the app is retrying calls - rising retries multiply the load on whatever is failing." if has_retries else ".") + onset_text
            if not ext_dep:
                f["next"].append("Find which call is failing and being retried - the evidence does not identify the dependency.")
            f.update(root=root, certainty="likely" if ext_dep else "possible", confidence=0.75 if ext_dep else 0.55,
                     restart_reason="The failure is in a service the app calls; restarting the app does not bring it back.")
            f["next"] += ["Check the downstream service's availability and the status codes it returns (429 means rate limiting, 503/504 overload or timeouts).",
                          "Review the app's timeout and retry settings: bounded retries with backoff, and a circuit breaker so it fails fast while the dependency is down."]
            f["alt"].append("A network or DNS problem between the app and the service would show the same symptoms.")
            f["verify"].append(f"The {error_rate} returns under threshold and retries drop back to normal while the dependency answers successfully.")
        elif kind == "queue":
            f.update(root="Work is piling up" + (" while retries rise" if has_retries else "") + ": the app is receiving work faster than it completes it." + onset_text,
                     certainty="possible", confidence=0.58,
                     restart_reason="A restart would drop or delay the queued work and the backlog would rebuild for the same reason.")
            f["next"].append("Check worker capacity and processing time per item, and whether a downstream step became slower.")
            if has_retries:
                f["next"].append("Check for retry amplification: failed items retried immediately add to the queue they are waiting in.")
            f["alt"].append("A burst of incoming work, rather than slower processing, produces the same backlog.")
            f["verify"].append("Queue depth trends back down and retries return to their usual level.")
        elif kind == "memory":
            f.update(root="The app's memory use is high" + (" and has been growing steadily" if rising("memory") else "") + ", which risks the process being killed." + onset_text,
                     certainty="possible", confidence=0.6, restart=True,
                     restart_reason="A restart frees the memory and buys time as a temporary mitigation; if a leak or unbounded cache is the cause, memory will grow again.")
            f["next"].append("Find what holds the memory: caches without limits, growing collections, or a leak in the affected workload.")
            f["alt"].append("A legitimate rise in load or data size, rather than a leak, also raises memory.")
            f["verify"].append("After the change, memory levels off instead of growing over the following hour.")
        elif kind == "cpu":
            f.update(root=("CPU saturation in the app's own process is slowing its requests" if has_latency else "The app's own process is saturating the CPU") + "." + onset_text,
                     certainty="likely", confidence=0.78, restart=True,
                     restart_reason="The pressure is inside the app's process; a restart clears runaway work and restores service as a temporary mitigation. It will return if the workload or code causing it does.")
            f["next"] += ["Find the CPU-heavy work (background jobs, hot loops, expensive requests) and whether the workload changed.",
                          "If the load is legitimate, add capacity or reduce work per request; a restart does not help with that."]
            f["alt"].append("A genuine traffic increase saturates the CPU the same way.")
            f["verify"].append("CPU and latency return under threshold and stay there for several minutes.")
        elif kind == "latency":
            f.update(root="Requests are slow without CPU pressure or errors - the app is most likely waiting on something (a dependency, lock or external call)." + onset_text,
                     certainty="possible", confidence=0.55,
                     restart_reason="Nothing shows the slowdown is inside the app's process, so a restart is unlikely to help.")
            f["next"].append("Check the latency of what the slow endpoint calls (database, downstream APIs) and for lock contention.")
            f["alt"].append("A slow external service the app does not report as a dependency would look exactly like this.")
            f["verify"].append("Latency returns under threshold.")
        else:
            if in_process:
                root = f"The app is failing with errors that come from its own process state ({top_type or top_message})."
            elif has_log and not has_errors:
                root = "Application logs show a repeated error pattern, most likely an unhandled exception; the evidence does not show its cause."
            else:
                root = (f"The app started returning {error_rate}{where}" + (f", mostly {top_type}" if top_type else "") +
                        resource_context() + "." + onset_text)
            f.update(root=root, certainty="possible" if (top_type or top_message or has_log) else "unknown",
                     confidence=0.62 if in_process else 0.68 if (has_log and not has_errors) else 0.5,
                     restart=in_process,
                     restart_reason=("These errors come from the process's own state (resources exhausted inside it); a restart clears that state as a temporary mitigation."
                                     if in_process else
                                     "With no measured resource pressure or dependency failure, the errors point at a code path, input or configuration problem; a restart would only help if the process state were corrupted, which the evidence does not show."))
            f["next"].append(f"Look at the code path that raises {top_type}{where}" + (" (stack trace attached in the evidence)." if stack else ".")
                             if top_type else f"Find which requests fail{where} and why - the errors carry no exception detail.")
            f["next"].append("Check for a recent deployment or configuration change around the time the errors started.")
            f["alt"].append("A dependency the app does not report as one (e.g. called without the SDK seeing it) could be the real source.")
            f["verify"].append(f"The {error_rate} returns under threshold{where}.")

        if competitor:
            if f["certainty"] in ("confirmed", "likely"):
                f["certainty"] = "possible"
            f["confidence"] = min(f["confidence"], 0.6)
            f["alt"].insert(0, {
                "database": "The evidence also fits a database problem (database errors or slow database calls).",
                "external": "The evidence also fits a failing downstream call (retries or timeout/connection errors)"
                            + (" - the dependency itself is not measured." if not ext_dep else "."),
                "cpu": "The evidence also fits CPU saturation inside the app's own process.",
                "memory": "The evidence also fits memory pressure inside the app's own process.",
                "queue": "The evidence also fits a backlog of work the app is not keeping up with.",
                "latency": "The evidence also fits a slow dependency or lock contention.",
            }.get(competitor, "The evidence also fits a code-path, input or configuration problem in the app itself."))

        offered = [a.get("action") for a in (e.get("available_actions") or []) if isinstance(a, dict) and a.get("action")]
        restart = "RestartApplication" if "RestartApplication" in offered else ("RestartService" if "RestartService" in offered else None)
        health = "RunHealthCheck" if "RunHealthCheck" in offered else None
        start = "StartService" if "StartService" in offered else None
        verify_first = f["verify"][0] if f["verify"] else "KAIRON verifies recovery from fresh telemetry."
        considered, recommendations = [], []
        if start and f["start"]:
            considered.append({"action": start, "verdict": "recommended", "reason": "The process has stopped; starting it restores the service."})
            recommendations.append({"action": start, "kind": "temporary_mitigation", "risk_level": "medium",
                                    "reason": "The monitored process stopped running; starting it restores availability while the cause of the exit is investigated.",
                                    "expected_outcome": "The service runs again.",
                                    "risks": "If the cause of the exit is still present, it may stop again.", "verification": verify_first})
        if restart:
            fits = f["restart"] and not f["start"]
            considered.append({"action": restart, "verdict": "recommended" if fits else "not_recommended", "reason": f["restart_reason"]})
            if fits:
                recommendations.append({"action": restart, "kind": "temporary_mitigation", "risk_level": "medium", "reason": f["restart_reason"],
                                        "expected_outcome": "The process starts fresh and the symptoms clear, if they come from in-process state.",
                                        "risks": "A few seconds of downtime while the app restarts; in-flight requests fail. It does not fix the underlying cause.",
                                        "verification": verify_first})
        if health and not recommendations and signals:
            considered.append({"action": health, "verdict": "recommended", "reason": "A read-only check of the target's state, useful while the cause is investigated."})
            recommendations.append({"action": health, "kind": "diagnostic", "risk_level": "low",
                                    "reason": "Confirm the target's current state while the cause is investigated.",
                                    "expected_outcome": "Current service state; this read-only check does not repair anything.",
                                    "risks": "None - it changes nothing.", "verification": "Not applicable."})

        has_forecast = bool(forecast) and forecast.get("outcome") not in (None, "inconclusive")
        symptoms = incident.get("symptoms") or []
        evidence_lines = [f"{x.get('metric')} {x.get('observed')}{x.get('unit', '')} vs threshold {x.get('threshold')}{x.get('unit', '')}" for x in signals[:6]]
        if first:
            evidence_lines.append(f"{first.get('label')} crossed its threshold first, {float(first.get('seconds_before_detection') or 0):.0f}s before detection")
        return {
            "summary": f"{incident.get('service', 'Service')} is degraded: " + "; ".join(str(x) for x in symptoms[:3]),
            "root_cause": f["root"],
            "root_cause_certainty": f["certainty"],
            "contributing_factors": [str(x.get("symptom", "")) for x in signals[:5]],
            "evidence": evidence_lines,
            "confidence": f["confidence"],
            "severity": str(incident.get("severity", "medium")).lower(),
            "affected_components": [c for c in [incident.get("affected_component"), incident.get("service")] if c],
            "predicted_failure": forecast.get("failure_mode", "") if has_forecast else "Prediction inconclusive: not enough trend data.",
            "estimated_risk": forecast.get("risk_level", "medium") if has_forecast else "medium",
            "recommendations": recommendations,
            "next_steps": f["next"],
            "considered_actions": considered,
            "alternative_explanations": f["alt"],
            "missing_evidence": list(gaps[:4]),
            "verification_steps": f["verify"],
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
