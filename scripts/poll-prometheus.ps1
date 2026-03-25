# Smoke-query local Prometheus (same TSDB as Grafana datasource prometheus_local).
# Usage (repo root): .\scripts\poll-prometheus.ps1 [-Query "up"]
param(
    [string]$BaseUrl = "http://127.0.0.1:9090",
    [string]$Query = "up"
)

$ErrorActionPreference = "Stop"
$enc = [System.Uri]::EscapeDataString($Query)
$uri = "$BaseUrl/api/v1/query?query=$enc"
Write-Host "GET $uri"
try {
    $r = Invoke-RestMethod -Uri $uri -Method Get
    $r | ConvertTo-Json -Depth 6
    Write-Host "PASS: Prometheus query returned."
} catch {
    Write-Host "FAIL: $_"
    exit 1
}
