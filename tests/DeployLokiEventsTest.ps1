# Post-deploy test: verify all deploy events reached Loki with expected values
# Requires: deploy.ps1 just completed (pushes to Grafana Cloud Loki or local Loki)
# Run: .\tests\DeployLokiEventsTest.ps1

$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$loadDotenv = Join-Path $repoRoot 'scripts\load-dotenv.ps1'
if (Test-Path $loadDotenv) {
    . $loadDotenv
    Import-DotEnv (Resolve-SimStewardEnvPaths -RepoRoot $repoRoot)
}

$lokiUrl = $env:SIMSTEWARD_LOKI_URL
if ([string]::IsNullOrWhiteSpace($lokiUrl)) { $lokiUrl = "http://localhost:3100" }

# Grafana Cloud read strategy: the Loki write token is write-only (access policy scope: logs:write).
# Reads are routed through the Grafana datasource proxy using a service account token that has
# Viewer+ permissions on the Grafana org.  This avoids needing a separate logs:read access policy.
#
# Required env vars (already in .env):
#   CURSOR_ELEVATED_GRAFANA_TOKEN  — service account token with Viewer role
#   SIMSTEWARD_GRAFANA_BASE_URL    — https://simsteward.grafana.net
#   SIMSTEWARD_LOKI_DATASOURCE_UID — grafanacloud-logs
#
# Falls back to direct Basic auth for a local Loki stack.
$grafanaBaseUrl    = $env:SIMSTEWARD_GRAFANA_BASE_URL
$lokiDatasourceUid = $env:SIMSTEWARD_LOKI_DATASOURCE_UID
$grafanaToken      = $env:CURSOR_ELEVATED_GRAFANA_TOKEN

$useGrafanaProxy = (
    $lokiUrl -match 'grafana\.net' -and
    -not [string]::IsNullOrWhiteSpace($grafanaBaseUrl) -and
    -not [string]::IsNullOrWhiteSpace($lokiDatasourceUid) -and
    -not [string]::IsNullOrWhiteSpace($grafanaToken)
)

if ($useGrafanaProxy) {
    $queryBase = "$($grafanaBaseUrl.TrimEnd('/'))/api/datasources/proxy/uid/$lokiDatasourceUid/loki/api/v1/query_range"
    Write-Host "Loki query: Grafana proxy ($grafanaBaseUrl, uid=$lokiDatasourceUid)"
} else {
    $queryBase = "$($lokiUrl.TrimEnd('/'))/loki/api/v1/query_range"
    Write-Host "Loki query: direct ($lokiUrl)"
}

$passed = 0
$failed = 0

