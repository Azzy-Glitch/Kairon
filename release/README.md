# Kairon release artifacts

Generate the Windows installer with `installer/build-installer.ps1`, the .NET package with
`dotnet pack sdk/Kairon.SDK/Kairon.SDK.csproj -c Release`, and the Python package with
`python -m build sdk-python`. Compare each release's generated files with its versioned
`checksums-<version>.txt` manifest before publishing. Historical checksum manifests are retained;
the current release candidate is 1.0.1.

The repository intentionally ignores generated binaries under `artifacts/`. CI uploads the
installer as a workflow artifact; package/container registry publication requires release-owner
credentials and is deliberately not performed by local builds.
