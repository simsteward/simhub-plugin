# Run the data capture suite end-to-end via WebSocket.
# Usage: .\scripts\run-data-capture-suite.ps1 [-MaxRunSec 1800] [-Skip "T1,T5b"]
# Requires: SimHub + Sim Steward plugin loaded, iRacing in replay mode, preflight passed (or will run it).
param(
    [int]$MaxRunSec = 1800,
    [string]$Skip = ""
)

$ErrorActionPreference = "Stop"
$port = 19847
$uri  = [System.Uri]"ws://localhost:$port"
$ws   = [System.Net.WebSockets.ClientWebSocket]::new()
$ws.ConnectAsync($uri, [System.Threading.CancellationToken]::None).Wait()
Write-Host "Connected to ws://localhost:$port"

function WsSend([string]$msg) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($msg)
    $seg   = [System.ArraySegment[byte]]::new($bytes)
    $ws.SendAsync($seg, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [System.Threading.CancellationToken]::None).Wait()
}

# Returns $null on timeout or closed socket.
function WsRecvTimeout([int]$timeoutMs = 60000) {
    $cts = [System.Threading.CancellationTokenSource]::new($timeoutMs)
    try {
        $buf = [byte[]]::new(131072)
        $seg = [System.ArraySegment[byte]]::new($buf)
        $res = $ws.ReceiveAsync($seg, $cts.Token).GetAwaiter().GetResult()
        return [System.Text.Encoding]::UTF8.GetString($buf, 0, $res.Count)
    } catch [System.OperationCanceledException] {
        return $null
    } catch {
        return $null
    } finally {
        $cts.Dispose()
    }
}

# Per-step wall-clock ceilings before we declare the step stuck and abort.
# Plugin T8 dynamic timeout cap = 21600 ticks @ 60Hz = 360s. Add 90s buffer = 450s.
# T1_Sweep: 3600 ticks/speed × 4 speeds = 14400 ticks = 240s + margin.
# Default: SeekTimeoutTicks=600 = 10s; 180s is generous.
function Get-StepTimeoutSec([string]$stepName) {
    switch ($stepName) {
        "T8_Poll"  { return 450  }
        "T1_Sweep" { return 300  }
        default    { return 180  }
    }
}

$lokiUrl           = "http://localhost:3100/loki/api/v1/query_range"
$lastLokiCheckTime = [DateTimeOffset]::MinValue

function Invoke-LokiT8DiagCheck {
    try {
        $query   = '{app="sim-steward"} |= "[T8_DIAG]"'
        $startNs = [DateTimeOffset]::UtcNow.AddMinutes(-3).ToUnixTimeMilliseconds() * 1000000
        $endNs   = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() * 1000000
        $url     = "$lokiUrl`?query=$([uri]::EscapeDataString($query))&start=$startNs&end=$endNs&limit=1"
        $resp    = Invoke-RestMethod -Uri $url -TimeoutSec 10 -ErrorAction Stop
        if (-not $resp -or $resp.data.result.Count -eq 0) {
            Write-Host "LOKI_WARN: no [T8_DIAG] in last 3 min - plugin may not be ticking in iRacing"
        } else {
            Write-Host "LOKI_OK: [T8_DIAG] heartbeat confirmed"
        }
    } catch {
        Write-Host ("LOKI_WARN: heartbeat query failed ({0})" -f $_.Exception.Message)
    }
}

# ── Read initial state ────────────────────────────────────────────────────────
$initState = $null
for ($i = 0; $i -lt 100; $i++) {
    $raw = WsRecvTimeout 10000
    if (-not $raw) { continue }
    try { $o = $raw | ConvertFrom-Json; if ($o.type -eq "state") { $initState = $o; break } } catch {}
}
if (-not $initState) { Write-Host "ERROR: no state message received"; exit 1 }

$pf = $initState.preflight
Write-Host ("Init: pf.phase={0} allPassed={1} suite.phase={2}" -f $pf.phase, $pf.allPassed, $initState.dataCaptureSuite.phase)

