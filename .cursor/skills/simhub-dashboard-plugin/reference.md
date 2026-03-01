# SimHub Dashboard + Plugin Reference

## SimHub Property Name Patterns

Properties follow a dotted naming convention. The property picker in SimHub's binding editor lists all available properties.

### DataCorePlugin (game telemetry)

```
DataCorePlugin.GameData.NewData.Rpms
DataCorePlugin.GameData.NewData.MaxRpm
DataCorePlugin.GameData.NewData.SpeedKmh
DataCorePlugin.GameData.NewData.SpeedMph
DataCorePlugin.GameData.NewData.SpeedLocal        (uses user's preferred unit)
DataCorePlugin.GameData.NewData.Gear
DataCorePlugin.GameData.NewData.IsInPit
DataCorePlugin.GameData.NewData.IsInPitLane
DataCorePlugin.GameData.NewData.BestLapTime
DataCorePlugin.GameData.NewData.LastLapTime
DataCorePlugin.GameData.NewData.CurrentLapTime
DataCorePlugin.GameData.NewData.Position
DataCorePlugin.GameData.NewData.OpponentsCount
DataCorePlugin.GameData.NewData.FuelLevel
DataCorePlugin.GameData.NewData.FuelPercent
DataCorePlugin.GameData.NewData.Throttle           (0-100)
DataCorePlugin.GameData.NewData.Brake              (0-100)
DataCorePlugin.GameData.NewData.Clutch             (0-100)
DataCorePlugin.GameData.NewData.TyrePressureFrontLeft
DataCorePlugin.GameData.NewData.TyrePressureFrontRight
DataCorePlugin.GameData.NewData.TyrePressureRearLeft
DataCorePlugin.GameData.NewData.TyrePressureRearRight
DataCorePlugin.GameData.NewData.TyreTemperatureFrontLeft
DataCorePlugin.GameData.NewData.TyreTemperatureFrontRight
DataCorePlugin.GameData.NewData.TyreTemperatureRearLeft
DataCorePlugin.GameData.NewData.TyreTemperatureRearRight
DataCorePlugin.GameData.NewData.AirTemperature
DataCorePlugin.GameData.NewData.RoadTemperature
DataCorePlugin.GameData.NewData.SessionTypeName
DataCorePlugin.GameData.NewData.RemainingLaps
DataCorePlugin.GameData.NewData.TotalLaps
DataCorePlugin.GameData.NewData.Flag_Green
DataCorePlugin.GameData.NewData.Flag_Yellow
DataCorePlugin.GameData.NewData.Flag_Blue
DataCorePlugin.GameData.NewData.Flag_White
DataCorePlugin.GameData.NewData.Flag_Checkered
DataCorePlugin.CurrentGame
```

### Plugin-defined properties

Plugin properties use the pattern `PluginName.Group.PropertyName`:
```
SimSteward.Data.Speed
SimSteward.Data.Gear
SimSteward.State.Mode
SimSteward.State.PitRequested
CalcLngWheelSlip.Computed.LngWheelSlip_FL
```

### Raw game data

Some games expose raw data under `DataCorePlugin.GameRawData.*`. Structure varies per game.

---

## NCalc Functions Reference

### Built-in NCalc functions

| Function | Description | Example |
|----------|-------------|---------|
| `Round(value, decimals)` | Round to N decimal places | `Round([Rpms] / 1000, 1)` |
| `Truncate(value)` | Remove decimal portion | `Truncate([SpeedKmh])` |
| `if(condition, trueVal, falseVal)` | Conditional | `if([Rpms] > 7000, 'SHIFT', '')` |
| `isnull(value, fallback)` | Null coalescing | `isnull([FuelLevel], 0)` |
| `changed(ms, value)` | True if value changed within N ms | `changed(500, [Gear])` |
| `format(value, formatStr)` | .NET format string | `format([SpeedKmh], '0.0')` |
| `Abs(value)` | Absolute value | `Abs([LateralG])` |
| `Max(a, b)` | Maximum | `Max([Throttle], [Brake])` |
| `Min(a, b)` | Minimum | `Min([FuelLevel], 100)` |
| `Pow(base, exp)` | Power | `Pow([SpeedKmh], 2)` |
| `Sqrt(value)` | Square root | `Sqrt([value])` |

### NCalc operators

- Arithmetic: `+`, `-`, `*`, `/`, `%`
- Comparison: `=`, `<`, `>`, `<=`, `>=`, `<>` (not equal)
- Logical: `and`, `or`, `not`
- String concat: `+`

### NCalc example formulas

```
// RPM percentage
[DataCorePlugin.GameData.NewData.Rpms] / [DataCorePlugin.GameData.NewData.MaxRpm] * 100

// Gear display with neutral
if([DataCorePlugin.GameData.NewData.Gear] = 'N', 'N', [DataCorePlugin.GameData.NewData.Gear])

// Speed with unit label
format([DataCorePlugin.GameData.NewData.SpeedLocal], '0') + ' km/h'

// Show 'PIT' when in pit lane
if([DataCorePlugin.GameData.NewData.IsInPitLane], 'PIT', '')

// Blink effect when gear changed
changed(300, [DataCorePlugin.GameData.NewData.Gear])
```

---

## Jint (ES5.1) Formula Examples

Remember: these run inside SimHub's formula engine, NOT in a browser.

```javascript
// RPM percentage
var rpm = $prop('DataCorePlugin.GameData.NewData.Rpms');
var maxRpm = $prop('DataCorePlugin.GameData.NewData.MaxRpm');
if (maxRpm > 0) {
    return (rpm / maxRpm) * 100;
}
return 0;
```

```javascript
// Lap time formatter
var ms = $prop('DataCorePlugin.GameData.NewData.LastLapTime');
if (ms == null) return '--:--.---';
var totalSec = timespantoseconds(ms);
var min = Math.floor(totalSec / 60);
var sec = totalSec % 60;
return format(min, '0') + ':' + format(sec, '00.000');
```

```javascript
// Persistent counter (using root)
if (root["lapCount"] == null) {
    root["lapCount"] = 0;
    root["lastLap"] = 0;
}
var currentLap = $prop('DataCorePlugin.GameData.NewData.CompletedLaps');
if (currentLap != root["lastLap"] && currentLap > 0) {
    root["lapCount"]++;
    root["lastLap"] = currentLap;
}
return root["lapCount"];
```

