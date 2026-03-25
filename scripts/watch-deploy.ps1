param(
    [int]$DebounceMs = 750,
    [string]$EnvFile = ""
)

$ErrorActionPreference = 'Stop'

$RepoRoot = (Get-Item -Path "$PSScriptRoot\..").FullName
$DeployScript = Join-Path $RepoRoot 'deploy.ps1'

if (-not (Test-Path $DeployScript)) {
    Write-Error "deploy.ps1 not found at $DeployScript"
}

$pendingDeploy = $false
$lastChangeTime = [DateTime]::UtcNow
$deployInProgress = $false
$queuedDeploy = $false
$stopRequested = $false

function Get-RelativePath($fullPath) {
    $normalizedFull = $fullPath.TrimEnd('\')
    $normalizedRoot = $RepoRoot.TrimEnd('\')
    if ($normalizedFull.Length -lt $normalizedRoot.Length) { return $null }
    if ($normalizedFull.Substring(0, $normalizedRoot.Length).Equals($normalizedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $normalizedFull.Substring($normalizedRoot.Length).TrimStart('\')
    }
    return $null
}

function ShouldIncludePath($relativePath) {
    if (-not $relativePath) { return $false }
    $relativePath = $relativePath.Replace('/', '\')
    if ($relativePath -match '(^|\\)(bin|obj|\.git)(\\|$)') { return $false }
    if ($relativePath -like 'src\SimSteward.Plugin\*') { return $true }
    if ($relativePath -like 'src\SimSteward.Dashboard\*') { return $true }
    $lower = $relativePath.ToLowerInvariant()
    if ($lower -eq 'deploy.ps1')   { return $true }
    if ($lower -eq 'scripts\watch-deploy.ps1') { return $true }
    return $false
}

function Queue-Deploy($triggeredPath) {
    if (-not (ShouldIncludePath $triggeredPath)) { return }
    $global:lastChangeTime = [DateTime]::UtcNow
    $global:pendingDeploy = $true
    Write-Host "[watch-deploy] Change detected: $triggeredPath"
}

function Invoke-Deploy {
    if ($deployInProgress) {
        $queuedDeploy = $true
        return
    }
    $deployInProgress = $true
    try {
        Write-Host '[watch-deploy] Starting deploy (SIMHUB_SKIP_LAUNCH=1)...'
        $env:SIMHUB_SKIP_LAUNCH = '0'
        Push-Location $RepoRoot
        try {
            if ([string]::IsNullOrWhiteSpace($EnvFile)) {
                & pwsh -NoProfile -File $DeployScript
            } else {
                & pwsh -NoProfile -File $DeployScript -EnvFile $EnvFile
            }
        } catch {
            Write-Host "[watch-deploy] Deploy failed: $($_.Exception.Message)"
        } finally {
            Pop-Location
        }
        Write-Host '[watch-deploy] Deploy run finished.'
    } finally {
        $deployInProgress = $false
        if ($queuedDeploy) {
            $queuedDeploy = $false
            Write-Host '[watch-deploy] Additional changes detected during deploy; rerunning.'
            Invoke-Deploy
        }
    }
}

$handlers = @()
$watchPaths = @(
    @{ Path = Join-Path $RepoRoot 'src\SimSteward.Plugin'; Filter = '*.*'; IncludeSubdirectories = $true },
    @{ Path = Join-Path $RepoRoot 'src\SimSteward.Dashboard'; Filter = '*.*'; IncludeSubdirectories = $true },
    @{ Path = $RepoRoot; Filter = 'deploy.ps1'; IncludeSubdirectories = $false }
)

function Register-Watcher($watchSpec, $suffix) {
    if (-not (Test-Path $watchSpec.Path)) {
        Write-Host "[watch-deploy] Path not found, skipping watcher: $($watchSpec.Path)"
        return
    }

    $watcher = New-Object System.IO.FileSystemWatcher $watchSpec.Path, $watchSpec.Filter
    $watcher.IncludeSubdirectories = $watchSpec.IncludeSubdirectories
    $watcher.NotifyFilter = [System.IO.NotifyFilters]'FileName, LastWrite, LastAccess, Size, CreationTime, DirectoryName'
    $watcher.EnableRaisingEvents = $true

    foreach ($event in @('Changed', 'Created', 'Deleted', 'Renamed')) {
        $id = "watch-deploy-$suffix-$event"
        $handlers += $id
        Register-ObjectEvent -InputObject $watcher -EventName $event -SourceIdentifier $id -Action {
            $fullPath = $Event.SourceEventArgs.FullPath
            $relative = Get-RelativePath $fullPath
            if (-not $relative) { return }
            Queue-Deploy $relative
        } | Out-Null
    }

    return $watcher
}

$watchers = @()
foreach ($spec in $watchPaths) {
    $watchers += Register-Watcher $spec ([System.Guid]::NewGuid().ToString('N'))
}

$cancelHandler = [ConsoleCancelEventHandler]{
    param($sender, $args)
    Write-Host '[watch-deploy] Ctrl+C received; exiting after current deploy.'
    $args.Cancel = $true
    $global:stopRequested = $true
}
[Console]::add_CancelKeyPress($cancelHandler)

Write-Host '[watch-deploy] Watching for changes. Press Ctrl+C to exit.'

try {
    while (-not $stopRequested) {
        if (-not $pendingDeploy) {
            Start-Sleep -Milliseconds 250
            continue
        }

        while (-not $stopRequested -and (([DateTime]::UtcNow - $lastChangeTime).TotalMilliseconds -lt $DebounceMs)) {
            Start-Sleep -Milliseconds 150
        }

        if ($stopRequested) { break }
        if ($pendingDeploy) {
            $pendingDeploy = $false
            Invoke-Deploy
        }
    }
} finally {
    foreach ($id in $handlers) {
        Unregister-Event -SourceIdentifier $id -ErrorAction SilentlyContinue
    }
    foreach ($watcher in $watchers) {
        if ($watcher) {
            $watcher.EnableRaisingEvents = $false
            $watcher.Dispose()
        }
    }
    [Console]::remove_CancelKeyPress($cancelHandler)
    Write-Host '[watch-deploy] Watcher exited.'
}
