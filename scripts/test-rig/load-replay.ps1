# Phase 4 Test Rig — replay loader API per docs/RULES-TestRig-Contract.md.
#
# Functions:
#   LoadReplay        -SubSessionId <int>
#   LoadReplayByPath  -Path <string>
#
# Both:
#   - Start-Process the .rpy (Windows file association → Steam → iRacing handles handoff)
#   - Poll IRSDK shared memory until ready (timeout 90s)
#   - Read SessionInfoYaml.WeekendInfo.SubSessionID via inline IRSDK header parse
#   - Emit replay_load_{started,success,mismatch,failed} via Send-TestRigLog
#
# When invoked as a script with -SubSessionId or -Path, runs the matching function
# and exits 1 on hard failure (file not found, IRSDK timeout). Mismatch exits 0.

[CmdletBinding(DefaultParameterSetName = 'ById')]
param(
    [Parameter(ParameterSetName = 'ById', Mandatory)]
    [int]$SubSessionId,

    [Parameter(ParameterSetName = 'ByPath', Mandatory)]
    [string]$Path,

    [int]$IrsdkTimeoutSec = 90
)

$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot '_send-log.ps1')

$ReplayFolder = 'C:\Users\winth\OneDrive\Documents\iRacing\replay'
$IrsdkHandle  = 'Local\IRSDKMemMapFileName'

