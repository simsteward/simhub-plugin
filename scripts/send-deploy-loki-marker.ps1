# Push a single structured line to Loki marking deploy.ps1 completion (optional).
# Skips silently if SIMSTEWARD_LOKI_URL is unset. Uses same 4-label schema as the plugin.
# See docs/GRAFANA-LOGGING.md (event deploy_marker).

param(
    [ValidateSet('ok', 'failed')]
    [string]$Status = 'ok',
    [switch]$PostDeployWarning,
    [string]$Detail = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$loadDotenv = Join-Path $repoRoot 'scripts\load-dotenv.ps1'
if (Test-Path $loadDotenv) {
    . $loadDotenv
    Import-DotEnv @(
        (Join-Path $repoRoot '.env'),
        (Join-Path $repoRoot 'observability\local\.env.observability.local')
    )
}

$url = $env:SIMSTEWARD_LOKI_URL
if ([string]::IsNullOrWhiteSpace($url)) { return }

$envName = $env:SIMSTEWARD_LOG_ENV
if ([string]::IsNullOrWhiteSpace($envName)) { $envName = 'local' }

$tsNs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() * 1000000

$bodyObj = [ordered]@{
    event            = 'deploy_marker'
    deploy_status    = $Status
    post_deploy_warn = $PostDeployWarning.IsPresent
    detail           = $Detail
    machine          = $env:COMPUTERNAME
    simhub_path      = $(if ($env:SIMHUB_PATH) { $env:SIMHUB_PATH } else { '' })
}
$line = ($bodyObj | ConvertTo-Json -Compress -Depth 5)

$lvl = if ($Status -eq 'failed') { 'ERROR' } elseif ($PostDeployWarning) { 'WARN' } else { 'INFO' }
$streamObj = [ordered]@{
    stream = @{
        app       = 'sim-steward'
        env       = $envName
        component = 'simhub-plugin'
        level     = $lvl
    }
    # Loki expects values as [[tsNs, line], ...] — leading comma forces a nested array in PowerShell.
    values = @( , @( [string]$tsNs, $line ) )
}
$root = [ordered]@{ streams = @( $streamObj ) }
$payload = $root | ConvertTo-Json -Depth 20 -Compress

$pushUri = $url.TrimEnd('/') + '/loki/api/v1/push'
$headers = @{ 'Content-Type' = 'application/json' }
$lokiUser = $env:SIMSTEWARD_LOKI_USER
$lokiPass = $env:SIMSTEWARD_LOKI_TOKEN
$gatewayToken = $env:LOKI_PUSH_TOKEN
if ($url -match 'grafana\.net' -and ([string]::IsNullOrWhiteSpace($lokiUser) -or [string]::IsNullOrWhiteSpace($lokiPass))) {
    Write-Host "send-deploy-loki-marker: warn — SIMSTEWARD_LOKI_URL looks like Grafana Cloud but SIMSTEWARD_LOKI_USER / SIMSTEWARD_LOKI_TOKEN missing (Basic auth required)."
}
# Grafana Cloud Loki: Basic (instance user id + API token). Local loki-gateway: Bearer LOKI_PUSH_TOKEN. Local Loki :3100: often no auth.
if (-not [string]::IsNullOrWhiteSpace($lokiUser) -and -not [string]::IsNullOrWhiteSpace($lokiPass)) {
    $pair = [Text.Encoding]::ASCII.GetBytes(("{0}:{1}" -f $lokiUser.Trim(), $lokiPass.Trim()))
    $headers['Authorization'] = 'Basic ' + [Convert]::ToBase64String($pair)
} elseif (-not [string]::IsNullOrWhiteSpace($gatewayToken)) {
    $headers['Authorization'] = 'Bearer ' + $gatewayToken.Trim()
}

try {
    Invoke-RestMethod -Uri $pushUri -Method Post -Headers $headers -Body $payload -TimeoutSec 15 | Out-Null
    $hostOnly = try { ([Uri]$url.Trim()).Host } catch { $url }
    Write-Host "send-deploy-loki-marker: pushed OK ($hostOnly)"
} catch {
    $code = ''
    try {
        $resp = $_.Exception.Response
        if ($null -ne $resp -and $resp.StatusCode) { $code = ' HTTP {0}' -f [int]$resp.StatusCode }
    } catch { }
    Write-Host "send-deploy-loki-marker: push failed (non-fatal):$code $($_.Exception.Message)"
    Write-Host "  Auth: Grafana Cloud -> set SIMSTEWARD_LOKI_USER + SIMSTEWARD_LOKI_TOKEN; local gateway :3500 -> LOKI_PUSH_TOKEN; local :3100 -> leave auth unset."
}
