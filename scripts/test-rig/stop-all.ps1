# Phase 4 Test Rig — clean shutdown.
#
# Order:
#   1. SimHubWPF.exe   — graceful (-exit) → taskkill (3s) → /F (5s)
#   2. iRacingSim64DX11.exe — taskkill → /F (5s)
#   3. iRacingUI.exe   — taskkill → /F (5s)
#   4. (optional) -IncludeSteam: steam.exe -shutdown → taskkill (10s)
#
# NEVER kills iRacingService.exe — the helper service must always run.
# Logs every step to Grafana Cloud Loki via Send-TestRigLog (domain "system").

[CmdletBinding()]
param(
    [switch]$IncludeSteam
)

$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot '_send-log.ps1')

$swTotal = [System.Diagnostics.Stopwatch]::StartNew()

Send-TestRigLog -Event 'test_rig_stop_all_started' -Level 'INFO' `
    -Message "Stopping rig stack (include_steam=$($IncludeSteam.IsPresent))" `
    -Fields @{ include_steam = [bool]$IncludeSteam }

function Test-ProcessAlive {
    param([string]$Name)
    return [bool](Get-Process -Name $Name -ErrorAction SilentlyContinue)
}

function Wait-ProcessExit {
    param(
        [string]$Name,
        [int]$TimeoutMs
    )
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        if (-not (Test-ProcessAlive -Name $Name)) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return -not (Test-ProcessAlive -Name $Name)
}

function Stop-SimHub {
    if (-not (Test-ProcessAlive -Name 'SimHubWPF')) { return }

    $swProc = [System.Diagnostics.Stopwatch]::StartNew()
    $simhubExe = $null
    try {
        $simhubExe = (Get-Process -Name 'SimHubWPF' -ErrorAction SilentlyContinue |
                      Select-Object -First 1).Path
    } catch {}

    # 1. Graceful: SimHubWPF.exe -exit
    if ($simhubExe -and (Test-Path -LiteralPath $simhubExe)) {
        try { Start-Process -FilePath $simhubExe -ArgumentList '-exit' -ErrorAction Stop } catch {}
    }
    if (Wait-ProcessExit -Name 'SimHubWPF' -TimeoutMs 3000) {
        Send-TestRigLog -Event 'test_rig_process_killed' -Level 'INFO' `
            -Fields @{ process_name = 'SimHubWPF.exe'; method = 'graceful'; wait_ms = $swProc.ElapsedMilliseconds }
        return
    }

    # 2. taskkill (no /F)
    & taskkill.exe /IM SimHubWPF.exe 2>&1 | Out-Null
    if (Wait-ProcessExit -Name 'SimHubWPF' -TimeoutMs 5000) {
        Send-TestRigLog -Event 'test_rig_process_killed' -Level 'INFO' `
            -Fields @{ process_name = 'SimHubWPF.exe'; method = 'taskkill'; wait_ms = $swProc.ElapsedMilliseconds }
        return
    }

    # 3. taskkill /F
    & taskkill.exe /F /IM SimHubWPF.exe 2>&1 | Out-Null
    Wait-ProcessExit -Name 'SimHubWPF' -TimeoutMs 3000 | Out-Null
    Send-TestRigLog -Event 'test_rig_process_killed' -Level 'INFO' `
        -Fields @{ process_name = 'SimHubWPF.exe'; method = 'force'; wait_ms = $swProc.ElapsedMilliseconds }
}

function Stop-NamedProcess {
    param([string]$ImageName, [string]$ProcessName)

    if (-not (Test-ProcessAlive -Name $ProcessName)) { return }

    $swProc = [System.Diagnostics.Stopwatch]::StartNew()

    # 1. graceful taskkill
    & taskkill.exe /IM $ImageName 2>&1 | Out-Null
    if (Wait-ProcessExit -Name $ProcessName -TimeoutMs 5000) {
        Send-TestRigLog -Event 'test_rig_process_killed' -Level 'INFO' `
            -Fields @{ process_name = $ImageName; method = 'taskkill'; wait_ms = $swProc.ElapsedMilliseconds }
        return
    }

    # 2. taskkill /F
    & taskkill.exe /F /IM $ImageName 2>&1 | Out-Null
    Wait-ProcessExit -Name $ProcessName -TimeoutMs 3000 | Out-Null
    Send-TestRigLog -Event 'test_rig_process_killed' -Level 'INFO' `
        -Fields @{ process_name = $ImageName; method = 'force'; wait_ms = $swProc.ElapsedMilliseconds }
}

function Stop-Steam {
    if (-not (Test-ProcessAlive -Name 'steam')) { return }
    $swProc = [System.Diagnostics.Stopwatch]::StartNew()

    $steamExe = 'C:\Program Files (x86)\Steam\steam.exe'
    if (Test-Path -LiteralPath $steamExe) {
        try { Start-Process -FilePath $steamExe -ArgumentList '-shutdown' -ErrorAction Stop } catch {}
    }
    if (Wait-ProcessExit -Name 'steam' -TimeoutMs 10000) {
        Send-TestRigLog -Event 'test_rig_process_killed' -Level 'INFO' `
            -Fields @{ process_name = 'steam.exe'; method = 'graceful'; wait_ms = $swProc.ElapsedMilliseconds }
        return
    }

    & taskkill.exe /IM steam.exe 2>&1 | Out-Null
    Wait-ProcessExit -Name 'steam' -TimeoutMs 5000 | Out-Null
    Send-TestRigLog -Event 'test_rig_process_killed' -Level 'INFO' `
        -Fields @{ process_name = 'steam.exe'; method = 'taskkill'; wait_ms = $swProc.ElapsedMilliseconds }
}

Stop-SimHub
Stop-NamedProcess -ImageName 'iRacingSim64DX11.exe' -ProcessName 'iRacingSim64DX11'
Stop-NamedProcess -ImageName 'iRacingUI.exe'        -ProcessName 'iRacingUI'

if ($IncludeSteam) { Stop-Steam }

$swTotal.Stop()
Send-TestRigLog -Event 'test_rig_stop_all_completed' -Level 'INFO' `
    -Fields @{ total_ms = $swTotal.ElapsedMilliseconds; include_steam = [bool]$IncludeSteam }
