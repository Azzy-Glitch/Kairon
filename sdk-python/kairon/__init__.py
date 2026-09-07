"""KAIRON telemetry SDK. See README.md for authenticated setup and lifespan handling."""

from .client import Kairon, get_default_instance, pair

__all__ = ["Kairon", "get_default_instance", "pair", "__version__"]
__version__ = "1.0.1"
