param(
    [string]$SimHubDir = $env:SIMHUB_DIR
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($SimHubDir)) {
    $SimHubDir = "C:\Program Files (x86)\SimHub"
}

if (-not (Test-Path -LiteralPath $SimHubDir)) {
    throw "SimHub directory not found: $SimHubDir"
}

$pluginDir = Split-Path -Parent $PSScriptRoot
$linksDir = Join-Path $pluginDir ".simhub-sdk"

if (-not (Test-Path -LiteralPath $linksDir)) {
    New-Item -ItemType Directory -Path $linksDir | Out-Null
}

$assemblies = @(
    "SimHub.Plugins.dll",
    "GameReaderCommon.dll"
)

foreach ($assembly in $assemblies) {
    $targetPath = Join-Path $SimHubDir $assembly
    $linkPath = Join-Path $linksDir $assembly

    if (-not (Test-Path -LiteralPath $targetPath)) {
        throw "Required SimHub assembly missing: $targetPath"
    }

    if (Test-Path -LiteralPath $linkPath) {
        Remove-Item -LiteralPath $linkPath -Force
    }

    try {
        New-Item -ItemType HardLink -Path $linkPath -Target $targetPath | Out-Null
        Write-Host "Linked (HardLink) $assembly -> $targetPath"
        continue
    }
    catch {
        Write-Host "HardLink failed for $assembly, trying SymbolicLink..."
    }

    try {
        New-Item -ItemType SymbolicLink -Path $linkPath -Target $targetPath | Out-Null
        Write-Host "Linked (SymbolicLink) $assembly -> $targetPath"
    }
    catch {
        throw "Failed to create link for $assembly. HardLink and SymbolicLink both failed. Run terminal as Administrator or enable Developer Mode."
    }
}

Write-Host "Done. Local SDK links created in: $linksDir"