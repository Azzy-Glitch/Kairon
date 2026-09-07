# Remediation recovery and deployment boundary

KAIRON supports **one active backend process/executor per database and Windows service target**. Do not run independent active replicas against the same database or target. Queue de-duplication and execution gates are process-local; they do not implement HA, distributed leases, or cross-replica ownership. Restarting the single backend is supported through the reconciliation below.

## Durable action state, volatile work delivery

The database's approval, execution-start marker, execution result, verification result and audit events are authoritative. The in-memory channel only delivers work. Every 10 seconds (including an initial startup pass), reconciliation considers at most 50 in-flight incidents, rotating by update time. Recovery is bounded to 15 minutes from the original approval; queue rejection does not refresh that authority. The gate excludes live execution/verification and serializes duplicate approval/delivery within this single backend.

| Persisted state after interruption | Recovery |
|---|---|
| Approved, no execution-start marker | Queue again within the approval window. Recheck deterministic policy, exact target binding, enrollment, credentials and permissions before any command. |
| Executing, or Approved with a start marker | Fail with an uncertain-outcome audit and require operator review. Never automatically replay the command: Windows may have accepted it before the backend stopped. |
| Executed with durable completion | Resume verification only, once, within the original approval window. Preserve execution evidence and mark any incomplete verification inconclusive. |
| Resumed verification interrupted again, expired authority, or inconsistent state | Fail and audit the reason. No command replay or assumed recovery. |
| Resolved or another terminal incident | Ignore stale work delivery. |

The executor saves its start marker before calling SCM. A full queue after approval is audited as deferred; the persisted approval remains discoverable by reconciliation. A database outage cannot be hidden: failed reconciliation is logged and retried on a later bounded sweep. Persisted state cannot be repaired while its database is unavailable.

After an uncertain outcome, inspect the actual target state and telemetry before any further change. The failed action is not replayable. Continuing remediation requires fresh incident evidence and a new operator decision/approval; do not assume a reported failure means Windows did nothing. An interrupted verification never implies success.

## Action-specific verification

RestartService retains the existing strict contract: fresh telemetry for the exact project/environment/service/machine, every breached signal within threshold, unchanged approved target binding, and native SCM Running state. StartService and RunHealthCheck use that running-service recovery contract as well.

StopService means intentional containment, not restored availability. It requires an executed action with a recorded completion time, an unchanged exact enrolled target, two fresh SCM Stopped probes separated by the configured settle period (bounded to 60 seconds), and an independent enrolled-machine heartbeat after operation completion. The observation has an overall deadline of settle time plus 10 seconds. A deliberately stopped workload need not emit metrics. A stale/offline machine, changed target, missing completion, denied probe, or service restarting between probes cannot pass. Audit and result text explicitly distinguish confirmed intentional stop from restored application availability.

For StopService, provision the machine agent independently of the workload being stopped; the workload's own heartbeat cannot prove post-stop machine health.

## SQL Server and SQLite

The existing forward migration `20260906120000_AddTelemetryMachineScope` adds nullable MachineId columns to Incidents and Metrics. Historical migrations are preserved. SQLite continues to use its separate version-4 schema migrator unchanged.

The backend tests validate the generated SQL and exercise fresh creation, upgrade with existing telemetry, machine-scoped writes/queries, and repeat migration against SQL Server. Windows tests use `(localdb)\MSSQLLocalDB` by default. Other test hosts can supply `KAIRON_TEST_SQLSERVER` for a dedicated test server; each test overrides its database name with a generated `Kairon_MigrationTest_<guid>` name and deletes only that database afterward. Without a server on non-Windows hosts, the two SQL Server integration tests are explicitly skipped rather than presented as executed.

Actual Production rollout still needs unique protected credentials, exact production target enrollment and SCM permissions, a trusted/authenticated agent-provisioning boundary, and validation of the intended hosting, backup and recovery setup. The development README credentials and loopback setup are not production provisioning. No multi-replica remediation support is claimed.