$mode = "pfwait"
if ($pf.phase -eq "complete" -and $pf.allPassed -eq $true) {
    Write-Host "Preflight already passed - starting suite"
    $startArg = if ($Skip) { "start:$Skip" } else { "start" }
    WsSend "{`"action`":`"data_capture_suite`",`"arg`":`"$startArg`"}"
    $mode = "suitewait"
} else {
    Write-Host "Running preflight..."
    WsSend '{"action":"data_capture_suite","arg":"preflight"}'
}

$startSec         = [int][DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$lastStep         = ""; $lastPhase = ""
$lastProgressTime = [DateTimeOffset]::UtcNow

# ── Main loop ─────────────────────────────────────────────────────────────────
while ($true) {
    $raw = WsRecvTimeout 60000
    $el  = [int]([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - $startSec)

    if ($null -eq $raw) {
        Write-Host ("WS_SILENT [{0}s] - no message for 60s, iRacing may be frozen" -f $el)
        exit 2
    }

    $obj = $null
    try { $obj = $raw | ConvertFrom-Json } catch { continue }
    if ($obj.type -ne "state") { continue }

    # ── Preflight wait ────────────────────────────────────────────────────────
    if ($mode -eq "pfwait") {
        $pf2 = $obj.preflight
        if ($pf2.phase -ne $lastPhase) {
            Write-Host ("pf[{0}s] phase={1} allPassed={2}" -f $el, $pf2.phase, $pf2.allPassed)
            $lastPhase        = $pf2.phase
            $lastProgressTime = [DateTimeOffset]::UtcNow
        }
        if ($pf2.phase -eq "complete") {
            if ($pf2.allPassed -eq $true) {
                Write-Host "PF_PASSED - starting suite"
                $startArg = if ($Skip) { "start:$Skip" } else { "start" }
                WsSend "{`"action`":`"data_capture_suite`",`"arg`":`"$startArg`"}"
                $mode = "suitewait"
                $startSec = [int][DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
                $lastStep = ""; $lastPhase = ""
                $lastProgressTime = [DateTimeOffset]::UtcNow
            } else {
                Write-Host ("PF_FAILED err={0}" -f $pf2.error)
                if ($pf2.MiniTests) {
                    foreach ($t in $pf2.MiniTests) {
                        if ($t.status -eq "fail") { Write-Host ("  FAIL [{0}] {1}: {2}" -f $t.id, $t.name, $t.detail) }
                    }
                }
                exit 1
            }
        }
        if ($el -gt 180) { Write-Host "PF_TIMEOUT"; exit 1 }
        continue
    }

    # ── Suite wait ────────────────────────────────────────────────────────────
    $suite  = $obj.dataCaptureSuite
    $sPhase = $suite.phase
    $sStep  = $suite.currentStepName
    $sProg  = $suite.currentStep
    $sTotal = $suite.totalSteps

    if ($sPhase -ne $lastPhase -or $sStep -ne $lastStep) {
        Write-Host ("[{0}s] suite={1} step={2} ({3}/{4})" -f $el, $sPhase, $sStep, $sProg, $sTotal)
        $lastPhase        = $sPhase
        $lastStep         = $sStep
        $lastProgressTime = [DateTimeOffset]::UtcNow
    }

    # Terminal phases - exit immediately without waiting for stuck detection
    if ($sPhase -eq "cancelled") {
        Write-Host ("SUITE_CANCELLED [{0}s]" -f $el)
        break
    }

    # Per-step stuck detection
    $stepTimeoutSec = Get-StepTimeoutSec $sStep
    $stepElapsed    = ([DateTimeOffset]::UtcNow - $lastProgressTime).TotalSeconds
    if ($stepElapsed -gt $stepTimeoutSec) {
        Write-Host ("STEP_STUCK [{0}s] step={1} no_progress_for={2}s limit={3}s" -f $el, $sStep, [int]$stepElapsed, $stepTimeoutSec)
        exit 2
    }

    # Loki T8_DIAG heartbeat - every 120s while in T8_Poll
    if ($sStep -eq "T8_Poll") {
        $sinceLastCheck = ([DateTimeOffset]::UtcNow - $lastLokiCheckTime).TotalSeconds
        if ($sinceLastCheck -gt 120) {
            $lastLokiCheckTime = [DateTimeOffset]::UtcNow
            Invoke-LokiT8DiagCheck
        }
    }

    if ($sPhase -eq "awaitingloki") {
        Write-Host "awaitingloki - sleeping 90s for Loki ingestion"
        Start-Sleep -Seconds 90
        for ($i = 0; $i -lt 300; $i++) {
            $r2 = WsRecvTimeout 10000
            if (-not $r2) { continue }
            try {
                $o2 = $r2 | ConvertFrom-Json
                if ($o2.type -eq "state" -and $o2.dataCaptureSuite.phase -eq "complete") { $obj = $o2; break }
            } catch {}
        }
        break
    }
    if ($sPhase -eq "complete") { break }
    if ($el -gt $MaxRunSec) { Write-Host ("Suite TIMEOUT at {0}s (limit={1}s)" -f $el, $MaxRunSec); break }
}

$final = $obj.dataCaptureSuite
Write-Host ""
Write-Host "=== Results ==="
if ($final.testResults) {
    [int]$pass = 0; [int]$fail = 0; [int]$skip = 0
    foreach ($r in $final.testResults) {
        $status = if ($r.status -eq "pass") { "PASS" } elseif ($r.status -eq "skip") { "SKIP" } else { "FAIL" }
        if ($r.status -eq "pass") { $pass++ } elseif ($r.status -eq "skip") { $skip++ } else { $fail++ }
        $line = ("  [{0}] {1}" -f $status, $r.testId)
        if ($r.kpiLabel) { $line += (" | {0}={1}" -f $r.kpiLabel, $r.kpiValue) }
        if ($r.error)    { $line += (" ERR:{0}" -f $r.error) }
        $line += (" status={0}" -f $r.status)
        Write-Host $line
    }
    Write-Host ""
    Write-Host ("Summary: {0} pass, {1} fail, {2} skip" -f $pass, $fail, $skip)
} else {
    Write-Host ("no_results phase={0}" -f $final.phase)
}
Write-Host ""
Write-Host ("testRunId={0}" -f $final.testRunId)
Write-Host ("elapsed={0}ms" -f $final.elapsedMs)
Write-Host ("phase={0}" -f $final.phase)
