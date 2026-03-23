# Deploy Sim Steward plugin (skeleton) to local SimHub.
# Run from plugin/:  .\deploy.ps1 [-EnvFile path\to\secrets.env]
# Requires: SimHub installed; place SimHub.Plugins.dll and GameReaderCommon.dll in lib\SimHub\ (or script copies from SimHub path).

param(
    [string]$EnvFile = ""
)

$ErrorActionPreference = "Stop"
$PluginRoot = $PSScriptRoot

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

# Deploy marker → Grafana Explore (Loki). Default local stack when unset; avoid template Cloud URL without creds (530).
$localLoki = "http://localhost:3100"
$deployMarkerLocal = $false
if ([string]::IsNullOrWhiteSpace($env:SIMSTEWARD_LOKI_URL)) {
    $env:SIMSTEWARD_LOKI_URL = $localLoki
    $deployMarkerLocal = $true
    Write-Host "Loki deploy log: SIMSTEWARD_LOKI_URL was unset — using $localLoki (start stack: npm run obs:up)."
} elseif ($env:SIMSTEWARD_LOKI_URL -match 'grafana\.net') {
    $hasCloudBasic = -not [string]::IsNullOrWhiteSpace($env:SIMSTEWARD_LOKI_USER) -and -not [string]::IsNullOrWhiteSpace($env:SIMSTEWARD_LOKI_TOKEN)
    if (-not $hasCloudBasic) {
        Write-Warning "SIMSTEWARD_LOKI_URL is Grafana Cloud but SIMSTEWARD_LOKI_USER / SIMSTEWARD_LOKI_TOKEN missing — using $localLoki for deploy marker. Set both for Cloud."
        $env:SIMSTEWARD_LOKI_URL = $localLoki
        $deployMarkerLocal = $true
    }
}
if ([string]::IsNullOrWhiteSpace($env:SIMSTEWARD_LOG_ENV)) { $env:SIMSTEWARD_LOG_ENV = "local" }
if ($deployMarkerLocal) { $env:SIMSTEWARD_LOG_ENV = "local" }

$PluginDlls = @(
    "SimSteward.Plugin.dll",
    "Fleck.dll",
    "Newtonsoft.Json.dll",
    "IRSDKSharper.dll",
    "YamlDotNet.dll"
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

# Populated after DLL copy + verify (AssemblyInformationalVersion → PE ProductVersion)
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
    Write-Error "SimHub not found at: $SimHubExe. Set SIMHUB_PATH to your SimHub folder."
}
Write-Host "SimHub path: $SimHubPath"
$DashboardSource = Join-Path $PluginRoot "src\SimSteward.Dashboard\index.html"
if (-not (Test-Path $DashboardSource)) {
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
Push-Location $PluginRoot
try {
    & dotnet build "src\SimSteward.Plugin\SimSteward.Plugin.csproj" -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
} finally { Pop-Location }
Write-Host "Build succeeded."

# Resolve build output folder
$outDir = Join-Path $PluginRoot "bin\Plugin"
if (-not (Test-Path (Join-Path $outDir "SimSteward.Plugin.dll"))) {
    $outDir = Join-Path $outDir "net48"
}
if (-not (Test-Path (Join-Path $outDir "SimSteward.Plugin.dll"))) {
    Write-Error "Build output not found. Expected SimSteward.Plugin.dll in bin\Plugin or bin\Plugin\net48"
}

# ── Run unit tests (if test projects exist) ─────────────────────────────────
$skipTests = $env:SIMSTEWARD_SKIP_TESTS -eq "1"
if (-not $skipTests) {
    $testProjects = @(Get-ChildItem -Path $PluginRoot -Recurse -Filter "*.csproj" |
        Where-Object { $_.Name -match "test" -or $_.Directory.Name -match "test" })
    if ($testProjects.Count -gt 0) {
        Write-Host "Running unit tests..."
        Push-Location $PluginRoot
        try {
            & dotnet test --nologo -v q --no-build -c Release
            if ($LASTEXITCODE -ne 0) {
                Write-Host "Tests failed. Retrying once..."
                & dotnet test --nologo -v q --no-build -c Release
                if ($LASTEXITCODE -ne 0) {
                    throw "Tests failed after retry. Deploy aborted — 100% pass required."
                }
            }
        } finally { Pop-Location }
        Write-Host "All unit tests passed."
    } else {
        Write-Host "No test projects found; skipping unit tests."
    }
} else {
    Write-Host "Skipping tests (SIMSTEWARD_SKIP_TESTS=1)."
}

# ── 1. Check if SimHub is open; if open, close (force) ───────────────────────
$running = @(Get-Process -Name "SimHubWPF" -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Host "SimHub is running. Closing (force)..."
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
        Write-Error "SimHub did not exit after 15s. Close it manually and re-run deploy."
    }
    Write-Host "SimHub closed."
} else {
    Write-Host "SimHub was not running."
}

