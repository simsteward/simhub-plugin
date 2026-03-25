# Deploy Sim Steward plugin to local SimHub.
# Copies: plugin DLLs + every *.html (and README.txt) from src\SimSteward.Dashboard -> SimHub\Web\sim-steward-dash\
# Run from plugin/:  .\deploy.ps1 [-EnvFile path\to\secrets.env]
# Requires: SimHub installed; place SimHub.Plugins.dll and GameReaderCommon.dll in lib\SimHub\ (or script copies from SimHub path).

param(
    [string]$EnvFile = ""
)

$ErrorActionPreference = "Stop"
$PluginRoot = $PSScriptRoot
$script:deployStartUtc = [DateTimeOffset]::UtcNow

# Load env: default .env + optional observability merge, or -EnvFile (absolute or repo-relative) + same merge.
$loadDotenv = Join-Path $PluginRoot "scripts\load-dotenv.ps1"
if (Test-Path $loadDotenv) {
    . $loadDotenv
    $dotPaths = Resolve-SimStewardEnvPaths -RepoRoot $PluginRoot -EnvFile $EnvFile
    Import-DotEnv $dotPaths
    if (-not [string]::IsNullOrWhiteSpace($EnvFile)) {
        Write-Host "Loaded secrets from -EnvFile $EnvFile (+ observability local merge if present)."
    }
}

# Deploy marker -> Grafana Explore (Loki). Default local stack when unset; avoid template Cloud URL without creds (530).
$localLoki = "http://localhost:3100"
$deployMarkerLocal = $false
if ([string]::IsNullOrWhiteSpace($env:SIMSTEWARD_LOKI_URL)) {
    $env:SIMSTEWARD_LOKI_URL = $localLoki
    $deployMarkerLocal = $true
    Write-Host "Loki deploy log: SIMSTEWARD_LOKI_URL was unset - using $localLoki (start stack: pnpm run obs:up)."
} elseif ($env:SIMSTEWARD_LOKI_URL -match 'grafana\.net') {
    $hasCloudBasic = -not [string]::IsNullOrWhiteSpace($env:SIMSTEWARD_LOKI_USER) -and -not [string]::IsNullOrWhiteSpace($env:SIMSTEWARD_LOKI_TOKEN)
    if (-not $hasCloudBasic) {
        Write-Warning "SIMSTEWARD_LOKI_URL is Grafana Cloud but SIMSTEWARD_LOKI_USER / SIMSTEWARD_LOKI_TOKEN missing - using $localLoki for deploy marker. Set both for Cloud."
        $env:SIMSTEWARD_LOKI_URL = $localLoki
        $deployMarkerLocal = $true
    }
}
if ([string]::IsNullOrWhiteSpace($env:SIMSTEWARD_LOG_ENV)) { $env:SIMSTEWARD_LOG_ENV = "local" }
if ($deployMarkerLocal) { $env:SIMSTEWARD_LOG_ENV = "local" }

# ── Loki push helper (fire-and-forget, never fatal) ──────────────────────────
$script:lokiHeaders = @{ 'Content-Type' = 'application/json' }
$lokiUser = $env:SIMSTEWARD_LOKI_USER
$lokiPass = $env:SIMSTEWARD_LOKI_TOKEN
$gatewayToken = $env:LOKI_PUSH_TOKEN
if (-not [string]::IsNullOrWhiteSpace($lokiUser) -and -not [string]::IsNullOrWhiteSpace($lokiPass)) {
    $pair = [Text.Encoding]::ASCII.GetBytes(("{0}:{1}" -f $lokiUser.Trim(), $lokiPass.Trim()))
    $script:lokiHeaders['Authorization'] = 'Basic ' + [Convert]::ToBase64String($pair)
} elseif (-not [string]::IsNullOrWhiteSpace($gatewayToken)) {
    $script:lokiHeaders['Authorization'] = 'Bearer ' + $gatewayToken.Trim()
}
$script:lokiPushUri = $env:SIMSTEWARD_LOKI_URL.TrimEnd('/') + '/loki/api/v1/push'

