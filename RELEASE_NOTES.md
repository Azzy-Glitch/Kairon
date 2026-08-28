# KAIRON 1.0.0

Release candidate date: 2026-08-28

KAIRON 1.0.0 is the first coherent local-first Windows product release over the verified SRE and
observability foundation. It preserves existing APIs and incident behavior while making SQLite the
zero-setup Local Mode default and retaining configurable SQL Server centralized deployment.

## Included artifacts

- KAIRON Windows installer and self-contained `KAIRON.exe`
- KAIRON Agent 1.0.0 Windows Service payload
- KAIRON .NET SDK 1.0.0 (`AIDIP.SDK`, legacy package ID preserved for compatibility)
- KAIRON Python SDK 1.0.0 (`kairon-sdk`)
- Backend and AI OCI Dockerfiles plus centralized deployment composition
- AI gateway API v2.0, packaged with the KAIRON 1.0.0 product

## Product highlights

- Automatic managed SQLite creation, EF migration, integrity readiness, retention, online backup,
  upgrade backup, and data-preserving uninstall behavior
- Durable machine identity, zero-code process/resource discovery, Agent heartbeat, reconnect, and
  bounded resource collection
- Unified application inventory with explicit Basic and Deep Monitoring states
- One-time, hashed, expiring, scoped, revocable .NET/Python SDK pairing and automatic telemetry
  association
- Correlated incidents, evidence-based AI diagnosis, approval-controlled remediation, and measured
  post-remediation verification
- Existing React UI delivered by the Windows product host, with Agent, application, integration,
  incident, persistence-health, and troubleshooting workflows
- Non-root cloud images, health checks, CI, and external SQL Server configuration for centralized use

## Compatibility

Legacy routes including `/analyze-error`, `/validate-api`, `/predict`, and `/recommend` remain
available. Existing telemetry APIs, SQL Server provider support, .NET SDK package identity, and
provider-independent AI contracts are preserved.

## Known release-gate limitations

This repository candidate has 414 passing automated tests and passed installed-binary SQLite
restart/data-survival verification. On the current verification host, Windows Service creation was
blocked by the unavailable service-control-manager administrator token, and the backend container's
final .NET base layer download stalled at the registry. Repeat those two checks on the elevated,
network-unrestricted release VM before publishing or signing the installer. See
[`docs/verification-report.md`](docs/verification-report.md) for exact evidence.
