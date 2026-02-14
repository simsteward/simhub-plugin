param(
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$copilotRoot = Join-Path $repoRoot '.copilot'
$cursorRoot = Join-Path $repoRoot '.cursor'

if (-not (Test-Path -LiteralPath $copilotRoot)) {
    throw "Missing source directory: $copilotRoot"
}

if (-not (Test-Path -LiteralPath $cursorRoot)) {
    New-Item -ItemType Directory -Path $cursorRoot | Out-Null
}

$copilotRootResolved = (Resolve-Path $copilotRoot).Path
$cursorRootResolved = (Resolve-Path $cursorRoot).Path

if (-not $CheckOnly) {
    Get-ChildItem $copilotRootResolved -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($copilotRootResolved.Length + 1)
        $dst = Join-Path $cursorRootResolved $rel
        $dstDir = Split-Path $dst -Parent
        if (-not (Test-Path -LiteralPath $dstDir)) {
            New-Item -ItemType Directory -Path $dstDir | Out-Null
        }
        Copy-Item -LiteralPath $_.FullName -Destination $dst -Force
    }
}

$mismatch = @()
Get-ChildItem $copilotRootResolved -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($copilotRootResolved.Length + 1)
    $dst = Join-Path $cursorRootResolved $rel

    if (-not (Test-Path -LiteralPath $dst)) {
        $mismatch += "MISSING\t$rel"
        return
    }

    $h1 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    $h2 = (Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash
    if ($h1 -ne $h2) {
        $mismatch += "DIFF\t$rel"
    }
}

$copilotFiles = Get-ChildItem $copilotRootResolved -Recurse -File | ForEach-Object {
    $_.FullName.Substring($copilotRootResolved.Length + 1)
}

$cursorExtras = Get-ChildItem $cursorRootResolved -Recurse -File | ForEach-Object {
    $_.FullName.Substring($cursorRootResolved.Length + 1)
} | Where-Object { $_ -notin $copilotFiles }

if ($mismatch.Count -eq 0) {
    Write-Host "OK: .cursor matches .copilot (SHA256 parity)."
}
else {
    Write-Host "Found mismatches:"
    $mismatch | ForEach-Object { Write-Host $_ }
    throw "Sync validation failed."
}

if ($cursorExtras.Count -gt 0) {
    Write-Host "Note: extra files in .cursor not present in .copilot:"
    $cursorExtras | ForEach-Object { Write-Host $_ }
}
else {
    Write-Host "No extra files in .cursor."
}

if ($CheckOnly) {
    Write-Host "Check-only mode complete."
}
else {
    Write-Host "Sync complete."
}
