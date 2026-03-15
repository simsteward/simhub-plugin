# Continuously poll Loki for SimSteward logs and print new lines (tail-style).
# Run from repo root: .\scripts\poll-loki.ps1
# Requires: Local Loki at http://localhost:3100 (npm run obs:up). Ctrl+C to stop.

param(
    [string]$LokiUrl = "http://localhost:3100",
    [string]$Query = '{app="sim-steward"}',
    [int]$IntervalSeconds = 2,
    [int]$LookbackSeconds = 60
)

$ErrorActionPreference = "Stop"
$script:LastEndNs = $null
$script:Seen = @{}  # dedupe by "ts|line"

function Get-UnixNano {
    $epoch = [datetime]::new(1970, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)
    [long](([DateTime]::UtcNow - $epoch).TotalSeconds * 1000000000)
}

function Get-LokiLogs {
    $endNs = Get-UnixNano
    $startNs = $endNs - ([long]$LookbackSeconds * 1000000000)
    $startSec = [math]::Floor($startNs / 1000000000)
    $endSec = [math]::Floor($endNs / 1000000000)
    $url = "$LokiUrl/loki/api/v1/query_range?query=" + [Uri]::EscapeDataString($Query) + "&limit=200&start=$startSec&end=$endSec"
    try {
        $r = Invoke-RestMethod -Uri $url -Method Get -ErrorAction Stop
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
    # Keep seen set bounded
    if ($script:Seen.Count -gt 5000) {
        $script:Seen = @{}
    }
    Start-Sleep -Seconds $IntervalSeconds
}