function Push-LokiEvent {
    param(
        [string]$Event,
        [string]$Level = 'INFO',
        [string]$Message = '',
        [hashtable]$Fields = @{}
    )
    try {
        $tsNs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() * 1000000
        $body = [ordered]@{
            event   = $Event
            level   = $Level
            message = $Message
            machine = $env:COMPUTERNAME
        }
        foreach ($k in $Fields.Keys) { $body[$k] = $Fields[$k] }
        $line = $body | ConvertTo-Json -Compress -Depth 5
        $stream = [ordered]@{
            stream = @{
                app       = 'sim-steward'
                env       = $env:SIMSTEWARD_LOG_ENV
                component = 'local-deployment'
                level     = $Level
            }
            values = @( , @( [string]$tsNs, $line ) )
        }
        $payload = ([ordered]@{ streams = @( $stream ) }) | ConvertTo-Json -Depth 20 -Compress
        Invoke-RestMethod -Uri $script:lokiPushUri -Method Post -Headers $script:lokiHeaders -Body $payload -TimeoutSec 5 | Out-Null
    } catch {
        # Non-fatal: deploy must not fail because Loki is down
    }
}

# Resolve git info for deploy context
$gitBranch = ''
$gitSha = ''
try {
    $gitBranch = (& git -C $PluginRoot rev-parse --abbrev-ref HEAD 2>$null)
    $gitSha = (& git -C $PluginRoot rev-parse --short HEAD 2>$null)
} catch {}

# ── EVENT: deploy_started ────────────────────────────────────────────────────
Push-LokiEvent 'deploy_started' 'INFO' 'Deploy script started' @{
    git_branch = $gitBranch
    git_sha    = $gitSha
    env_file   = $(if ($EnvFile) { $EnvFile } else { '.env (default)' })
    loki_url   = $env:SIMSTEWARD_LOKI_URL
}

$PluginDlls = @(
    "SimSteward.Plugin.dll",
    "Fleck.dll",
    "Newtonsoft.Json.dll",
    "IRSDKSharper.dll",
    "YamlDotNet.dll",
    "Sentry.dll"
)

function Read-PluginDllProductVersion {
    param([string]$DllPath)
    try {
        if (-not (Test-Path -LiteralPath $DllPath)) { return $null }
        $full = (Resolve-Path -LiteralPath $DllPath).Path
        return ([System.Diagnostics.FileVersionInfo]::GetVersionInfo($full)).ProductVersion
    } catch {
        return $null
    }
}

# Populated after DLL copy + verify (AssemblyInformationalVersion -> PE ProductVersion)
$script:SimStewardPluginVersionDeployed = $null

# ── Locate SimHub install path ───────────────────────────────────────────────
$SimHubPath = $null
if ($env:SIMHUB_PATH -and (Test-Path $env:SIMHUB_PATH)) {
    $SimHubPath = (Resolve-Path $env:SIMHUB_PATH).Path
}
if (-not $SimHubPath) {
    $regPath = "HKCU:\Software\SimHub"
    if (Test-Path $regPath) {
        $installDir = (Get-ItemProperty $regPath -ErrorAction SilentlyContinue).InstallDirectory
        if ($installDir -and (Test-Path $installDir)) { $SimHubPath = $installDir }
    }
}
if (-not $SimHubPath) {
    $proc = Get-Process -Name "SimHubWPF" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($proc) { try { $SimHubPath = Split-Path $proc.MainModule.FileName } catch {} }
}
if (-not $SimHubPath) { $SimHubPath = "C:\Program Files (x86)\SimHub" }

$SimHubExe = Join-Path $SimHubPath "SimHubWPF.exe"
if (-not (Test-Path $SimHubExe)) {
    Push-LokiEvent 'deploy_failed' 'ERROR' 'SimHub not found' @{ simhub_path = $SimHubPath }
    Write-Error "SimHub not found at: $SimHubExe. Set SIMHUB_PATH to your SimHub folder."
}
Write-Host "SimHub path: $SimHubPath"

# ── EVENT: deploy_simhub_found ───────────────────────────────────────────────
Push-LokiEvent 'deploy_simhub_found' 'INFO' "SimHub located at $SimHubPath" @{
    simhub_path = $SimHubPath
    simhub_exe  = $SimHubExe
}

$DashboardSource = Join-Path $PluginRoot "src\SimSteward.Dashboard\index.html"
if (-not (Test-Path $DashboardSource)) {
    Push-LokiEvent 'deploy_failed' 'ERROR' 'Dashboard source not found' @{ path = $DashboardSource }
    Write-Error "Dashboard source not found: $DashboardSource"
}
# SimHub serves static HTML from Web/, not DashTemplates/ (DashTemplates requires .djson catalog)
$DashboardTargetDir = Join-Path $SimHubPath "Web\sim-steward-dash"
New-Item -ItemType Directory -Path $DashboardTargetDir -Force | Out-Null
$oldDashboardDir = Join-Path $SimHubPath "DashTemplates\SimSteward"
if (Test-Path $oldDashboardDir) {
    Remove-Item $oldDashboardDir -Recurse -Force
    Write-Host "Removed old dashboard folder: DashTemplates\SimSteward"
}

