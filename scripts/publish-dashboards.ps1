# Publish Grafana dashboards from JSON files to the Grafana API.
# Usage (any cwd): .\scripts\publish-dashboards.ps1
$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot | Split-Path -Parent
$GrafanaUrl = "http://localhost:3000"
$DashboardDir = Join-Path $repoRoot "observability\local\grafana\provisioning\dashboards"

$loadDotenv = Join-Path $repoRoot "scripts\load-dotenv.ps1"
if (-not (Test-Path (Join-Path $repoRoot ".env"))) {
    Write-Host "FAIL: .env not found at $(Join-Path $repoRoot '.env'). Run grafana-bootstrap.ps1 first."
    exit 1
}
if (Test-Path $loadDotenv) {
    . $loadDotenv
    Import-DotEnv @(
        (Join-Path $repoRoot ".env"),
        (Join-Path $repoRoot "observability\local\.env.observability.local")
    )
}
$gu = [System.Environment]::GetEnvironmentVariable("GRAFANA_URL", "Process")
if ($gu) { $GrafanaUrl = $gu.Trim().TrimEnd('/') }

$grafanaApiToken = [System.Environment]::GetEnvironmentVariable("GRAFANA_API_TOKEN", "Process")

if (-not $grafanaApiToken) {
    Write-Host "FAIL: GRAFANA_API_TOKEN not found in .env file."
    exit 1
}

$headers = @{
    "Authorization" = "Bearer " + $grafanaApiToken
    "Content-Type" = "application/json"
}

$dashboardFiles = @(Get-ChildItem -Path $DashboardDir -Filter "*.json" -ErrorAction SilentlyContinue)
if ($dashboardFiles.Count -eq 0) {
    Write-Host "PASS: No dashboard JSON in $DashboardDir — nothing to publish."
    exit 0
}

foreach ($file in $dashboardFiles) {
    Write-Host "Publishing $($file.Name)..."
    $dashboardJson = Get-Content -Path $file.FullName -Raw
    
    $dashboardObject = ConvertFrom-Json $dashboardJson
    
    # API expects the dashboard model directly if it's the root object in the file
    $dashboardPayload = $dashboardObject.dashboard
    if (-not $dashboardPayload) {
        $dashboardPayload = $dashboardObject
    }

    $apiPayloadObject = @{
        dashboard = $dashboardPayload
        overwrite = $true
    }
    
    $payload = $apiPayloadObject | ConvertTo-Json -Depth 10

    $publishUrl = "$GrafanaUrl/api/dashboards/db"
    Invoke-RestMethod -Uri $publishUrl -Method Post -Headers $headers -Body $payload
}

Write-Host "PASS: All dashboards published successfully."