---

## Fleck WebSocket Server Setup

### NuGet dependency

In the plugin `.csproj`, add the Fleck NuGet package. For .NET Framework 4.8:

```xml
<PackageReference Include="Fleck" Version="1.2.0" />
```

Or via Package Manager Console:
```
Install-Package Fleck
```

Also add Newtonsoft.Json for serialization (SimHub bundles it, but declaring the dependency is good practice):
```xml
<PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
```

### Full plugin skeleton with Fleck

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using GameReaderCommon;
using SimHub.Plugins;
using Fleck;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SimSteward
{
    [PluginName("SimSteward")]
    [PluginDescription("Sim Steward: HTML dashboard bridge via WebSocket")]
    [PluginAuthor("SimSteward")]
    public class SimStewardPlugin : IPlugin, IDataPlugin
    {
        public PluginManager PluginManager { get; set; }

        private WebSocketServer _wsServer;
        private readonly List<IWebSocketConnection> _clients = new List<IWebSocketConnection>();
        private readonly object _clientLock = new object();

        private const int WS_PORT = 9000;

        public void Init(PluginManager pluginManager)
        {
            pluginManager.AddProperty("SimSteward.State.Connected", this.GetType(), false);
            pluginManager.AddProperty("SimSteward.State.ClientCount", this.GetType(), 0);

            pluginManager.AddAction("SimSteward.Action.Example", this.GetType(), (a, b) =>
            {
                SimHub.Logging.Current.Info("SimSteward: Example action triggered");
            });

            _wsServer = new WebSocketServer($"ws://0.0.0.0:{WS_PORT}");
            _wsServer.Start(socket =>
            {
                socket.OnOpen = () =>
                {
                    lock (_clientLock) { _clients.Add(socket); }
                    SimHub.Logging.Current.Info($"SimSteward: client connected ({_clients.Count} total)");
                };
                socket.OnClose = () =>
                {
                    lock (_clientLock) { _clients.Remove(socket); }
                    SimHub.Logging.Current.Info($"SimSteward: client disconnected ({_clients.Count} total)");
                };
                socket.OnMessage = msg =>
                {
                    HandleClientMessage(pluginManager, socket, msg);
                };
            });

            SimHub.Logging.Current.Info($"SimSteward: WebSocket server started on port {WS_PORT}");
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            int clientCount;
            lock (_clientLock) { clientCount = _clients.Count; }

            pluginManager.SetPropertyValue("SimSteward.State.ClientCount", this.GetType(), clientCount);
            pluginManager.SetPropertyValue("SimSteward.State.Connected", this.GetType(), clientCount > 0);

            if (clientCount == 0 || !data.GameRunning || data.NewData == null)
                return;

            var telemetry = new
            {
                type = "telemetry",
                speed = data.NewData.SpeedKmh,
                rpm = data.NewData.Rpms,
                maxRpm = data.NewData.MaxRpm,
                gear = data.NewData.Gear,
                fuel = data.NewData.Fuel,
                position = data.NewData.Position,
                bestLap = data.NewData.BestLapTime?.ToString() ?? "--",
                lastLap = data.NewData.LastLapTime?.ToString() ?? "--",
                currentLap = data.NewData.CurrentLapTime?.ToString() ?? "--",
                throttle = data.NewData.Throttle,
                brake = data.NewData.Brake,
                isInPit = data.NewData.IsInPit,
                gameName = data.GameName
            };

            var json = JsonConvert.SerializeObject(telemetry);
            List<IWebSocketConnection> snapshot;
            lock (_clientLock) { snapshot = new List<IWebSocketConnection>(_clients); }
            foreach (var client in snapshot)
            {
                try { client.Send(json); }
                catch { /* client likely disconnected, OnClose will handle removal */ }
            }
        }

        private void HandleClientMessage(PluginManager pluginManager, IWebSocketConnection socket, string msg)
        {
            try
            {
                var cmd = JObject.Parse(msg);
                var action = cmd["action"]?.ToString();

                if (string.IsNullOrEmpty(action))
                    return;

                switch (action)
                {
                    case "ping":
                        socket.Send(JsonConvert.SerializeObject(new { type = "pong" }));
                        break;

                    default:
                        SimHub.Logging.Current.Info($"SimSteward: received action '{action}'");
                        socket.Send(JsonConvert.SerializeObject(new
                        {
                            type = "actionResult",
                            action = action,
                            success = true
                        }));
                        break;
                }
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn($"SimSteward: error handling message: {ex.Message}");
            }
        }

        public void End(PluginManager pluginManager)
        {
            if (_wsServer != null)
            {
                _wsServer.Dispose();
                _wsServer = null;
            }
            lock (_clientLock) { _clients.Clear(); }
            SimHub.Logging.Current.Info("SimSteward: WebSocket server stopped");
        }
    }
}
```

### Thread safety notes

- Fleck callbacks (`OnOpen`, `OnClose`, `OnMessage`) run on thread-pool threads.
- `DataUpdate` runs on SimHub's plugin thread (~60 Hz).
- Always lock when reading/writing the `_clients` list.
- `HandleClientMessage` can call `pluginManager.SetPropertyValue` — this is thread-safe in SimHub.

---

## Static File Serving from Plugin

To serve HTML files from the plugin on the same port as the WebSocket, you have two options:

### Option A: Separate TcpListener for HTTP

Run a simple `TcpListener` on a second port (e.g. 9001) that serves static files:

```csharp
private TcpListener _httpListener;
private Thread _httpThread;
private string _webRoot;

private void StartHttpServer(int port)
{
    _webRoot = Path.Combine(
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
        "dashboard"
    );

    _httpListener = new TcpListener(IPAddress.Any, port);
    _httpListener.Start();

    _httpThread = new Thread(() =>
    {
        while (true)
        {
            try
            {
                var client = _httpListener.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(_ => HandleHttpRequest(client));
            }
            catch (SocketException) { break; }
        }
    });
    _httpThread.IsBackground = true;
    _httpThread.Start();
}