# ── Ensure SDK DLLs in lib\SimHub (for build) ────────────────────────────────
$libSimHub = Join-Path $PluginRoot "lib\SimHub"
$sdkDlls = @("SimHub.Plugins.dll", "GameReaderCommon.dll")
$sdkMissing = $sdkDlls | Where-Object { -not (Test-Path (Join-Path $libSimHub $_)) }
if ($sdkMissing) {
    New-Item -ItemType Directory -Path $libSimHub -Force | Out-Null
    foreach ($d in $sdkMissing) {
        $src = Join-Path $SimHubPath $d
        if (-not (Test-Path $src)) { Write-Error "SimHub SDK DLL not found: $src" }
        Copy-Item $src $libSimHub -Force
        Write-Host "Copied SDK: $d -> lib/SimHub/"
    }
}

# ── Build ───────────────────────────────────────────────────────────────────
Write-Host "Building..."
Push-LokiEvent 'deploy_build_started' 'INFO' 'dotnet build started'
$buildStart = Get-Date
Push-Location $PluginRoot
try {
    & dotnet build "src\SimSteward.Plugin\SimSteward.Plugin.csproj" -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) {
        Push-LokiEvent 'deploy_build_result' 'ERROR' "Build failed (exit $LASTEXITCODE)" @{
            status    = 'failed'
            exit_code = $LASTEXITCODE
            duration_s = [math]::Round(((Get-Date) - $buildStart).TotalSeconds, 1)
        }
        throw "Build failed with exit code $LASTEXITCODE."
    }
} finally { Pop-Location }
$buildDuration = [math]::Round(((Get-Date) - $buildStart).TotalSeconds, 1)
Write-Host "Build succeeded."
Push-LokiEvent 'deploy_build_result' 'INFO' "Build succeeded in ${buildDuration}s" @{
    status     = 'ok'
    duration_s = $buildDuration
}

# Resolve build output folder
$outDir = Join-Path $PluginRoot "bin\Plugin"
if (-not (Test-Path (Join-Path $outDir "SimSteward.Plugin.dll"))) {
    $outDir = Join-Path $outDir "net48"
}
if (-not (Test-Path (Join-Path $outDir "SimSteward.Plugin.dll"))) {
    Push-LokiEvent 'deploy_failed' 'ERROR' 'Build output not found' @{ out_dir = $outDir }
    Write-Error "Build output not found. Expected SimSteward.Plugin.dll in bin\Plugin or bin\Plugin\net48"
}

# ── Run unit tests (if test projects exist) ─────────────────────────────────
$skipTests = $env:SIMSTEWARD_SKIP_TESTS -eq "1"
if (-not $skipTests) {
    $testProjects = @(Get-ChildItem -Path $PluginRoot -Recurse -Filter "*.csproj" |
        Where-Object { $_.Name -match "test" -or $_.Directory.Name -match "test" })
    if ($testProjects.Count -gt 0) {
        Write-Host "Running unit tests..."
        Push-LokiEvent 'deploy_tests_started' 'INFO' 'Unit tests started' @{
            test_type = 'unit'
            project_count = $testProjects.Count
        }
        $testStart = Get-Date
        $testRetried = $false
        Push-Location $PluginRoot
        try {
            & dotnet test --nologo -v q --no-build -c Release
            if ($LASTEXITCODE -ne 0) {
                Write-Host "Tests failed. Retrying once..."
                $testRetried = $true
                & dotnet test --nologo -v q --no-build -c Release
                if ($LASTEXITCODE -ne 0) {
                    $testDuration = [math]::Round(((Get-Date) - $testStart).TotalSeconds, 1)
                    Push-LokiEvent 'deploy_tests_result' 'ERROR' 'Unit tests failed after retry' @{
                        test_type  = 'unit'
                        status     = 'failed'
                        retried    = $true
                        duration_s = $testDuration
                    }
                    throw "Tests failed after retry. Deploy aborted - 100% pass required."
                }
            }
        } finally { Pop-Location }
        $testDuration = [math]::Round(((Get-Date) - $testStart).TotalSeconds, 1)
        Write-Host "All unit tests passed."
        Push-LokiEvent 'deploy_tests_result' 'INFO' "Unit tests passed in ${testDuration}s" @{
            test_type  = 'unit'
            status     = 'ok'
            retried    = $testRetried
            duration_s = $testDuration
        }
    } else {
        Write-Host "No test projects found; skipping unit tests."
    }
} else {
    Write-Host "Skipping tests (SIMSTEWARD_SKIP_TESTS=1)."
    Push-LokiEvent 'deploy_tests_result' 'INFO' 'Unit tests skipped' @{
        test_type = 'unit'
        status    = 'skipped'
    }
}

