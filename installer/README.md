# KAIRON Windows installer

`build-installer.ps1` creates self-contained Windows x64 backend and Agent publishes, bundles the
React UI, packages the local AI gateway, and compiles `KAIRON.iss` with Inno Setup 6.

The installer registers the existing `KAIRON.Agent.exe` as the automatic `KAIRON.Agent` Windows
Service. Launching KAIRON starts the backend/UI and its packaged local AI gateway. Local data under
`%LOCALAPPDATA%\KAIRON` is deliberately preserved during uninstall; this prevents monitoring and
incident history from being destroyed accidentally. Remove that directory manually only when a
full data purge is intended.

Upgrades stop and replace the Agent service. The backend creates a consistent SQLite startup
backup before applying migrations on the first launch of the upgraded product.
