<#
.SYNOPSIS
    Removes every remediation grant made to the Kairon.Backend service account, on every service.

.DESCRIPTION
    Run elevated. The Kairon installer runs this during uninstall. KAIRON's backend runs as the
    virtual service account NT SERVICE\Kairon.Backend, whose SID is unique to KAIRON, so its grants
    can be told apart from anything else on the machine. For each Windows service this removes
    only allow ACEs for that SID whose rights are a subset of Query/Start/Stop (exactly what
    Set-KaironServicePermission.ps1 grants). Any ACE for that SID carrying other rights is left
    untouched and reported. Only the DACL is written. Services that cannot be opened (protected
    services, access denied) are skipped.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ServiceAccount = 'Kairon.Backend'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated (Administrator) PowerShell.'
}

Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class KaironGrantSweep {
    const uint SC_MANAGER_CONNECT = 0x1, READ_CONTROL = 0x20000, WRITE_DAC = 0x40000, DACL_SECURITY_INFORMATION = 0x4;
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr OpenSCManager(string m, string d, uint a);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr OpenService(IntPtr m, string n, uint a);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool CloseServiceHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool QueryServiceObjectSecurity(IntPtr s, uint info, byte[] sd, uint size, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetServiceObjectSecurity(IntPtr s, uint info, byte[] sd);
    public static byte[] Read(string name) {
        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return null;
        IntPtr svc = OpenService(scm, name, READ_CONTROL);
        try {
            if (svc == IntPtr.Zero) return null;
            uint needed; QueryServiceObjectSecurity(svc, DACL_SECURITY_INFORMATION, new byte[0], 0, out needed);
            if (needed == 0) return null;
            byte[] sd = new byte[needed];
            return QueryServiceObjectSecurity(svc, DACL_SECURITY_INFORMATION, sd, needed, out needed) ? sd : null;
        } finally { if (svc != IntPtr.Zero) CloseServiceHandle(svc); CloseServiceHandle(scm); }
    }
    public static void Write(string name, byte[] sd) {
        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr svc = OpenService(scm, name, READ_CONTROL | WRITE_DAC);
        try {
            if (svc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!SetServiceObjectSecurity(svc, DACL_SECURITY_INFORMATION, sd)) throw new Win32Exception(Marshal.GetLastWin32Error());
        } finally { if (svc != IntPtr.Zero) CloseServiceHandle(svc); CloseServiceHandle(scm); }
    }
}
'@

# The virtual account SID is derived from the service name, so it is known even after the
# service itself has been deleted.
$sidLine = (& "$env:SystemRoot\System32\sc.exe" showsid $ServiceAccount) | Where-Object { $_ -match 'SERVICE SID:\s*(S-1-5-80-[\d-]+)' } | Select-Object -First 1
if (-not ($sidLine -match '(S-1-5-80-[\d-]+)')) { throw "Could not determine the SID of NT SERVICE\$ServiceAccount." }
$identity = New-Object Security.Principal.SecurityIdentifier($Matches[1])
$kaironMask = 0x0004 -bor 0x0010 -bor 0x0020

$removed = 0
foreach ($service in Get-Service -ErrorAction SilentlyContinue) {
    $bytes = [KaironGrantSweep]::Read($service.Name)
    if ($null -eq $bytes) { continue }
    $sd = New-Object Security.AccessControl.RawSecurityDescriptor($bytes, 0)
    $changed = $false
    for ($i = $sd.DiscretionaryAcl.Count - 1; $i -ge 0; $i--) {
        $ace = $sd.DiscretionaryAcl[$i]
        if ($ace -isnot [Security.AccessControl.CommonAce] -or $ace.SecurityIdentifier -ne $identity) { continue }
        if ($ace.AceQualifier -eq [Security.AccessControl.AceQualifier]::AccessAllowed -and ($ace.AccessMask -band (-bnot $kaironMask)) -eq 0) {
            $sd.DiscretionaryAcl.RemoveAce($i)
            $changed = $true
        } else {
            Write-Warning "Left a non-KAIRON ACE for NT SERVICE\$ServiceAccount on '$($service.Name)' untouched."
        }
    }
    if ($changed -and $PSCmdlet.ShouldProcess($service.Name, "Remove KAIRON remediation grant")) {
        $out = New-Object byte[] $sd.BinaryLength
        $sd.GetBinaryForm($out, 0)
        try { [KaironGrantSweep]::Write($service.Name, $out); $removed++; Write-Output "Removed KAIRON remediation grant from '$($service.Name)'." }
        catch { Write-Warning "Could not update '$($service.Name)': $($_.Exception.Message)" }
    }
}
Write-Output "KAIRON remediation grants removed from $removed service(s)."