# ── 1. Check if SimHub is open; if open, close (force) ───────────────────────
$running = @(Get-Process -Name "SimHubWPF" -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host "SimHub is running. Closing (force)..."
    Push-LokiEvent 'deploy_simhub_stopping' 'INFO' "Killing SimHub ($($running.Count) process(es))" @{
        pid_list = ($running | ForEach-Object { $_.Id }) -join ','
    }
    foreach ($p in $running) {
        try {
            $p.Kill()
            Write-Host "  Killed PID $($p.Id)"
        } catch {
            Write-Host "  Could not kill PID $($p.Id): $_"
        }
    }
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        $still = @(Get-Process -Name "SimHubWPF" -ErrorAction SilentlyContinue)
        if ($still.Count -eq 0) { break }
        Start-Sleep -Milliseconds 500
    }
    $still = @(Get-Process -Name "SimHubWPF" -ErrorAction SilentlyContinue)
    if ($still.Count -gt 0) {
        Push-LokiEvent 'deploy_failed' 'ERROR' 'SimHub did not exit after 15s'
        Write-Error "SimHub did not exit after 15s. Close it manually and re-run deploy."
    }
    Write-Host "SimHub closed."
    Push-LokiEvent 'deploy_simhub_stopped' 'INFO' 'SimHub process stopped'
} else {
    Write-Host "SimHub was not running."
    Push-LokiEvent 'deploy_simhub_stopped' 'INFO' 'SimHub was not running'
}

# ── 2. Delete existing plugin files in target location ───────────────────────
Write-Host "Removing existing plugin DLLs from $SimHubPath ..."
$deletedDlls = @()
foreach ($d in $PluginDlls) {
    $target = Join-Path $SimHubPath $d
    if (Test-Path $target) {
        Remove-Item $target -Force
        Write-Host "  Deleted: $d"
        $deletedDlls += $d
    }
}
Push-LokiEvent 'deploy_dlls_cleaned' 'INFO' "Removed $($deletedDlls.Count) existing DLLs" @{
    deleted = $deletedDlls -join ','
}

# ── 3. Copy build files to target location ──────────────────────────────────
function Copy-DeployDlls {
    foreach ($d in $PluginDlls) {
        $src = Join-Path $outDir $d
        if (-not (Test-Path $src)) { throw "Build output missing: $src" }
        Copy-Item $src $SimHubPath -Force
    }
}
Write-Host "Copying DLLs to $SimHubPath ..."
Copy-DeployDlls
foreach ($d in $PluginDlls) { Write-Host "  $d" }
Push-LokiEvent 'deploy_dlls_copied' 'INFO' "Copied $($PluginDlls.Count) DLLs to SimHub" @{
    dlls       = $PluginDlls -join ','
    target_dir = $SimHubPath
}

Write-Host "Copying dashboard to $DashboardTargetDir ..."
$dashboardSrcDir = Join-Path $PluginRoot "src\SimSteward.Dashboard"
$dashboardTargetFile = Join-Path $DashboardTargetDir "index.html"
Copy-Item $DashboardSource $dashboardTargetFile -Force
Write-Host "  index.html"
$copiedDashboards = @("index.html")
foreach ($f in Get-ChildItem -Path $dashboardSrcDir -File -ErrorAction SilentlyContinue) {
    if ($f.Extension -eq ".html" -and $f.Name -ne "index.html") {
        Copy-Item $f.FullName (Join-Path $DashboardTargetDir $f.Name) -Force
        Write-Host "  $($f.Name)"
        $copiedDashboards += $f.Name
    }
}
$readmeSource = Join-Path $dashboardSrcDir "README.txt"
if (Test-Path $readmeSource) {
    Copy-Item $readmeSource (Join-Path $DashboardTargetDir "README.txt") -Force
    Write-Host "  README.txt"
}
if (-not (Test-Path $dashboardTargetFile)) {
    Push-LokiEvent 'deploy_failed' 'ERROR' 'Dashboard copy failed' @{ target = $dashboardTargetFile }
    Write-Error "Dashboard copy failed."
}
Push-LokiEvent 'deploy_dashboard_copied' 'INFO' "Copied $($copiedDashboards.Count) dashboard files" @{
    dashboards = $copiedDashboards -join ','
    target_dir = $DashboardTargetDir
}

