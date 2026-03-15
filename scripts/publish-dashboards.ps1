# Publish Grafana dashboards from JSON files to the Grafana API.
# Usage: .\scripts\publish-dashboards.ps1
$ErrorActionPreference = "Stop"
$GrafanaUrl = "http://localhost:3000"
$DashboardDir = "observability/local/grafana/provisioning/dashboards"

# Load .env file to get GRAFANA_API_TOKEN
$envFile = ".env"
if (-not (Test-Path $envFile)) {
    Write-Host "FAIL: .env file not found. Run grafana-bootstrap.ps1 first."
    exit 1
}
Get-Content $envFile | Foreach-Object {
    if ($_ -match '^(?<name>[^=]+)=(?<value>.*)') {
        $name = $Matches.name.Trim()
        $value = $Matches.value.Trim().Trim('"')
        # Set environment variable for the current process
        [System.Environment]::SetEnvironmentVariable($name, $value, "Process")
    }
}

$grafanaApiToken = [System.Environment]::GetEnvironmentVariable("GRAFANA_API_TOKEN", "Process")

if (-not $grafanaApiToken) {
    Write-Host "FAIL: GRAFANA_API_TOKEN not found in .env file."
    exit 1
}

$headers = @{
    "Authorization" = "Bearer " + $grafanaApiToken
    "Content-Type" = "application/json"
}

$dashboardFiles = Get-ChildItem -Path $DashboardDir -Filter "*.json"

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
