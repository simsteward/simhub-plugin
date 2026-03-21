# Create a service account and API token for Grafana dashboard management.
# Usage: .\scripts\grafana-bootstrap.ps1
# Auth: GRAFANA_ADMIN_USER_OVERRIDE / GRAFANA_ADMIN_PASSWORD_OVERRIDE, else repo .env GRAFANA_ADMIN_USER / GRAFANA_ADMIN_PASSWORD, else admin/admin.
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

$GrafanaUrl = "http://localhost:3000"
$gu = [System.Environment]::GetEnvironmentVariable("GRAFANA_URL", "Process")
if ($gu) { $GrafanaUrl = $gu.Trim().TrimEnd('/') }

$AdminUser = [System.Environment]::GetEnvironmentVariable("GRAFANA_ADMIN_USER_OVERRIDE", "Process")
if (-not $AdminUser) { $AdminUser = [System.Environment]::GetEnvironmentVariable("GRAFANA_ADMIN_USER", "Process") }
if (-not $AdminUser) { $AdminUser = "admin" }
$AdminPass = [System.Environment]::GetEnvironmentVariable("GRAFANA_ADMIN_PASSWORD_OVERRIDE", "Process")
if (-not $AdminPass) { $AdminPass = [System.Environment]::GetEnvironmentVariable("GRAFANA_ADMIN_PASSWORD", "Process") }
if (-not $AdminPass) { $AdminPass = "admin" }

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
if (-not (Test-Path $envFile)) {
    Write-Host "FAIL: .env not found at $envFile — copy from .env.example first."
    exit 1
}
$envContent = Get-Content $envFile -Raw
$tokenLine = "GRAFANA_API_TOKEN=`"$token`""
if ($envContent -match "GRAFANA_API_TOKEN") {
    $envContent = $envContent -replace 'GRAFANA_API_TOKEN=.*', $tokenLine
} else {
    $envContent = $envContent + "`n" + $tokenLine
}
Set-Content -Path $envFile -Value $envContent

Write-Host "PASS: .env file updated with GRAFANA_API_TOKEN."