# ── 4. Confirm new copy is deployed; if not, retry once ──────────────────────
function Test-DeploySuccess {
    $ok = $true
    foreach ($d in $PluginDlls) {
        $target = Join-Path $SimHubPath $d
        if (-not (Test-Path $target)) { Write-Host "  Missing: $d"; $ok = $false }
        elseif ((Get-Item $target).Length -eq 0) { Write-Host "  Empty: $d"; $ok = $false }
    }
    $dashSrc = Join-Path $PluginRoot "src\SimSteward.Dashboard"
    foreach ($html in Get-ChildItem -Path $dashSrc -Filter "*.html" -File -ErrorAction SilentlyContinue) {
        $t = Join-Path $DashboardTargetDir $html.Name
        if (-not (Test-Path $t)) { Write-Host "  Missing dashboard: $($html.Name)"; $ok = $false }
        elseif ((Get-Item $t).Length -eq 0) { Write-Host "  Empty dashboard: $($html.Name)"; $ok = $false }
    }
    return $ok
}

Write-Host "Verifying deploy..."
$verifyRetried = $false
if (-not (Test-DeploySuccess)) {
    Write-Host "Deploy verification failed. Retrying copy once..."
    $verifyRetried = $true
    Copy-DeployDlls
    if (-not (Test-DeploySuccess)) {
        Push-LokiEvent 'deploy_verified' 'ERROR' 'Verification failed after retry' @{
            status  = 'failed'
            retried = $true
        }
        Write-Error "Deploy failed after retry. Check permissions and disk space."
    }
}
Write-Host "Deploy verified."
Push-LokiEvent 'deploy_verified' 'INFO' 'All files verified in target' @{
    status  = 'ok'
    retried = $verifyRetried
}

$deployedPluginDll = Join-Path $SimHubPath "SimSteward.Plugin.dll"
$script:SimStewardPluginVersionDeployed = Read-PluginDllProductVersion $deployedPluginDll
Write-Host ""
if (-not [string]::IsNullOrWhiteSpace($script:SimStewardPluginVersionDeployed)) {
    Write-Host "=== SimSteward plugin version (deployed): $($script:SimStewardPluginVersionDeployed) ===" -ForegroundColor Cyan
    Push-LokiEvent 'deploy_version_resolved' 'INFO' "Plugin version: $($script:SimStewardPluginVersionDeployed)" @{
        plugin_version = $script:SimStewardPluginVersionDeployed
    }
} else {
    Write-Warning "Could not read ProductVersion from SimSteward.Plugin.dll after deploy."
    Push-LokiEvent 'deploy_version_resolved' 'WARN' 'Could not read plugin version from DLL' @{
        plugin_version = 'unknown'
    }
}
Write-Host ""

# ── Sentry release + deploy tracking ────────────────────────────────────────
$sentryOrg = 'sim-steward'
$sentryProjects = @('simhub-plugin', 'web-dashboards')
$sentryAuthToken = if (-not [string]::IsNullOrWhiteSpace($env:SENTRY_AUTH_TOKEN)) { $env:SENTRY_AUTH_TOKEN }
                   elseif (-not [string]::IsNullOrWhiteSpace($env:SENTRY_ELEVATED_API_KEY)) { $env:SENTRY_ELEVATED_API_KEY }
                   else { $null }
$sentryRelease = if (-not [string]::IsNullOrWhiteSpace($script:SimStewardPluginVersionDeployed)) { $script:SimStewardPluginVersionDeployed } else { $null }

function Parse-SentryDsn {
    param([string]$Dsn)
    # https://<public_key>@<ingest_domain>/<project_id>
    if ($Dsn -match '^https://([^@]+)@([^/]+)/(\d+)$') {
        return @{
            PublicKey     = $Matches[1]
            IngestDomain  = $Matches[2]
            ProjectId     = $Matches[3]
        }
    }
    return $null
}

