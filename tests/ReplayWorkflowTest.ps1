# Replay capture workflow test — WebSocket state shape
# Requires: SimHub running with Sim Steward plugin loaded
# Run: .\tests\ReplayWorkflowTest.ps1

$ErrorActionPreference = "Stop"
$port = 19847
$timeoutMs = 8000

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
    if ($mode -notin @("Replay", "Unknown")) {
        Write-Host "FAIL: State pluginMode unexpected: $mode"
        exit 1
    }
    Write-Host "PASS: State has pluginMode=$mode"

    if (-not $stateObj.PSObject.Properties["pluginVersion"]) {
        Write-Host "FAIL: State missing pluginVersion"
        exit 1
    }
    if ([string]::IsNullOrWhiteSpace([string]$stateObj.pluginVersion)) {
        Write-Host "FAIL: pluginVersion empty"
        exit 1
    }
    Write-Host "PASS: State has pluginVersion=$($stateObj.pluginVersion)"

    # Expect: diagnostics present (WebSocket state mirrors PluginSnapshot.Diagnostics)
    if (-not $stateObj.PSObject.Properties["diagnostics"]) {
        Write-Host "FAIL: State missing diagnostics"
        exit 1
    }
    $diag = $stateObj.diagnostics
    Write-Host "PASS: State has diagnostics"

    if ($stateObj.PSObject.Properties["lap"]) {
        if ($stateObj.lap -isnot [int] -and $stateObj.lap -isnot [long] -and $stateObj.lap -isnot [double]) {
            Write-Host "WARN: State lap is not numeric: $($stateObj.lap)"
        } else {
            Write-Host "PASS: State has lap=$($stateObj.lap)"
        }
    } else {
        Write-Host "PASS: State lap absent (ok for older clients)"
    }

    # When sessions array present, expect structure (Test case 2 — Sessions list)
    if ($diag.PSObject.Properties["sessions"] -and $null -ne $diag.sessions) {
        $sessions = $diag.sessions
        if ($sessions -isnot [Array]) {
            Write-Host "FAIL: diagnostics.sessions is not an array"
            exit 1
        }
        foreach ($s in $sessions) {
            if (-not $s.PSObject.Properties["sessionNum"]) { Write-Host "FAIL: session entry missing sessionNum"; exit 1 }
            if (-not $s.PSObject.Properties["sessionType"]) { Write-Host "FAIL: session entry missing sessionType"; exit 1 }
            if (-not $s.PSObject.Properties["sessionName"]) { Write-Host "FAIL: session entry missing sessionName"; exit 1 }
        }
        Write-Host "PASS: diagnostics.sessions array has sessionNum, sessionType, sessionName (count=$($sessions.Count))"
    } else {
        Write-Host "PASS: diagnostics.sessions absent or empty (ok when not replay or no SessionInfo)"
    }
} catch {
    Write-Host "FAIL: $($_.Exception.Message)"
    exit 1
} finally {
    $ws.Dispose()
}

Write-Host ""
Write-Host "All replay workflow checks passed."
