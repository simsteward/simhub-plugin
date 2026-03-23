# WebSocket connectivity test for Sim Steward plugin
# Requires: SimHub running with Sim Steward plugin loaded
# Run: .\tests\WebSocketConnectTest.ps1

$ErrorActionPreference = "Stop"
$port = 19847
$timeoutMs = 5000

Add-Type -AssemblyName System.Core

$tcp = New-Object System.Net.Sockets.TcpClient
try {
    $connectTask = $tcp.ConnectAsync("127.0.0.1", $port)
    $completed = $connectTask.Wait($timeoutMs)
    if (-not $completed) {
        Write-Host "FAIL: Connection to localhost:$port timed out after ${timeoutMs}ms"
        exit 1
    }
    if (-not $tcp.Connected) {
        Write-Host "FAIL: Could not connect to localhost:$port"
        exit 1
    }
    Write-Host "PASS: Port $port is reachable (TCP connection succeeded)"
} catch {
    Write-Host "FAIL: $($_.Exception.Message)"
    exit 1
} finally {
    $tcp.Close()
}

# Try WebSocket handshake and first message via raw HTTP upgrade
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
    Write-Host "PASS: WebSocket connected to ws://127.0.0.1:$port/"

    # Receive messages until we get state (plugin may send logEvents first)
    $buffer = [byte[]]::new(65536)
    $segment = [System.ArraySegment[byte]]::new($buffer)
    $stateReceived = $false
    $maxMessages = 5
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
            Write-Host "PASS: Received state message (pluginMode=$($obj.pluginMode))"
            if (-not $obj.PSObject.Properties["pluginVersion"]) {
                Write-Host "FAIL: State missing pluginVersion"
                exit 1
            }
            if ([string]::IsNullOrWhiteSpace([string]$obj.pluginVersion)) {
                Write-Host "FAIL: pluginVersion empty"
                exit 1
            }
            Write-Host "PASS: pluginVersion=$($obj.pluginVersion)"
            if ($null -ne $obj.incidents) {
                Write-Host "PASS: State contains incidents array (count=$($obj.incidents.Count))"
            }
            $stateReceived = $true
            break
        }
    }
    if (-not $stateReceived) {
        Write-Host "FAIL: Did not receive state message after $maxMessages messages"
        exit 1
    }

    Write-Host ""
    Write-Host "All checks passed. Plugin WebSocket is functioning."
} catch {
    Write-Host "FAIL: $($_.Exception.Message)"
    exit 1
} finally {
    $ws.Dispose()
}
