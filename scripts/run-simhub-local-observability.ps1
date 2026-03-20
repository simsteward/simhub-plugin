# Start SimHub with env vars set for local Loki (plugin pushes to local Grafana stack).
# Run from plugin repo root:  .\scripts\run-simhub-local-observability.ps1
# See docs/observability-local.md and docs/GRAFANA-LOGGING.md.

$ErrorActionPreference = "Stop"
$ScriptDir = $PSScriptRoot
$PluginRoot = (Resolve-Path (Join-Path $ScriptDir "..")).Path

# Load .env from repo root if present (KEY=VALUE; skip comments and empty lines)
$envFile = Join-Path $PluginRoot ".env"
if (Test-Path $envFile) {
    Get-Content $envFile -Raw | ForEach-Object {
        $_ -split "`n" | ForEach-Object {
            $line = $_.Trim()
            if ($line -and -not $line.StartsWith("#")) {
                $idx = $line.IndexOf("=")
                if ($idx -gt 0) {
                    $key = $line.Substring(0, $idx).Trim()
                    $val = $line.Substring($idx + 1).Trim()
                    if ($val.Length -ge 2 -and $val.StartsWith('"') -and $val.EndsWith('"')) { $val = $val.Substring(1, $val.Length - 2) }
                    if ($key -and $key -notmatch "^\s*#") { Set-Item -Path "Env:$key" -Value $val }
                }
            }
        }
    }
    Write-Host "Loaded env from: $envFile"
}

# Debug log for agent sessions (writes to workspace so we can read after run)
$env:SIMSTEWARD_DEBUG_LOG_PATH = Join-Path $PluginRoot "debug-e2bb5f.log"

# Force local Loki so plugin pushes to local Docker stack
$env:SIMSTEWARD_LOKI_URL = "http://localhost:3100"
$env:SIMSTEWARD_LOKI_USER = ""
$env:SIMSTEWARD_LOKI_TOKEN = ""
$env:SIMSTEWARD_LOG_ENV = "local"
if (-not $env:SIMSTEWARD_LOG_DEBUG) { $env:SIMSTEWARD_LOG_DEBUG = "1" }

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

Write-Host "Starting SimHub with local Loki (SIMSTEWARD_LOKI_URL=$env:SIMSTEWARD_LOKI_URL, SIMSTEWARD_LOG_ENV=$env:SIMSTEWARD_LOG_ENV)"
Write-Host "SimHub: $SimHubExe"
& $SimHubExe
