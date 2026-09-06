# Windows service remediation

KAIRON runtime environments are Development, Staging and Production. These use the same real
executor and safety gates. SDK telemetry environment labels remain application metadata; labels
such as Demo or Hackathon do not authorize product remediation.

## Enrollment and authorization

1. Register the project and issue a dedicated project API credential for the target workload.
2. Enroll the Windows machine with distinct generated Agent and UserAgent credentials. Keep its
   machine heartbeat active. Obtain the enrolled machine ID and verify its hostname independently.
3. Configure exactly one `WindowsRemediation:Targets` entry per project/environment/logical service.
   Assign a distinct logical service name when separately targeting multiple instances.
4. Configure the workload SDK with that dedicated API key, project ID, logical service,
   environment and `MachineId` (`machine_id` in Python).
5. Give the backend's Windows account SCM query and only the required service-control rights on
   the named machine/service. Remote targets also need the appropriate Windows RPC connectivity.
   Protect target configuration and the backend identity from untrusted writers. The executor
   never accepts credentials, hostnames, commands or service names from the AI.

Example target (replace every placeholder; no targets are enabled by default):

```json
{
  "WindowsRemediation": {
    "MachineHeartbeatMaxAgeSeconds": 90,
    "Targets": [{
      "ProjectId": "<registered-project-guid>",
      "Environment": "Production",
      "Service": "orders-primary",
      "MachineId": "<enrolled-machine-guid>",
      "TelemetryCredentialId": "<dedicated-project-credential-guid>",
      "ExpectedHostName": "orders-host",
      "WindowsServiceName": "OrdersService",
      "AllowedOperations": ["RestartService", "RunHealthCheck"]
    }]
  }
}
```

The supported operations are `StartService`, `RestartService`, `StopService` and `RunHealthCheck`.
Restart requires a currently running service, so it cannot silently start one deliberately stopped
by an operator. Stop is high risk and must be explicitly allowed. SCM service names currently use
letters, digits, periods, underscores or hyphens. Arbitrary process execution and application-specific
cache/retry/concurrency controls are not Windows service operations. The previous simulator
implementations exist only as historical test fixtures, not product executors or API routes.

Production requires operator and telemetry authentication. Keep `RequireApprovalForEveryAction`
enabled unless an operator has deliberately authorized another policy. Global risk/tool limits,
per-target operation allowlists and execution limits are cumulative. A server-generated fingerprint
binds each recommendation and approval to the exact target, operation, enrollment and telemetry
credential. Target changes or stale enrollment invalidate it. Restart the backend after editing
target configuration; existing approvals must be regenerated when their binding changes.

## Execution and verification

AI recommendations pass deterministic policy before approval and again before execution. The
Windows backend invokes only fixed SCM operations through `sc.exe` using argument arrays, with a
30-second tool deadline and the configured executor deadline. Operations against the same physical
service are serialized within a backend process. Deploy one active executor per target; coordination
across independent backend replicas is not implemented.

SCM success does not resolve an incident. After settling, verification requires sufficient fresh
telemetry for the exact project, environment, service and enrolled machine, plus a fresh SCM Running
probe. Ingestion accepts a machine identity only with its assigned active telemetry credential.
Legacy rows without a machine identity remain readable but cannot establish machine-target recovery.
Every actual breached signal must be accounted for and within threshold. Unsupported signals and
missing measurements cannot pass. A stopped service alone is not application recovery; successful
StopService execution will not manufacture a recovered incident.

Timeouts and failures are recorded rather than retried blindly. Windows may already have accepted
an operation when a timeout occurs; inspect the target before authorizing another action. A failed
restart may leave the service stopped. Do not interpret a failed action as proof that no change took
place. Audit records retain execution identity and the verification scope/result.

## Local validation and deployment limits

Run backend/frontend and AI service on localhost from the same checkout for validation. Use a
dedicated non-critical service and an isolated database; never point a validation allowlist at a
system or production service. The SDK queues are bounded in memory, expose failed/dropped delivery,
and support bounded drain; they do not guarantee durable or exactly-once delivery.

The mock AI provider remains explicitly identified as mock. Passing a lifecycle test with mock
reasoning proves the pipeline and executor, not a live provider's diagnosis quality. Operators must
configure their actual AI provider, Windows permissions, protected configuration and deployment
network boundary before use. These changes alone do not certify every deployment production-ready.