private void HandleHttpRequest(TcpClient client)
{
    using (var stream = client.GetStream())
    using (var reader = new StreamReader(stream))
    {
        var requestLine = reader.ReadLine();
        if (requestLine == null) return;

        var parts = requestLine.Split(' ');
        if (parts.Length < 2) return;

        var path = parts[1] == "/" ? "/index.html" : parts[1];
        path = path.Split('?')[0]; // strip query string
        var filePath = Path.Combine(_webRoot, path.TrimStart('/').Replace('/', '\\'));

        byte[] responseBody;
        string status;
        string contentType;

        if (File.Exists(filePath))
        {
            responseBody = File.ReadAllBytes(filePath);
            status = "200 OK";
            contentType = GetMimeType(filePath);
        }
        else
        {
            responseBody = Encoding.UTF8.GetBytes("404 Not Found");
            status = "404 Not Found";
            contentType = "text/plain";
        }

        var header = $"HTTP/1.1 {status}\r\n" +
                     $"Content-Type: {contentType}\r\n" +
                     $"Content-Length: {responseBody.Length}\r\n" +
                     "Connection: close\r\n\r\n";

        var headerBytes = Encoding.UTF8.GetBytes(header);
        stream.Write(headerBytes, 0, headerBytes.Length);
        stream.Write(responseBody, 0, responseBody.Length);
    }
    client.Close();
}

private string GetMimeType(string path)
{
    var ext = Path.GetExtension(path).ToLower();
    switch (ext)
    {
        case ".html": return "text/html; charset=utf-8";
        case ".css": return "text/css; charset=utf-8";
        case ".js": return "application/javascript; charset=utf-8";
        case ".json": return "application/json";
        case ".png": return "image/png";
        case ".jpg":
        case ".jpeg": return "image/jpeg";
        case ".svg": return "image/svg+xml";
        case ".ico": return "image/x-icon";
        case ".woff2": return "font/woff2";
        default: return "application/octet-stream";
    }
}
```

### Option B: SimHub web folder

Place HTML files in SimHub's `Web\sim-steward-dash\` folder (e.g. `C:\Program Files (x86)\SimHub\Web\sim-steward-dash\`). Access at `http://localhost:8888/Web/sim-steward-dash/index.html`. WebSocket connects to `ws://localhost:9000` (or the plugin's configured port). Plugin DLLs go in the **SimHub root**; dashboard static files go in **SimHub\Web\sim-steward-dash\**.

Note: different ports on the same host still count as cross-origin in browsers. The Fleck WebSocket server does not enforce origin checks by default, so WebSocket connections from :8888 to :9000 will work. However, if you add HTTP fetch calls to the plugin server, you would need CORS headers.

---

## HTML Dashboard Template

Minimal HTML dashboard with WebSocket connection and clickable buttons:

```html
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>SimSteward Dashboard</title>
  <link rel="stylesheet" href="style.css">
</head>
<body>
  <div id="dash">
    <div id="connection-status">Connecting...</div>
    <div class="telemetry-grid">
      <div class="gauge">
        <span class="label">Speed</span>
        <span id="speed" class="value">0</span>
        <span class="unit">km/h</span>
      </div>
      <div class="gauge">
        <span class="label">RPM</span>
        <span id="rpm" class="value">0</span>
      </div>
      <div class="gauge">
        <span class="label">Gear</span>
        <span id="gear" class="value">N</span>
      </div>
      <div class="gauge">
        <span class="label">Fuel</span>
        <span id="fuel" class="value">0</span>
        <span class="unit">%</span>
      </div>
    </div>
    <div class="actions">
      <button id="btn-pit" class="action-btn">Request Pit</button>
      <button id="btn-reset" class="action-btn">Reset</button>
    </div>
  </div>
  <script src="app.js"></script>
</body>
</html>
```

```javascript
// app.js — runs in browser, full ES6+
const WS_URL = `ws://${location.hostname || 'localhost'}:9000`;
let ws;

function connect() {
  ws = new WebSocket(WS_URL);
  const statusEl = document.getElementById('connection-status');

  ws.onopen = () => {
    statusEl.textContent = 'Connected';
    statusEl.classList.add('connected');
  };

  ws.onclose = () => {
    statusEl.textContent = 'Disconnected — reconnecting...';
    statusEl.classList.remove('connected');
    setTimeout(connect, 2000);
  };

  ws.onmessage = (event) => {
    const data = JSON.parse(event.data);
    if (data.type === 'telemetry') {
      document.getElementById('speed').textContent = Math.round(data.speed);
      document.getElementById('rpm').textContent = Math.round(data.rpm);
      document.getElementById('gear').textContent = data.gear || 'N';
      document.getElementById('fuel').textContent = Math.round(data.fuel);
    }
  };

  ws.onerror = () => ws.close();
}

function sendAction(action, payload = {}) {
  if (ws && ws.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify({ action, ...payload }));
  }
}

document.getElementById('btn-pit').addEventListener('click', () => sendAction('RequestPit'));
document.getElementById('btn-reset').addEventListener('click', () => sendAction('Reset'));

