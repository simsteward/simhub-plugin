# Start SimHub with env vars set for local Loki (plugin pushes to local Grafana stack).
# Run from plugin repo root:  .\scripts\run-simhub-local-observability.ps1
# See docs/observability-local.md and docs/GRAFANA-LOGGING.md.

$ErrorActionPreference = "Stop"
$ScriptDir = $PSScriptRoot
$PluginRoot = (Resolve-Path (Join-Path $ScriptDir "..")).Path

# Load .env (+ optional Docker stack token file) — same as deploy.ps1
$loadDotenv = Join-Path $ScriptDir "load-dotenv.ps1"
if (Test-Path $loadDotenv) {
    . $loadDotenv
    Import-DotEnv @(
        (Join-Path $PluginRoot ".env"),
        (Join-Path $PluginRoot "observability\local\.env.observability.local")
    )
    Write-Host "Loaded env from .env / .env.observability.local (if present)"
}

# Debug log for agent sessions (writes to workspace so we can read after run)
$env:SIMSTEWARD_DEBUG_LOG_PATH = Join-Path $PluginRoot "debug-e2bb5f.log"

# Defaults for local stack only when not set in .env
if ([string]::IsNullOrWhiteSpace($env:SIMSTEWARD_LOKI_URL)) { $env:SIMSTEWARD_LOKI_URL = "http://localhost:3100" }
if ([string]::IsNullOrWhiteSpace($env:SIMSTEWARD_LOG_ENV)) { $env:SIMSTEWARD_LOG_ENV = "local" }
if ([string]::IsNullOrWhiteSpace($env:SIMSTEWARD_LOG_DEBUG)) { $env:SIMSTEWARD_LOG_DEBUG = "1" }

# OTLP → OpenTelemetry Collector (see docs/observability-local.md). Override in .env if needed.
if (-not $env:OTEL_EXPORTER_OTLP_ENDPOINT) { $env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://127.0.0.1:4317" }

# Resolve SimHub path (same logic as deploy.ps1)
$SimHubPath = $null
if ($env:SIMHUB_PATH -and (Test-Path $env:SIMHUB_PATH)) {
    $SimHubPath = (Resolve-Path $env:SIMHUB_PATH).Path
}
if (-not $SimHubPath) {
    $regPath = "HKCU:\Software\SimHub"
    if (Test-Path $regPath) {
        $installDir = (Get-ItemProperty $regPath -ErrorAction SilentlyContinue).InstallDirectory
        if ($installDir -and (Test-Path $installDir)) { $SimHubPath = $installDir }
    }
}
if (-not $SimHubPath) {
    $proc = Get-Process -Name "SimHubWPF" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($proc) { try { $SimHubPath = Split-Path $proc.MainModule.FileName } catch {} }
}
if (-not $SimHubPath) { $SimHubPath = "C:\Program Files (x86)\SimHub" }

$SimHubExe = Join-Path $SimHubPath "SimHubWPF.exe"
if (-not (Test-Path $SimHubExe)) {
    Write-Error "SimHub not found at: $SimHubExe. Set SIMHUB_PATH to your SimHub folder."
}

Write-Host "Starting SimHub with local Loki + OTLP metrics (SIMSTEWARD_LOKI_URL=$env:SIMSTEWARD_LOKI_URL, OTEL_EXPORTER_OTLP_ENDPOINT=$env:OTEL_EXPORTER_OTLP_ENDPOINT, SIMSTEWARD_LOG_ENV=$env:SIMSTEWARD_LOG_ENV)"
Write-Host "SimHub: $SimHubExe"
& $SimHubExe