# ── 2. Delete existing plugin files in target location ───────────────────────
Write-Host "Removing existing plugin DLLs from $SimHubPath ..."
foreach ($d in $PluginDlls) {
    $target = Join-Path $SimHubPath $d
    if (Test-Path $target) {
        Remove-Item $target -Force
        Write-Host "  Deleted: $d"
    }
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

Write-Host "Copying dashboard to $DashboardTargetDir ..."
$dashboardTargetFile = Join-Path $DashboardTargetDir "index.html"
Copy-Item $DashboardSource $dashboardTargetFile -Force
Write-Host "  index.html"
$DashboardReplaySource = Join-Path $PluginRoot "src\SimSteward.Dashboard\replay-incident-index.html"
if (Test-Path $DashboardReplaySource) {
    Copy-Item $DashboardReplaySource (Join-Path $DashboardTargetDir "replay-incident-index.html") -Force
    Write-Host "  replay-incident-index.html"
}
$readmeSource = Join-Path $PluginRoot "src\SimSteward.Dashboard\README.txt"
if (Test-Path $readmeSource) {
    Copy-Item $readmeSource (Join-Path $DashboardTargetDir "README.txt") -Force
    Write-Host "  README.txt"
}
if (-not (Test-Path $dashboardTargetFile)) {
    Write-Error "Dashboard copy failed."
}

# ── 4. Confirm new copy is deployed; if not, retry once ──────────────────────
function Test-DeploySuccess {
    $ok = $true
    foreach ($d in $PluginDlls) {
        $target = Join-Path $SimHubPath $d
        if (-not (Test-Path $target)) { Write-Host "  Missing: $d"; $ok = $false }
        elseif ((Get-Item $target).Length -eq 0) { Write-Host "  Empty: $d"; $ok = $false }
    }
    return $ok
}

Write-Host "Verifying deploy..."
if (-not (Test-DeploySuccess)) {
    Write-Host "Deploy verification failed. Retrying copy once..."
    Copy-DeployDlls
    if (-not (Test-DeploySuccess)) {
        Write-Error "Deploy failed after retry. Check permissions and disk space."
    }
}
Write-Host "Deploy verified."

$deployedPluginDll = Join-Path $SimHubPath "SimSteward.Plugin.dll"
$script:SimStewardPluginVersionDeployed = Read-PluginDllProductVersion $deployedPluginDll
Write-Host ""
if (-not [string]::IsNullOrWhiteSpace($script:SimStewardPluginVersionDeployed)) {
    Write-Host "=== SimSteward plugin version (deployed): $($script:SimStewardPluginVersionDeployed) ===" -ForegroundColor Cyan
} else {
    Write-Warning "Could not read ProductVersion from SimSteward.Plugin.dll after deploy."
}
Write-Host ""

# ── 5. Re-launch SimHub ─────────────────────────────────────────────────────
$skipLaunch = $env:SIMHUB_SKIP_LAUNCH -eq "1"
if ($skipLaunch) {
    Write-Host "Skipping SimHub launch (SIMHUB_SKIP_LAUNCH=1). Plugin WebSocket server listens on port 19847 once SimHub starts."
} else {
    Write-Host "Launching SimHub..."
    Start-Process -FilePath $SimHubExe -WorkingDirectory $SimHubPath
    Write-Host "Done. Dashboard: http://localhost:8888/Web/sim-steward-dash/index.html (Web Page component) | WebSocket: $(if ($env:SIMSTEWARD_WS_BIND) { $env:SIMSTEWARD_WS_BIND } else { '127.0.0.1' }):$(if ($env:SIMSTEWARD_WS_PORT) { $env:SIMSTEWARD_WS_PORT } else { '19847' })"
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
            foreach ($ts in $testScripts) {
                Write-Host "  Running: $($ts.Name)"
                & pwsh -NoProfile -File $ts.FullName
                if ($LASTEXITCODE -ne 0) {
                    Write-Host "  FAIL: $($ts.Name) (exit code $LASTEXITCODE). Retrying once..."
                    Start-Sleep -Milliseconds 3000
                    & pwsh -NoProfile -File $ts.FullName
                    if ($LASTEXITCODE -ne 0) {
                        Write-Host "  FAIL: $($ts.Name) failed after retry."
                        $postDeployFailed = $true
                    } else {
                        Write-Host "  PASS: $($ts.Name) (passed on retry)"
                    }
                } else {
                    Write-Host "  PASS: $($ts.Name)"
                }
            }
            if ($postDeployFailed) {
                Write-Warning "Post-deploy tests failed. Deploy files are in place but integration is not fully verified."
            } else {
                Write-Host "All post-deploy tests passed."
            }
        } else {
            Write-Host "SimHub not running; skipping post-deploy tests (run tests\*.ps1 manually after starting SimHub)."
        }
    }
}