connect();
```

---

## iRacing Shared Memory Reference

### Enabling shared memory

Set `irsdkEnableMem=1` in `Documents/iRacing/app.ini` (usually already enabled for SimHub users).

### IRSDKSharper setup in a SimHub plugin

Add the NuGet package to the plugin `.csproj`:

```xml
<PackageReference Include="IRSDKSharper" Version="1.1.4" />
```

**Important**: IRSDKSharper targets both `net6.0` and `net471`. The `net471` build is compatible with SimHub's .NET Framework 4.8.

### Incident tracker — full implementation pattern

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using IRSDKSharper;
using Newtonsoft.Json;

public enum IncidentType
{
    Unknown,
    OffTrack,       // 1x — car center crossed illegal surface
    WallContact,    // 2x — hard contact with wall/barrier
    Spin,           // 2x — loss of control / spin
    HeavyContact,   // 4x — heavy car-to-car contact (paved) / 2x on dirt
    LightContact,   // 0x — minor brush (noted, count doesn't increase)
}

public class IncidentEvent
{
    public double SessionTime { get; set; }
    public int SessionNum { get; set; }
    public int CarIdx { get; set; }
    public string DriverName { get; set; }
    public string CarNumber { get; set; }
    public int IncidentDelta { get; set; }   // 0, 1, 2, or 4
    public IncidentType IncidentType { get; set; } = IncidentType.Unknown;
    public int TotalIncidents { get; set; }
    public int Lap { get; set; }
    public double LapDistPct { get; set; }
    public string PhysicsCause { get; set; } // e.g. "G-force 4.2g lat" or "YawRate 2.1 rad/s"
}

public class IncidentTracker
{
    private readonly Dictionary<int, int> _lastKnownIncidents = new Dictionary<int, int>();
    private readonly Dictionary<int, DriverSnapshot> _driverMap = new Dictionary<int, DriverSnapshot>();
    private readonly ConcurrentQueue<IncidentEvent> _pendingEvents = new ConcurrentQueue<IncidentEvent>();
    private int _lastSessionInfoUpdate = -1;

    private class DriverSnapshot
    {
        public string UserName;
        public string CarNumber;
        public int Incidents;
        public int LapsComplete;
    }

    /// <summary>
    /// Call from OnSessionInfo or DataUpdate when SessionInfoUpdate changes.
    /// Parses session YAML and detects incident deltas.
    ///
    /// IMPORTANT LIMITATION: During a live race, ResultsPositions[].Incidents stays 0
    /// for all drivers UNLESS the local client is the session admin. This is enforced
    /// by iRacing's server, not the SDK. During a replay (SimMode == "replay"), all
    /// driver incidents are available regardless of admin status.
    /// </summary>
    public void ProcessSessionInfo(IRacingSdkData data, int sessionNum, double sessionTime)
    {
        int currentUpdate = data.SessionInfoUpdate;
        if (currentUpdate == _lastSessionInfoUpdate)
            return;
        _lastSessionInfoUpdate = currentUpdate;

        var sessionInfo = data.SessionInfo;
        if (sessionInfo == null) return;

        // During a live race (SimMode == "full"), only admins get other drivers'
        // incidents in the YAML. Non-admins see 0 for all other drivers.
        // During a replay (SimMode == "replay"), all data is available.
        bool isReplay = sessionInfo.WeekendInfo?.SimMode == "replay";
        bool isLiveRace = !isReplay;

        // Update driver map from DriverInfo
        if (sessionInfo.DriverInfo?.Drivers != null)
        {
            foreach (var driver in sessionInfo.DriverInfo.Drivers)
            {
                _driverMap[driver.CarIdx] = new DriverSnapshot
                {
                    UserName = driver.UserName,
                    CarNumber = driver.CarNumber,
                    Incidents = driver.CurDriverIncidentCount,
                    LapsComplete = 0
                };
            }
        }

        // Check ResultsPositions for incident changes
        if (sessionInfo.SessionInfo?.Sessions == null) return;

        var session = sessionInfo.SessionInfo.Sessions
            .FirstOrDefault(s => s.SessionNum == sessionNum);
        if (session?.ResultsPositions == null) return;

        foreach (var pos in session.ResultsPositions)
        {
            int carIdx = pos.CarIdx;
            int currentInc = pos.Incidents;

            if (!_lastKnownIncidents.TryGetValue(carIdx, out int previousInc))
            {
                _lastKnownIncidents[carIdx] = currentInc;
                continue;
            }

            if (currentInc > previousInc)
            {
                int delta = currentInc - previousInc;

                string driverName = "Unknown";
                string carNumber = "?";
                if (_driverMap.TryGetValue(carIdx, out var snap))
                {
                    driverName = snap.UserName;
                    carNumber = snap.CarNumber;
                }

                _pendingEvents.Enqueue(new IncidentEvent
                {
                    SessionTime = sessionTime,
                    SessionNum = sessionNum,
                    CarIdx = carIdx,
                    DriverName = driverName,
                    CarNumber = carNumber,
                    IncidentDelta = delta,
                    TotalIncidents = currentInc,
                    Lap = pos.LapsComplete
                });

                _lastKnownIncidents[carIdx] = currentInc;
            }
        }
    }

    /// <summary>
    /// Drain pending events (call from DataUpdate to broadcast via WebSocket).
    /// </summary>
    public List<IncidentEvent> DrainEvents()
    {
        var events = new List<IncidentEvent>();
        while (_pendingEvents.TryDequeue(out var evt))
            events.Add(evt);
        return events;
    }

    /// <summary>
    /// Get current incident totals for all drivers.
    /// </summary>
    public List<object> GetAllDriverIncidents()
    {
        var result = new List<object>();
        foreach (var kvp in _lastKnownIncidents)
        {
            string name = "Unknown";
            string carNum = "?";
            if (_driverMap.TryGetValue(kvp.Key, out var snap))
            {
                name = snap.UserName;
                carNum = snap.CarNumber;
            }
            result.Add(new
            {
                carIdx = kvp.Key,
                driverName = name,
                carNumber = carNum,
                incidents = kvp.Value
            });
        }
        return result;
    }

    public void Reset()
    {
        _lastKnownIncidents.Clear();
        _driverMap.Clear();
        while (_pendingEvents.TryDequeue(out _)) { }
        _lastSessionInfoUpdate = -1;
    }
}
```

### Integrating IncidentTracker into the SimHub plugin

```csharp
// In Init():
_irsdk = new IRacingSdk();
_incidentTracker = new IncidentTracker();

_irsdk.OnSessionInfo += () =>
{
    var sessionNum = _irsdk.Data.GetInt("SessionNum");
    var sessionTime = _irsdk.Data.GetDouble("SessionTime");
    _incidentTracker.ProcessSessionInfo(_irsdk.Data, sessionNum, sessionTime);
};

_irsdk.UpdateInterval = 1; // every frame for maximum fidelity
_irsdk.Start();

// In DataUpdate():
var newEvents = _incidentTracker.DrainEvents();
if (newEvents.Count > 0)
{
    var json = JsonConvert.SerializeObject(new
    {
        type = "incidentEvents",
        events = newEvents
    });
    BroadcastToClients(json);
}
```

### Player incident tracker — per-incident at any replay speed

