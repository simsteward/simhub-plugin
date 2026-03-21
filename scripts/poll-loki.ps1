# Continuously poll Loki for SimSteward logs and print new lines (tail-style).
# Run from repo root: .\scripts\poll-loki.ps1 [-Query '{app="sim-steward"} | json | level != "DEBUG"']
# Auth: loads repo .env — LOKI_QUERY_URL or SIMSTEWARD_LOKI_URL; optional SIMSTEWARD_LOKI_USER + SIMSTEWARD_LOKI_TOKEN (Grafana Cloud).
# Ctrl+C to stop.

param(
    [string]$LokiUrl = "",
    [string]$Query = '{app="sim-steward"} | json | level != "DEBUG"',
    [int]$IntervalSeconds = 2,
    [int]$LookbackSeconds = 120
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot | Split-Path -Parent
$envFile = Join-Path $repoRoot ".env"

if (Test-Path $envFile) {
    Get-Content $envFile | ForEach-Object {
        if ($_ -match '^\s*([^#][^=]*)=(.*)$') {
            $name = $Matches[1].Trim()
            $value = $Matches[2].Trim().Trim('"')
            [System.Environment]::SetEnvironmentVariable($name, $value, "Process")
        }
    }
}

$lu = $LokiUrl.Trim()
if (-not $lu) {
    $tmp = [System.Environment]::GetEnvironmentVariable("LOKI_QUERY_URL", "Process")
    if ($tmp) { $lu = $tmp.Trim() }
}
if (-not $lu) {
    $tmp = [System.Environment]::GetEnvironmentVariable("SIMSTEWARD_LOKI_URL", "Process")
    if ($tmp) { $lu = $tmp.Trim() }
}
if (-not $lu) { $lu = "http://localhost:3100" }
$LokiUrl = $lu.TrimEnd('/')

$lokiUser = [System.Environment]::GetEnvironmentVariable("SIMSTEWARD_LOKI_USER", "Process")
if ($lokiUser) { $lokiUser = $lokiUser.Trim() }
$lokiToken = [System.Environment]::GetEnvironmentVariable("SIMSTEWARD_LOKI_TOKEN", "Process")
if ($lokiToken) { $lokiToken = $lokiToken.Trim() }

$script:Seen = @{}

function Get-UnixNano {
    $epoch = [datetime]::new(1970, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)
    [long](([DateTime]::UtcNow - $epoch).TotalSeconds * 1000000000)
}

function Get-LokiHeaders {
    $h = @{}
    if ($lokiUser -and $lokiToken) {
        $pair = "${lokiUser}:${lokiToken}"
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($pair)
        $h["Authorization"] = "Basic " + [Convert]::ToBase64String($bytes)
    }
    $h
}

function Get-LokiLogs {
    $endNs = Get-UnixNano
    $startNs = $endNs - ([long]$LookbackSeconds * 1000000000)
    $url = "$LokiUrl/loki/api/v1/query_range?query=" + [Uri]::EscapeDataString($Query) + "&limit=200&start=$startNs&end=$endNs"
    try {
        $r = Invoke-RestMethod -Uri $url -Method Get -Headers (Get-LokiHeaders) -ErrorAction Stop
    } catch {
        Write-Host "Loki request failed: $_" -ForegroundColor Red
        return @()
    }
    $lines = @()
    foreach ($res in $r.data.result) {
        foreach ($v in $res.values) {
            $ts = $v[0]
            $line = $v[1]
            $lines += [pscustomobject]@{ Ts = $ts; Line = $line }
        }
    }
    $lines | Sort-Object { [long]$_.Ts }
}

function Format-LogLine {
    param([string]$TsNs, [string]$Line)
    $sec = [long]$TsNs / 1000000000
    $dt = [DateTimeOffset]::FromUnixTimeSeconds($sec).LocalDateTime.ToString("HH:mm:ss.fff")
    $preview = if ($Line.Length -gt 200) { $Line.Substring(0, 200) + "..." } else { $Line }
    "$dt $preview"
}

Write-Host "Polling Loki every ${IntervalSeconds}s for $Query (lookback ${LookbackSeconds}s). Ctrl+C to stop." -ForegroundColor Cyan
Write-Host "Loki URL: $LokiUrl" -ForegroundColor Gray
Write-Host ""

while ($true) {
    $logs = Get-LokiLogs
    foreach ($entry in $logs) {
        $key = "$($entry.Ts)|$($entry.Line)"
        if (-not $script:Seen.ContainsKey($key)) {
            $script:Seen[$key] = $true
            Write-Host (Format-LogLine -TsNs $entry.Ts -Line $entry.Line)
        }
    }
    if ($script:Seen.Count -gt 5000) {
        $script:Seen = @{}
    }
    Start-Sleep -Seconds $IntervalSeconds
}
