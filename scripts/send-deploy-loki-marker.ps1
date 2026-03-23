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
$token = $env:LOKI_PUSH_TOKEN
if (-not [string]::IsNullOrWhiteSpace($token)) {
    $headers['Authorization'] = 'Bearer ' + $token.Trim()
}

try {
    Invoke-RestMethod -Uri $pushUri -Method Post -Headers $headers -Body $payload -TimeoutSec 15 | Out-Null
} catch {
    Write-Host "send-deploy-loki-marker: push failed (non-fatal): $($_.Exception.Message)"
}
