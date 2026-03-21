# Wipe persisted data for the local observability stack (Loki chunks/WAL, optional Grafana, sample logs).
# Does NOT remove Loki config, datasource provisioning, gateway tokens in .env, or compose services.
# Usage (repo root): .\scripts\obs-wipe-local-data.ps1 -Force
# Optional: -Grafana -SampleLogs or -All (both optional dirs with -Force).
param(
    [switch]$Force,
    [switch]$All,
    [switch]$Grafana,
    [switch]$SampleLogs
)

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$composeFile = Join-Path $repoRoot "observability/local/docker-compose.yml"
$envObs = Join-Path $repoRoot "observability/local/.env.observability.local"

function Get-GrafanaStoragePath {
    $default = if ($env:OS -like "*Windows*") { "S:\sim-steward-grafana-storage" } else { "/tmp/sim-steward-grafana-storage" }
    if (-not (Test-Path $envObs)) { return $default }
    foreach ($line in Get-Content $envObs) {
        if ($line -match '^\s*GRAFANA_STORAGE_PATH\s*=\s*(.+)$') {
            $v = $Matches[1].Trim().Trim('"').Trim("'")
            if ($v) { return $v }
        }
    }
    $default
}

function Clear-DirectoryContents {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return }
    Get-ChildItem -LiteralPath $Path -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction Stop
}

if (-not $Force) {
    Write-Host "Refusing to wipe: pass -Force. See docs/observability-local.md (Housekeeping)."
    exit 1
}

if ($All) {
    $Grafana = $true
    $SampleLogs = $true
}

$base = Get-GrafanaStoragePath
if (-not [System.IO.Path]::IsPathRooted($base)) {
    $base = Join-Path $repoRoot $base
}
$base = [System.IO.Path]::GetFullPath($base)

Write-Host "Stopping compose (observability/local)..."
Push-Location $repoRoot
try {
    if (Test-Path $envObs) {
        docker compose --env-file $envObs -f $composeFile down
    }
    else {
        docker compose -f $composeFile down
    }
}
finally {
    Pop-Location
}

Write-Host "Wiping Loki data under: $(Join-Path $base 'loki')"
Clear-DirectoryContents (Join-Path $base "loki")

if ($Grafana) {
    Write-Host "Wiping Grafana lib under: $(Join-Path $base 'grafana')"
    Clear-DirectoryContents (Join-Path $base "grafana")
}

if ($SampleLogs) {
    $sample = Join-Path $repoRoot "observability/local/sample-logs"
    Write-Host "Clearing sample logs: $sample"
    Get-ChildItem -Path $sample -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction Stop
}

Write-Host "PASS: Local observability data wiped. Restart with npm run obs:up or obs:up:env."
if ($Grafana) {
    Write-Host "Note: Grafana volume cleared — re-run scripts/grafana-bootstrap.ps1 if you use GRAFANA_API_TOKEN."
}
