<#
.SYNOPSIS
    Grants or revokes the minimum Windows SCM rights KAIRON needs to remediate ONE named service.

.DESCRIPTION
    KAIRON's backend runs sc.exe under its own Windows identity and never holds WRITE_DAC: it can
    never grant itself anything. An administrator runs this script, explicitly and elevated, to add
    exactly one allow ACE to exactly one service's DACL for exactly one identity:

        Query -> SERVICE_QUERY_STATUS (0x0004)   needed by every operation
        Start -> SERVICE_START        (0x0010)   StartService, RestartService
        Stop  -> SERVICE_STOP         (0x0020)   StopService, RestartService

    It never grants SERVICE_ALL_ACCESS, SERVICE_CHANGE_CONFIG, WRITE_DAC, WRITE_OWNER or DELETE,
    never touches any other service, and never edits an ACE it did not create: if the identity
    already has an ACE carrying any other right, the script stops without changing anything.
    -Remove deletes only that identity's ACE, and only when it holds nothing but these rights.
    Only the DACL is written (DACL_SECURITY_INFORMATION); owner, group and SACL are untouched.

.EXAMPLE
    # Allow KAIRON to query/start/stop OrdersService (copy the SID from the KAIRON pre-flight):
    .\Set-KaironServicePermission.ps1 -ServiceName OrdersService -Sid S-1-5-21-...-1001 -Rights Query,Start,Stop

.EXAMPLE
    # Revoke it again:
    .\Set-KaironServicePermission.ps1 -ServiceName OrdersService -Sid S-1-5-21-...-1001 -Remove
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Za-z0-9_.-]{1,256}$')][string]$ServiceName,
    [Parameter(Mandatory = $true)][ValidatePattern('^S-1-5-(21|80)(-\d+)+$')][string]$Sid,
    [ValidateSet('Query', 'Start', 'Stop')][string[]]$Rights = @('Query'),
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated (Administrator) PowerShell. It changes one service''s permissions.'
}
if ($ServiceName -like 'Kairon*') {
    throw "Refusing: '$ServiceName' is a KAIRON service. KAIRON never remediates its own infrastructure."
}

Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class KaironServiceDacl {
    const uint SC_MANAGER_CONNECT = 0x1, READ_CONTROL = 0x20000, WRITE_DAC = 0x40000, DACL_SECURITY_INFORMATION = 0x4;
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr OpenSCManager(string m, string d, uint a);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr OpenService(IntPtr m, string n, uint a);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool CloseServiceHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool QueryServiceObjectSecurity(IntPtr s, uint info, byte[] sd, uint size, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool SetServiceObjectSecurity(IntPtr s, uint info, byte[] sd);
    static IntPtr Open(string name, uint access, out IntPtr scm) {
        scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        IntPtr svc = OpenService(scm, name, access);
        if (svc == IntPtr.Zero) { int e = Marshal.GetLastWin32Error(); CloseServiceHandle(scm); throw new Win32Exception(e); }
        return svc;
    }
    public static byte[] Read(string name) {
        IntPtr scm; IntPtr svc = Open(name, READ_CONTROL, out scm);
        try {
            uint needed; QueryServiceObjectSecurity(svc, DACL_SECURITY_INFORMATION, new byte[0], 0, out needed);
            byte[] sd = new byte[needed];
            if (!QueryServiceObjectSecurity(svc, DACL_SECURITY_INFORMATION, sd, needed, out needed)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return sd;
        } finally { CloseServiceHandle(svc); CloseServiceHandle(scm); }
    }
    public static void Write(string name, byte[] sd) {
        IntPtr scm; IntPtr svc = Open(name, READ_CONTROL | WRITE_DAC, out scm);
        try { if (!SetServiceObjectSecurity(svc, DACL_SECURITY_INFORMATION, sd)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        finally { CloseServiceHandle(svc); CloseServiceHandle(scm); }
    }
}
'@

$rightMasks = @{ Query = 0x0004; Start = 0x0010; Stop = 0x0020 }
$kaironMask = 0x0004 -bor 0x0010 -bor 0x0020
$mask = 0
foreach ($r in ($Rights + 'Query' | Select-Object -Unique)) { $mask = $mask -bor $rightMasks[$r] }

$identity = New-Object Security.Principal.SecurityIdentifier($Sid)
$bytes = [KaironServiceDacl]::Read($ServiceName)
$sd = New-Object Security.AccessControl.RawSecurityDescriptor($bytes, 0)
$before = $sd.GetSddlForm([Security.AccessControl.AccessControlSections]::Access)

$existing = @()
for ($i = 0; $i -lt $sd.DiscretionaryAcl.Count; $i++) {
    $ace = $sd.DiscretionaryAcl[$i]
    if ($ace -is [Security.AccessControl.CommonAce] -and $ace.SecurityIdentifier -eq $identity) { $existing += [pscustomobject]@{ Index = $i; Ace = $ace } }
}
foreach ($e in $existing) {
    if ($e.Ace.AceQualifier -ne [Security.AccessControl.AceQualifier]::AccessAllowed -or ($e.Ace.AccessMask -band (-bnot $kaironMask)) -ne 0) {
        throw "Refusing: $Sid already has a non-KAIRON ACE on '$ServiceName' ($($e.Ace.AceQualifier), mask 0x$('{0:X}' -f $e.Ace.AccessMask)). Nothing was changed."
    }
}

# Remove this identity's KAIRON-only ACE(s) (highest index first), then re-add the exact grant.
foreach ($e in ($existing | Sort-Object Index -Descending)) { $sd.DiscretionaryAcl.RemoveAce($e.Index) }
if (-not $Remove) {
    $ace = New-Object Security.AccessControl.CommonAce([Security.AccessControl.AceFlags]::None,
        [Security.AccessControl.AceQualifier]::AccessAllowed, $mask, $identity, $false, $null)
    $sd.DiscretionaryAcl.InsertAce($sd.DiscretionaryAcl.Count, $ace)
}
$after = $sd.GetSddlForm([Security.AccessControl.AccessControlSections]::Access)

if ($before -eq $after) { Write-Output "No change needed for '$ServiceName'."; return }
$action = if ($Remove) { "Revoke KAIRON rights for $Sid" } else { "Grant $(($Rights + 'Query' | Select-Object -Unique) -join '/') to $Sid" }
if ($PSCmdlet.ShouldProcess($ServiceName, $action)) {
    $out = New-Object byte[] $sd.BinaryLength
    $sd.GetBinaryForm($out, 0)
    [KaironServiceDacl]::Write($ServiceName, $out)
    Write-Output "$action on '$ServiceName'."
    Write-Output "DACL before: $before"
    Write-Output "DACL after:  $after"
}
