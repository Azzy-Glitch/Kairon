"""Transport-security contract for every endpoint this SDK is ever asked to use.

The .NET counterpart is sdk/Kairon.SDK/KaironEndpointSecurity.cs - both SDKs implement the exact
same policy so a caller cannot end up more or less protected depending on which language they use.

Plain, unencrypted HTTP is trusted ONLY for a genuine loopback destination (this same machine) -
anywhere else, a pairing code, a project API key, or telemetry could cross a real network in clear
text. HTTPS is accepted for any destination, local or remote. Embedded userinfo
(https://user:pass@host) is never accepted either - that is a credential leak vector of its own
(logged URLs, proxies, shell history) and this SDK never needs it.

Applied everywhere an endpoint can enter the SDK - explicit configuration, a stored connection, and
critically a pairing response's own returned endpoint - so a compromised or malicious backend/
pairing response can never redirect this SDK onto a remote plaintext address merely by returning
one.
"""

from __future__ import annotations

import ipaddress
from urllib.parse import urlsplit


def is_endpoint_allowed(endpoint: str | None) -> bool:
    """True only for an absolute http(s) URL with no embedded credentials, where plain HTTP is
    additionally restricted to a loopback host (localhost, 127.0.0.0/8, ::1)."""
    if not endpoint:
        return False

    try:
        parsed = urlsplit(endpoint)
    except ValueError:
        return False

    if parsed.scheme not in ("http", "https"):
        return False
    if not parsed.hostname:
        return False
    # Embedded userinfo (user:pass@host) is never allowed, on either scheme.
    if parsed.username or parsed.password:
        return False

    if parsed.scheme == "https":
        return True

    # Plain HTTP is trusted only for this exact machine. urlsplit().hostname already strips the
    # [] bracket notation from an IPv6 literal (e.g. "[::1]" -> "::1") and lowercases it, so
    # ipaddress.ip_address handles both IPv4 and IPv6 loopback forms uniformly, including the
    # whole 127.0.0.0/8 range via is_loopback - not merely a literal "127.0.0.1" string match.
    host = parsed.hostname
    if host == "localhost":
        return True
    try:
        return ipaddress.ip_address(host).is_loopback
    except ValueError:
        return False
