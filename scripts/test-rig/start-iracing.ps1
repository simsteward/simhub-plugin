# Phase 4 Test Rig — boot iRacing from cold via Steam.
#
# 1. Start-Process steam://rungameid/266410       (Steam handles auth + iRacing launch)
# 2. Wait for iRacingUI.exe         (timeout 60s)
# 3. Wait for iRacingSim64DX11.exe  (timeout 60s after UI shows up)
# 4. Wait for IRSDK shared mem      (Local\IRSDKMemMapFileName, timeout 60s)
# 5. Return when SDK is queryable.

[CmdletBinding()]
param(
    [int]$UiTimeoutSec     = 60,
    [int]$SimTimeoutSec    = 60,
    [int]$IrsdkTimeoutSec  = 60
)

$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot '_send-log.ps1')

Add-Type -AssemblyName 'System.Core' -ErrorAction SilentlyContinue | Out-Null

function Test-IrsdkReady {
    try {
        $mmf = [System.IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting('Local\IRSDKMemMapFileName')
        $mmf.Dispose()
        return $true
    } catch {
        return $false
    }
}

function Wait-Until {
    param(
        [scriptblock]$Predicate,
        [int]$TimeoutMs,
        [int]$IntervalMs = 500
    )
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        if (& $Predicate) { return $sw.ElapsedMilliseconds }
        Start-Sleep -Milliseconds $IntervalMs
    }
    return -1
}

Send-TestRigLog -Event 'test_rig_iracing_launch_started' -Level 'INFO' `
    -Message 'Launching iRacing via Steam'

$swTotal = [System.Diagnostics.Stopwatch]::StartNew()

try {
    Start-Process 'steam://rungameid/266410' -ErrorAction Stop
} catch {
    Send-TestRigLog -Event 'test_rig_iracing_launch_failed' -Level 'ERROR' `
        -Fields @{ phase = 'steam_uri'; timeout_ms = 0; error = $_.Exception.Message }
    exit 1
}

# 2. iRacingUI.exe
$msToUi = Wait-Until -Predicate { [bool](Get-Process -Name 'iRacingUI' -ErrorAction SilentlyContinue) } `
    -TimeoutMs ($UiTimeoutSec * 1000)
if ($msToUi -lt 0) {
    Send-TestRigLog -Event 'test_rig_iracing_launch_failed' -Level 'ERROR' `
        -Fields @{ phase = 'iracing_ui'; timeout_ms = $UiTimeoutSec * 1000; error = 'iRacingUI.exe did not appear' }
    exit 1
}
Send-TestRigLog -Event 'test_rig_iracing_ui_ready' -Level 'INFO' `
    -Fields @{ ms_to_ui = $msToUi }

# 3. iRacingSim64DX11.exe
$msToSim = Wait-Until -Predicate { [bool](Get-Process -Name 'iRacingSim64DX11' -ErrorAction SilentlyContinue) } `
    -TimeoutMs ($SimTimeoutSec * 1000)
if ($msToSim -lt 0) {
    Send-TestRigLog -Event 'test_rig_iracing_launch_failed' -Level 'ERROR' `
        -Fields @{ phase = 'iracing_sim'; timeout_ms = $SimTimeoutSec * 1000; error = 'iRacingSim64DX11.exe did not appear' }
    exit 1
}

# 4. IRSDK shared memory handle
$msToIrsdk = Wait-Until -Predicate { Test-IrsdkReady } -TimeoutMs ($IrsdkTimeoutSec * 1000)
if ($msToIrsdk -lt 0) {
    Send-TestRigLog -Event 'test_rig_iracing_launch_failed' -Level 'ERROR' `
        -Fields @{ phase = 'irsdk_handle'; timeout_ms = $IrsdkTimeoutSec * 1000; error = 'IRSDK shared memory not ready' }
    exit 1
}

$swTotal.Stop()
Send-TestRigLog -Event 'test_rig_iracing_sim_ready' -Level 'INFO' `
    -Fields @{
        ms_to_sim    = $msToSim
        ms_to_irsdk  = $msToIrsdk
        total_ms     = $swTotal.ElapsedMilliseconds
    }
