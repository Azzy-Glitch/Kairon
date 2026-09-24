"""Exercise Django's actual synchronous and asynchronous request handlers."""

from __future__ import annotations

import asyncio

import pytest

django = pytest.importorskip("django")
from django.conf import settings
from django.http import HttpResponse
from django.test import AsyncClient, Client, override_settings
from django.urls import path

from kairon import Kairon, get_default_instance


def _error(_request):
    raise RuntimeError("django request failed")


urlpatterns = [
    path("orders", lambda request: HttpResponse("ok")),
    path("error", _error),
]


def _configure(tmp_path):
    if not settings.configured:
        settings.configure(SECRET_KEY="test-only", ALLOWED_HOSTS=["testserver"])
    django.setup()
    return override_settings(
        ROOT_URLCONF=__name__,
        MIDDLEWARE=["kairon.django.KaironMiddleware"],
        KAIRON={
            "endpoint": "http://127.0.0.1:1",
            "project_id": "11111111-1111-1111-1111-111111111111",
            "api_key": "krn_test", "config_path": str(tmp_path / "credential.json"),
            "enable_metrics": False,
        },
    )


def test_django_wsgi_handler_records_a_real_request(tmp_path, monkeypatch):
    observed = []
    monkeypatch.setattr(Kairon, "_send_normalized", lambda self, items: observed.extend(items) or True)
    with _configure(tmp_path):
        response = Client().get("/orders")
        assert response.status_code == 200
        collector = get_default_instance()
        assert collector is not None and collector.stop(2)
    assert len(observed) == 1
    assert observed[0][1]["Endpoint"] == "/orders"


def test_django_asgi_handler_records_a_real_request(tmp_path, monkeypatch):
    observed = []
    monkeypatch.setattr(Kairon, "_send_normalized", lambda self, items: observed.extend(items) or True)
    with _configure(tmp_path):
        response = asyncio.run(AsyncClient().get("/orders"))
        assert response.status_code == 200
        collector = get_default_instance()
        assert collector is not None and collector.stop(2)
    assert len(observed) == 1
    assert observed[0][1]["Endpoint"] == "/orders"


@pytest.mark.parametrize("asynchronous", [False, True])
def test_django_handlers_record_unhandled_500_without_changing_response(tmp_path, monkeypatch, asynchronous):
    observed = []
    monkeypatch.setattr(Kairon, "_send_normalized", lambda self, items: observed.extend(items) or True)
    with _configure(tmp_path):
        if asynchronous:
            response = asyncio.run(AsyncClient(raise_request_exception=False).get("/error"))
        else:
            response = Client(raise_request_exception=False).get("/error")
        assert response.status_code == 500
        collector = get_default_instance()
        assert collector is not None and collector.stop(2)
    assert len(observed) == 1
    assert observed[0][1]["StatusCode"] == 500
