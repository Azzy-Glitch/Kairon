# Database settings

SQLite remains the default. Installation requires no SQL Server or external database infrastructure. The Settings page can optionally select an existing SQL Server database, test access, and save encrypted settings.

## Applying a selection

Database services are bound at startup. Save does not switch a running backend, restart it, or copy any data. The UI displays the active provider separately from the saved selection and explicitly requires a backend restart. Arrange an idle remediation window and back up data before restarting. Each database retains its own projects, incidents, machine enrollment and AI credentials. Verify health and operator configuration after restart.

SQL Server Test and Save open the specified existing database and execute SELECT 1 with bounded timeouts. They never provision a server or create a database. The normal forward EF migration chain runs on backend startup; the backend identity must have permission to apply the schema. A successful connectivity test alone does not prove migration permissions. Production requires TLS encryption with server certificate validation. Windows authentication uses the backend process identity, not the browser user's identity.

## Protection and precedence

Operator authorization is required on GET/POST /api/v1/database-config and POST /api/v1/database-config/test. Responses never contain a password or full connection string. Blank passwords reuse a saved SQL login password only for the identical target, identity and TLS settings. Connection failures return generic messages; password-bearing exception details are not logged or sent to the browser.

The bootstrap selection is outside either database: config/database-settings.protected under KAIRON's resolved local data root. It uses the existing ASP.NET Data Protection key ring and application name, with DPAPI on Windows. Protect the data directory and key ring with OS account access controls. Back up the protected file together with its keys; Windows DPAPI binds recovery to the corresponding account/machine protection context.

A saved selection overrides deployment provider/connection settings at next startup. With no saved file, existing deployment settings and the SQLite default are unchanged. SQL connection failure or unreadable protected configuration fails startup rather than silently falling back. To recover an inaccessible selection, stop the backend, preserve the protected file and keys for diagnosis, and move database-settings.protected aside to resume deployment defaults; then restart. This does not delete either database. No live provider switching or cross-database data migration is supported.