`PlayerCarMyIncidentCount` is a live telemetry int that updates at the exact frame the incident occurs. At 16x, incidents 1 simulated second apart are still ~4 real frames apart — individually detectable.

```csharp
/// <summary>
/// Tracks PlayerCarMyIncidentCount at 60 Hz (real-clock).
/// The delta value IS the incident type:
///   1 = off-track (1x), 2 = wall contact or spin (2x), 4 = heavy car contact (4x).
/// Physics signals disambiguate 2x (wall vs. spin) and confirm all types.
/// Quick-succession rule: iRacing promotes lower → higher (e.g., 2x spin → 4x contact
/// shows delta=4 not delta=6). The physics layers still see both events.
/// </summary>
public class PlayerIncidentTracker
{
    private int _lastCount = -1;
    private IRacingSdkDatum _incCountDatum;
    private IRacingSdkDatum _sessionTimeDatum;
    private IRacingSdkDatum _lapDatum;
    private IRacingSdkDatum _lapDistPctDatum;
    private IRacingSdkDatum _trackSurfaceDatum;
    private IRacingSdkDatum _yawRateDatum;
    private IRacingSdkDatum _latAccelDatum;
    private IRacingSdkDatum _longAccelDatum;
    private readonly ConcurrentQueue<IncidentEvent> _events = new ConcurrentQueue<IncidentEvent>();
    private bool _initialized;
    private bool _isDirt;

    private const float SPIN_YAW_THRESHOLD = 1.2f;       // rad/s
    private const float WALL_GFORCE_THRESHOLD = 2.0f;     // g

    public void Initialize(IRacingSdkData data, bool isDirt)
    {
        _incCountDatum    = data.TelemetryDataProperties["PlayerCarMyIncidentCount"];
        _sessionTimeDatum = data.TelemetryDataProperties["SessionTime"];
        _lapDatum         = data.TelemetryDataProperties["Lap"];
        _lapDistPctDatum  = data.TelemetryDataProperties["LapDistPct"];
        _trackSurfaceDatum = data.TelemetryDataProperties["PlayerTrackSurface"];
        _yawRateDatum     = data.TelemetryDataProperties["YawRate"];
        _latAccelDatum    = data.TelemetryDataProperties["LatAccel"];
        _longAccelDatum   = data.TelemetryDataProperties["LongAccel"];
        _isDirt = isDirt;
        _lastCount = -1;
        _initialized = true;
    }

    public void Sample(IRacingSdkData data, int playerCarIdx, string playerName, string carNumber)
    {
        if (!_initialized) return;

        int currentCount = data.GetInt(_incCountDatum);
        if (_lastCount < 0) { _lastCount = currentCount; return; }
        if (currentCount <= _lastCount) return;

        int delta = currentCount - _lastCount;
        _lastCount = currentCount;

        float yawRate = Math.Abs(data.GetFloat(_yawRateDatum));
        float latG = Math.Abs(data.GetFloat(_latAccelDatum) / 9.81f);
        float longG = Math.Abs(data.GetFloat(_longAccelDatum) / 9.81f);
        float totalG = (float)Math.Sqrt(latG * latG + longG * longG);
        int trackSurface = data.GetInt(_trackSurfaceDatum);

        IncidentType type = ClassifyFromDelta(delta, totalG, yawRate, trackSurface);

        string cause = BuildPhysicsCause(type, totalG, latG, longG, yawRate, trackSurface);

        _events.Enqueue(new IncidentEvent
        {
            SessionTime = data.GetDouble(_sessionTimeDatum),
            CarIdx = playerCarIdx,
            DriverName = playerName,
            CarNumber = carNumber,
            IncidentDelta = delta,
            IncidentType = type,
            TotalIncidents = currentCount,
            Lap = data.GetInt(_lapDatum),
            LapDistPct = data.GetDouble(_lapDistPctDatum),
            PhysicsCause = cause,
        });
    }

    private IncidentType ClassifyFromDelta(int delta, float totalG, float yawRate, int trackSurface)
    {
        switch (delta)
        {
            case 0: return IncidentType.LightContact;
            case 1: return IncidentType.OffTrack;
            case 4 when !_isDirt:
                return IncidentType.HeavyContact;
            case 2:
                if (_isDirt) return IncidentType.HeavyContact;
                if (totalG > WALL_GFORCE_THRESHOLD) return IncidentType.WallContact;
                if (yawRate > SPIN_YAW_THRESHOLD) return IncidentType.Spin;
                return trackSurface == 0 ? IncidentType.WallContact : IncidentType.Spin;
            default:
                return IncidentType.Unknown;
        }
    }

    private string BuildPhysicsCause(IncidentType type, float totalG, float latG, float longG, float yawRate, int surface)
    {
        var parts = new List<string>();
        if (totalG > 1.0f) parts.Add($"G-force {totalG:F1}g (lat {latG:F1}, long {longG:F1})");
        if (yawRate > 0.5f) parts.Add($"YawRate {yawRate:F2} rad/s");
        if (surface == 0) parts.Add("OffTrack surface");
        return parts.Count > 0 ? string.Join(", ", parts) : type.ToString();
    }

    public List<IncidentEvent> DrainEvents()
    {
        var list = new List<IncidentEvent>();
        while (_events.TryDequeue(out var e)) list.Add(e);
        return list;
    }

    public void Reset() { _lastCount = -1; _initialized = false; while (_events.TryDequeue(out _)) { } }
}
```

### All-car impact detector — G-force from CarIdxLapDistPct

Detects impacts and off-tracks for **all 64 cars** at any replay speed by computing velocity/acceleration from `CarIdxLapDistPct` deltas, and monitoring `CarIdxTrackSurface` transitions.

