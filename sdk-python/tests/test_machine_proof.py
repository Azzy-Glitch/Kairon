"""Machine scope is optional, body-bound and never discloses SDK credentials to localhost."""

import hashlib
import io
import json
import unittest
from unittest.mock import patch
from uuid import uuid4

from kairon import _machine_proof


class MachineProofTests(unittest.TestCase):
    def test_no_agent_keeps_ordinary_telemetry_unscoped(self):
        def never_open(*_args, **_kwargs):
            self.fail("backend challenge should not be created without an Agent")

        with patch.object(_machine_proof, "_agent_exchange", return_value=False):
            self.assertIsNone(_machine_proof.acquire(
                "http://localhost:8000", str(uuid4()), "Orders", "Production",
                "krn_test-secret", b"{}", never_open, 1.0,
            ))

    def test_agent_sees_only_opaque_id_and_backend_gets_body_bound_challenge(self):
        proof_id = uuid4()
        project_id = uuid4()
        body = b'{"events":[{"status":500}]}'
        local_calls = []

        def agent_exchange(endpoint, proof=None):
            local_calls.append((endpoint, proof))
            return True

        def backend_open(request, timeout):
            self.assertEqual(timeout, 1.0)
            self.assertEqual(request.get_header("X-kairon-api-key"), "krn_test-secret")
            self.assertEqual(request.full_url, "http://localhost:8000/api/v1/telemetry/machine-proofs")
            challenge = json.loads(request.data)
            self.assertEqual(challenge["projectId"], str(project_id))
            self.assertEqual(challenge["service"], "Orders")
            self.assertEqual(challenge["environment"], "Production")
            self.assertEqual(challenge["bodySha256"], hashlib.sha256(body).hexdigest().upper())
            return io.BytesIO(json.dumps({"proofId": str(proof_id)}).encode())

        with patch.object(_machine_proof, "_agent_exchange", side_effect=agent_exchange):
            actual = _machine_proof.acquire(
                "http://localhost:8000", str(project_id), "Orders", "Production",
                "krn_test-secret", body, backend_open, 1.0,
            )
        self.assertEqual(actual, str(proof_id))
        self.assertEqual(local_calls, [("http://localhost:8000", None),
                                       ("http://localhost:8000", str(proof_id))])


if __name__ == "__main__":
    unittest.main()
