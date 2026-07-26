<#
.SYNOPSIS
  Install fps-exporter as a Windows Service (LocalSystem) via NSSM, so PresentMon's
  admin/ETW requirement is satisfied once at install time instead of on every run.

.EXAMPLE
  # From an elevated PowerShell:
  .\scripts\fps-exporter\install-service.ps1
#>
param(
    [string]$ServiceName = "FpsExporter"
)

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object System.Security.Principal.WindowsPrincipal($identity)).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Error "This script installs a Windows Service and must run from an elevated (Administrator) PowerShell."
    exit 1
}

$nodePath = (Get-Command node -ErrorAction SilentlyContinue).Source
if (-not $nodePath) {
    Write-Error "node.exe not found on PATH. Install Node.js first."
    exit 1
}

function Find-Nssm {
    Get-ChildItem -Path "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Filter "nssm.exe" -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match 'win64' } | Select-Object -First 1 -ExpandProperty FullName
}

$nssmPath = Find-Nssm
if (-not $nssmPath) {
    Write-Output "NSSM not found, installing via winget..."
    winget install --id NSSM.NSSM -e --accept-package-agreements --accept-source-agreements
    $nssmPath = Find-Nssm
}

if (-not $nssmPath) {
    Write-Error "NSSM install via winget did not produce nssm.exe under $env:LOCALAPPDATA\Microsoft\WinGet\Packages. Install NSSM manually and re-run."
    exit 1
}

Write-Output "Using NSSM at: $nssmPath"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$entryScript = Join-Path $scriptDir "fps-exporter.js"

if (-not (Test-Path $entryScript)) {
    Write-Error "fps-exporter.js not found at $entryScript"
    exit 1
}

New-Item -ItemType Directory -Force -Path "$env:LOCALAPPDATA\FpsExporter" | Out-Null

& $nssmPath install $ServiceName $nodePath $entryScript
& $nssmPath set $ServiceName AppDirectory $scriptDir
& $nssmPath set $ServiceName Start SERVICE_AUTO_START
& $nssmPath set $ServiceName AppStdout "$env:LOCALAPPDATA\FpsExporter\service-stdout.log"
& $nssmPath set $ServiceName AppStderr "$env:LOCALAPPDATA\FpsExporter\service-stderr.log"
& $nssmPath set $ServiceName AppRotateFiles 1
& $nssmPath set $ServiceName AppRotateBytes 10485760

& $nssmPath start $ServiceName

Write-Output "Service '$ServiceName' installed and started."
Write-Output "Check status: nssm status $ServiceName  (or Get-Service $ServiceName)"
Write-Output "Verify metrics: curl http://127.0.0.1:9101/metrics"
