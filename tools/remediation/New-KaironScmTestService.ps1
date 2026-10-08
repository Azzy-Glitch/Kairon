<#
.SYNOPSIS
    Creates the disposable "ScmTestDependency" Windows service used to validate real KAIRON
    remediation (and the opt-in real-SCM integration test), and grants ONE account the minimum
    rights to query/start/stop ONLY that service.

.DESCRIPTION
    Run from an elevated PowerShell. Steps (each reversed by Remove-KaironScmTestService.ps1):
      1. Copies the published ScmTestService into C:\ProgramData\ScmTestDependency with inheritance
         removed: Administrators/SYSTEM full control, Users read & execute only (no file planting).
      2. Registers the service as NT AUTHORITY\LocalService, manual start, loopback port 18080.
      3. Grants -Sid only Query/Start/Stop on this one service via Set-KaironServicePermission.ps1.
      4. Starts it.
    The service name deliberately does NOT start with "Kairon": KAIRON refuses to remediate its own
    infrastructure, and this stands in for an ordinary application dependency. If any step fails,
    everything already done is rolled back. Nothing else on the machine is changed.

.EXAMPLE
    dotnet publish tools\remediation\ScmTestService -c Release -o $env:TEMP\kxscm
    .\New-KaironScmTestService.ps1 -SourceDir $env:TEMP\kxscm -Sid S-1-5-21-...-1001
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SourceDir,
    [Parameter(Mandatory = $true)][ValidatePattern('^S-1-5-(21|80)(-\d+)+$')][string]$Sid,
    [ValidateSet('Query', 'Start', 'Stop')][string[]]$Rights = @('Query', 'Start', 'Stop')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated (Administrator) PowerShell.'
}

$serviceName = 'ScmTestDependency'
$installDir = Join-Path $env:ProgramData 'ScmTestDependency'
$exe = Join-Path $installDir 'Kairon.ScmTestService.exe'
if (-not (Test-Path (Join-Path $SourceDir 'Kairon.ScmTestService.exe'))) {
    throw "Kairon.ScmTestService.exe not found in $SourceDir. Publish tools\remediation\ScmTestService first."
}
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    throw "Service '$serviceName' already exists. Run Remove-KaironScmTestService.ps1 first."
}

$createdDir = $false
$createdService = $false
try {
    if (-not (Test-Path $installDir)) { $createdDir = $true }
    New-Item -ItemType Directory -Force -Path $installDir | Out-Null
    Copy-Item -Path (Join-Path $SourceDir '*') -Destination $installDir -Recurse -Force
    # Replace inherited ProgramData permissions (which let Users create files) with a closed ACL.
    & icacls.exe $installDir /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls failed ($LASTEXITCODE)." }

    & sc.exe create $serviceName binPath= "`"$exe`"" start= demand obj= 'NT AUTHORITY\LocalService' DisplayName= 'KAIRON Remediation Test Dependency' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc create failed ($LASTEXITCODE)." }
    $createdService = $true
    & sc.exe description $serviceName 'Disposable dependency used only to validate KAIRON Windows service remediation. Safe to remove.' | Out-Null

    & (Join-Path $PSScriptRoot 'Set-KaironServicePermission.ps1') -ServiceName $serviceName -Sid $Sid -Rights $Rights
    Start-Service -Name $serviceName
}
catch {
    Write-Warning "Setup failed; rolling back: $($_.Exception.Message)"
    if ($createdService) { & sc.exe stop $serviceName 2>$null | Out-Null; & sc.exe delete $serviceName | Out-Null }
    if ($createdDir -and (Test-Path $installDir)) { Start-Sleep -Seconds 1; Remove-Item -Path $installDir -Recurse -Force -ErrorAction SilentlyContinue }
    throw
}

Get-Service -Name $serviceName | Format-Table -AutoSize Name, Status, StartType
Write-Output "$serviceName is running on http://127.0.0.1:18080 (POST /wedge to degrade it; a restart clears it)."