# SimHub serves /Web/... on its own HTTP port (default 8888). Deploy only copies files; binding is SimHub's job.
if (-not $skipLaunch) {
    Start-Sleep -Seconds 6
    if (@(Get-Process -Name "SimHubWPF" -ErrorAction SilentlyContinue).Count -gt 0) {
        try {
            $probe8888 = Test-NetConnection -ComputerName 127.0.0.1 -Port 8888 -WarningAction SilentlyContinue
            if (-not $probe8888.TcpTestSucceeded) {
                Write-Host ""
                Write-Warning "Port 8888 is not accepting connections. HTML is deployed under SimHub\Web\sim-steward-dash\ but SimHub's built-in web server is not listening. In SimHub: check Settings for HTTP/web port (default 8888), open Dash Studio or http://127.0.0.1:8888/ after startup, firewall. See docs/TROUBLESHOOTING.md §3b."
            }
        } catch {
            # ignore probe errors
        }
    }
}

Write-Host "Recording deploy in Loki (Grafana Explore: {app=`"sim-steward`",env=`"$($env:SIMSTEWARD_LOG_ENV)`"} | json | event=`"deploy_marker`") ..."
$markerDetail = "deploy.ps1 finished"
if (-not [string]::IsNullOrWhiteSpace($script:SimStewardPluginVersionDeployed)) {
    $markerDetail += "; pluginVersion=$($script:SimStewardPluginVersionDeployed)"
}
& (Join-Path $PluginRoot "scripts\send-deploy-loki-marker.ps1") -Status ok -PostDeployWarning:$postDeployFailed -Detail $markerDetail -EnvFile $EnvFile

$pvOut = if ([string]::IsNullOrWhiteSpace($script:SimStewardPluginVersionDeployed)) { "(unknown)" } else { $script:SimStewardPluginVersionDeployed }
Write-Host "Deploy complete. Plugin version: $pvOut"
