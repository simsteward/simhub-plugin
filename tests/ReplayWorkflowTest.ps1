# Replay capture workflow test — state shape and snapshot file structure
# Requires: SimHub running with Sim Steward plugin loaded
# Run: .\tests\ReplayWorkflowTest.ps1
# Expectations: WebSocket state shape and session-discovery.jsonl structure (see script checks below).

$ErrorActionPreference = "Stop"
$port = 19847
$timeoutMs = 8000
$snapshotPath = "$env:LOCALAPPDATA\SimHubWpf\PluginsData\SimSteward\session-discovery.jsonl"

Add-Type -AssemblyName System.Core

# --- 1. WebSocket connect and receive state (Test case 1 — Detect) ---
$uri = [System.Uri]::new("ws://127.0.0.1:$port/")
$ws = New-Object System.Net.WebSockets.ClientWebSocket
$cts = New-Object System.Threading.CancellationTokenSource
$cts.CancelAfter($timeoutMs)

try {
    $connectTask = $ws.ConnectAsync($uri, $cts.Token)
    $connectTask.Wait()
    if ($ws.State -ne [System.Net.WebSockets.WebSocketState]::Open) {
        Write-Host "FAIL: WebSocket did not open. State=$($ws.State)"
        exit 1
    }
    Write-Host "PASS: WebSocket connected"

    $buffer = [byte[]]::new(65536)
    $segment = [System.ArraySegment[byte]]::new($buffer)
    $stateObj = $null
    $maxMessages = 10
    for ($i = 0; $i -lt $maxMessages; $i++) {
        $receiveTask = $ws.ReceiveAsync($segment, $cts.Token)
        $receiveTask.Wait()
        $result = $receiveTask.Result
        if ($result.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) {
            Write-Host "FAIL: Server sent close frame"
            exit 1
        }
        $json = [System.Text.Encoding]::UTF8.GetString($buffer, 0, $result.Count)
        $obj = $json | ConvertFrom-Json
        if ($obj.type -eq "state") {
            $stateObj = $obj
            break
        }
    }
    if ($null -eq $stateObj) {
        Write-Host "FAIL: Did not receive state message after $maxMessages messages"
        exit 1
    }

    # Expect: pluginMode present
    if (-not $stateObj.PSObject.Properties["pluginMode"]) {
        Write-Host "FAIL: State missing pluginMode"
        exit 1
    }
    $mode = $stateObj.pluginMode
    if ($mode -notin @("Replay", "Live", "Unknown")) {
        Write-Host "FAIL: State pluginMode unexpected: $mode"
        exit 1
    }
    Write-Host "PASS: State has pluginMode=$mode"

    # Expect: sessionDiagnostics present
    if (-not $stateObj.PSObject.Properties["sessionDiagnostics"]) {
        Write-Host "FAIL: State missing sessionDiagnostics"
        exit 1
    }
    $diag = $stateObj.sessionDiagnostics
    Write-Host "PASS: State has sessionDiagnostics"

    # When sessions array present, expect structure (Test case 2 — Sessions list)
    if ($diag.PSObject.Properties["sessions"] -and $null -ne $diag.sessions) {
        $sessions = $diag.sessions
        if ($sessions -isnot [Array]) {
            Write-Host "FAIL: sessionDiagnostics.sessions is not an array"
            exit 1
        }
        foreach ($s in $sessions) {
            if (-not $s.PSObject.Properties["sessionNum"]) { Write-Host "FAIL: session entry missing sessionNum"; exit 1 }
            if (-not $s.PSObject.Properties["sessionType"]) { Write-Host "FAIL: session entry missing sessionType"; exit 1 }
            if (-not $s.PSObject.Properties["sessionName"]) { Write-Host "FAIL: session entry missing sessionName"; exit 1 }
        }
        Write-Host "PASS: sessionDiagnostics.sessions array has sessionNum, sessionType, sessionName (count=$($sessions.Count))"
    } else {
        Write-Host "PASS: sessionDiagnostics.sessions absent or empty (ok when not replay or no SessionInfo)"
    }
} catch {
    Write-Host "FAIL: $($_.Exception.Message)"
    exit 1
} finally {
    $ws.Dispose()
}

# --- 2. Snapshot file structure (Test case 5 — payload shape) ---
if (Test-Path -LiteralPath $snapshotPath) {
    $allLines = Get-Content -LiteralPath $snapshotPath -Encoding UTF8 -ErrorAction Stop
    $nonEmpty = @($allLines | Where-Object { $_.Trim().Length -gt 0 })
    if ($nonEmpty.Count -ge 1) {
        $lastLine = $nonEmpty[-1].Trim()
        try {
            $snap = $lastLine | ConvertFrom-Json
        } catch {
            Write-Host "FAIL: Last snapshot line is not valid JSON"
            exit 1
        }
        if ($snap.type -ne "sessionSnapshot") {
            Write-Host "FAIL: Snapshot line type is '$($snap.type)', expected sessionSnapshot"
            exit 1
        }
        if (-not $snap.PSObject.Properties["trigger"]) { Write-Host "FAIL: Snapshot missing trigger"; exit 1 }
        if (-not $snap.PSObject.Properties["playerCarIdx"]) { Write-Host "FAIL: Snapshot missing playerCarIdx"; exit 1 }
        if (-not $snap.PSObject.Properties["sessionDiagnostics"]) { Write-Host "FAIL: Snapshot missing sessionDiagnostics"; exit 1 }
        if (-not $snap.PSObject.Properties["replayFrameNum"]) { Write-Host "FAIL: Snapshot missing replayFrameNum"; exit 1 }
        Write-Host "PASS: Last snapshot line has type, trigger, playerCarIdx, sessionDiagnostics, replayFrameNum"
        if ($snap.PSObject.Properties["replayMetadata"] -and $null -ne $snap.replayMetadata) {
            $meta = $snap.replayMetadata
            $required = @("sessionID", "subSessionID", "trackDisplayName", "category", "simMode", "driverRoster", "sessions", "incidentFeed")
            foreach ($key in $required) {
                if (-not $meta.PSObject.Properties[$key]) {
                    Write-Host "FAIL: replayMetadata missing $key"
                    exit 1
                }
            }
            Write-Host "PASS: Snapshot replayMetadata has sessionID, subSessionID, trackDisplayName, category, simMode, driverRoster, sessions, incidentFeed"
        } else {
            Write-Host "PASS: No replayMetadata in snapshot (ok when SessionInfo not available)"
        }
    } else {
        Write-Host "PASS: Snapshot file empty (no snapshot lines yet)"
    }
} else {
    Write-Host "PASS: Snapshot file not present (no snapshots recorded yet)"
}

Write-Host ""
Write-Host "All replay workflow checks passed."
