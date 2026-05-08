# Phase 4 Test Rig — full reset orchestrator.
#
# 1. stop-all.ps1   ([-IncludeSteam])
# 2. sleep 2s
# 3. load-replay.ps1 -SubSessionId  (Steam handoff handles iRacing launch)
# 4. (load-replay already waits for IRSDK)
# 5. Start SimHubWPF.exe (auto-attaches to fresh IRSDK)
# 6. Wait 5s for SimHub plugin init
# 7. test_rig_reset_complete with total_ms

[CmdletBinding()]
param(
    [Parameter(Mandatory)][int]$SubSessionId,
    [switch]$IncludeSteam,
    [string]$SimHubExe = 'C:\Program Files (x86)\SimHub\SimHubWPF.exe',
    [int]$SimHubInitWaitSec = 5
)

$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot '_send-log.ps1')

$swTotal = [System.Diagnostics.Stopwatch]::StartNew()
$actualId = $null

Send-TestRigLog -Event 'test_rig_reset_started' -Level 'INFO' `
    -Fields @{ sub_session_id = $SubSessionId; include_steam = [bool]$IncludeSteam }

function Fail-Reset {
    param([string]$Phase, [string]$ErrorMessage)
    Send-TestRigLog -Event 'test_rig_reset_failed' -Level 'ERROR' `
        -Fields @{ sub_session_id = $SubSessionId; phase = $Phase; error = $ErrorMessage }
    exit 1
}

# 1. stop-all
$stopArgs = @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'stop-all.ps1'))
if ($IncludeSteam) { $stopArgs += '-IncludeSteam' }
$stop = Start-Process -FilePath 'pwsh.exe' -ArgumentList $stopArgs -NoNewWindow -PassThru -Wait
if ($stop.ExitCode -ne 0) {
    Fail-Reset -Phase 'stop_all' -ErrorMessage "stop-all.ps1 exited with code $($stop.ExitCode)"
}

# 2. settle
Start-Sleep -Seconds 2

# 3. load-replay
$loadArgs = @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'load-replay.ps1'),
              '-SubSessionId', $SubSessionId)
$load = Start-Process -FilePath 'pwsh.exe' -ArgumentList $loadArgs -NoNewWindow -PassThru -Wait
if ($load.ExitCode -ne 0) {
    Fail-Reset -Phase 'load_replay' -ErrorMessage "load-replay.ps1 exited with code $($load.ExitCode)"
}

# load-replay already waited for IRSDK readiness; re-read SessionInfo to capture actual id
# directly here so reset_complete has the value (load-replay logs it but doesn't return it).
try {
    $mmf = [System.IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting('Local\IRSDKMemMapFileName')
    $accessor = $mmf.CreateViewAccessor(0, 1MB, [System.IO.MemoryMappedFiles.MemoryMappedFileAccess]::Read)
    $sessionInfoLen    = $accessor.ReadInt32(16)
    $sessionInfoOffset = $accessor.ReadInt32(20)
    if ($sessionInfoLen -gt 0 -and $sessionInfoOffset -gt 0 -and $sessionInfoLen -lt (8 * 1024 * 1024)) {
        $bytes = New-Object byte[] $sessionInfoLen
        $accessor.ReadArray($sessionInfoOffset, $bytes, 0, $sessionInfoLen) | Out-Null
        $yaml = [System.Text.Encoding]::UTF8.GetString($bytes).TrimEnd([char]0)
        if ($yaml -match '(?ms)^WeekendInfo:\s*\r?\n(?<body>(?:[ \t].*\r?\n?)+)' -and
            $Matches['body'] -match '(?m)^\s*SubSessionID:\s*(?<id>\d+)') {
            $actualId = [int]$Matches['id']
        }
    }
    $accessor.Dispose(); $mmf.Dispose()
} catch {
    # Non-fatal — load-replay already reported the canonical state.
}

# 5. SimHub
if (-not (Test-Path -LiteralPath $SimHubExe)) {
    Fail-Reset -Phase 'simhub_launch' -ErrorMessage "SimHubWPF.exe not found at $SimHubExe"
}
try {
    Start-Process -FilePath $SimHubExe -ErrorAction Stop | Out-Null
} catch {
    Fail-Reset -Phase 'simhub_launch' -ErrorMessage $_.Exception.Message
}

# 6. SimHub plugin init grace period
Start-Sleep -Seconds $SimHubInitWaitSec

$swTotal.Stop()
Send-TestRigLog -Event 'test_rig_reset_complete' -Level 'INFO' `
    -Fields @{
        sub_session_id        = $SubSessionId
        actual_sub_session_id = $actualId
        total_ms              = $swTotal.ElapsedMilliseconds
    }
