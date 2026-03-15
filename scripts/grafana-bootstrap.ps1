# Create a service account and API token for Grafana dashboard management.
# Usage: .\scripts\grafana-bootstrap.ps1
$ErrorActionPreference = "Stop"
$GrafanaUrl = "http://localhost:3000"
$AdminUser = $env:GRAFANA_ADMIN_USER_OVERRIDE
$AdminPass = $env:GRAFANA_ADMIN_PASSWORD_OVERRIDE

$headers = @{
    "Authorization" = "Basic " + [System.Convert]::ToBase64String([System.Text.Encoding]::ASCII.GetBytes("${AdminUser}:${AdminPass}"))
    "Content-Type" = "application/json"
}

# 1. Create Service Account
$saBody = @{
    name = "simsteward-dashboard-publisher"
    role = "Admin"
} | ConvertTo-Json
$saUrl = "$GrafanaUrl/api/serviceaccounts"
$saResponse = Invoke-RestMethod -Uri $saUrl -Method Post -Headers $headers -Body $saBody -ErrorAction SilentlyContinue
$saId = $saResponse.id

if (-not $saId) {
    # If it already exists, find it
    $existingSAs = Invoke-RestMethod -Uri $saUrl -Headers $headers
    $existingSA = $existingSAs.serviceAccounts | Where-Object { $_.name -eq "simsteward-dashboard-publisher" }
    $saId = $existingSA.id
    if (-not $saId) {
        Write-Host "FAIL: Could not create or find Service Account."
        exit 1
    }
     Write-Host "Service Account 'simsteward-dashboard-publisher' already exists with ID $saId."
} else {
    Write-Host "Service Account 'simsteward-dashboard-publisher' created with ID $saId."
}


# 2. Create API Token
$tokenBody = @{
    name = "dashboard-publisher-token"
} | ConvertTo-Json
$tokenUrl = "$GrafanaUrl/api/serviceaccounts/$saId/tokens"
$tokenResponse = Invoke-RestMethod -Uri $tokenUrl -Method Post -Headers $headers -Body $tokenBody
$token = $tokenResponse.key

if (-not $token) {
    Write-Host "FAIL: Could not create API token."
    exit 1
}

Write-Host "API Token generated."

# 3. Update .env file
$envFile = "c:\Users\winth\dev\sim-steward\plugin\.env"
$envContent = Get-Content $envFile -Raw
$tokenLine = "GRAFANA_API_TOKEN=`"$token`""
if ($envContent -match "GRAFANA_API_TOKEN") {
    $envContent = $envContent -replace 'GRAFANA_API_TOKEN=.*', $tokenLine
} else {
    $envContent = $envContent + "`n" + $tokenLine
}
Set-Content -Path $envFile -Value $envContent

Write-Host "PASS: .env file updated with GRAFANA_API_TOKEN."
