"""AIDIP AI service package.

The intelligence layer for AIDIP Autonomous AI SRE: provider-independent, credential-safe, and
advisory only. It diagnoses, predicts and recommends; the backend decides and executes.
"""

from .config import AiConfig
from .schemas import EvidencePackage, InvestigationResult
from .service import AiService, AiServiceError

__all__ = [
    "AiConfig",
    "AiService",
    "AiServiceError",
    "EvidencePackage",
    "InvestigationResult",
]
