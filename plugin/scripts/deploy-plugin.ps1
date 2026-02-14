param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = 'Stop'

$pluginDir = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $pluginDir "SimSteward.csproj"
$outputDllCandidates = @(
    (Join-Path $pluginDir "bin\$Configuration\net48\SimStewardPlugin.dll"),
    (Join-Path $pluginDir "bin\$Configuration\SimStewardPlugin.dll")
)
$simHubPluginsDir = "C:\Program Files (x86)\SimHub\PluginsData"
$simHubRootDir = "C:\Program Files (x86)\SimHub"
$destinationDll = Join-Path $simHubPluginsDir "SimStewardPlugin.dll"
$destinationRootDll = Join-Path $simHubRootDir "SimStewardPlugin.dll"
$pluginsActivationPath = Join-Path $simHubPluginsDir "PluginsActivation.json"
$simHubExePath = "C:\Program Files (x86)\SimHub\SimHubWPF.exe"

$simHubProcesses = @("SimHubWPF", "SimHub")
$runningSimHub = Get-Process -Name $simHubProcesses -ErrorAction SilentlyContinue
if ($runningSimHub) {
    Write-Host "Closing SimHub before deployment..."

    foreach ($proc in $runningSimHub) {
        try {
            if ($proc.MainWindowHandle -ne 0) {
                $null = $proc.CloseMainWindow()
            }
        }
        catch {
        }
    }

    Start-Sleep -Seconds 2

    $stillRunning = Get-Process -Name $simHubProcesses -ErrorAction SilentlyContinue
    if ($stillRunning) {
        $stillRunning | Stop-Process -Force
        Start-Sleep -Seconds 1
    }

    $finalCheck = Get-Process -Name $simHubProcesses -ErrorAction SilentlyContinue
    if ($finalCheck) {
        throw "Could not close SimHub. Please close it manually and retry deployment."
    }
}

$verifyStopped = Get-Process -Name $simHubProcesses -ErrorAction SilentlyContinue
if ($verifyStopped) {
    throw "SimHub is still running. Deployment requires SimHub to be fully stopped."
}
Write-Host "SimHub confirmed stopped."

Write-Host "Building SimStewardPlugin ($Configuration)..."
$msbuild = Get-Command msbuild -ErrorAction SilentlyContinue
if ($msbuild) {
    & $msbuild.Path $projectPath /p:Configuration=$Configuration /nologo | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed via msbuild (Configuration=$Configuration)."
    }
}
else {
    dotnet build $projectPath -c $Configuration | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed via dotnet build (Configuration=$Configuration)."
    }
}

$outputDll = $outputDllCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

if (-not $outputDll) {
    throw "Build output not found in expected paths: $($outputDllCandidates -join ', '). Build first (Configuration=$Configuration)."
}

if (-not (Test-Path -LiteralPath $simHubPluginsDir)) {
    throw "SimHub plugin directory not found: $simHubPluginsDir"
}

if (-not (Test-Path -LiteralPath $simHubRootDir)) {
    throw "SimHub install directory not found: $simHubRootDir"
}

Copy-Item -LiteralPath $outputDll -Destination $destinationDll -Force
Copy-Item -LiteralPath $outputDll -Destination $destinationRootDll -Force
Write-Host "Deployed SimStewardPlugin.dll to: $destinationDll"
Write-Host "Deployed SimStewardPlugin.dll to: $destinationRootDll"

$sourceHash = (Get-FileHash -Path $outputDll -Algorithm SHA256).Hash
$destinationHash = (Get-FileHash -Path $destinationDll -Algorithm SHA256).Hash
$destinationRootHash = (Get-FileHash -Path $destinationRootDll -Algorithm SHA256).Hash
if ($sourceHash -ne $destinationHash -or $sourceHash -ne $destinationRootHash) {
    throw "Deployment verification failed: deployed DLL hash does not match built DLL."
}
Write-Host "Deployment verification passed (SHA256 hash match for both destinations)."

if (Test-Path -LiteralPath $pluginsActivationPath) {
    $activation = Get-Content $pluginsActivationPath -Raw | ConvertFrom-Json
    $entry = $activation | Where-Object { $_.ClassName -eq "SimStewardPlugin.SimStewardPlugin" }

    if (-not $entry) {
        $entry = [pscustomobject]@{
            ClassName = "SimStewardPlugin.SimStewardPlugin"
            IsEnabled = $true
            ShowInMainMenu = $true
            ShowInMainMenuPosition = 0
        }
        $activation = @($activation) + $entry
    }
    else {
        $entry.IsEnabled = $true
        $entry.ShowInMainMenu = $true
    }

    $activation | ConvertTo-Json -Depth 10 | Set-Content -Path $pluginsActivationPath -Encoding UTF8
    Write-Host "Ensured SimSteward plugin is enabled and visible in left menu."
}

if (-not (Test-Path -LiteralPath $simHubExePath)) {
    throw "SimHub executable not found: $simHubExePath"
}

Write-Host "Launching SimHub..."
Start-Process -FilePath $simHubExePath | Out-Null
Start-Sleep -Seconds 2

$runningAfterLaunch = Get-Process -Name $simHubProcesses -ErrorAction SilentlyContinue
if (-not $runningAfterLaunch) {
    throw "SimHub did not start after deployment. Please launch it manually."
}

Write-Host "SimHub started successfully."