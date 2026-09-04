"""Wire schemas for the Kairon AI service.

These mirror the backend's DTOs exactly (snake_case on the wire), so the .NET side deserializes
what FastAPI emits with no translation layer in between.
"""

from __future__ import annotations

from datetime import datetime
from typing import Any, Dict, List, Optional

from pydantic import BaseModel, Field

# --- Legacy request models. Unchanged: the existing dashboard screens still post these. ---


class LogReq(BaseModel):
    log: str


class PredictReq(BaseModel):
    recent_logs: List[Any] = Field(default_factory=list)
    current_log: str = ""


class ContextReq(BaseModel):
    context: str


class MismatchReq(BaseModel):
    mismatches: List[Any] = Field(default_factory=list)


# --- Runtime provider configuration (frontend AI Configuration panel). ---


class ConfigureRequest(BaseModel):
    """Applies a provider selection at runtime. `api_key` is optional so a caller can change just
    the model without resending an already-known key; omitted/blank fields keep their current
    value rather than clearing it. `endpoint` overrides the provider's default base URL - needed
    for deployments that front a provider behind a dedicated/regional endpoint (for example an
    Alibaba Model Studio workspace's own inference domain) rather than the shared public one."""

    provider: str
    api_key: Optional[str] = None
    model: Optional[str] = None
    endpoint: Optional[str] = None


# --- Evidence package (AI PRD section 6). ---


class IncidentContext(BaseModel):
    incident_id: str = ""
    incident_key: str = ""
    title: str = ""
    application: str = ""
    service: str = ""
    environment: str = ""
    severity: str = "medium"
    status: str = ""
    affected_component: str = ""
    affected_endpoint: str = ""
    detected_at: Optional[datetime] = None
    symptoms: List[str] = Field(default_factory=list)


class MetricSample(BaseModel):
    timestamp: Optional[datetime] = None
    cpu_percent: Optional[float] = None
    memory_percent: Optional[float] = None
    response_time_ms: Optional[float] = None
    request_count: int = 0
    error_count: int = 0
    retry_count: Optional[int] = None
    queue_depth: Optional[int] = None


class RelatedError(BaseModel):
    timestamp: Optional[datetime] = None
    endpoint: str = ""
    method: str = ""
    status_code: int = 0
    duration_ms: int = 0
    error_type: Optional[str] = None
    error_message: Optional[str] = None


class CorrelatedSignal(BaseModel):
    rule: str = ""
    metric: str = ""
    symptom: str = ""
    observed: Optional[float] = None
    threshold: Optional[float] = None
    unit: str = ""
    severity: str = ""
    detected_at: Optional[datetime] = None


class AgentEvent(BaseModel):
    """A KAIRON Agent event - a log pattern match or a process lifecycle/resource event
    (docs/OBSERVABILITY_MIGRATION.md). Collected by a log tailer or a process watcher, not an
    SDK, which is the point: this is what proves diagnosis is not limited to HTTP/metric
    evidence.
    """

    timestamp: Optional[datetime] = None
    event_type: str = ""
    severity: str = ""
    message: str = ""
    source: str = ""
    occurrence_count: int = 1


class HistoricalIncident(BaseModel):
    incident_key: str = ""
    title: str = ""
    root_cause: Optional[str] = None
    resolution: Optional[str] = None
    detected_at: Optional[datetime] = None
    status: str = ""


class AvailableAction(BaseModel):
    """A remediation tool the backend has registered.

    The model may only ever name one of these. Anything else is refused by backend policy before
    it can reach an executor, so inventing an action accomplishes nothing.
    """

    action: str = ""
    description: str = ""
    risk_level: str = "medium"


class EvidencePackage(BaseModel):
    incident: IncidentContext = Field(default_factory=IncidentContext)
    recent_metrics: List[MetricSample] = Field(default_factory=list)
    related_errors: List[RelatedError] = Field(default_factory=list)
    correlated_signals: List[CorrelatedSignal] = Field(default_factory=list)
    log_events: List[AgentEvent] = Field(default_factory=list)
    historical_incidents: List[HistoricalIncident] = Field(default_factory=list)
    available_actions: List[AvailableAction] = Field(default_factory=list)


# --- Structured investigation result (AI PRD section 7). ---


class Recommendation(BaseModel):
    action: str = ""
    reason: str = ""
    expected_outcome: str = ""
    risk_level: str = "medium"
    parameters: Optional[Dict[str, str]] = None


class InvestigationResult(BaseModel):
    summary: str = ""
    root_cause: str = ""
    contributing_factors: List[str] = Field(default_factory=list)
    evidence: List[str] = Field(default_factory=list)
    confidence: float = 0.0
    severity: str = "medium"
    affected_components: List[str] = Field(default_factory=list)
    predicted_failure: str = ""
    estimated_risk: str = "medium"
    recommendations: List[Recommendation] = Field(default_factory=list)
    provider: Optional[str] = None
    model: Optional[str] = None
