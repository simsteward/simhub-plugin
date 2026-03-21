# Poll Loki using URL/auth from .env and append result to debug log (NDJSON) for validation.
# Run from repo root: .\scripts\validate-grafana-logs.ps1 [path\to\debug-2291d4.log]
# .env keys used: LOKI_QUERY_URL or SIMSTEWARD_LOKI_URL, optional SIMSTEWARD_LOKI_USER + SIMSTEWARD_LOKI_TOKEN (Basic auth).
# Optional: GRAFANA_URL (default http://localhost:3000), GRAFANA_API_TOKEN or GRAFANA_ADMIN_USER + GRAFANA_ADMIN_PASSWORD for Grafana API.

param(
    [string]$DebugLogPath = "debug-2291d4.log",
    [string]$Query = '{app="sim-steward"}',
    [int]$LookbackSeconds = 7200
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot | Split-Path -Parent
$envFile = Join-Path $repoRoot ".env"
$debugLog = if ([System.IO.Path]::IsPathRooted($DebugLogPath)) { $DebugLogPath } else { Join-Path $repoRoot $DebugLogPath }

# Load .env
if (Test-Path $envFile) {
    Get-Content $envFile | ForEach-Object {
        if ($_ -match '^\s*([^#][^=]*)=(.*)$') {
            $name = $Matches[1].Trim()
            $value = $Matches[2].Trim().Trim('"')
            [System.Environment]::SetEnvironmentVariable($name, $value, "Process")
        }
    }
}

$lokiUrl = [System.Environment]::GetEnvironmentVariable("LOKI_QUERY_URL", "Process")
if (-not $lokiUrl) { $lokiUrl = [System.Environment]::GetEnvironmentVariable("SIMSTEWARD_LOKI_URL", "Process") }
if (-not $lokiUrl) { $lokiUrl = "http://localhost:3100" }
$lokiUrl = $lokiUrl.Trim().TrimEnd('/')

$lokiUser = [System.Environment]::GetEnvironmentVariable("SIMSTEWARD_LOKI_USER", "Process")?.Trim()
$lokiToken = [System.Environment]::GetEnvironmentVariable("SIMSTEWARD_LOKI_TOKEN", "Process")?.Trim()

$endSec = [long]([DateTimeOffset]::UtcNow.ToUnixTimeSeconds())
$startSec = $endSec - $LookbackSeconds
$url = "$lokiUrl/loki/api/v1/query_range?query=" + [Uri]::EscapeDataString($Query) + "&limit=500&start=$startSec&end=$endSec"

$payload = @{
    sessionId   = "2291d4"
    timestamp   = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    location    = "validate-grafana-logs.ps1"
    message     = "grafana_loki_poll"
    hypothesisId = "poll"
    data        = @{}
}

try {
    $headers = @{}
    if ($lokiUser -and $lokiToken) {
        $pair = "${lokiUser}:${lokiToken}"
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($pair)
        $headers["Authorization"] = "Basic " + [Convert]::ToBase64String($bytes)
    }
    $response = Invoke-RestMethod -Uri $url -Method Get -Headers $headers -ErrorAction Stop
    $streamCount = 0
    $totalLines = 0
    $sampleLines = [System.Collections.ArrayList]::new()
    if ($response.data -and $response.data.result) {
        $streamCount = $response.data.result.Count
        foreach ($res in $response.data.result) {
            if ($res.values) {
                $totalLines += $res.values.Count
                foreach ($v in $res.values) {
                    if ($sampleLines.Count -lt 3) {
                        $sampleLines.Add($v[1]) | Out-Null
                    }
                }
            }
        }
    }
    $payload.data = @{
        lokiUrl     = $lokiUrl
        status      = "ok"
        streamCount = $streamCount
        totalLines  = $totalLines
        sampleLines = @($sampleLines)
    }
} catch {
    $payload.data = @{
        lokiUrl = $lokiUrl
        status  = "error"
        error   = $_.Exception.Message
    }
}

$line = $payload | ConvertTo-Json -Compress -Depth 5
Add-Content -Path $debugLog -Value $line -Encoding UTF8
$d = $payload.data
$msg = "Poll result written to $debugLog : status=$($d.status)"
if ($d.streamCount -ne $null) { $msg += ", streams=$($d.streamCount)" }
if ($d.totalLines -ne $null) { $msg += ", lines=$($d.totalLines)" }
if ($d.error) { $msg += ", error=$($d.error)" }
Write-Host $msg
if ($d.status -eq "ok" -and $d.totalLines -eq 0 -and $lokiUrl -match "localhost") {
    Write-Host ""
    Write-Host "No plugin logs in Loki. Confirm plugin-structured.jsonl is ingested to Loki (your shipper or Grafana Cloud). SimHub data dir e.g. $env:LOCALAPPDATA\SimHubWpf\PluginsData\SimSteward — see docs/observability-local.md." -ForegroundColor Yellow
}
