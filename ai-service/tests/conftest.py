"""Shared test fixtures.

The service is importable from the repository's ai-service directory, so tests add it to the path
rather than requiring an installed package.
"""

from __future__ import annotations

import sys
from datetime import datetime, timezone
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from kairon.config import AiConfig  # noqa: E402
from kairon.schemas import (  # noqa: E402
    AgentEvent,
    AvailableAction,
    CorrelatedSignal,
    EvidencePackage,
    IncidentContext,
    MetricSample,
    RelatedError,
)


@pytest.fixture
def mock_config() -> AiConfig:
    """Configuration with no credentials, which resolves to the deterministic mock provider."""
    return AiConfig(provider="qwen", model="qwen-plus", timeout_seconds=5, max_retries=1)


@pytest.fixture
def retry_storm_evidence() -> EvidencePackage:
    """The demo scenario's evidence: a retry loop driving CPU, latency, errors and queue depth."""
    now = datetime.now(timezone.utc)

    return EvidencePackage(
        incident=IncidentContext(
            incident_id="11111111-1111-1111-1111-111111111111",
            incident_key="INC-0001",
            title="OrderProcessingService Service Degradation",
            application="Kairon.DemoApp",
            service="OrderProcessingService",
            environment="Demo",
            severity="High",
            status="Investigating",
            affected_component="OrderProcessingService",
            affected_endpoint="/api/orders/process",
            detected_at=now,
            symptoms=[
                "Retry rate 90.0/min (450 retries), threshold 30/min",
                "CPU sustained at 94.0% (peak 95.0%), threshold 80%",
                "Response time peaked at 2400ms, threshold 1000ms",
            ],
        ),
        recent_metrics=[
            MetricSample(
                timestamp=now,
                cpu_percent=94.0,
                memory_percent=82.0,
                response_time_ms=2400,
                request_count=20,
                error_count=7,
                retry_count=30,
                queue_depth=85,
            )
        ],
        related_errors=[
            RelatedError(
                timestamp=now,
                endpoint="/api/orders/process",
                method="POST",
                status_code=503,
                duration_ms=2400,
                error_type="DownstreamTimeoutException",
                error_message="Order processing retry exhausted while calling inventory service",
            )
        ],
        correlated_signals=[
            CorrelatedSignal(
                rule="retry-storm", metric="retries", symptom="Retry rate 90.0/min",
                observed=90.0, threshold=30.0, unit="/min", severity="High", detected_at=now,
            ),
            CorrelatedSignal(
                rule="cpu-threshold", metric="cpu", symptom="CPU sustained at 94.0%",
                observed=94.0, threshold=80.0, unit="%", severity="High", detected_at=now,
            ),
            CorrelatedSignal(
                rule="latency-threshold", metric="latency", symptom="Response time peaked at 2400ms",
                observed=2400.0, threshold=1000.0, unit="ms", severity="High", detected_at=now,
            ),
        ],
        available_actions=[
            AvailableAction(action="DisableDemoRetryLoop", description="Disables the retry loop", risk_level="Low"),
            AvailableAction(action="RestartDemoService", description="Restarts the demo service", risk_level="Medium"),
            AvailableAction(action="RunHealthCheck", description="Read-only health probe", risk_level="Low"),
        ],
    )


@pytest.fixture
def cpu_only_evidence(retry_storm_evidence: EvidencePackage) -> EvidencePackage:
    """Same incident with only the CPU and latency signals, to exercise a different diagnosis path."""
    evidence = retry_storm_evidence.model_copy(deep=True)
    evidence.correlated_signals = [s for s in evidence.correlated_signals if s.metric in {"cpu", "latency"}]
    evidence.available_actions.append(
        AvailableAction(
            action="ReduceDemoWorkerConcurrency",
            description="Halves worker concurrency",
            risk_level="Medium",
        )
    )
    return evidence


@pytest.fixture
def process_crash_evidence(retry_storm_evidence: EvidencePackage) -> EvidencePackage:
    """A KAIRON Agent-only incident: the process stopped running, no metric signal at all
    (docs/OBSERVABILITY_MIGRATION.md) - proves diagnosis is not limited to HTTP/metric evidence."""
    evidence = retry_storm_evidence.model_copy(deep=True)
    now = datetime.now(timezone.utc)
    evidence.correlated_signals = [
        CorrelatedSignal(
            rule="process-crash", metric="processCrash",
            symptom="Process 'Kairon.DemoApp' is no longer running (was PID 12345).",
            observed=1.0, threshold=0.0, unit="", severity="Critical", detected_at=now,
        )
    ]
    evidence.log_events = [
        AgentEvent(
            timestamp=now, event_type="ProcessCrash", severity="Critical",
            message="Process 'Kairon.DemoApp' is no longer running (was PID 12345).",
            source="Kairon.DemoApp", occurrence_count=1,
        )
    ]
    return evidence


@pytest.fixture
def log_pattern_only_evidence(retry_storm_evidence: EvidencePackage) -> EvidencePackage:
    """A KAIRON Agent-only incident: a repeated log error pattern, no metric threshold breached."""
    evidence = retry_storm_evidence.model_copy(deep=True)
    now = datetime.now(timezone.utc)
    evidence.correlated_signals = [
        CorrelatedSignal(
            rule="log-pattern-match", metric="logPattern",
            symptom="2 matched log pattern(s), most recent: Order processing failed: timeout",
            observed=2.0, threshold=2.0, unit=" occurrences", severity="High", detected_at=now,
        )
    ]
    evidence.log_events = [
        AgentEvent(
            timestamp=now, event_type="LogPatternMatch", severity="Error",
            message="Order processing failed: Order processing retry exhausted while calling inventory service.",
            source="demo/Kairon.DemoApp/logs/kairon-demo-20260827.txt", occurrence_count=1,
        )
    ]
    return evidence
