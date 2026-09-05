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
# Accept either repository line-ending convention. In multiline .NET regex mode `$` matches
# before `\n` but not before the preceding `\r`, so a strict end anchor rejects a valid CRLF
# Inno source file before any packaging work starts.
if ($installerScript -notmatch '(?m)^#define MyAppVersion "([^"]+)"\r?$') {
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
$pythonBuildEnvironment = Join-Path $artifacts "python-build-venv"

$artifactsRoot = [System.IO.Path]::GetFullPath($artifacts).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
foreach ($stagingDirectory in @($package, $backend, $agent, $useragent, $ai, $pythonBuildEnvironment)) {
    $resolvedStagingDirectory = [System.IO.Path]::GetFullPath($stagingDirectory)
    if (-not $resolvedStagingDirectory.StartsWith($artifactsRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a staging directory outside the repository artifacts directory: $resolvedStagingDirectory"
    }
    if (Test-Path -LiteralPath $resolvedStagingDirectory) {
        Remove-Item -LiteralPath $resolvedStagingDirectory -Recurse -Force
    }
}
New-Item -ItemType Directory -Force -Path $package, $backend, $agent, $useragent, $ai, (Join-Path $artifacts "installer") | Out-Null
Copy-Item -LiteralPath (Join-Path $repository "LICENSE"), (Join-Path $repository "NOTICE") -Destination $package

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

$bootstrapPython = (Get-Command python -ErrorAction SilentlyContinue).Source
if (-not $bootstrapPython) { throw "No Python interpreter was found on PATH to create the isolated packaging environment." }

& $bootstrapPython -m venv $pythonBuildEnvironment
if ($LASTEXITCODE -ne 0) { throw "Could not create the isolated AI packaging environment." }
$python = Join-Path $pythonBuildEnvironment "Scripts\python.exe"

Push-Location (Join-Path $repository "ai-service")
try {
    # Install only into artifacts/python-build-venv. Packaging never mutates the developer's
    # global interpreter or a pre-existing project environment.
    & $python -m pip install --disable-pip-version-check -r requirements-build.txt
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

# --- Optional Authenticode signing -----------------------------------------------------------
# Off by default and never required to build: a local/CI build with no signing material
# configured produces a legitimate, working, *unsigned* installer, and this script says so
# explicitly rather than letting a NotSigned installer look no different from a deliberate one.
#
# Two supported sources for a *real* certificate, matching how production code-signing
# certificates actually get delivered - never a certificate committed to this repository:
#   - $env:KAIRON_SIGN_THUMBPRINT: a certificate already installed in the current user's or
#     machine's certificate store (the normal shape for an EV certificate on a hardware token or
#     cloud HSM - EV certificates cannot be exported as a portable .pfx at all per CA/Browser
#     Forum rules, so this is the path that matters most for actually clearing SmartScreen).
#   - $env:KAIRON_SIGN_PFX_PATH (+ $env:KAIRON_SIGN_PFX_PASSWORD): a traditional OV certificate
#     exported to a .pfx file kept outside the repository, e.g. mounted from a secrets manager.
$installerPath = Join-Path $artifacts "installer\Kairon-Setup-$productVersion-win-x64.exe"
$installerExe = Get-Item -LiteralPath $installerPath -ErrorAction SilentlyContinue
if (-not $installerExe) {
    throw "Installer compilation reported success but the expected output was not found: $installerPath"
}

$signThumbprint = $env:KAIRON_SIGN_THUMBPRINT
$signPfxPath = $env:KAIRON_SIGN_PFX_PATH
$signPfxPassword = $env:KAIRON_SIGN_PFX_PASSWORD

if ([string]::IsNullOrWhiteSpace($signThumbprint) -and [string]::IsNullOrWhiteSpace($signPfxPath)) {
    Write-Host "No code-signing certificate configured (KAIRON_SIGN_THUMBPRINT / KAIRON_SIGN_PFX_PATH not set) - $($installerExe.Name) is unsigned."
} else {
    $signtool = Get-ChildItem -Path "C:\Program Files (x86)\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like "*x64*" } | Select-Object -First 1
    if (-not $signtool) { throw "A signing certificate was configured but signtool.exe was not found. Install the Windows SDK." }

    if (-not [string]::IsNullOrWhiteSpace($signThumbprint)) {
        & $signtool.FullName sign /sha1 $signThumbprint /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $installerExe.FullName
    } else {
        if ([string]::IsNullOrWhiteSpace($signPfxPassword)) {
            throw "KAIRON_SIGN_PFX_PATH is set but KAIRON_SIGN_PFX_PASSWORD is not."
        }
        & $signtool.FullName sign /f $signPfxPath /p $signPfxPassword /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $installerExe.FullName
    }
    if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE." }

    & $signtool.FullName verify /pa $installerExe.FullName
    if ($LASTEXITCODE -ne 0) { throw "Signed $($installerExe.Name) but Authenticode verification failed - the resulting installer would not be trusted." }

    Write-Host "Signed and verified $($installerExe.Name)."
}
