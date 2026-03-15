# Seed plugin-structured.jsonl (so Alloy can tail it) then poll Loki and append result to debug log.
# Run from repo root. Requires: Docker stack up from observability/local with default mount
# (SIMSTEWARD_DATA_PATH unset so Alloy tails ./sample-logs = observability/local/sample-logs).
# Usage: .\scripts\seed-and-validate-loki.ps1 [path\to\debug-2291d4.log]

param(
    [string]$DebugLogPath = "debug-2291d4.log",
    [int]$WaitSeconds = 20
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot | Split-Path -Parent
$sampleLogsDir = Join-Path $repoRoot "observability\local\sample-logs"
$jsonlPath = Join-Path $sampleLogsDir "plugin-structured.jsonl"

if (-not (Test-Path $sampleLogsDir)) { New-Item -ItemType Directory -Force -Path $sampleLogsDir | Out-Null }
$ts = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
$line = "{`"level`":`"INFO`",`"message`":`"Seed line at $ts — pipeline check`",`"timestamp`":`"$ts`",`"component`":`"simhub-plugin`",`"event`":`"pipeline_test`",`"domain`":`"lifecycle`"}"
Add-Content -Path $jsonlPath -Value $line -Encoding UTF8
Write-Host "Appended 1 line to $jsonlPath"

Write-Host "Waiting ${WaitSeconds}s for Alloy to tail and push..."
Start-Sleep -Seconds $WaitSeconds

$env:LOKI_QUERY_URL = "http://localhost:3100"
& (Join-Path $repoRoot "scripts\validate-grafana-logs.ps1") -DebugLogPath (Join-Path $repoRoot $DebugLogPath)