$script:sentryDsn = Parse-SentryDsn $env:SIMSTEWARD_SENTRY_DSN

function Push-SentryApi {
    param([string]$Path, [hashtable]$Body)
    if ([string]::IsNullOrWhiteSpace($sentryAuthToken) -or [string]::IsNullOrWhiteSpace($sentryRelease)) { return }
    try {
        $url = "https://sentry.io/api/0/organizations/$sentryOrg/$Path"
        $json = $Body | ConvertTo-Json -Compress -Depth 5
        $headers = @{ Authorization = "Bearer $sentryAuthToken"; 'Content-Type' = 'application/json' }
        Invoke-RestMethod -Uri $url -Method Post -Headers $headers -Body $json -ErrorAction Stop | Out-Null
    } catch {
        # Non-fatal: deploy must not fail because Sentry API is down
        Write-Warning "Sentry API ($Path): $($_.Exception.Message)"
    }
}

function Push-SentryCheckIn {
    param([string]$MonitorSlug, [string]$Status, [string]$CheckInId)
    if (-not $script:sentryDsn) { return $null }
    try {
        $baseUrl = "https://$($script:sentryDsn.IngestDomain)/api/$($script:sentryDsn.ProjectId)/cron/$MonitorSlug/$($script:sentryDsn.PublicKey)/"
        $headers = @{ 'Content-Type' = 'application/json' }
        if ([string]::IsNullOrWhiteSpace($CheckInId)) {
            # Initial check-in: POST with monitor_config for upsert/auto-creation
            $body = @{
                status         = $Status
                monitor_config = @{
                    schedule        = @{ type = 'interval'; value = 1; unit = 'day' }
                    checkin_margin  = 5
                    max_runtime     = 10
                    timezone        = 'UTC'
                }
            }
            $json = $body | ConvertTo-Json -Compress -Depth 5
            $resp = Invoke-RestMethod -Uri $baseUrl -Method Post -Headers $headers -Body $json -ErrorAction Stop
            $newId = $resp.id
            Push-LokiEvent 'deploy_sentry_checkin' 'INFO' "Sentry cron check-in started: $MonitorSlug" @{
                monitor_slug = $MonitorSlug
                status       = $Status
                checkin_id   = $newId
            }
            return $newId
        } else {
            # Completion check-in: POST with check_in_id to update existing
            $url = "${baseUrl}?check_in_id=$CheckInId"
            $body = @{ status = $Status }
            $json = $body | ConvertTo-Json -Compress -Depth 5
            Invoke-RestMethod -Uri $url -Method Post -Headers $headers -Body $json -ErrorAction Stop | Out-Null
            Push-LokiEvent 'deploy_sentry_checkin' 'INFO' "Sentry cron check-in completed: $MonitorSlug ($Status)" @{
                monitor_slug = $MonitorSlug
                status       = $Status
                checkin_id   = $CheckInId
            }
            return $null
        }
    } catch {
        # Non-fatal: deploy must not fail because Sentry API is down
        Write-Warning "Sentry CheckIn ($MonitorSlug/$Status): $($_.Exception.Message)"
        return $null
    }
}

if (-not [string]::IsNullOrWhiteSpace($sentryAuthToken) -and -not [string]::IsNullOrWhiteSpace($sentryRelease)) {
    Write-Host "Registering Sentry release: $sentryRelease (3 projects)"

    # Create release across all 3 projects with commits
    $fullSha = ''
    try { $fullSha = (& git -C $PluginRoot rev-parse HEAD 2>$null) } catch {}
    Push-SentryApi "releases/" @{
        version  = $sentryRelease
        projects = $sentryProjects
        ref      = $fullSha
    }

    # URL-encode release version for path segments ('+' in SemVer breaks URLs)
    $encodedRelease = [System.Uri]::EscapeDataString($sentryRelease)

    # Deploy: simhub-plugin (C# DLLs)
    Push-SentryApi "releases/$encodedRelease/deploys/" @{
        environment = 'local'
        name        = 'simhub-plugin'
    }

    # Deploy: web-dashboards (all HTML/JS dashboards)
    Push-SentryApi "releases/$encodedRelease/deploys/" @{
        environment = 'local'
        name        = 'web-dashboards'
    }

    Write-Host "Sentry release + 2 deploys registered (simhub-plugin, web-dashboards)."
    Push-LokiEvent 'deploy_sentry_release' 'INFO' "Sentry release registered: $sentryRelease" @{
        sentry_release  = $sentryRelease
        sentry_org      = $sentryOrg
        sentry_projects = ($sentryProjects -join ',')
    }
} elseif ([string]::IsNullOrWhiteSpace($sentryAuthToken)) {
    Write-Host "Skipping Sentry release tracking (SENTRY_AUTH_TOKEN not set)."
}

