# Kairon 1.0.0 release artifacts

Generate the Windows installer with `installer/build-installer.ps1`, the .NET package with
`dotnet pack sdk/Kairon.SDK/Kairon.SDK.csproj -c Release`, and the Python package with
`python -m build sdk-python`. Compare the resulting files with `checksums-1.0.0.txt` before
publishing.

The repository intentionally ignores generated binaries under `artifacts/`. CI uploads the
installer as a workflow artifact; package/container registry publication requires release-owner
credentials and is deliberately not performed by local builds.
