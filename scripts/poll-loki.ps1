# Continuously poll Loki for SimSteward logs and print new lines (tail-style).
# Run from repo root:
#   Direct Loki:  .\scripts\poll-loki.ps1
#   Via Grafana:  .\scripts\poll-loki.ps1 -ViaGrafana   (or npm run obs:poll:grafana)
# .env: LOKI_QUERY_URL / SIMSTEWARD_LOKI_URL + optional SIMSTEWARD_LOKI_* (cloud).
# -ViaGrafana: GRAFANA_URL, Bearer token = GRAFANA_API_TOKEN or CURSOR_ELEVATED_GRAFANA_TOKEN (or GRAFANA_ADMIN_USER + GRAFANA_ADMIN_PASSWORD), optional GRAFANA_LOKI_DATASOURCE_UID.
# Env SIMSTEWARD_LOKI_VIA_GRAFANA=1 enables -ViaGrafana without the switch.

param(
    [string]$LokiUrl = "",
    [string]$Query = '{app="sim-steward"} | json | level != "DEBUG"',
    [int]$IntervalSeconds = 2,
    [int]$LookbackSeconds = 120,
    [switch]$ViaGrafana
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot | Split-Path -Parent
$loadDotenv = Join-Path $repoRoot "scripts\load-dotenv.ps1"
if (Test-Path $loadDotenv) {
    . $loadDotenv
    Import-DotEnv @(
        (Join-Path $repoRoot ".env"),
        (Join-Path $repoRoot "observability\local\.env.observability.local")
    )
}

$useGrafanaProxy = [bool]$ViaGrafana
if (-not $useGrafanaProxy) {
    $vf = [System.Environment]::GetEnvironmentVariable("SIMSTEWARD_LOKI_VIA_GRAFANA", "Process")
    if ($vf) {
        $vf = $vf.Trim().ToLowerInvariant()
        $useGrafanaProxy = ($vf -eq "1" -or $vf -eq "true" -or $vf -eq "yes")
    }
}

$GrafanaUrl = "http://localhost:3000"
$tmpG = [System.Environment]::GetEnvironmentVariable("GRAFANA_URL", "Process")
if ($tmpG) { $GrafanaUrl = $tmpG.Trim().TrimEnd('/') }

$DatasourceUid = "loki_local"
$tmpUid = [System.Environment]::GetEnvironmentVariable("GRAFANA_LOKI_DATASOURCE_UID", "Process")
if ($tmpUid) { $DatasourceUid = $tmpUid.Trim() }

$grafanaToken = [System.Environment]::GetEnvironmentVariable("GRAFANA_API_TOKEN", "Process")
if ($grafanaToken) { $grafanaToken = $grafanaToken.Trim() }
if (-not $grafanaToken) {
    $tmpElev = [System.Environment]::GetEnvironmentVariable("CURSOR_ELEVATED_GRAFANA_TOKEN", "Process")
    if ($tmpElev) { $grafanaToken = $tmpElev.Trim() }
}
$grafanaAdminUser = [System.Environment]::GetEnvironmentVariable("GRAFANA_ADMIN_USER", "Process")
if ($grafanaAdminUser) { $grafanaAdminUser = $grafanaAdminUser.Trim() }
$grafanaAdminPass = [System.Environment]::GetEnvironmentVariable("GRAFANA_ADMIN_PASSWORD", "Process")
if ($grafanaAdminPass) { $grafanaAdminPass = $grafanaAdminPass.Trim() }

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

if ($useGrafanaProxy) {
    if (-not $grafanaToken -and (-not $grafanaAdminUser -or -not $grafanaAdminPass)) {
        Write-Host "FAIL: -ViaGrafana requires GRAFANA_API_TOKEN or CURSOR_ELEVATED_GRAFANA_TOKEN (Bearer), or GRAFANA_ADMIN_USER + GRAFANA_ADMIN_PASSWORD." -ForegroundColor Red
        exit 1
    }
}

$script:Seen = @{}

function Get-UnixNano {
    $epoch = [datetime]::new(1970, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)
    [long](([DateTime]::UtcNow - $epoch).TotalSeconds * 1000000000)
}

function Get-DirectLokiHeaders {
    $h = @{}
    if ($lokiUser -and $lokiToken) {
        $pair = "${lokiUser}:${lokiToken}"
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($pair)
        $h["Authorization"] = "Basic " + [Convert]::ToBase64String($bytes)
    }
    $h
}

function Get-GrafanaProxyHeaders {
    $h = @{}
    if ($grafanaToken) {
        $h["Authorization"] = "Bearer " + $grafanaToken
    } elseif ($grafanaAdminUser -and $grafanaAdminPass) {
        $pair = "${grafanaAdminUser}:${grafanaAdminPass}"
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($pair)
        $h["Authorization"] = "Basic " + [Convert]::ToBase64String($bytes)
    }
    $h
}

function Get-LokiLogs {
    $endNs = Get-UnixNano
    $startNs = $endNs - ([long]$LookbackSeconds * 1000000000)
    $q = [Uri]::EscapeDataString($Query)
    if ($useGrafanaProxy) {
        $base = "$GrafanaUrl/api/datasources/proxy/uid/$DatasourceUid"
        $url = "$base/loki/api/v1/query_range?query=$q&limit=200&start=$startNs&end=$endNs"
        $headers = Get-GrafanaProxyHeaders
    } else {
        $url = "$LokiUrl/loki/api/v1/query_range?query=$q&limit=200&start=$startNs&end=$endNs"
        $headers = Get-DirectLokiHeaders
    }
    try {
        $r = Invoke-RestMethod -Uri $url -Method Get -Headers $headers -ErrorAction Stop
    } catch {
        Write-Host "Loki request failed: $_" -ForegroundColor Red
        return @()
    }
    if (-not $r.data -or -not $r.data.result) {
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

Write-Host "Polling every ${IntervalSeconds}s for $Query (lookback ${LookbackSeconds}s). Ctrl+C to stop." -ForegroundColor Cyan
if ($useGrafanaProxy) {
    Write-Host "Route: Grafana proxy → $GrafanaUrl (datasource uid=$DatasourceUid)" -ForegroundColor Gray
} else {
    Write-Host "Route: direct Loki → $LokiUrl" -ForegroundColor Gray
}
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
