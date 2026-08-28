param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$InnoCompiler = "",
    [switch]$SkipFrontendRestore
)

$ErrorActionPreference = "Stop"
$repository = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$artifacts = Join-Path $repository "artifacts"
$package = Join-Path $artifacts "windows-package"
$backend = Join-Path $package "backend"
$agent = Join-Path $package "agent"
$ai = Join-Path $package "ai"

$artifactsRoot = [System.IO.Path]::GetFullPath($artifacts).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
foreach ($stagingDirectory in @($backend, $agent, $ai)) {
    $resolvedStagingDirectory = [System.IO.Path]::GetFullPath($stagingDirectory)
    if (-not $resolvedStagingDirectory.StartsWith($artifactsRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a staging directory outside the repository artifacts directory: $resolvedStagingDirectory"
    }
    if (Test-Path -LiteralPath $resolvedStagingDirectory) {
        Remove-Item -LiteralPath $resolvedStagingDirectory -Recurse -Force
    }
}
New-Item -ItemType Directory -Force -Path $backend, $agent, $ai, (Join-Path $artifacts "installer") | Out-Null
Push-Location (Join-Path $repository "frontend")
try {
    if (-not $SkipFrontendRestore) { & npm.cmd ci; if ($LASTEXITCODE -ne 0) { throw "Frontend restore failed." } }
    & npm.cmd run build
    if ($LASTEXITCODE -ne 0) { throw "Frontend build failed." }
} finally { Pop-Location }
& dotnet publish (Join-Path $repository "backend\AIDIP.Backend.csproj") -c $Configuration -r $Runtime --self-contained true -p:SkipFrontendBuild=true -o $backend
if ($LASTEXITCODE -ne 0) { throw "Backend publish failed." }
& dotnet publish (Join-Path $repository "agent\KAIRON.Agent.csproj") -c $Configuration -r $Runtime --self-contained true -o $agent
if ($LASTEXITCODE -ne 0) { throw "Agent publish failed." }

$python = Join-Path $repository "ai-service\.venv\Scripts\python.exe"
if (-not (Test-Path -LiteralPath $python)) { throw "Create ai-service/.venv and install requirements-build.txt first." }
& $python -m PyInstaller --noconfirm --clean --onefile --name KAIRON.AI --distpath $ai --workpath (Join-Path $artifacts "pyinstaller-work") --specpath (Join-Path $artifacts "pyinstaller-spec") --paths (Join-Path $repository "ai-service") --collect-all fastapi --collect-all uvicorn (Join-Path $repository "ai-service\launcher.py")
if ($LASTEXITCODE -ne 0) { throw "AI gateway packaging failed." }

if ([string]::IsNullOrWhiteSpace($InnoCompiler)) {
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    )
    $InnoCompiler = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler)) { throw "Inno Setup 6 compiler was not found." }
& $InnoCompiler "/DPackageRoot=$package" (Join-Path $PSScriptRoot "KAIRON.iss")
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed." }