```csharp
public class AllCarImpactEvent
{
    public double SessionTime { get; set; }
    public int CarIdx { get; set; }
    public string DriverName { get; set; }
    public string CarNumber { get; set; }
    public float GForce { get; set; }       // longitudinal G along track
    public bool OffTrack { get; set; }
    public int Lap { get; set; }
    public float LapDistPct { get; set; }
}

public class AllCarImpactDetector
{
    private const float ONE_G = 9.80665f;
    private const int MAX_CARS = 64;

    public float GForceThreshold { get; set; } = 2.0f; // in g's
    public double CooldownSeconds { get; set; } = 1.5;

    private float _trackLengthMeters;
    private IRacingSdkDatum _lapDistPctDatum;
    private IRacingSdkDatum _trackSurfaceDatum;
    private IRacingSdkDatum _lapDatum;
    private IRacingSdkDatum _sessionTimeDatum;

    private readonly float[] _prevDistPct  = new float[MAX_CARS];
    private readonly float[] _prevVelocity = new float[MAX_CARS];
    private readonly int[]   _prevSurface  = new int[MAX_CARS];
    private readonly double[] _lastFired   = new double[MAX_CARS];
    private double _prevSessionTime;
    private bool _hasPrev;
    private bool _initialized;

    private readonly ConcurrentQueue<AllCarImpactEvent> _events = new ConcurrentQueue<AllCarImpactEvent>();

    // driverMap is shared with IncidentTracker
    private Dictionary<int, (string Name, string Number)> _driverMap = new Dictionary<int, (string, string)>();

    public void SetDriverMap(Dictionary<int, (string Name, string Number)> map) { _driverMap = map; }

    public void Initialize(IRacingSdkData data, float trackLengthMeters)
    {
        _trackLengthMeters = trackLengthMeters;
        _lapDistPctDatum   = data.TelemetryDataProperties["CarIdxLapDistPct"];
        _trackSurfaceDatum = data.TelemetryDataProperties["CarIdxTrackSurface"];
        _lapDatum          = data.TelemetryDataProperties["CarIdxLap"];
        _sessionTimeDatum  = data.TelemetryDataProperties["SessionTime"];
        _hasPrev = false;
        _initialized = true;
        for (int i = 0; i < MAX_CARS; i++) { _prevSurface[i] = -1; _lastFired[i] = -999; }
    }

    public void Sample(IRacingSdkData data)
    {
        if (!_initialized) return;

        double sessionTime = data.GetDouble(_sessionTimeDatum);
        double dt = sessionTime - _prevSessionTime;
        _prevSessionTime = sessionTime;

        if (dt <= 0 || dt > 2.0) { _hasPrev = false; } // skip gaps / session resets

        var distPct = new float[MAX_CARS];
        var surfaces = new int[MAX_CARS];
        var laps = new int[MAX_CARS];
        data.GetFloatArray(_lapDistPctDatum, distPct, 0, MAX_CARS);
        data.GetIntArray(_trackSurfaceDatum, surfaces, 0, MAX_CARS);
        data.GetIntArray(_lapDatum, laps, 0, MAX_CARS);

        for (int i = 0; i < MAX_CARS; i++)
        {
            if (distPct[i] < 0) continue; // car not in world

            if (_hasPrev && _prevDistPct[i] >= 0)
            {
                float dPct = distPct[i] - _prevDistPct[i];
                if (dPct < -0.5f) dPct += 1.0f;      // crossed S/F line
                else if (dPct > 0.5f) dPct -= 1.0f;

                float velocity = (dPct * _trackLengthMeters) / (float)dt;
                float accel    = (velocity - _prevVelocity[i]) / (float)dt;
                float gForce   = accel / ONE_G;

                if (Math.Abs(gForce) > GForceThreshold &&
                    sessionTime - _lastFired[i] > CooldownSeconds)
                {
                    _lastFired[i] = sessionTime;
                    var name = _driverMap.TryGetValue(i, out var d) ? d.Name : "Unknown";
                    var num  = _driverMap.TryGetValue(i, out var d2) ? d2.Number : "?";
                    _events.Enqueue(new AllCarImpactEvent
                    {
                        SessionTime = sessionTime,
                        CarIdx = i,
                        DriverName = name,
                        CarNumber = num,
                        GForce = gForce,
                        OffTrack = false,
                        Lap = laps[i],
                        LapDistPct = distPct[i],
                    });
                }

                _prevVelocity[i] = velocity;
            }

            // Track surface transitions: OnTrack(3) → OffTrack(0) = off-track event
            if (_prevSurface[i] == 3 && surfaces[i] == 0 &&
                sessionTime - _lastFired[i] > CooldownSeconds)
            {
                _lastFired[i] = sessionTime;
                var name = _driverMap.TryGetValue(i, out var d) ? d.Name : "Unknown";
                var num  = _driverMap.TryGetValue(i, out var d2) ? d2.Number : "?";
                _events.Enqueue(new AllCarImpactEvent
                {
                    SessionTime = sessionTime,
                    CarIdx = i,
                    DriverName = name,
                    CarNumber = num,
                    GForce = 0,
                    OffTrack = true,
                    Lap = laps[i],
                    LapDistPct = distPct[i],
                });
            }

            _prevDistPct[i] = distPct[i];
            _prevSurface[i] = surfaces[i];
        }

        _hasPrev = true;
    }

    public List<AllCarImpactEvent> DrainEvents()
    {
        var list = new List<AllCarImpactEvent>();
        while (_events.TryDequeue(out var e)) list.Add(e);
        return list;
    }

    public void Reset()
    {
        _hasPrev = false; _initialized = false;
        while (_events.TryDequeue(out _)) { }
    }
}
```

### 16x replay speed — design notes

The iRacing SDK shared memory TickRate is fixed at **60 per real second** regardless of replay speed (confirmed in `irsdk_defines.h`). At 16x replay speed:

- Each real-second tick still fires 60 times, but `SessionTime` advances by ~0.267 simulated seconds per tick (vs ~0.017s at 1x).
- `ReplayPlaySpeed` (int telemetry variable) tells you the current multiplier.
- `PlayerCarMyIncidentCount` still increments per-incident per-frame — the player's individual incidents are fully capturable at 16x.
- `CarIdxLapDistPct` and `CarIdxTrackSurface` arrays still update every frame for all cars — G-force and off-track detection works at 16x with ~0.27s resolution.
- The session YAML update (`SessionInfoUpdate` counter) is **event-driven**. At 16x, multiple driver incidents can collapse into one `Incidents` delta. Use G-force/track-surface events to decompose.
- **Frame dropping**: IRSDKSharper's `Data.FramesDropped` counter shows missed frames. Keep `OnTelemetryData` handlers fast (< 1ms).
- **Broadcast API**: Use `ReplaySearch(RpySrch_NextIncident)` to step through every incident frame, or `ReplaySearchSessionTime(sessionNum, sessionTimeMS)` to seek to a specific moment.