# ── 5. Re-launch SimHub ─────────────────────────────────────────────────────
$skipLaunch = $env:SIMHUB_SKIP_LAUNCH -eq "1"
if ($skipLaunch) {
    Write-Host "Skipping SimHub launch (SIMHUB_SKIP_LAUNCH=1). Plugin WebSocket server listens on port 19847 once SimHub starts."
    Push-LokiEvent 'deploy_simhub_launch' 'INFO' 'SimHub launch skipped' @{ status = 'skipped' }
} else {
    Write-Host "Launching SimHub..."
    Start-Process -FilePath $SimHubExe -WorkingDirectory $SimHubPath
    Write-Host "Done. Dashboard: http://localhost:8888/Web/sim-steward-dash/index.html (Web Page component) | WebSocket: $(if ($env:SIMSTEWARD_WS_BIND) { $env:SIMSTEWARD_WS_BIND } else { '127.0.0.1' }):$(if ($env:SIMSTEWARD_WS_PORT) { $env:SIMSTEWARD_WS_PORT } else { '19847' })"
    Push-LokiEvent 'deploy_simhub_launch' 'INFO' 'SimHub process started' @{
        status     = 'launched'
        simhub_exe = $SimHubExe
    }
}

# ── 6. Post-deploy tests ───────────────────────────────────────────────────
$postDeployFailed = $false
if (-not $skipTests) {
    $testsDir = Join-Path $PluginRoot "tests"
    $testScripts = @()
    if (Test-Path $testsDir) {
        $testScripts = @(Get-ChildItem -Path $testsDir -Filter "*.ps1" -File)
    }
    if ($testScripts.Count -gt 0) {
        $simHubRunning = @(Get-Process -Name "SimHubWPF" -ErrorAction SilentlyContinue).Count -gt 0
        if (-not $simHubRunning -and -not $skipLaunch) {
            Write-Host "Waiting for SimHub to start before post-deploy tests..."
            $waitDeadline = (Get-Date).AddSeconds(30)
            while ((Get-Date) -lt $waitDeadline) {
                Start-Sleep -Milliseconds 2000
                $simHubRunning = @(Get-Process -Name "SimHubWPF" -ErrorAction SilentlyContinue).Count -gt 0
                if ($simHubRunning) { break }
            }
        }
        if ($simHubRunning) {
            Write-Host "Running post-deploy tests ($($testScripts.Count) script(s))..."
            Push-LokiEvent 'deploy_post_tests_started' 'INFO' "Running $($testScripts.Count) post-deploy test(s)" @{
                test_type    = 'post-deploy'
                script_count = $testScripts.Count
                scripts      = ($testScripts | ForEach-Object { $_.Name }) -join ','
            }
            $checkInId = Push-SentryCheckIn 'post-deploy-tests' 'in_progress'
            foreach ($ts in $testScripts) {
                Write-Host "  Running: $($ts.Name)"
                $tsStart = Get-Date
                & pwsh -NoProfile -File $ts.FullName
                if ($LASTEXITCODE -ne 0) {
                    Write-Host "  FAIL: $($ts.Name) (exit code $LASTEXITCODE). Retrying once..."
                    Push-LokiEvent 'deploy_post_test_result' 'WARN' "$($ts.Name) failed, retrying" @{
                        test_type  = 'post-deploy'
                        script     = $ts.Name
                        status     = 'failed_will_retry'
                        exit_code  = $LASTEXITCODE
                    }
                    Start-Sleep -Milliseconds 3000
                    & pwsh -NoProfile -File $ts.FullName
                    $tsDuration = [math]::Round(((Get-Date) - $tsStart).TotalSeconds, 1)
                    if ($LASTEXITCODE -ne 0) {
                        Write-Host "  FAIL: $($ts.Name) failed after retry."
                        $postDeployFailed = $true
                        Push-LokiEvent 'deploy_post_test_result' 'ERROR' "$($ts.Name) failed after retry" @{
                            test_type  = 'post-deploy'
                            script     = $ts.Name
                            status     = 'failed'
                            retried    = $true
                            exit_code  = $LASTEXITCODE
                            duration_s = $tsDuration
                        }
                    } else {
                        Write-Host "  PASS: $($ts.Name) (passed on retry)"
                        Push-LokiEvent 'deploy_post_test_result' 'INFO' "$($ts.Name) passed on retry" @{
                            test_type  = 'post-deploy'
                            script     = $ts.Name
                            status     = 'ok'
                            retried    = $true
                            duration_s = $tsDuration
                        }
                    }
                } else {
                    $tsDuration = [math]::Round(((Get-Date) - $tsStart).TotalSeconds, 1)
                    Write-Host "  PASS: $($ts.Name)"
                    Push-LokiEvent 'deploy_post_test_result' 'INFO' "$($ts.Name) passed" @{
                        test_type  = 'post-deploy'
                        script     = $ts.Name
                        status     = 'ok'
                        retried    = $false
                        duration_s = $tsDuration
                    }
                }
            }
            $checkInStatus = if ($postDeployFailed) { 'error' } else { 'ok' }
            Push-SentryCheckIn 'post-deploy-tests' $checkInStatus $checkInId | Out-Null
            if ($postDeployFailed) {
                Write-Warning "Post-deploy tests failed. Deploy files are in place but integration is not fully verified."
            } else {
                Write-Host "All post-deploy tests passed."
            }
        } else {
            Write-Host "SimHub not running; skipping post-deploy tests (run tests\*.ps1 manually after starting SimHub)."
            Push-LokiEvent 'deploy_post_tests_started' 'WARN' 'Post-deploy tests skipped - SimHub not running' @{
                test_type = 'post-deploy'
                status    = 'skipped'
                reason    = 'simhub_not_running'
            }
        }
    }
}

