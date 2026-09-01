"""
Kairon telemetry SDK for Python applications.

Core usage (no framework dependency):

    from kairon import Kairon

    kairon = Kairon(
        endpoint="https://your-kairon-server",
        project_id="my-project",
        service="OrderProcessingService",
    )
    kairon.start()

FastAPI/Starlette integration (imported separately - see kairon.middleware - so the base
package never requires starlette/fastapi to be installed):

    from kairon.middleware import KaironMiddleware
    app.add_middleware(KaironMiddleware)
"""

from .client import Kairon, get_default_instance, pair

__all__ = ["Kairon", "get_default_instance", "pair", "__version__"]
__version__ = "1.0.1"