### iRacing live telemetry variables (complete per-car-index list)

Per-car arrays (indexed by `CarIdx`, 0 to ~63):

| Variable | Type | Description |
|----------|------|-------------|
| `CarIdxLap` | int[] | Current lap number |
| `CarIdxLapCompleted` | int[] | Laps completed |
| `CarIdxLapDistPct` | float[] | Track position (0.0–1.0) |
| `CarIdxPosition` | int[] | Overall position |
| `CarIdxClassPosition` | int[] | In-class position |
| `CarIdxF2Time` | float[] | Race time behind leader |
| `CarIdxEstTime` | float[] | Estimated time around track |
| `CarIdxOnPitRoad` | bool[] | On pit road |
| `CarIdxTrackSurface` | int[] | Track surface enum (on track, pit, off-world) |
| `CarIdxTrackSurfaceMaterial` | int[] | Surface material enum |
| `CarIdxGear` | int[] | Current gear |
| `CarIdxRPM` | float[] | Engine RPM |
| `CarIdxSteer` | float[] | Steering angle |
| `CarIdxPaceLine` | int[] | Pace car line index |
| `CarIdxPaceRow` | int[] | Pace car row |
| `CarIdxPaceFlags` | uint[] | Pace flags bitfield |
| `CarIdxBestLapTime` | float[] | Best lap time |
| `CarIdxLastLapTime` | float[] | Last lap time |
| `CarIdxSessionFlags` | uint[] | Per-car session flags |

Player-only telemetry:

| Variable | Type | Description |
|----------|------|-------------|
| `PlayerCarIdx` | int | Player's CarIdx |
| `PlayerCarMyIncidentCount` | int | Player's own incident total |
| `PlayerCarTeamIncidentCount` | int | Player's team incident total |
| `PlayerCarDriverIncidentCount` | int | Current driver's incidents (team racing) |
| `PlayerCarPosition` | int | Player's race position |
| `PlayerCarClassPosition` | int | Player's in-class position |
| `PlayerCarTowTime` | float | Time remaining for tow (>0 = towing) |
| `PlayerCarInPitStall` | bool | Car is in pit stall |
| `PlayerCarWeightPenalty` | float | Weight penalty (kg) |
| `PlayerCarPowerAdjust` | float | Power adjustment (%) |

Session variables:

| Variable | Type | Description |
|----------|------|-------------|
| `SessionTime` | double | Seconds since session start |
| `SessionTick` | int | Current update number |
| `SessionNum` | int | Current session number |
| `SessionState` | int | Session state enum |
| `SessionFlags` | uint | Global session flags bitfield |
| `SessionTimeRemain` | double | Seconds remaining |
| `SessionLapsRemainEx` | int | Laps remaining |
| `ReplayPlaySpeed` | int | Current replay speed (1 = normal, 16 = 16x) |
| `ReplaySessionNum` | int | Session number being replayed |
| `ReplaySessionTime` | double | Session time of replay position |

### Session info YAML — key sections for incident tracking

```yaml
WeekendInfo:
  TrackName: "daytona international speedway"
  WeekendOptions:
    IncidentLimit: "unlimited"  # or a number like "17"

DriverInfo:
  DriverCarIdx: 18       # player's own CarIdx
  Drivers:
    - CarIdx: 0
      UserName: "John Doe"
      AbbrevName: "J. Doe"
      CarNumber: "42"
      CarNumberRaw: 42
      CarClassID: 1234
      CarClassShortName: "GT3"
      CurDriverIncidentCount: 3
      TeamIncidentCount: 5
      IRating: 2500
      LicLevel: 20
      LicString: "A 4.99"
      LicColor: "0x00ff00"
      IsSpectator: 0
      CarIsPaceCar: 0
    # ... repeated for every driver in session

SessionInfo:
  Sessions:
    - SessionNum: 0
      SessionType: "Practice"
      ResultsPositions:
        - Position: 1
          CarIdx: 5
          Lap: 12
          FastestLap: 8
          FastestTime: 85.4321
          LastTime: 86.1234
          LapsLed: 0
          LapsComplete: 12
          LapsDriven: 0.0
          Incidents: 2        # <-- incident total for this driver
          ReasonOutId: 0
          ReasonOutStr: "Running"
        # ... repeated for every driver in session

    - SessionNum: 1
      SessionType: "Race"
      ResultsPositions:
        # same structure
```

### Physics telemetry for crash/incident detection (player car only)

All variables below are **player-car only**, updated at 60 Hz. For other cars, only CarIdx array variables are available.

#### Acceleration (inertial)

| Variable | Unit | Notes |
|----------|------|-------|
| `LongAccel` | m/s² | + = forward, − = braking / frontal impact. Includes gravity on hills. |
| `LatAccel` | m/s² | + = left, − = right. Spikes = side impact or snap oversteer. |
| `VertAccel` | m/s² | + = up. Spikes on kerbs, airborne landings. |
| `LongAccel_ST` | m/s² | Supra-tick filtered version (higher accuracy for analysis). |
| `LatAccel_ST` | m/s² | Supra-tick filtered. |
| `VertAccel_ST` | m/s² | Supra-tick filtered. |

To convert to g's: `gForce = accel_ms2 / 9.80665`

#### Rotation rates (spin / weight transfer)

| Variable | Unit | Notes |
|----------|------|-------|
| `YawRate` | rad/s | Vertical axis rotation. High = spin onset or violent snap. |
| `PitchRate` | rad/s | Lateral axis. Pitching under braking / airborne. |
| `RollRate` | rad/s | Longitudinal axis. Car rolling on kerb or banking. |
| `Yaw` | rad | Absolute yaw heading. |
| `Pitch` | rad | Absolute pitch. |
| `Roll` | rad | Absolute roll. |

#### Velocity