# SimHub serves /Web/... on its own HTTP port (default 8888). Deploy only copies files; binding is SimHub's job.
if (-not $skipLaunch) {
    Start-Sleep -Seconds 6
    if (@(Get-Process -Name "SimHubWPF" -ErrorAction SilentlyContinue).Count -gt 0) {
        try {
            $probe8888 = Test-NetConnection -ComputerName 127.0.0.1 -Port 8888 -WarningAction SilentlyContinue
            if ($probe8888.TcpTestSucceeded) {
                Push-LokiEvent 'deploy_port_probe' 'INFO' 'Port 8888 is listening' @{
                    port   = 8888
                    status = 'ok'
                }
            } else {
                Write-Host ""
                Write-Warning "Port 8888 is not accepting connections. HTML is deployed under SimHub\Web\sim-steward-dash\ but SimHub's built-in web server is not listening. In SimHub: check Settings for HTTP/web port (default 8888), open Dash Studio or http://127.0.0.1:8888/ after startup, firewall. See docs/TROUBLESHOOTING.md (section 3b)."
                Push-LokiEvent 'deploy_port_probe' 'WARN' 'Port 8888 not listening' @{
                    port   = 8888
                    status = 'not_listening'
                }
            }
        } catch {
            Push-LokiEvent 'deploy_port_probe' 'WARN' "Port 8888 probe error: $($_.Exception.Message)" @{
                port   = 8888
                status = 'error'
            }
        }
    }
}

# ── EVENT: deploy_completed ──────────────────────────────────────────────────
$totalDuration = [math]::Round(([DateTimeOffset]::UtcNow - $script:deployStartUtc).TotalSeconds, 1)
$finalStatus = if ($postDeployFailed) { 'completed_with_warnings' } else { 'ok' }
$finalLevel = if ($postDeployFailed) { 'WARN' } else { 'INFO' }
$pvOut = if ([string]::IsNullOrWhiteSpace($script:SimStewardPluginVersionDeployed)) { "(unknown)" } else { $script:SimStewardPluginVersionDeployed }

Push-LokiEvent 'deploy_completed' $finalLevel "Deploy finished in ${totalDuration}s - $finalStatus" @{
    status             = $finalStatus
    plugin_version     = $pvOut
    post_deploy_warn   = $postDeployFailed
    duration_s         = $totalDuration
    git_branch         = $gitBranch
    git_sha            = $gitSha
    simhub_path        = $SimHubPath
}

Write-Host "Deploy complete. Plugin version: $pvOut (${totalDuration}s, $($finalStatus))"