function Test-IrsdkReady {
    try {
        $mmf = [System.IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($IrsdkHandle)
        $mmf.Dispose()
        return $true
    } catch { return $false }
}

# Wait for IRSDK to be queryable. Returns elapsed ms, or -1 on timeout.
function Wait-IrsdkReady {
    param([int]$TimeoutSec)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $deadline = $TimeoutSec * 1000
    while ($sw.ElapsedMilliseconds -lt $deadline) {
        if (Test-IrsdkReady) { return $sw.ElapsedMilliseconds }
        Start-Sleep -Milliseconds 500
    }
    return -1
}

# Read the IRSDK SessionInfo YAML block via shared-memory header parse.
# Header layout (IRSDKDefines.h): int ver, status, tickRate, sessionInfoUpdate,
#   sessionInfoLen, sessionInfoOffset, numVars, ...  All little-endian int32.
# Returns the YAML string, or $null on failure.
function Read-IrsdkSessionYaml {
    $mmf = $null; $accessor = $null
    try {
        $mmf = [System.IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($IrsdkHandle)
        # 1 MiB is enough for any SessionInfo block (typically <512 KiB).
        $accessor = $mmf.CreateViewAccessor(0, 1MB, [System.IO.MemoryMappedFiles.MemoryMappedFileAccess]::Read)
        $sessionInfoLen    = $accessor.ReadInt32(16)
        $sessionInfoOffset = $accessor.ReadInt32(20)
        if ($sessionInfoLen -le 0 -or $sessionInfoLen -gt (8 * 1024 * 1024)) { return $null }
        if ($sessionInfoOffset -le 0) { return $null }
        $bytes = New-Object byte[] $sessionInfoLen
        $accessor.ReadArray($sessionInfoOffset, $bytes, 0, $sessionInfoLen) | Out-Null
        # IRSDK YAML is ASCII/UTF-8; trim trailing NUL padding.
        $yaml = [System.Text.Encoding]::UTF8.GetString($bytes).TrimEnd([char]0)
        return $yaml
    } catch {
        return $null
    } finally {
        if ($accessor) { $accessor.Dispose() }
        if ($mmf)      { $mmf.Dispose() }
    }
}

# Pull WeekendInfo.SubSessionID from a YAML block. Tolerant of whitespace.
function Get-SubSessionIdFromYaml {
    param([string]$Yaml)
    if (-not $Yaml) { return $null }
    # WeekendInfo:\n ...  SubSessionID: 12345678 — only the WeekendInfo block.
    if ($Yaml -match '(?ms)^WeekendInfo:\s*\r?\n(?<body>(?:[ \t].*\r?\n?)+)') {
        $body = $Matches['body']
        if ($body -match '(?m)^\s*SubSessionID:\s*(?<id>\d+)') {
            return [int]$Matches['id']
        }
    }
    # Fallback: any top-level SubSessionID line.
    if ($Yaml -match '(?m)^\s*SubSessionID:\s*(?<id>\d+)') {
        return [int]$Matches['id']
    }
    return $null
}

# Public: extract subSessionId from .rpy filename via regex `subses(\d{7,9})`.
# Exported for unit testing — callable via `. load-replay.ps1; ParseSubSessionFromFilename ...`.
function ParseSubSessionFromFilename {
    param([string]$FileName)
    if ([string]::IsNullOrWhiteSpace($FileName)) { return $null }
    # Match `subses<7-9 digits>` followed by a non-digit boundary (`.`, `_`, `-`, end of string,
    # etc.). Plain `\b` won't work since `_` counts as a word character.
    if ($FileName -match '(?i)subses(?<id>\d{7,9})(?:\D|$)') { return [int]$Matches['id'] }
    return $null
}

# Glob the replay folder and pick the best match for $SubSessionId.
# Prefers exact `subses{id}.rpy`; falls back to embedded match.
function Find-ReplayFile {
    param([int]$SubSessionId)
    if (-not (Test-Path -LiteralPath $ReplayFolder)) { return $null }

    $candidates = Get-ChildItem -LiteralPath $ReplayFolder -Filter '*.rpy' -File -ErrorAction SilentlyContinue
    $tagged = foreach ($f in $candidates) {
        $id = ParseSubSessionFromFilename -FileName $f.Name
        if ($id) { [pscustomobject]@{ SubSessionId = $id; Path = $f.FullName; Name = $f.Name } }
    }

    $matches = @($tagged | Where-Object { $_.SubSessionId -eq $SubSessionId })
    if ($matches.Count -eq 0) { return $null }

    # Prefer exact `subses{id}.rpy` (no other glue around the id).
    $exact = $matches | Where-Object { $_.Name -match ('(?i)^subses' + $SubSessionId + '\.rpy$') } | Select-Object -First 1
    if ($exact) { return $exact.Path }
    return ($matches | Select-Object -First 1).Path
}

function Invoke-ReplayLoad {
    param(
        [string]$Mode,                 # 'by_id' | 'by_path'
        [int]$RequestedSubSessionId,   # 0 when by_path
        [string]$FilePath,
        [int]$IrsdkTimeoutSec
    )

    Send-TestRigLog -Event 'replay_load_started' -Level 'INFO' `
        -Fields @{
            requested_sub_session_id = $RequestedSubSessionId
            file_path                = $FilePath
            mode                     = $Mode
        }

    if (-not (Test-Path -LiteralPath $FilePath)) {
        Send-TestRigLog -Event 'replay_load_failed' -Level 'ERROR' `
            -Fields @{
                requested_sub_session_id = $RequestedSubSessionId
                file_path                = $FilePath
                error                    = 'file_not_found'
                phase                    = 'pre_launch'
            }
        return 1
    }

    try {
        Start-Process -FilePath $FilePath -ErrorAction Stop
    } catch {
        Send-TestRigLog -Event 'replay_load_failed' -Level 'ERROR' `
            -Fields @{
                requested_sub_session_id = $RequestedSubSessionId
                file_path                = $FilePath
                error                    = "start_process_failed: $($_.Exception.Message)"
                phase                    = 'launch'
            }
        return 1
    }

    $msToReady = Wait-IrsdkReady -TimeoutSec $IrsdkTimeoutSec
    if ($msToReady -lt 0) {
        Send-TestRigLog -Event 'replay_load_failed' -Level 'ERROR' `
            -Fields @{
                requested_sub_session_id = $RequestedSubSessionId
                file_path                = $FilePath
                error                    = 'irsdk_timeout'
                phase                    = 'irsdk_wait'
            }
        return 1
    }

    # Give iRacing a beat to fully populate SessionInfo after the handle appears.
    # WeekendInfo.SubSessionID lands in the first publish (~250 ms) but be generous.
    $yaml = $null
    $actualId = $null
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ([DateTime]::UtcNow -lt $deadline) {
        $yaml = Read-IrsdkSessionYaml
        $actualId = Get-SubSessionIdFromYaml -Yaml $yaml
        if ($actualId) { break }
        Start-Sleep -Milliseconds 500
    }

    if (-not $actualId) {
        Send-TestRigLog -Event 'replay_load_failed' -Level 'ERROR' `
            -Fields @{
                requested_sub_session_id = $RequestedSubSessionId
                file_path                = $FilePath
                error                    = 'session_yaml_unreadable'
                phase                    = 'yaml_parse'
                ms_to_irsdk_ready        = $msToReady
            }
        return 1
    }

    if ($Mode -eq 'by_path') {
        # No equality check — just record what loaded.
        Send-TestRigLog -Event 'replay_load_success' -Level 'INFO' `
            -Fields @{
                requested_sub_session_id = $actualId   # treat actual as requested for by_path
                actual_sub_session_id    = $actualId
                file_path                = $FilePath
                ms_to_irsdk_ready        = $msToReady
            }
        return 0
    }

    if ($actualId -eq $RequestedSubSessionId) {
        Send-TestRigLog -Event 'replay_load_success' -Level 'INFO' `
            -Fields @{
                requested_sub_session_id = $RequestedSubSessionId
                actual_sub_session_id    = $actualId
                file_path                = $FilePath
                ms_to_irsdk_ready        = $msToReady
            }
        return 0
    }

    Send-TestRigLog -Event 'replay_load_mismatch' -Level 'WARN' `
        -Fields @{
            requested_sub_session_id = $RequestedSubSessionId
            actual_sub_session_id    = $actualId
            file_path                = $FilePath
            ms_to_irsdk_ready        = $msToReady
        }
    return 0
}

function LoadReplay {
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$SubSessionId, [int]$IrsdkTimeoutSec = 90)

    $path = Find-ReplayFile -SubSessionId $SubSessionId
    if (-not $path) {
        Send-TestRigLog -Event 'replay_load_started' -Level 'INFO' `
            -Fields @{
                requested_sub_session_id = $SubSessionId
                file_path                = $null
                mode                     = 'by_id'
            }
        Send-TestRigLog -Event 'replay_load_failed' -Level 'ERROR' `
            -Fields @{
                requested_sub_session_id = $SubSessionId
                file_path                = $null
                error                    = 'not_found_locally'
                phase                    = 'glob'
            }
        return 1
    }
    return (Invoke-ReplayLoad -Mode 'by_id' -RequestedSubSessionId $SubSessionId -FilePath $path -IrsdkTimeoutSec $IrsdkTimeoutSec)
}

function LoadReplayByPath {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path, [int]$IrsdkTimeoutSec = 90)
    return (Invoke-ReplayLoad -Mode 'by_path' -RequestedSubSessionId 0 -FilePath $Path -IrsdkTimeoutSec $IrsdkTimeoutSec)
}

# When invoked as a script (not dot-sourced), dispatch to the right function.
if ($MyInvocation.InvocationName -ne '.') {
    switch ($PSCmdlet.ParameterSetName) {
        'ById'   { exit (LoadReplay       -SubSessionId $SubSessionId -IrsdkTimeoutSec $IrsdkTimeoutSec) }
        'ByPath' { exit (LoadReplayByPath -Path         $Path         -IrsdkTimeoutSec $IrsdkTimeoutSec) }
    }
}
