<#
.SYNOPSIS
  Publishes Deco Plotter and installs it as a Windows service (auto-start, restarts on failure).

.DESCRIPTION
  Must be run from an elevated PowerShell. Re-running upgrades the binaries in place and keeps existing
  settings and data. Settings (router address, credentials, port) live in
  %ProgramData%\DecoPlotter\settings.json, readable only by Administrators, SYSTEM and the service account.

.PARAMETER Reconfigure
  Prompt for the router settings again even if a settings file already exists.

.PARAMETER Port
  HTTP port the service listens on (default 5180).

.PARAMETER OpenFirewall
  Also add an inbound Windows Firewall rule for the port (otherwise only this PC can reach the dashboard).
#>
[CmdletBinding()]
param(
    [string]$ServiceName = 'DecoPlotter',
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'DecoPlotter'),
    [string]$DataDir = (Join-Path $env:ProgramData 'DecoPlotter'),
    [int]$Port = 5180,
    [switch]$Reconfigure,
    [switch]$OpenFirewall
)

$ErrorActionPreference = 'Stop'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated (Administrator) PowerShell.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The dotnet SDK was not found on PATH.' }

$project = Join-Path $PSScriptRoot '..\src\DecoPlotter\DecoPlotter.csproj'
if (-not (Test-Path $project)) { throw "Project not found: $project" }
$serviceAccount = 'NT AUTHORITY\LocalService'
$settingsFile = Join-Path $DataDir 'settings.json'
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

# Publish to a staging folder first so a failed build never damages a working install.
$staging = Join-Path ([IO.Path]::GetTempPath()) "decoplotter-publish-$([guid]::NewGuid().ToString('N'))"
Write-Host "Publishing to $staging ..."
dotnet publish $project -c Release -o $staging --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

if ($existing) {
    Write-Host "Stopping existing service '$ServiceName' ..."
    if ($existing.Status -ne 'Stopped') { Stop-Service $ServiceName -Force; $existing.WaitForStatus('Stopped', '00:00:30') }
}

Write-Host "Copying files to $InstallDir ..."
New-Item -ItemType Directory -Force $InstallDir | Out-Null
Get-ChildItem $InstallDir -Force | Remove-Item -Recurse -Force
Copy-Item (Join-Path $staging '*') $InstallDir -Recurse -Force
Remove-Item $staging -Recurse -Force
Get-ChildItem (Join-Path $InstallDir 'appsettings.Development.json') -ErrorAction SilentlyContinue | Remove-Item -Force

New-Item -ItemType Directory -Force $DataDir, (Join-Path $DataDir 'data') | Out-Null

if ($Reconfigure -or -not (Test-Path $settingsFile)) {
    $routerHost = Read-Host 'Deco IP address or host [192.168.68.1]'
    if (-not $routerHost) { $routerHost = '192.168.68.1' }
    $user = Read-Host 'Deco username [admin]'
    if (-not $user) { $user = 'admin' }
    $secure = Read-Host 'Deco password (owner / TP-Link ID password)' -AsSecureString
    $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
    if (-not $password) { throw 'A password is required.' }

    $settings = [ordered]@{
        Urls = "http://0.0.0.0:$Port"
        Deco = [ordered]@{
            Host         = $routerHost
            Username     = $user
            Password     = $password
            DatabasePath = (Join-Path $DataDir 'data\deco.db')
        }
    }
    $settings | ConvertTo-Json -Depth 4 | Set-Content -Path $settingsFile -Encoding UTF8
    Write-Host "Wrote $settingsFile"
}
else {
    Write-Host "Keeping existing settings in $settingsFile (use -Reconfigure to change)."
}

# Lock down the settings file (contains the password); the service account needs read access only.
& icacls $settingsFile /inheritance:r /grant:r 'SYSTEM:(F)' 'BUILTIN\Administrators:(F)' "${serviceAccount}:(R)" | Out-Null
# The service account writes the database under the data folder.
& icacls (Join-Path $DataDir 'data') /grant "${serviceAccount}:(OI)(CI)M" | Out-Null

$binPath = "`"$(Join-Path $InstallDir 'DecoPlotter.exe')`""
if ($existing) {
    & sc.exe config $ServiceName binPath= $binPath obj= $serviceAccount start= delayed-auto | Out-Null
}
else {
    Write-Host "Creating service '$ServiceName' ..."
    & sc.exe create $ServiceName binPath= $binPath obj= $serviceAccount start= delayed-auto DisplayName= 'Deco Plotter' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'sc.exe create failed.' }
}
& sc.exe description $ServiceName 'Collects bandwidth and device usage from a TP-Link Deco mesh and serves a dashboard and API.' | Out-Null
# Restart after 5s, 30s, then 60s; reset the failure count after a day.
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/30000/restart/60000 | Out-Null

if ($OpenFirewall) {
    $rule = "Deco Plotter ($Port)"
    Get-NetFirewallRule -DisplayName $rule -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName $rule -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow -Profile Private | Out-Null
    Write-Host "Opened TCP $Port on the Private firewall profile."
}

Write-Host 'Starting service ...'
Start-Service $ServiceName

# Read the port from the settings file in case it was configured earlier with a different value.
$listenPort = $Port
try { $listenPort = ([uri](Get-Content $settingsFile -Raw | ConvertFrom-Json).Urls.Split(';')[0].Replace('0.0.0.0', 'localhost')).Port } catch {}

$ok = $false
foreach ($i in 1..15) {
    Start-Sleep -Seconds 2
    try {
        $s = Invoke-RestMethod "http://localhost:$listenPort/api/status" -TimeoutSec 5
        $ok = $true
        break
    } catch {}
}
if ($ok) {
    Write-Host "Service is running: http://localhost:$listenPort/" -ForegroundColor Green
    if ($s.lastError) { Write-Warning "Last poll error: $($s.lastError)" }
}
else {
    Write-Warning "Service started but did not answer on port $listenPort. Check Event Viewer > Windows Logs > Application (source '$ServiceName')."
}
