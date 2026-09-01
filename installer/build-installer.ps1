# Builds the full Windows package: desktop shell, backend, Agent, and the PyInstaller-packaged
# AI service, then (if Inno Setup is available) compiles the installer.
#
# Adapted from Azzy's productization branch (origin/main / codex/productization-windows-20260828)
# and updated for this codebase's actual layout (docs/DESKTOP_SHELL.md):
#   - backend/Kairon.Backend.csproj (was backend/AIDIP.Backend.csproj)
#   - agent/Kairon.Agent/Kairon.Agent.csproj (was agent/KAIRON.Agent.csproj)
#   - a real desktop/Kairon.Desktop/Kairon.Desktop.csproj now exists and is published too - the
#     source branch never built one, it just added a --desktop flag to the backend that opened a
#     browser
#   - ai-service/entrypoint.py (was ai-service/launcher.py) - a self-running uvicorn entrypoint;
#     main.py itself stays an importable ASGI module for local dev (`python -m uvicorn main:app`)
#   - this repo has no dedicated ai-service/.venv; falls back to system `python` if one isn't
#     present, rather than requiring one to exist first
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$InnoCompiler = "",
    [switch]$SkipFrontendRestore,
    [switch]$SkipInstallerCompile
)

$ErrorActionPreference = "Stop"
$repository = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifacts = Join-Path $repository "artifacts"
$versionPropsPath = Join-Path $repository "Directory.Build.props"
[xml]$versionProps = Get-Content -LiteralPath $versionPropsPath -Raw
$productVersion = [string]$versionProps.Project.PropertyGroup.VersionPrefix
if ($productVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "Directory.Build.props VersionPrefix must be a three-part release version; found '$productVersion'."
}

$installerScriptPath = Join-Path $PSScriptRoot "Kairon.iss"
$installerScript = Get-Content -LiteralPath $installerScriptPath -Raw
if ($installerScript -notmatch '(?m)^#define MyAppVersion "([^"]+)"$') {
    throw "Kairon.iss does not define MyAppVersion."
}
if ($Matches[1] -ne $productVersion) {
    throw "Release version mismatch: Directory.Build.props=$productVersion, Kairon.iss=$($Matches[1])."
}

$package = Join-Path $artifacts "windows-package"
$backend = Join-Path $package "backend"
$agent = Join-Path $package "agent"
$useragent = Join-Path $package "useragent"
$ai = Join-Path $package "ai"

$artifactsRoot = [System.IO.Path]::GetFullPath($artifacts).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
foreach ($stagingDirectory in @($package, $backend, $agent, $useragent, $ai)) {
    $resolvedStagingDirectory = [System.IO.Path]::GetFullPath($stagingDirectory)
    if (-not $resolvedStagingDirectory.StartsWith($artifactsRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a staging directory outside the repository artifacts directory: $resolvedStagingDirectory"
    }
    if (Test-Path -LiteralPath $resolvedStagingDirectory) {
        Remove-Item -LiteralPath $resolvedStagingDirectory -Recurse -Force
    }
}
New-Item -ItemType Directory -Force -Path $package, $backend, $agent, $useragent, $ai, (Join-Path $artifacts "installer") | Out-Null

Push-Location (Join-Path $repository "frontend")
try {
    if (-not $SkipFrontendRestore) { & npm.cmd ci; if ($LASTEXITCODE -ne 0) { throw "Frontend restore failed." } }
    & npm.cmd run build
    if ($LASTEXITCODE -ne 0) { throw "Frontend build failed." }
} finally { Pop-Location }

# Backend's own BuildKaironFrontend MSBuild target would rebuild the frontend again on publish;
# SkipFrontendBuild=true reuses the build just produced above instead.
& dotnet publish (Join-Path $repository "backend\Kairon.Backend.csproj") -c $Configuration -r $Runtime --self-contained true -p:SkipFrontendBuild=true -o $backend
if ($LASTEXITCODE -ne 0) { throw "Backend publish failed." }

& dotnet publish (Join-Path $repository "agent\Kairon.Agent\Kairon.Agent.csproj") -c $Configuration -r $Runtime --self-contained true -o $agent
if ($LASTEXITCODE -ne 0) { throw "Agent publish failed." }

& dotnet publish (Join-Path $repository "agent\Kairon.UserAgent\Kairon.UserAgent.csproj") -c $Configuration -r $Runtime --self-contained true -o $useragent
if ($LASTEXITCODE -ne 0) { throw "UserAgent publish failed." }

& dotnet publish (Join-Path $repository "desktop\Kairon.Desktop\Kairon.Desktop.csproj") -c $Configuration -r $Runtime --self-contained true -o $package
if ($LASTEXITCODE -ne 0) { throw "Desktop shell publish failed." }

$python = Join-Path $repository "ai-service\.venv\Scripts\python.exe"
if (-not (Test-Path -LiteralPath $python)) {
    $python = (Get-Command python -ErrorAction SilentlyContinue).Source
    if (-not $python) { throw "No Python found (looked for ai-service/.venv or 'python' on PATH)." }
}
Push-Location (Join-Path $repository "ai-service")
try {
    # Installed from the committed lockfile, not whatever happens to already be present in
    # whichever Python this run found - otherwise the packaged AI service reproducibly builds from
    # source but not from a known dependency set (matches .github/workflows/windows-installer.yml).
    & $python -m pip install -r requirements.txt
    if ($LASTEXITCODE -ne 0) { throw "AI service dependency install failed." }

    $versionParts = $productVersion.Split('.') | ForEach-Object { [int]$_ }
    $aiVersionFile = Join-Path $artifacts "pyinstaller-version.txt"
    $aiVersionResource = @"
VSVersionInfo(
  ffi=FixedFileInfo(
    filevers=($($versionParts[0]), $($versionParts[1]), $($versionParts[2]), 0),
    prodvers=($($versionParts[0]), $($versionParts[1]), $($versionParts[2]), 0),
    mask=0x3f,
    flags=0x0,
    OS=0x40004,
    fileType=0x1,
    subtype=0x0,
    date=(0, 0)
  ),
  kids=[
    StringFileInfo([
      StringTable('040904B0', [
        StringStruct('CompanyName', 'Kairon'),
        StringStruct('FileDescription', 'Kairon AI Service'),
        StringStruct('FileVersion', '$productVersion.0'),
        StringStruct('ProductName', 'Kairon'),
        StringStruct('ProductVersion', '$productVersion')
      ])
    ]),
    VarFileInfo([VarStruct('Translation', [1033, 1200])])
  ]
)
"@
    Set-Content -LiteralPath $aiVersionFile -Value $aiVersionResource -Encoding Ascii

    & $python -m PyInstaller --noconfirm --clean --onefile --name Kairon.AI `
        --version-file $aiVersionFile --distpath $ai `
        --workpath (Join-Path $artifacts "pyinstaller-work") --specpath (Join-Path $artifacts "pyinstaller-spec") `
        --collect-all fastapi --collect-all uvicorn entrypoint.py
    if ($LASTEXITCODE -ne 0) { throw "AI service packaging failed." }
} finally { Pop-Location }

if ($SkipInstallerCompile) {
    Write-Host "Skipping installer compilation (-SkipInstallerCompile). Package staged at $package"
    exit 0
}

if ([string]::IsNullOrWhiteSpace($InnoCompiler)) {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    )
    $InnoCompiler = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler)) {
    throw "Inno Setup 6 compiler was not found. Install it, or pass -SkipInstallerCompile to stop after staging the package at $package."
}
& $InnoCompiler "/DPackageRoot=$package" $installerScriptPath
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed." }
