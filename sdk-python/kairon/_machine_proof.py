"""Optional Agent-backed machine proof for telemetry; never exposes SDK or Agent keys locally.

The normal direct delivery path stays available when the Windows Agent is absent. Only a
backend-confirmed, one-use proof can make an observation remediation-capable. The localhost
exchange carries an opaque challenge id, NOT the saved SDK API key.
"""

import hashlib
import json
import socket
import struct
import urllib.request
import uuid


_PORT = 47891


def _agent_exchange(endpoint: str, proof_id: str | None = None) -> bool:
    frame = json.dumps({"endpoint": endpoint, "proofId": proof_id, "ping": proof_id is None}).encode("utf-8")
    try:
        with socket.create_connection(("127.0.0.1", _PORT), timeout=0.2) as connection:
            connection.settimeout(3.0)
            connection.sendall(struct.pack(">I", len(frame)) + frame)
            length = struct.unpack(">I", _read_exact(connection, 4))[0]
            if not 0 < length <= 1024:
                return False
            answer = json.loads(_read_exact(connection, length))
            return answer.get("confirmed") is True
    except (OSError, ValueError, KeyError, TypeError, json.JSONDecodeError):
        return False


def _read_exact(connection: socket.socket, count: int) -> bytes:
    chunks = bytearray()
    while len(chunks) < count:
        part = connection.recv(count - len(chunks))
        if not part:
            raise ConnectionError("Machine-proof listener disconnected")
        chunks.extend(part)
    return bytes(chunks)


def acquire(endpoint: str, project_id: str, service: str, environment: str,
            api_key: str | None, body: bytes, opener, timeout: float) -> str | None:
    if not api_key or not _agent_exchange(endpoint):
        return None
    challenge = json.dumps({
        "projectId": project_id,
        "service": service,
        "environment": environment,
        "bodySha256": hashlib.sha256(body).hexdigest().upper(),
    }).encode("utf-8")
    request = urllib.request.Request(
        endpoint + "/api/v1/telemetry/machine-proofs", data=challenge, method="POST",
        headers={"Content-Type": "application/json", "X-Kairon-API-Key": api_key},
    )
    try:
        with opener(request, timeout=timeout) as response:
            answer = json.loads(response.read(4096))
        proof_id = str(uuid.UUID(answer["proofId"]))
        return proof_id if _agent_exchange(endpoint, proof_id) else None
    except (OSError, ValueError, KeyError, TypeError, json.JSONDecodeError):
        return None
