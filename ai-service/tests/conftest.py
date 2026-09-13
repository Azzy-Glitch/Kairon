"""Shared test fixtures.

The service is importable from the repository's ai-service directory, so tests add it to the path
rather than requiring an installed package.
"""

from __future__ import annotations

import sys
import os
from datetime import datetime, timezone
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

# The production service fails closed when this secret is absent. Tests use a synthetic key and
# never rely on a developer credential or a checked-in production secret.
os.environ.setdefault("KAIRON_AI_API_KEY", "kairon-ai-test-key-not-for-production")

# Hermetic by construction, deliberately overriding (not setdefault) whatever a developer's own
# ai-service/.env or shell environment happens to have set (a real AI__Provider/*_API_KEY - this
# machine's own .env is exactly that: untracked, local, and never something a test run should read
# or depend on). main.py's module-level CONFIG/SERVICE singletons are built once at import time via
# AiConfig.from_env() -> load_dotenv(), and load_dotenv() never overrides an already-set variable,
# so setting these here BEFORE any test imports main is what keeps the whole HTTP-level suite
# (test_api.py, test_configure.py) forced onto the deterministic mock provider - never a live call
# to a real provider using whatever credential happens to be lying around on the machine running
# the tests (AI PRD section 18; this task's own "do not use live credentials in tests" rule).
os.environ["AI__MockMode"] = "true"
os.environ["AI__Provider"] = "mock"
# Set (not merely removed) to an empty string: load_dotenv() only fills in a variable that is
# genuinely ABSENT from os.environ, so simply deleting these would leave the .env file free to
# populate them right back in. An explicit empty string blocks that and reads as "no credential" to
# is_placeholder() either way.
for _provider_key in ("QWEN_API_KEY", "GEMINI_API_KEY", "GROQ_API_KEY"):
    os.environ[_provider_key] = ""

# The whole test session shares one FastAPI app instance (module import is cached), so every
# HTTP-level test across every test file counts against the same production rate limiter. The
# default (12/min) is sized for one real desktop process, not a test suite making dozens of calls
# in under a second - raise the ceiling for tests only; production's own default is untouched.
os.environ.setdefault("KAIRON_AI_REQUESTS_PER_MINUTE", "1000")
os.environ.setdefault("KAIRON_AI_REQUESTS_PER_HOUR", "10000")

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
    """Explicitly mock (force_mock=True), not merely a real provider with no credential - since
    AiConfig.effective_provider no longer silently substitutes mock for the latter (see
    kairon.config's own remarks), this fixture must ask for mock on purpose to keep resolving to
    MockProvider, exactly like any genuine test/development opt-in would."""
    return AiConfig(provider="qwen", model="qwen-plus", timeout_seconds=5, max_retries=1, force_mock=True)


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
            environment="Development",
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
            AvailableAction(action="RunHealthCheck", description="Disables the retry loop", risk_level="Low"),
            AvailableAction(action="StartService", description="Restarts the demo service", risk_level="Medium"),
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
            action="RunHealthCheck",
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