| Variable | Unit | Notes |
|----------|------|-------|
| `Speed` | m/s | GPS vehicle speed. |
| `VelocityX` | m/s | World-frame X (typically forward). |
| `VelocityY` | m/s | World-frame Y (lateral). |
| `VelocityZ` | m/s | World-frame Z (vertical). |

#### Wheel / tire

| Variable | Unit | Notes |
|----------|------|-------|
| `LFspeed` | m/s | LF wheel hub speed. Compare vs `Speed` to detect slip/lockup. |
| `RFspeed` | m/s | RF wheel hub speed. |
| `LRspeed` | m/s | LR wheel hub speed. |
| `RRspeed` | m/s | RR wheel hub speed. |
| `LFpressure` | kPa | LF cold pressure as set. |
| `LFtempL/M/R` | °C | LF tire surface temperature (left, mid, right strip). |
| `LFwearL/M/R` | % | LF tire tread remaining per strip. |

**Tire slip ratio** (not a direct variable — calculate):
```
slipRatio[corner] = (wheelSpeed - vehicleSpeed) / max(vehicleSpeed, 1.0)
// > 0.10 = wheelspin; < -0.10 = brake lockup
```

#### Suspension

| Variable | Unit | Notes |
|----------|------|-------|
| `LFshockDefl` | m | LF shock deflection (travel). |
| `RFshockDefl` | m | RF shock deflection. |
| `LRshockDefl` | m | LR shock deflection. |
| `RRshockDefl` | m | RR shock deflection. |
| `LFshockVel` | m/s | LF shock velocity. + = compression (hitting kerb). |
| `RFshockVel` | m/s | RF shock velocity. |
| `LRshockVel` | m/s | LR shock velocity. |
| `RRshockVel` | m/s | RR shock velocity. |
| `LFrideHeight` | m | LF ride height. |
| `RFrideHeight` | m | RF ride height. |
| `LRrideHeight` | m | LR ride height. |
| `RRrideHeight` | m | RR ride height. |

**Weight transfer** (no direct variable):
- Under braking: `LFshockDefl` and `RFshockDefl` increase; `LRshockDefl` and `RRshockDefl` decrease.
- Under cornering: diagonal pairs shift. Monitor `LFshockDefl - RRshockDefl` and `RFshockDefl - LRshockDefl`.
- `LongAccel` and `LatAccel` are the primary signal; suspension deflection gives secondary corroboration.

#### Crash / incident detection thresholds (tunable starting points)

| Signal | Variable | Threshold | Typical cause |
|--------|----------|-----------|---------------|
| Frontal impact | `abs(LongAccel) > 29.4` (3g) | — | Contact with wall or car ahead |
| Side impact | `abs(LatAccel) > 29.4` (3g) | — | T-bone or barrier contact |
| Airborne / kerb | `abs(VertAccel) > 19.6` (2g) | — | Big kerb, ramp, jump |
| Spin onset | `abs(YawRate) > 1.2 rad/s` AND `abs(LatAccel) > 14.7` (1.5g) | — | Combined = weight transfer into spin |
| Kerb / bottoming | `max(abs(shockVel)) > 1.5 m/s` | — | Hard suspension contact |
| Tire lockup | `vehicleSpeed - minWheelSpeed > 5 m/s` | — | Brake lockup |
| Tire spinout | `maxWheelSpeed - vehicleSpeed > 10 m/s` | — | Drive wheel spin |

---

### iRacing session flags (bitfield)

```
irsdk_checkered    = 0x00000001
irsdk_white        = 0x00000002
irsdk_green        = 0x00000004
irsdk_yellow       = 0x00000008
irsdk_red          = 0x00000010
irsdk_blue         = 0x00000020
irsdk_debris       = 0x00000040
irsdk_crossed      = 0x00000080
irsdk_yellowWaving = 0x00000100
irsdk_oneLapToGreen = 0x00000200
irsdk_greenHeld    = 0x00000400
irsdk_tenToGo      = 0x00000800
irsdk_fiveToGo     = 0x00001000
irsdk_randomWaving = 0x00002000
irsdk_caution      = 0x00004000
irsdk_cautionWaving = 0x00008000
irsdk_black        = 0x00010000
irsdk_disqualify   = 0x00020000
irsdk_servicible   = 0x00040000 (car can be serviced)
irsdk_furled       = 0x00080000
irsdk_repair       = 0x00100000
```

### TrackSurface enum values

```
irsdk_NotInWorld       = -1
irsdk_OffTrack         = 0
irsdk_InPitStall       = 1
irsdk_AproachingPits   = 2
irsdk_OnTrack          = 3
```

---

## .csproj Template (SDK-style, net48)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <OutputType>Library</OutputType>
    <AssemblyName>SimStewardPlugin</AssemblyName>
    <RootNamespace>SimSteward</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Fleck" Version="1.2.0" />
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
    <PackageReference Include="IRSDKSharper" Version="1.1.4" />
  </ItemGroup>

  <ItemGroup>
    <!-- SimHub references — adjust paths to your SimHub installation -->
    <Reference Include="SimHub.Plugins">
      <HintPath>C:\Program Files (x86)\SimHub\SimHub.Plugins.dll</HintPath>
      <Private>false</Private>
    </Reference>
    <Reference Include="GameReaderCommon">
      <HintPath>C:\Program Files (x86)\SimHub\GameReaderCommon.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

After building, copy the output DLL (and Fleck.dll if not merged) to the **SimHub root** folder. Restart SimHub and enable the plugin in Settings > Plugins.

---

## Deployment Checklist

1. Build the plugin in Release mode.
2. Copy `SimStewardPlugin.dll` and `Fleck.dll` to the **SimHub root** (e.g. `C:\Program Files (x86)\SimHub\`).
3. If using Option A (plugin-hosted HTTP), also copy the `dashboard/` folder next to the DLL.
4. If using Option B (SimHub web folder), copy HTML files to **SimHub\Web\sim-steward-dash\** (e.g. `C:\Program Files (x86)\SimHub\Web\sim-steward-dash\`).
5. Launch SimHub. Activate the plugin in Settings > Plugins.
6. Open browser to `http://<ip>:9001/` (Option A) or `http://<ip>:8888/Web/sim-steward-dash/index.html` (Option B).
7. The WebSocket connects automatically to `ws://<ip>:9000`.
