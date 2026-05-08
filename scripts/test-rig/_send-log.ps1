# Shared helper: push a single structured log line to Grafana Cloud Loki.
# Stream labels: app="sim-steward", env=$env:SIMSTEWARD_LOG_ENV, component, level.
# Cloud-only — silently no-ops if SIMSTEWARD_LOKI_URL is unset, empty, or localhost
# (matches scripts/hooks/loki-log.js). Auth: Basic <user>:<token> from .env.
#
# Usage (after dot-sourcing):
#   Send-TestRigLog -Event 'test_rig_stop_all_started' -Level 'INFO' `
#                   -Domain 'system' -Fields @{ include_steam = $false }
#
# All fields land in a single JSON object alongside event/domain/timestamp/component.

$ErrorActionPreference = 'Stop'

$script:TestRigEnvLoaded = $false

function Initialize-TestRigEnv {
    if ($script:TestRigEnvLoaded) { return }
    $repoRoot = (Get-Item -Path "$PSScriptRoot\..\..").FullName
    $loader = Join-Path $repoRoot 'scripts\load-dotenv.ps1'
    if (Test-Path -LiteralPath $loader) {
        . $loader
        $envPath = Join-Path $repoRoot '.env'
        if (Test-Path -LiteralPath $envPath) {
            try { Import-DotEnv @($envPath) } catch { Write-Verbose "Import-DotEnv failed: $($_.Exception.Message)" }
        }
    }
    $script:TestRigEnvLoaded = $true
}

function Send-TestRigLog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Event,
        [string]$Level = 'INFO',
        [string]$Domain = 'system',
        [string]$Component = 'test-rig',
        [string]$Message = '',
        [hashtable]$Fields = @{}
    )

    Initialize-TestRigEnv

    $rawUrl = ($env:SIMSTEWARD_LOKI_URL).Trim().TrimEnd('/')
    # Cloud-only: skip silently when unset or pointed at localhost (matches loki-log.js)
    if ([string]::IsNullOrWhiteSpace($rawUrl)) { return }
    if ($rawUrl -match '^(?i)https?://(localhost|127\.0\.0\.1)') { return }

    $envLabel = if ($env:SIMSTEWARD_LOG_ENV) { $env:SIMSTEWARD_LOG_ENV } else { 'local' }
    $machine  = if ($env:COMPUTERNAME) { $env:COMPUTERNAME } else { [System.Net.Dns]::GetHostName() }

    $payload = [ordered]@{
        event     = $Event
        domain    = $Domain
        component = $Component
        level     = $Level
        timestamp = (Get-Date).ToUniversalTime().ToString('o')
        machine   = $machine
        env       = $envLabel
        source    = 'test-rig'
    }
    if (-not [string]::IsNullOrEmpty($Message)) { $payload.message = $Message }
    if ($Fields) {
        foreach ($k in $Fields.Keys) { $payload[$k] = $Fields[$k] }
    }

    $logLine = ($payload | ConvertTo-Json -Compress -Depth 6)

    $tsNs = ([string]([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())) + '000000'

    $stream = [ordered]@{
        app       = 'sim-steward'
        env       = $envLabel
        component = $Component
        level     = $Level
    }

    $body = @{
        streams = @(
            @{
                stream = $stream
                values = @(, @($tsNs, $logLine))
            }
        )
    } | ConvertTo-Json -Compress -Depth 6

    $headers = @{ 'Content-Type' = 'application/json' }
    if ($env:SIMSTEWARD_LOKI_USER -and $env:SIMSTEWARD_LOKI_TOKEN) {
        $pair = "$($env:SIMSTEWARD_LOKI_USER):$($env:SIMSTEWARD_LOKI_TOKEN)"
        $b64  = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($pair))
        $headers.Authorization = "Basic $b64"
    }

    try {
        Invoke-RestMethod -Method Post -Uri "$rawUrl/loki/api/v1/push" `
            -Headers $headers -Body $body -TimeoutSec 4 | Out-Null
    } catch {
        # Best-effort logging; never crash the rig because Loki was unreachable.
        Write-Verbose "Loki push failed: $($_.Exception.Message)"
    }

    # Mirror to console for live operator visibility.
    Write-Host "[$Level] $Event $logLine"
}
