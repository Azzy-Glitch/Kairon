<#
.SYNOPSIS
    Removes the disposable "ScmTestDependency" service and its files created by
    New-KaironScmTestService.ps1. Deleting the service also deletes its DACL, so the narrow
    Query/Start/Stop grant disappears with it. Also removes a "KaironScmTest" service/folder left by
    an earlier version of the setup script. Nothing else on the machine is touched.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated (Administrator) PowerShell.'
}

foreach ($serviceName in @('ScmTestDependency', 'KaironScmTest')) {
    $installDir = Join-Path $env:ProgramData $serviceName
    if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        & sc.exe delete $serviceName | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "sc delete $serviceName failed ($LASTEXITCODE)." }
        Write-Output "Service '$serviceName' deleted."
    }
    if (Test-Path $installDir) {
        Start-Sleep -Seconds 1
        Remove-Item -Path $installDir -Recurse -Force
        Write-Output "Removed $installDir."
    }
}
