<#
.SYNOPSIS
  Stops and removes the Deco Plotter Windows service and its installed files.

.PARAMETER PurgeData
  Also delete %ProgramData%\DecoPlotter (settings with the router password, and the SQLite history).
  Without it the history is kept so a later reinstall continues where it left off.
#>
[CmdletBinding()]
param(
    [string]$ServiceName = 'DecoPlotter',
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'DecoPlotter'),
    [string]$DataDir = (Join-Path $env:ProgramData 'DecoPlotter'),
    [switch]$PurgeData
)

$ErrorActionPreference = 'Stop'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated (Administrator) PowerShell.'
}

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Stopped') {
        Write-Host "Stopping '$ServiceName' ..."
        Stop-Service $ServiceName -Force
        $svc.WaitForStatus('Stopped', '00:00:30')
    }
    & sc.exe delete $ServiceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'sc.exe delete failed.' }
    Write-Host "Service '$ServiceName' removed."
}
else {
    Write-Host "Service '$ServiceName' is not installed."
}

Get-NetFirewallRule -DisplayName 'Deco Plotter (*)' -ErrorAction SilentlyContinue | Remove-NetFirewallRule

if (Test-Path $InstallDir) {
    Remove-Item $InstallDir -Recurse -Force
    Write-Host "Deleted $InstallDir"
}

if ($PurgeData) {
    if (Test-Path $DataDir) { Remove-Item $DataDir -Recurse -Force; Write-Host "Deleted $DataDir" }
}
elseif (Test-Path $DataDir) {
    Write-Host "Kept settings and history in $DataDir (use -PurgeData to delete)."
}