function Query-LokiEvent {
    param([string]$EventName, [int]$LookbackMinutes = 30)
    $startNs = ([DateTimeOffset]::UtcNow.AddMinutes(-$LookbackMinutes).ToUnixTimeMilliseconds()) * 1000000
    $endNs   = ([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()) * 1000000
    $query   = '{component="local-deployment"} |= "' + $EventName + '"'

    # Build URL with query string manually to avoid PowerShell encoding issues
    $qs  = "query=$([Uri]::EscapeDataString($query))&start=$startNs&end=$endNs&limit=10&direction=BACKWARD"
    $uri = "${script:queryBase}?$qs"

    $headers = @{}
    if ($script:useGrafanaProxy) {
        # Grafana datasource proxy — Bearer token (service account with Viewer role)
        $headers['Authorization'] = 'Bearer ' + $script:grafanaToken.Trim()
    } else {
        # Direct local Loki — no auth needed (or Basic if env vars present)
        $lokiUser = $env:SIMSTEWARD_LOKI_USER
        $lokiPass = $env:SIMSTEWARD_LOKI_TOKEN
        if (-not [string]::IsNullOrWhiteSpace($lokiUser) -and -not [string]::IsNullOrWhiteSpace($lokiPass)) {
            $pair = [Text.Encoding]::ASCII.GetBytes(("{0}:{1}" -f $lokiUser.Trim(), $lokiPass.Trim()))
            $headers['Authorization'] = 'Basic ' + [Convert]::ToBase64String($pair)
        }
    }

    try {
        $raw = Invoke-WebRequest -Uri $uri -Method Get -Headers $headers -TimeoutSec 10 -UseBasicParsing
        $resp = $raw.Content | ConvertFrom-Json
    } catch {
        Write-Host "  DEBUG: Loki query failed for $EventName : $($_.Exception.Message)"
        Write-Host "  DEBUG: URI = $uri"
        return @()
    }

    $entries = @()
    if ($null -eq $resp.data -or $null -eq $resp.data.result) { return $entries }
    foreach ($stream in $resp.data.result) {
        if ($null -eq $stream.values) { continue }
        foreach ($val in $stream.values) {
            $line = $val[1]
            if ($null -ne $line) {
                $entries += ($line | ConvertFrom-Json)
            }
        }
    }
    return $entries
}

function Assert-Event {
    param(
        [string]$EventName,
        [hashtable]$ExpectedFields = @{}
    )

    $entries = Query-LokiEvent $EventName
    if ($entries.Count -eq 0) {
        Write-Host "FAIL: [$EventName] not found in Loki"
        $script:failed++
        return
    }
    Write-Host "PASS: [$EventName] found in Loki ($($entries.Count) entry/entries)"
    $script:passed++

    $entry = $entries[0]
    foreach ($key in $ExpectedFields.Keys) {
        $expected = $ExpectedFields[$key]
        $actual = $entry.$key
        if ($expected -is [scriptblock]) {
            $ok = & $expected $actual
            if ($ok) {
                Write-Host "PASS: [$EventName] $key validated"
                $script:passed++
            } else {
                Write-Host "FAIL: [$EventName] $key validation failed (actual=$actual)"
                $script:failed++
            }
        } else {
            if ("$actual" -eq "$expected") {
                Write-Host "PASS: [$EventName] $key=$actual"
                $script:passed++
            } else {
                Write-Host "FAIL: [$EventName] $key expected=$expected actual=$actual"
                $script:failed++
            }
        }
    }
}

# ── Verify every deploy event ────────────────────────────────────────────────

Assert-Event 'deploy_started' @{
    level      = 'INFO'
    machine    = { param($v) -not [string]::IsNullOrWhiteSpace($v) }
    git_branch = { param($v) -not [string]::IsNullOrWhiteSpace($v) }
    git_sha    = { param($v) -not [string]::IsNullOrWhiteSpace($v) }
}

Assert-Event 'deploy_simhub_found' @{
    level       = 'INFO'
    simhub_path = { param($v) -not [string]::IsNullOrWhiteSpace($v) }
}

Assert-Event 'deploy_build_started' @{
    level = 'INFO'
}

Assert-Event 'deploy_build_result' @{
    level      = 'INFO'
    status     = 'ok'
    duration_s = { param($v) $null -ne $v -and [double]$v -ge 0 }
}

Assert-Event 'deploy_tests_result' @{
    level  = 'INFO'
    status = { param($v) $v -in @('ok', 'skipped') }
}

Assert-Event 'deploy_simhub_stopped' @{
    level = 'INFO'
}

Assert-Event 'deploy_dlls_cleaned' @{
    level   = 'INFO'
    deleted = { param($v) -not [string]::IsNullOrWhiteSpace($v) }
}

Assert-Event 'deploy_dlls_copied' @{
    level = 'INFO'
    dlls  = { param($v) $v -match 'SimSteward\.Plugin\.dll' }
}

Assert-Event 'deploy_dashboard_copied' @{
    level      = 'INFO'
    dashboards = { param($v) $v -match 'index\.html' }
}

Assert-Event 'deploy_verified' @{
    level  = 'INFO'
    status = 'ok'
}

Assert-Event 'deploy_version_resolved' @{
    plugin_version = { param($v) -not [string]::IsNullOrWhiteSpace($v) -and $v -ne 'unknown' }
}

Assert-Event 'deploy_simhub_launch' @{
    level  = 'INFO'
    status = { param($v) $v -in @('launched', 'skipped') }
}

Assert-Event 'deploy_post_tests_started' @{
    level = 'INFO'
}


# ── Summary ──────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "Loki deploy events: $passed passed, $failed failed"
if ($failed -gt 0) {
    Write-Host "FAIL: $failed assertion(s) failed"
    exit 1
}
Write-Host "All deploy Loki event checks passed."
