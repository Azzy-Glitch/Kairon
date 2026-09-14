"""Transport-security contract (kairon._endpoint_security): plain HTTP is trusted only for a
genuine loopback destination; HTTPS is trusted for any destination, local or remote. Mirrors
tests/Kairon.SDK.Tests/EndpointSecurityTests.cs - both SDKs must enforce the identical policy.
"""

from __future__ import annotations

import pytest

from kairon._endpoint_security import is_endpoint_allowed


@pytest.mark.parametrize("endpoint", [
    "http://localhost",
    "http://localhost:8000",
    "http://LOCALHOST",  # host matching is case-insensitive
    "http://127.0.0.1",
    "http://127.0.0.1:8000",
    "http://127.1.2.3",
    "http://127.255.255.255",
    "http://[::1]",
    "http://[::1]:8000",
])
def test_loopback_http_is_allowed(endpoint):
    assert is_endpoint_allowed(endpoint) is True


@pytest.mark.parametrize("endpoint", [
    "http://192.168.1.20",
    "http://10.0.0.20",
    "http://172.16.1.20",
    "http://8.8.8.8",
    "http://example.com",
    "http://example.com:8000",
    # Not proper URI parsing pitfalls: a naive prefix/substring check could be fooled by these.
    "http://localhost.evil.com",
    "http://evil.com/localhost",
    "http://127.0.0.1.evil.com",
])
def test_remote_plaintext_http_is_rejected(endpoint):
    assert is_endpoint_allowed(endpoint) is False


@pytest.mark.parametrize("endpoint", [
    "https://localhost",
    "https://127.0.0.1",
    "https://[::1]",
    "https://192.168.1.20",
    "https://10.0.0.20",
    "https://172.16.1.20",
    "https://8.8.8.8",
    "https://example.com",
    "https://api.example.com:8443",
])
def test_https_is_allowed_for_any_host_local_or_remote(endpoint):
    assert is_endpoint_allowed(endpoint) is True


@pytest.mark.parametrize("endpoint", [
    "https://user:password@example.com",
    "http://user:password@127.0.0.1",
    "https://user@example.com",
])
def test_embedded_userinfo_is_rejected_regardless_of_scheme_or_host(endpoint):
    assert is_endpoint_allowed(endpoint) is False


@pytest.mark.parametrize("endpoint", [
    None,
    "",
    "   ",
    "not-a-url",
    "ftp://127.0.0.1",
    "javascript://127.0.0.1",
    "http://",
    "http:///no-host",
])
def test_malformed_or_non_http_schemes_are_rejected(endpoint):
    assert is_endpoint_allowed(endpoint) is False
