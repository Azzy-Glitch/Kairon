# KAIRON centralized deployment

Cloud Mode runs the same backend, APIs, telemetry normalization, incident engine, SDKs, Agent, and
AI gateway as Local Mode. Only persistence and infrastructure configuration differ. The supplied
composition deliberately does not bundle a database: point it at an existing managed SQL Server
instance using a secret supplied by the deployment platform.

## Required configuration

Set these values in the host or secret manager. Do not commit them to an environment file:

```text
KAIRON_SQL_CONNECTION  SQL Server connection string
KAIRON_OPERATOR_KEY    high-entropy operator authorization key
KAIRON_ALLOWED_ORIGIN  public HTTPS origin of the KAIRON UI
```

Optional AI settings are `KAIRON_AI_PROVIDER`, `KAIRON_AI_MODEL`, and the provider credential such
as `QWEN_API_KEY`. With `KAIRON_AI_PROVIDER=mock`, no paid provider or credential is required.

Start the deployment from the repository root:

```bash
docker compose -f docker-compose.cloud.yml up --build -d
docker compose -f docker-compose.cloud.yml ps
```

The backend applies its existing provider-specific EF migrations at startup and reports database
readiness at `/api/health/ready`. The AI container reports health at `/health`. Both containers run
as non-root users. Agents and SDKs use the public backend URL exactly as they do in Local Mode; they
never receive the SQL connection string and never access persistence directly.

## Alibaba Cloud

The images are ordinary OCI Linux images and can be pushed to Alibaba Cloud Container Registry.
Run them on ACK or ECS with the SQL connection and API keys sourced from Secrets Manager or sealed
Kubernetes secrets. Use an Alibaba Cloud SQL Server-compatible managed service or a supported
externally operated SQL Server endpoint, allow network access only from the backend workload, and
terminate TLS at the ingress/load balancer. The Qwen provider uses DashScope's OpenAI-compatible
endpoint and reads `QWEN_API_KEY` only in the AI container.

## Upgrade and rollback

Back up the managed external database using its native consistent backup mechanism before changing
image versions. Deploy immutable version tags, wait for backend readiness, and retain the previous
image tag for rollback. Never roll back database files by copying a live volume. Review EF migration
compatibility before downgrading an application image.
