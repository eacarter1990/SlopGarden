<#
.SYNOPSIS
    Installs the SlopGarden client as a Windows service.

.EXAMPLE
    # From an elevated PowerShell, in the win-x64 publish output (or pass -Source):
    .\install.ps1 -Server http://192.168.1.20:5080
#>
[CmdletBinding()]
param(
    # Publish output containing slopgarden-client.exe. Defaults to this script's folder, else artifacts\client\win-x64.
    [string]$Source,
    # Server URL written to client.json; omit to use LAN auto-discovery.
    [string]$Server = "",
    # Device name shown in the server; defaults to the computer name.
    [string]$Name = "",
    [string]$InstallDir = "$env:ProgramFiles\SlopGarden\Client",
    [switch]$NoStart
)

$ErrorActionPreference = "Stop"
$serviceName = "SlopGardenClient"
$displayName = "SlopGarden Client"
$configDir = Join-Path $env:ProgramData "SlopGarden"
$dataDir = Join-Path $configDir "client"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated (Administrator) PowerShell."
}

if (-not $Source) {
    $Source = if (Test-Path (Join-Path $PSScriptRoot "slopgarden-client.exe")) { $PSScriptRoot }
              else { Join-Path (Resolve-Path (Join-Path $PSScriptRoot "..\..")) "artifacts\client\win-x64" }
}
$exe = Join-Path $Source "slopgarden-client.exe"
if (-not (Test-Path $exe)) { throw "slopgarden-client.exe not found in $Source (run scripts/publish-client.sh or pass -Source)." }

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne "Stopped") {
    Write-Host "Stopping $displayName"
    Stop-Service -Name $serviceName -Force
    $existing.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
}

New-Item -ItemType Directory -Force -Path $InstallDir, $configDir, $dataDir, (Join-Path $InstallDir "plugins") | Out-Null
# Replace the binaries but keep operator-installed plugins.
Get-ChildItem -Path $InstallDir -Exclude "plugins" | Remove-Item -Recurse -Force
Get-ChildItem -Path $Source -Exclude "plugins" | Copy-Item -Destination $InstallDir -Recurse -Force

$configFile = Join-Path $configDir "client.json"
if (-not (Test-Path $configFile)) {
    @{ Client = @{ ServerUrl = $Server; Name = $Name } } | ConvertTo-Json -Depth 3 | Set-Content -Path $configFile -Encoding UTF8
    Write-Host "Wrote $configFile"
} elseif ($Server -or $Name) {
    Write-Warning "$configFile exists and was not changed; edit it to set ServerUrl/Name."
}

$binary = "`"$(Join-Path $InstallDir 'slopgarden-client.exe')`" run"
if (-not $existing) {
    New-Service -Name $serviceName -DisplayName $displayName -BinaryPathName $binary -StartupType Automatic `
        -Description "SlopGarden garden automation client: sensors, actuators and cameras." | Out-Null
} else {
    & sc.exe config $serviceName binPath= $binary start= auto | Out-Null
}
# Restart after 5 s on every failure (reset the failure counter daily).
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
& sc.exe failureflag $serviceName 1 | Out-Null

if (-not $NoStart) {
    Start-Service -Name $serviceName
    Write-Host "$displayName started. Status: & '$InstallDir\slopgarden-client.exe' status   Logs: Event Viewer > Application"
} else {
    Write-Host "$displayName installed (not started)."
}
