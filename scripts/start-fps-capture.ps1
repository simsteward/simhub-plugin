<#
.SYNOPSIS
  Capture per-process frame timing/FPS system-wide via PresentMon, for correlating
  choppy sim racing sessions against what else was running on the desktop.

.DESCRIPTION
  Wraps PresentMon.exe (Intel's open-source capture CLI: github.com/GameTechDev/PresentMon).
  Records one CSV row per presented frame, per process (game, SimHub dashboard renderer,
  browser, etc.) with millisecond-resolution timing. Use the "Application" and "ProcessID"
  columns to compute FPS per program over time.

  Per-monitor breakdown: PresentMon rows are per-swapchain, not per-physical-display, so
  there is no direct "monitor name" column. To split by screen, map known process names to
  known monitor assignments (e.g. iRacingSim64DX11.exe -> Monitor 1) after capture.

.PARAMETER DurationMinutes
  Stop automatically after N minutes. Omit to capture until Ctrl+C.

.PARAMETER OutDir
  Where to write the timestamped CSV. Defaults to Documents\PresentMon-Captures.

.EXAMPLE
  .\scripts\start-fps-capture.ps1
  .\scripts\start-fps-capture.ps1 -DurationMinutes 180
#>
param(
    [int]$DurationMinutes = 0,
    [string]$OutDir = "$env:USERPROFILE\Documents\PresentMon-Captures",
    [string]$PresentMonPath = "$env:USERPROFILE\Tools\PresentMon\PresentMon.exe"
)

if (-not (Test-Path $PresentMonPath)) {
    Write-Error "PresentMon.exe not found at $PresentMonPath. Download from https://github.com/GameTechDev/PresentMon/releases"
    exit 1
}

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object System.Security.Principal.WindowsPrincipal($identity)).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Error "PresentMon needs an elevated (Administrator) PowerShell to start its capture session. Re-run this script from an admin terminal."
    exit 1
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$stamp = Get-Date -Format "yyyy-MM-dd_HHmmss"
$csvPath = Join-Path $OutDir "fps-capture_$stamp.csv"

$capArgs = @(
    "--output_file", $csvPath,
    "--qpc_time_ms",
    "--terminate_on_proc_exit"
)
if ($DurationMinutes -gt 0) {
    $capArgs += @("--timed", ($DurationMinutes * 60), "--terminate_after_timed")
}

Write-Output "Capturing all processes' frame timing to: $csvPath"
Write-Output "Stop with Ctrl+C (or it auto-stops after $DurationMinutes min if set)."
& $PresentMonPath @capArgs
Write-Output "Capture complete: $csvPath"
