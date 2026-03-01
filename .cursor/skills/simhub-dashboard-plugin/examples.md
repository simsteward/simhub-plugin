# SimHub Dashboard + Plugin Examples

## Example 1: Live Speed Gauge (HTML Dashboard)

A speed gauge that updates in real-time via WebSocket from the plugin.

### Plugin side (C#)

The telemetry broadcast in `DataUpdate` already includes speed. No extra code needed beyond the skeleton in [reference.md](reference.md). The broadcast payload includes `speed` (km/h as a double).

### Dashboard HTML

```html
<div class="speed-gauge">
  <svg viewBox="0 0 200 120" class="gauge-svg">
    <path d="M 20 100 A 80 80 0 0 1 180 100" fill="none" stroke="#333" stroke-width="12" stroke-linecap="round"/>
    <path id="speed-arc" d="M 20 100 A 80 80 0 0 1 180 100" fill="none" stroke="#00ff88" stroke-width="12" stroke-linecap="round" stroke-dasharray="0 251"/>
  </svg>
  <div class="speed-value">
    <span id="speed-num">0</span>
    <span class="speed-unit">km/h</span>
  </div>
</div>
```

### Dashboard JavaScript (browser ES6+)

```javascript
const MAX_SPEED = 300;
const ARC_LENGTH = 251; // approximate length of the SVG arc path

function updateSpeedGauge(speed) {
  const clamped = Math.min(speed, MAX_SPEED);
  const fraction = clamped / MAX_SPEED;
  const dashLen = fraction * ARC_LENGTH;

  document.getElementById('speed-arc').setAttribute(
    'stroke-dasharray', `${dashLen} ${ARC_LENGTH}`
  );
  document.getElementById('speed-num').textContent = Math.round(speed);

  const arc = document.getElementById('speed-arc');
  if (speed > 250) {
    arc.setAttribute('stroke', '#ff3333');
  } else if (speed > 180) {
    arc.setAttribute('stroke', '#ffaa00');
  } else {
    arc.setAttribute('stroke', '#00ff88');
  }
}

// Called from ws.onmessage handler:
// updateSpeedGauge(data.speed);
```

### Dashboard CSS

```css
.speed-gauge {
  position: relative;
  width: 200px;
  text-align: center;
}
.gauge-svg {
  width: 100%;
}
.speed-value {
  position: absolute;
  bottom: 10px;
  left: 50%;
  transform: translateX(-50%);
  font-size: 2rem;
  font-weight: bold;
  color: #fff;
}
.speed-unit {
  font-size: 0.8rem;
  color: #888;
  display: block;
}
```

---

## Example 2: Pit Request Button (Dashboard → Plugin Action)

A clickable button that sends a "RequestPit" action to the plugin. The plugin receives it, processes it, and confirms back.

### Plugin side (C#)

```csharp
public void Init(PluginManager pluginManager)
{
    pluginManager.AddProperty("SimSteward.State.PitRequested", this.GetType(), false);

    // ... WebSocket server setup ...
}

private void HandleClientMessage(PluginManager pm, IWebSocketConnection socket, string msg)
{
    var cmd = JObject.Parse(msg);
    var action = cmd["action"]?.ToString();

    switch (action)
    {
        case "RequestPit":
            var current = (bool)pm.GetPropertyValue("SimSteward.State.PitRequested");
            pm.SetPropertyValue("SimSteward.State.PitRequested", this.GetType(), !current);

            socket.Send(JsonConvert.SerializeObject(new
            {
                type = "actionResult",
                action = "RequestPit",
                pitRequested = !current
            }));

            SimHub.Logging.Current.Info($"SimSteward: Pit request toggled to {!current}");
            break;
    }
}
```

### Dashboard HTML

```html
<button id="btn-pit" class="action-btn">
  <span class="btn-icon">🏁</span>
  <span class="btn-label">Request Pit</span>
</button>
<div id="pit-status" class="status-badge">PIT: OFF</div>
```

### Dashboard JavaScript (browser ES6+)

```javascript
const pitBtn = document.getElementById('btn-pit');
const pitStatus = document.getElementById('pit-status');

pitBtn.addEventListener('click', () => {
  sendAction('RequestPit');
  pitBtn.classList.add('pending');
});

// In ws.onmessage handler:
function handleMessage(data) {
  if (data.type === 'actionResult' && data.action === 'RequestPit') {
    pitBtn.classList.remove('pending');
    if (data.pitRequested) {
      pitStatus.textContent = 'PIT: REQUESTED';
      pitStatus.classList.add('active');
      pitBtn.classList.add('active');
    } else {
      pitStatus.textContent = 'PIT: OFF';
      pitStatus.classList.remove('active');
      pitBtn.classList.remove('active');
    }
  }
}
```

### Dashboard CSS

```css
.action-btn {
  padding: 12px 24px;
  font-size: 1rem;
  font-weight: bold;
  border: 2px solid #555;
  border-radius: 8px;
  background: #222;
  color: #fff;
  cursor: pointer;
  transition: all 0.2s;
  user-select: none;
  -webkit-tap-highlight-color: transparent;
}
.action-btn:hover {
  background: #333;
  border-color: #888;
}
.action-btn:active {
  transform: scale(0.95);
}
.action-btn.active {
  background: #1a472a;
  border-color: #00ff88;
  color: #00ff88;
}
.action-btn.pending {
  opacity: 0.6;
  pointer-events: none;
}

.status-badge {
  display: inline-block;
  padding: 4px 12px;
  border-radius: 4px;
  font-size: 0.85rem;
  font-weight: bold;
  background: #333;
  color: #888;
  margin-top: 8px;
}
.status-badge.active {
  background: #1a472a;
  color: #00ff88;
}
```

---

## Example 3: Gear Indicator (Live Telemetry Display)

A large gear display that changes color based on RPM percentage and flashes at shift point.

### Plugin side

No extra code beyond the standard telemetry broadcast. The payload includes `gear`, `rpm`, and `maxRpm`.

### Dashboard HTML

```html
<div id="gear-display" class="gear-container">
  <span id="gear-value" class="gear-text">N</span>
</div>
```

### Dashboard JavaScript (browser ES6+)

```javascript
const SHIFT_THRESHOLD = 0.92; // flash when RPM > 92% of max
let flashInterval = null;

function updateGearDisplay(gear, rpm, maxRpm) {
  const gearEl = document.getElementById('gear-value');
  const containerEl = document.getElementById('gear-display');

  gearEl.textContent = gear || 'N';

  const rpmPct = maxRpm > 0 ? rpm / maxRpm : 0;

  if (rpmPct >= SHIFT_THRESHOLD) {
    if (!flashInterval) {
      flashInterval = setInterval(() => {
        containerEl.classList.toggle('flash');
      }, 100);
    }
    containerEl.style.color = '#ff3333';
  } else {
    if (flashInterval) {
      clearInterval(flashInterval);
      flashInterval = null;
      containerEl.classList.remove('flash');
    }
    if (rpmPct > 0.75) {
      containerEl.style.color = '#ffaa00';
    } else if (rpmPct > 0.5) {
      containerEl.style.color = '#00ff88';
    } else {
      containerEl.style.color = '#ffffff';
    }
  }
}

// Called from ws.onmessage handler:
// updateGearDisplay(data.gear, data.rpm, data.maxRpm);
```

### Dashboard CSS

```css
.gear-container {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 120px;
  height: 120px;
  border: 3px solid #444;
  border-radius: 16px;
  background: #111;
  transition: color 0.15s;
}
.gear-text {
  font-size: 4rem;
  font-weight: bold;
  font-family: 'Courier New', monospace;
}
.gear-container.flash {
  background: #ff3333;
}
.gear-container.flash .gear-text {
  color: #fff !important;
}
```

---

## Example 4: Full WebSocket Integration (Wiring It All Together)

A complete `app.js` showing how the WebSocket client connects, receives telemetry, handles action results, and wires up all buttons.

```javascript
const WS_PORT = 9000;
const WS_URL = `ws://${location.hostname || 'localhost'}:${WS_PORT}`;
let ws;
let reconnectTimer = null;

function connect() {
  ws = new WebSocket(WS_URL);

  ws.onopen = () => {
    document.getElementById('connection-status').textContent = 'Connected';
    document.getElementById('connection-status').className = 'status connected';
    if (reconnectTimer) {
      clearTimeout(reconnectTimer);
      reconnectTimer = null;
    }
  };

  ws.onclose = () => {
    document.getElementById('connection-status').textContent = 'Disconnected';
    document.getElementById('connection-status').className = 'status disconnected';
    reconnectTimer = setTimeout(connect, 2000);
  };

  ws.onerror = () => {
    ws.close();
  };

  ws.onmessage = (event) => {
    const data = JSON.parse(event.data);

    switch (data.type) {
      case 'telemetry':
        updateSpeedGauge(data.speed);
        updateGearDisplay(data.gear, data.rpm, data.maxRpm);
        document.getElementById('fuel').textContent = Math.round(data.fuel);
        document.getElementById('position').textContent = data.position;
        document.getElementById('best-lap').textContent = data.bestLap;
        document.getElementById('last-lap').textContent = data.lastLap;
        document.getElementById('throttle-bar').style.width = `${data.throttle}%`;
        document.getElementById('brake-bar').style.width = `${data.brake}%`;
        break;

      case 'actionResult':
        handleActionResult(data);
        break;

      case 'incidentEvents':
        handleIncidentEvents(data.events);
        break;

      case 'incidentSnapshot':
        handleIncidentSnapshot(data.drivers);
        break;

      case 'pong':
        break;
    }
  };
}

function sendAction(action, payload = {}) {
  if (ws && ws.readyState === WebSocket.OPEN) {
    ws.send(JSON.stringify({ action, ...payload }));
  }
}

function handleActionResult(data) {
  switch (data.action) {
    case 'RequestPit':
      // Update pit button UI (see Example 2)
      break;
    case 'Reset':
      break;
  }
}

// Wire up buttons
document.querySelectorAll('[data-action]').forEach(btn => {
  btn.addEventListener('click', () => {
    sendAction(btn.dataset.action);
  });
});

connect();
```

Usage in HTML with `data-action` attributes:

```html
<button data-action="RequestPit" class="action-btn">Request Pit</button>
<button data-action="Reset" class="action-btn">Reset</button>
<button data-action="ToggleOverlay" class="action-btn">Toggle Overlay</button>
```

---

## Example 5: All-Driver Incident Tracker Dashboard (iRacing)

A live incident log showing every driver, their incident count, and when incidents occurred — including at 16x replay speed. The plugin reads iRacing shared memory directly via IRSDKSharper, tracks incident deltas per driver, and broadcasts events + full driver list to the HTML dashboard via WebSocket.

### Plugin side (C# — iRacing-specific incident tracking)

```csharp
using IRSDKSharper;

// Add these fields to your SimStewardPlugin class:
private IRacingSdk _irsdk;
private IncidentTracker _incidentTracker;
private int _lastSessionNum = -1;

// In Init():
public void Init(PluginManager pluginManager)
{
    // ... existing WebSocket setup from reference.md skeleton ...

    _incidentTracker = new IncidentTracker();

    _irsdk = new IRacingSdk();
    _irsdk.UpdateInterval = 1; // every frame for max fidelity at 16x
    _irsdk.OnSessionInfo += () =>
    {
        var sessionNum = _irsdk.Data.GetInt("SessionNum");
        var sessionTime = _irsdk.Data.GetDouble("SessionTime");
        _incidentTracker.ProcessSessionInfo(_irsdk.Data, sessionNum, sessionTime);
    };
    _irsdk.OnConnected += () =>
    {
        SimHub.Logging.Current.Info("SimSteward: iRacing connected via IRSDKSharper");
    };
    _irsdk.OnDisconnected += () =>
    {
        _incidentTracker.Reset();
        SimHub.Logging.Current.Info("SimSteward: iRacing disconnected");
    };
    _irsdk.Start();
}

// In DataUpdate():
public void DataUpdate(PluginManager pluginManager, ref GameData data)
{
    // ... existing telemetry broadcast ...

    // Drain any incident events and broadcast them
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

    // Periodically send full driver incident snapshot (e.g. every 2 seconds)
    // so newly connected clients get current state
    if (_frameCount % 120 == 0)
    {
        var snapshot = _incidentTracker.GetAllDriverIncidents();
        if (snapshot.Count > 0)
        {
            var json = JsonConvert.SerializeObject(new
            {
                type = "incidentSnapshot",
                drivers = snapshot
            });
            BroadcastToClients(json);
        }
    }
    _frameCount++;
}

// In End():
public void End(PluginManager pluginManager)
{
    _irsdk?.Stop();
    // ... existing cleanup ...
}
```

### Dashboard HTML

```html
<div class="incident-panel">
  <h2>Incident Log</h2>
  <div class="incident-controls">
    <button id="btn-clear-log" data-action="ClearIncidentLog" class="action-btn small">Clear Log</button>
    <span id="total-incidents" class="total-badge">Total: 0</span>
  </div>

  <table id="incident-table" class="data-table">
    <thead>
      <tr>
        <th>Time</th>
        <th>Car #</th>
        <th>Driver</th>
        <th>Inc</th>
        <th>Total</th>
        <th>Lap</th>
      </tr>
    </thead>
    <tbody id="incident-log"></tbody>
  </table>

  <h2>Driver Standings</h2>
  <table class="data-table">
    <thead>
      <tr>
        <th>Car #</th>
        <th>Driver</th>
        <th>Incidents</th>
      </tr>
    </thead>
    <tbody id="driver-incidents"></tbody>
  </table>
</div>
```

### Dashboard JavaScript (browser ES6+)

```javascript
const incidentLog = [];
const driverIncidents = new Map(); // carIdx -> {driverName, carNumber, incidents}

function handleIncidentEvents(events) {
  for (const evt of events) {
    incidentLog.unshift(evt); // newest first

    driverIncidents.set(evt.carIdx, {
      driverName: evt.driverName,
      carNumber: evt.carNumber,
      incidents: evt.totalIncidents
    });
  }
  renderIncidentLog();
  renderDriverIncidents();
}

function handleIncidentSnapshot(drivers) {
  for (const d of drivers) {
    driverIncidents.set(d.carIdx, {
      driverName: d.driverName,
      carNumber: d.carNumber,
      incidents: d.incidents
    });
  }
  renderDriverIncidents();
}

function formatSessionTime(seconds) {
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  const s = Math.floor(seconds % 60);
  if (h > 0) return `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
  return `${m}:${String(s).padStart(2, '0')}`;
}

function renderIncidentLog() {
  const tbody = document.getElementById('incident-log');
  tbody.innerHTML = '';

  let totalInc = 0;
  for (const evt of incidentLog) {
    totalInc += evt.incidentDelta;
    const row = document.createElement('tr');
    row.className = evt.incidentDelta >= 4 ? 'incident-severe' :
                    evt.incidentDelta >= 2 ? 'incident-moderate' : '';
    row.innerHTML = `
      <td>${formatSessionTime(evt.sessionTime)}</td>
      <td>${evt.carNumber}</td>
      <td>${evt.driverName}</td>
      <td class="incident-delta">${evt.incidentDelta}x</td>
      <td>${evt.totalIncidents}</td>
      <td>${evt.lap}</td>
    `;
    tbody.appendChild(row);
  }

  document.getElementById('total-incidents').textContent = `Total: ${totalInc}`;
}

function renderDriverIncidents() {
  const tbody = document.getElementById('driver-incidents');
  const sorted = [...driverIncidents.entries()]
    .sort((a, b) => b[1].incidents - a[1].incidents);

  tbody.innerHTML = '';
  for (const [, driver] of sorted) {
    const row = document.createElement('tr');
    row.innerHTML = `
      <td>${driver.carNumber}</td>
      <td>${driver.driverName}</td>
      <td class="incident-count">${driver.incidents}</td>
    `;
    tbody.appendChild(row);
  }
}

// Add to ws.onmessage switch:
// case 'incidentEvents':
//   handleIncidentEvents(data.events);
//   break;
// case 'incidentSnapshot':
//   handleIncidentSnapshot(data.drivers);
//   break;
```

### Dashboard CSS

```css
.incident-panel {
  max-width: 700px;
  margin: 0 auto;
  font-family: 'Segoe UI', sans-serif;
}

.incident-controls {
  display: flex;
  align-items: center;
  gap: 16px;
  margin-bottom: 12px;
}

.total-badge {
  font-size: 1.1rem;
  font-weight: bold;
  color: #ffaa00;
}

.data-table {
  width: 100%;
  border-collapse: collapse;
  margin-bottom: 24px;
}

.data-table th,
.data-table td {
  padding: 6px 10px;
  text-align: left;
  border-bottom: 1px solid #333;
  font-size: 0.9rem;
}

.data-table th {
  background: #1a1a2e;
  color: #aaa;
  font-weight: 600;
  text-transform: uppercase;
  font-size: 0.75rem;
  letter-spacing: 0.5px;
}

.data-table tbody tr:hover {
  background: #1a1a2e;
}

.incident-delta {
  font-weight: bold;
  color: #ffaa00;
}

.incident-severe {
  background: rgba(255, 50, 50, 0.15);
}

.incident-severe .incident-delta {
  color: #ff3333;
}

.incident-moderate {
  background: rgba(255, 170, 0, 0.1);
}

.incident-count {
  font-weight: bold;
  font-variant-numeric: tabular-nums;
}

.action-btn.small {
  padding: 6px 14px;
  font-size: 0.8rem;
}
```

---

## Example 6: NCalc Formula in Dash Studio (Native Component)

For native Dash Studio components (not HTML), bind properties using NCalc or Jint. This is the **Jint (ES5.1)** context, NOT browser JS.

### Speed text with conditional color (NCalc)

Bind `Text` property:
```
format([DataCorePlugin.GameData.NewData.SpeedLocal], '0')
```

Bind `Color` property:
```
if([DataCorePlugin.GameData.NewData.SpeedLocal] > 200, '#FF3333', if([DataCorePlugin.GameData.NewData.SpeedLocal] > 100, '#FFAA00', '#FFFFFF'))
```

### Gear display with shift flash (Jint)

Bind `Visible` property (use javascript):
```javascript
var rpm = $prop('DataCorePlugin.GameData.NewData.Rpms');
var maxRpm = $prop('DataCorePlugin.GameData.NewData.MaxRpm');
if (maxRpm == 0) return true;
var pct = rpm / maxRpm;
if (pct > 0.92) {
    if (root["blink"] == null) root["blink"] = 0;
    root["blink"]++;
    return root["blink"] % 4 < 2;
}
return true;
```

### Plugin property in binding (NCalc)

Read a custom plugin property in a Dash Studio text component:
```
if([SimSteward.State.PitRequested], 'PIT REQUESTED', '')
```

---

## Example 7: Physics-Based Crash / Incident Detection (Per-Frame Telemetry)

This example shows how to detect potential incidents in real-time by sampling per-frame iRacing telemetry for all cars — G-force spikes, lateral/longitudinal acceleration, weight transfer, and tire slip. This runs in the `OnTelemetryData` callback (60 Hz) and is completely separate from the YAML-based incident count tracker (Example 5).

### What signals indicate an incident or crash

| Signal | Variable(s) | Threshold | Meaning |
|--------|-------------|-----------|---------|
| Longitudinal deceleration spike | `LongAccel` (player) | < −3.0 g (−29.4 m/s²) | Hard braking or frontal impact |
| Lateral G-force spike | `LatAccel` (player) | > ±3.0 g | Side impact, major snap oversteer |
| Vertical G spike | `VertAccel` (player) | > ±3.0 g | Airborne / kerb impact |
| Per-car G-force (replay) | `CarIdxGForce[n]` (calculated) | > ±2.0 g | IRSDKSharper event system |
| Longitudinal tire slip (all 4 wheels) | `LFtireSlip`, `RFtireSlip`, etc. | > 0.15–0.25 | Wheelspin, lockup |
| Lateral tire slip (cornering) | `LFlatTireSlip`, etc. | High combined | Oversteer / understeer |
| Wheel speed vs. vehicle speed delta | `LFspeed`, `RFspeed`, etc. vs. `Speed` | Sustained difference | Tire locking / spinning |
| Suspension jounce rate | `LFshockVel`, `RFshockVel`, etc. | High positive spike | Kerb hit, bottoming out |

**Note on player-only vs. all-cars**: Most physics telemetry variables (`LongAccel`, `LatAccel`, `VertAccel`, tire slip, shock data) are **player-car only**. For all other cars, only `CarIdxLapDistPct`, `CarIdxGear`, `CarIdxRPM`, and `CarIdxSteer` are available live. The per-car G-force approximation is a calculated track from IRSDKSharper's event system based on `CarIdxLapDistPct` velocity changes.

### Plugin side (C# — physics incident detector)

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using IRSDKSharper;

public enum IncidentSignalType
{
    LongAccelSpike,      // Frontal impact / hard braking
    LatAccelSpike,       // Side impact / snap oversteer
    VertAccelSpike,      // Airborne / kerb
    TireLockup,          // Wheel speed << vehicle speed (brake lockup)
    TireSpinout,         // Wheel speed >> vehicle speed (wheelspin)
    SuspensionImpact,    // Shock velocity spike (kerb, bottoming)
    WeightTransferExtreme, // Combined: yaw rate + lateral accel extreme
}

public class PhysicsIncidentEvent
{
    public double SessionTime { get; set; }
    public string Signal { get; set; }
    public float Value { get; set; }
    public float Threshold { get; set; }
    public int CarIdx { get; set; }       // -1 = player car
    public string DriverName { get; set; }
    public int Lap { get; set; }
    public float LapDistPct { get; set; } // 0.0 – 1.0, where on track
}

public class PhysicsIncidentDetector
{
    // Thresholds — tune these based on the category/car
    public float LongAccelThreshold { get; set; } = 29.4f;  // 3.0g in m/s²
    public float LatAccelThreshold  { get; set; } = 29.4f;  // 3.0g in m/s²
    public float VertAccelThreshold { get; set; } = 19.6f;  // 2.0g in m/s²
    public float ShockVelThreshold  { get; set; } = 1.5f;   // m/s shock velocity
    public float TireLockThreshold  { get; set; } = 5.0f;   // m/s wheel-vs-car delta
    public float YawRateThreshold   { get; set; } = 1.2f;   // rad/s (~70°/s)

    // Cooldown prevents the same signal from spamming (in simulated seconds)
    private readonly Dictionary<string, double> _lastFired = new Dictionary<string, double>();
    private const double CooldownSeconds = 1.5;

    private readonly ConcurrentQueue<PhysicsIncidentEvent> _events = new ConcurrentQueue<PhysicsIncidentEvent>();

    // Pre-cache datum references for speed — call after IRSDKSharper connects
    private IRacingSdkDatum _longAccel, _latAccel, _vertAccel;
    private IRacingSdkDatum _yawRate;
    private IRacingSdkDatum _lfShockVel, _rfShockVel, _lrShockVel, _rrShockVel;
    private IRacingSdkDatum _lfSpeed, _rfSpeed, _lrSpeed, _rrSpeed;
    private IRacingSdkDatum _speed;
    private IRacingSdkDatum _sessionTime, _lap, _lapDistPct;
    private bool _initialized = false;

    public void Initialize(IRacingSdkData data)
    {
        _longAccel  = data.TelemetryDataProperties["LongAccel"];
        _latAccel   = data.TelemetryDataProperties["LatAccel"];
        _vertAccel  = data.TelemetryDataProperties["VertAccel"];
        _yawRate    = data.TelemetryDataProperties["YawRate"];
        _lfShockVel = data.TelemetryDataProperties["LFshockVel"];
        _rfShockVel = data.TelemetryDataProperties["RFshockVel"];
        _lrShockVel = data.TelemetryDataProperties["LRshockVel"];
        _rrShockVel = data.TelemetryDataProperties["RRshockVel"];
        _lfSpeed    = data.TelemetryDataProperties["LFspeed"];
        _rfSpeed    = data.TelemetryDataProperties["RFspeed"];
        _lrSpeed    = data.TelemetryDataProperties["LRspeed"];
        _rrSpeed    = data.TelemetryDataProperties["RRspeed"];
        _speed      = data.TelemetryDataProperties["Speed"];
        _sessionTime = data.TelemetryDataProperties["SessionTime"];
        _lap        = data.TelemetryDataProperties["Lap"];
        _lapDistPct = data.TelemetryDataProperties["LapDistPct"];
        _initialized = true;
    }

    public void Sample(IRacingSdkData data, string playerName, int playerCarIdx)
    {
        if (!_initialized) return;

        var sessionTime = data.GetDouble(_sessionTime);
        var lap  = data.GetInt(_lap);
        var dist = data.GetFloat(_lapDistPct);

        float longAccel = data.GetFloat(_longAccel);
        float latAccel  = data.GetFloat(_latAccel);
        float vertAccel = data.GetFloat(_vertAccel);
        float yawRate   = data.GetFloat(_yawRate);
        float speed     = data.GetFloat(_speed);  // m/s

        // Longitudinal impact / hard braking
        // LongAccel is positive = forward, negative = braking / frontal impact
        if (Math.Abs(longAccel) > LongAccelThreshold)
            FireEvent(sessionTime, "LongAccelSpike", longAccel, LongAccelThreshold,
                      playerCarIdx, playerName, lap, dist);

        // Lateral impact / snap
        if (Math.Abs(latAccel) > LatAccelThreshold)
            FireEvent(sessionTime, "LatAccelSpike", latAccel, LatAccelThreshold,
                      playerCarIdx, playerName, lap, dist);

        // Vertical / airborne / kerb
        if (Math.Abs(vertAccel) > VertAccelThreshold)
            FireEvent(sessionTime, "VertAccelSpike", vertAccel, VertAccelThreshold,
                      playerCarIdx, playerName, lap, dist);

        // Yaw rate extreme — combined with latAccel indicates weight transfer / spin onset
        if (Math.Abs(yawRate) > YawRateThreshold && Math.Abs(latAccel) > 14.7f /* 1.5g */)
            FireEvent(sessionTime, "WeightTransferExtreme", yawRate, YawRateThreshold,
                      playerCarIdx, playerName, lap, dist);

        // Shock velocity spikes — kerb or bottoming impact
        float maxShock = Math.Max(
            Math.Max(Math.Abs(data.GetFloat(_lfShockVel)), Math.Abs(data.GetFloat(_rfShockVel))),
            Math.Max(Math.Abs(data.GetFloat(_lrShockVel)), Math.Abs(data.GetFloat(_rrShockVel)))
        );
        if (maxShock > ShockVelThreshold)
            FireEvent(sessionTime, "SuspensionImpact", maxShock, ShockVelThreshold,
                      playerCarIdx, playerName, lap, dist);

        // Tire lockup detection: wheel speed significantly less than vehicle speed
        if (speed > 5.0f)  // only meaningful when moving
        {
            float minWheelSpeed = Math.Min(
                Math.Min(data.GetFloat(_lfSpeed), data.GetFloat(_rfSpeed)),
                Math.Min(data.GetFloat(_lrSpeed), data.GetFloat(_rrSpeed))
            );
            if (speed - minWheelSpeed > TireLockThreshold)
                FireEvent(sessionTime, "TireLockup", speed - minWheelSpeed, TireLockThreshold,
                          playerCarIdx, playerName, lap, dist);

            // Tire spinout: wheel speed significantly greater than vehicle speed
            float maxWheelSpeed = Math.Max(
                Math.Max(data.GetFloat(_lfSpeed), data.GetFloat(_rfSpeed)),
                Math.Max(data.GetFloat(_lrSpeed), data.GetFloat(_rrSpeed))
            );
            if (maxWheelSpeed - speed > TireLockThreshold * 2)
                FireEvent(sessionTime, "TireSpinout", maxWheelSpeed - speed, TireLockThreshold * 2,
                          playerCarIdx, playerName, lap, dist);
        }
    }

    private void FireEvent(double sessionTime, string signal, float value, float threshold,
                           int carIdx, string driverName, int lap, float dist)
    {
        string key = $"{carIdx}:{signal}";
        if (_lastFired.TryGetValue(key, out double lastTime) &&
            sessionTime - lastTime < CooldownSeconds)
            return;

        _lastFired[key] = sessionTime;
        _events.Enqueue(new PhysicsIncidentEvent
        {
            SessionTime  = sessionTime,
            Signal       = signal,
            Value        = value,
            Threshold    = threshold,
            CarIdx       = carIdx,
            DriverName   = driverName,
            Lap          = lap,
            LapDistPct   = dist,
        });
    }

    public List<PhysicsIncidentEvent> DrainEvents()
    {
        var list = new List<PhysicsIncidentEvent>();
        while (_events.TryDequeue(out var e)) list.Add(e);
        return list;
    }

    public void Reset()
    {
        _lastFired.Clear();
        while (_events.TryDequeue(out _)) { }
        _initialized = false;
    }
}
```

### Wiring into the plugin

```csharp
// In plugin class fields:
private PhysicsIncidentDetector _physicsDetector;
private string _playerName = "Unknown";
private int _playerCarIdx = -1;

// In Init():
_physicsDetector = new PhysicsIncidentDetector();

_irsdk.OnConnected += () =>
{
    _physicsDetector.Initialize(_irsdk.Data);
};

_irsdk.OnSessionInfo += () =>
{
    // Cache player name and carIdx from session YAML
    var playerCarIdx = _irsdk.Data.GetInt("PlayerCarIdx");
    _playerCarIdx = playerCarIdx;
    var drivers = _irsdk.Data.SessionInfo?.DriverInfo?.Drivers;
    if (drivers != null)
    {
        foreach (var d in drivers)
        {
            if (d.CarIdx == playerCarIdx)
            {
                _playerName = d.UserName;
                break;
            }
        }
    }
};

_irsdk.OnTelemetryData += () =>
{
    _physicsDetector.Sample(_irsdk.Data, _playerName, _playerCarIdx);
};

// In DataUpdate():
var physicsEvents = _physicsDetector.DrainEvents();
if (physicsEvents.Count > 0)
{
    var json = JsonConvert.SerializeObject(new
    {
        type = "physicsEvents",
        events = physicsEvents
    });
    BroadcastToClients(json);
}
```

### Correlating physics events with YAML incidents

A physics event fires when something **feels like** an incident — but iRacing may or may not award incident points. Cross-referencing the two gives rich context:

```csharp
// In your IncidentTracker.ProcessSessionInfo, after detecting a delta:
// Store the sessionTime of the delta. In the dashboard, search physicsEvents
// within ± CooldownSeconds of that sessionTime to find the physical cause.
```

The dashboard can then display:
- "Driver X received 2x at T=12:34 — G-force spike detected: +3.4g lateral, shock impact 1.8 m/s"

---

### Telemetry variables used (player car, all at 60 Hz)

| Variable | Unit | Notes |
|----------|------|-------|
| `LongAccel` | m/s² | + = forward, − = braking/frontal impact. Includes gravity component on hills. |
| `LatAccel` | m/s² | + = left, − = right. High values = side impact or snap. |
| `VertAccel` | m/s² | + = up, − = down. Spikes on kerbs, airborne landings. |
| `YawRate` | rad/s | Rotation rate around vertical axis. High = spin onset. |
| `PitchRate` | rad/s | Rotation rate around lateral axis. |
| `RollRate` | rad/s | Rotation rate around longitudinal axis. |
| `LFshockVel` | m/s | Left-front suspension velocity. + = compression. |
| `RFshockVel` | m/s | Right-front suspension velocity. |
| `LRshockVel` | m/s | Left-rear suspension velocity. |
| `RRshockVel` | m/s | Right-rear suspension velocity. |
| `LFshockDefl` | m | Left-front suspension deflection (travel). |
| `RFshockDefl` | m | Right-front suspension deflection. |
| `LRshockDefl` | m | Left-rear suspension deflection. |
| `RRshockDefl` | m | Right-rear suspension deflection. |
| `LFspeed` | m/s | Left-front wheel hub speed. |
| `RFspeed` | m/s | Right-front wheel hub speed. |
| `LRspeed` | m/s | Left-rear wheel hub speed. |
| `RRspeed` | m/s | Right-rear wheel hub speed. |
| `Speed` | m/s | GPS vehicle speed. |
| `VelocityX` | m/s | Longitudinal velocity in world frame. |
| `VelocityY` | m/s | Lateral velocity in world frame. |
| `VelocityZ` | m/s | Vertical velocity in world frame. |
| `LFtempL/M/R` | °C | LF tire surface temps (left/mid/right strip). |
| `RFtempL/M/R` | °C | RF tire surface temps. |
| `LRtempL/M/R` | °C | LR tire surface temps. |
| `RRtempL/M/R` | °C | RR tire surface temps. |

**Tire slip ratio** is not directly exposed as a single variable. It must be approximated from wheel hub speed vs. vehicle speed:
```
slipRatio = (wheelSpeed - vehicleSpeed) / max(vehicleSpeed, 1.0)
// > 0.10 = wheelspin; < -0.10 = lockup
```

**Weight transfer** (shift of load onto a corner) is inferred from shock deflection ratios and lateral/longitudinal acceleration, not a direct variable. Use the rate of change of `LFshockDefl` vs `RRshockDefl` (diagonal) during hard braking/cornering.

---

## Example 8: Physics Event Stream — Dashboard Display

A live scrolling event feed on the dashboard that shows both YAML incidents (official points) and physics signals (detected forces), correlated by session time.

### Dashboard JavaScript (browser ES6+)

```javascript
// Unified event log combining YAML incidents + physics signals
const eventLog = [];

const SIGNAL_LABELS = {
  LongAccelSpike:       { label: 'Impact (long)',  css: 'sig-impact',   icon: '💥' },
  LatAccelSpike:        { label: 'Impact (lat)',   css: 'sig-impact',   icon: '💥' },
  VertAccelSpike:       { label: 'Airborne/Kerb',  css: 'sig-kerb',     icon: '🛞' },
  TireLockup:           { label: 'Tire Lockup',    css: 'sig-tire',     icon: '🔒' },
  TireSpinout:          { label: 'Wheelspin',      css: 'sig-tire',     icon: '🌀' },
  SuspensionImpact:     { label: 'Kerb/Shock',     css: 'sig-kerb',     icon: '⚡' },
  WeightTransferExtreme:{ label: 'Weight Transfer',css: 'sig-weight',   icon: '⚖️' },
};

function formatSessionTime(s) {
  const m = Math.floor(s / 60), sec = Math.floor(s % 60);
  return `${m}:${String(sec).padStart(2,'0')}`;
}

function handlePhysicsEvents(events) {
  for (const evt of events) {
    const gValue = (Math.abs(evt.value) / 9.80665).toFixed(2);
    eventLog.unshift({
      kind: 'physics',
      sessionTime: evt.sessionTime,
      signal: evt.signal,
      driverName: evt.driverName,
      carIdx: evt.carIdx,
      lap: evt.lap,
      detail: `${gValue}g`,
      lapDistPct: evt.lapDistPct,
    });
  }
  if (eventLog.length > 200) eventLog.length = 200;
  renderEventLog();
}

// Incident type labels from the delta value and plugin classification
const INCIDENT_TYPE_LABELS = {
  OffTrack:      { label: 'Off-Track',           css: 'inc-offtrack',  icon: '🟡' },
  WallContact:   { label: 'Wall Contact',        css: 'inc-wall',      icon: '🧱' },
  Spin:          { label: 'Spin / Loss of Ctrl',  css: 'inc-spin',      icon: '🔄' },
  HeavyContact:  { label: 'Heavy Car Contact',   css: 'inc-contact',   icon: '💥' },
  LightContact:  { label: 'Light Contact',       css: 'inc-light',     icon: '·' },
  Unknown:       { label: 'Incident',            css: 'inc-unknown',   icon: '❓' },
};

function handleIncidentEvents(events) {
  for (const evt of events) {
    const typeInfo = INCIDENT_TYPE_LABELS[evt.incidentType] || INCIDENT_TYPE_LABELS.Unknown;
    eventLog.unshift({
      kind: 'incident',
      sessionTime: evt.sessionTime,
      driverName: evt.driverName,
      carNumber: evt.carNumber,
      lap: evt.lap,
      delta: evt.incidentDelta,
      total: evt.totalIncidents,
      incidentType: evt.incidentType,
      typeLabel: typeInfo.label,
      typeIcon: typeInfo.icon,
      typeCss: typeInfo.css,
      physicsCause: evt.physicsCause || null,
      lapDistPct: evt.lapDistPct,
    });
    // Also look for nearby physics events to add extra context
    const related = eventLog.filter(e =>
      e.kind === 'physics' &&
      Math.abs(e.sessionTime - evt.sessionTime) < 2.0
    );
    if (related.length > 0) {
      eventLog[0].physicsCorrelation = related.map(r =>
        SIGNAL_LABELS[r.signal]?.label || r.signal
      ).join(', ');
    }
  }
  if (eventLog.length > 200) eventLog.length = 200;
  renderEventLog();
}

function renderEventLog() {
  const container = document.getElementById('event-stream');
  container.innerHTML = '';

  for (const evt of eventLog) {
    const div = document.createElement('div');

    if (evt.kind === 'incident') {
      div.className = `event-entry event-incident ${evt.typeCss} sev-${evt.delta >= 4 ? 'high' : evt.delta >= 2 ? 'mid' : 'low'}`;
      div.innerHTML = `
        <span class="evt-time">${formatSessionTime(evt.sessionTime)}</span>
        <span class="evt-badge inc-badge">${evt.delta}x ${evt.typeIcon} ${evt.typeLabel}</span>
        <span class="evt-driver">${evt.carNumber} ${evt.driverName}</span>
        <span class="evt-lap">L${evt.lap}</span>
        <span class="evt-total">(total: ${evt.total})</span>
        ${evt.physicsCause ? `<span class="evt-cause">↳ ${evt.physicsCause}</span>` : ''}
        ${evt.physicsCorrelation ? `<span class="evt-corr">↳ Signals: ${evt.physicsCorrelation}</span>` : ''}
      `;
    } else {
      const meta = SIGNAL_LABELS[evt.signal] || { label: evt.signal, css: '', icon: '•' };
      div.className = `event-entry event-physics ${meta.css}`;
      div.innerHTML = `
        <span class="evt-time">${formatSessionTime(evt.sessionTime)}</span>
        <span class="evt-badge phy-badge">${meta.icon} ${meta.label}</span>
        <span class="evt-driver">${evt.driverName}</span>
        <span class="evt-lap">L${evt.lap}</span>
        <span class="evt-detail">${evt.detail}</span>
      `;
    }

    container.appendChild(div);
  }
}

// Wire into main ws.onmessage:
// case 'physicsEvents':   handlePhysicsEvents(data.events); break;
// case 'incidentEvents':  handleIncidentEvents(data.events); break;
```

### Dashboard HTML

```html
<div class="event-panel">
  <div class="event-panel-header">
    <h2>Live Event Stream</h2>
    <div class="legend">
      <span class="legend-inc">■ Official Inc</span>
      <span class="legend-phy">■ Physics Signal</span>
    </div>
  </div>
  <div id="event-stream" class="event-stream"></div>
</div>
```

### Dashboard CSS

```css
.event-panel {
  max-width: 800px;
  font-family: 'Segoe UI', monospace;
}

.event-panel-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 10px;
}

.legend { font-size: 0.75rem; color: #888; }
.legend-inc { color: #ff6b6b; margin-right: 12px; }
.legend-phy { color: #6bc5ff; }

.event-stream {
  max-height: 500px;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: 3px;
}

.event-entry {
  display: flex;
  align-items: baseline;
  gap: 8px;
  padding: 5px 10px;
  border-radius: 4px;
  font-size: 0.83rem;
  border-left: 3px solid transparent;
}

/* Official incident rows — severity */
.event-incident { background: rgba(255, 80, 80, 0.08); }
.sev-high { border-left-color: #ff3333; background: rgba(255, 50, 50, 0.18); }
.sev-mid  { border-left-color: #ffaa00; background: rgba(255, 170, 0, 0.12); }
.sev-low  { border-left-color: #ffdd88; }

/* Incident type sub-classes */
.inc-offtrack { border-left-color: #e8d44d; }
.inc-wall     { border-left-color: #ff8855; }
.inc-spin     { border-left-color: #ffaa00; }
.inc-contact  { border-left-color: #ff3333; }
.inc-light    { border-left-color: #666; }
.inc-unknown  { border-left-color: #999; }

/* Physics signal rows */
.event-physics { background: rgba(80, 150, 255, 0.07); border-left-color: #3a7bd5; }
.sig-impact  { border-left-color: #ff6b6b; background: rgba(255, 100, 100, 0.1); }
.sig-kerb    { border-left-color: #ffaa00; }
.sig-tire    { border-left-color: #a0e0ff; }
.sig-weight  { border-left-color: #c084fc; }

.evt-time   { color: #888; min-width: 50px; font-variant-numeric: tabular-nums; }
.evt-badge  { font-weight: bold; min-width: 130px; }
.inc-badge  { color: #ff6b6b; }
.phy-badge  { color: #6bc5ff; }
.evt-driver { color: #eee; flex: 1; }
.evt-lap    { color: #888; font-size: 0.75rem; }
.evt-total  { color: #888; font-size: 0.75rem; }
.evt-detail { color: #ffd580; font-size: 0.75rem; font-variant-numeric: tabular-nums; }
.evt-cause, .evt-corr  {
  display: block;
  width: 100%;
  color: #a0c4ff;
  font-size: 0.72rem;
  padding-left: 118px;
  margin-top: 2px;
}
.evt-corr { color: #88bbdd; }
```

---

## Example 9: G-Force Spike Meter (Live Player Car HUD Element)

A real-time lateral/longitudinal G-force display with a peak-hold indicator — useful as a HUD element to confirm that thresholds are tuned correctly during testing.

### Plugin side

No extra C# needed — extend the existing telemetry payload to include the physics channels:

```csharp
var telemetry = new
{
    type = "telemetry",
    // ... existing fields ...
    longAccelG = data.GetFloat("LongAccel") / 9.80665f,
    latAccelG  = data.GetFloat("LatAccel")  / 9.80665f,
    vertAccelG = data.GetFloat("VertAccel") / 9.80665f,
    yawRate    = data.GetFloat("YawRate"),
    lfShockVel = data.GetFloat("LFshockVel"),
    rfShockVel = data.GetFloat("RFshockVel"),
    lrShockVel = data.GetFloat("LRshockVel"),
    rrShockVel = data.GetFloat("RRshockVel"),
    lfSpeed    = data.GetFloat("LFspeed"),
    rfSpeed    = data.GetFloat("RFspeed"),
    lrSpeed    = data.GetFloat("LRspeed"),
    rrSpeed    = data.GetFloat("RRspeed"),
    speed      = data.GetFloat("Speed"),
};
```

### Dashboard HTML

```html
<div class="gforce-hud">
  <div class="gforce-circle">
    <canvas id="gforce-canvas" width="180" height="180"></canvas>
    <div class="gforce-readout">
      <span id="g-lat">0.0</span> / <span id="g-long">0.0</span>
      <small>Lat / Long (g)</small>
    </div>
  </div>
  <div class="peak-hold">
    Peak: <span id="g-peak">0.0</span>g
  </div>
  <div class="shock-bars">
    <div class="shock-label">Suspension</div>
    <div class="shock-row">
      <div class="shock-bar-wrap"><div id="shock-lf" class="shock-bar"></div><span>LF</span></div>
      <div class="shock-bar-wrap"><div id="shock-rf" class="shock-bar"></div><span>RF</span></div>
    </div>
    <div class="shock-row">
      <div class="shock-bar-wrap"><div id="shock-lr" class="shock-bar"></div><span>LR</span></div>
      <div class="shock-bar-wrap"><div id="shock-rr" class="shock-bar"></div><span>RR</span></div>
    </div>
  </div>
</div>
```

### Dashboard JavaScript (browser ES6+)

```javascript
const canvas = document.getElementById('gforce-canvas');
const ctx = canvas.getContext('2d');
const CX = 90, CY = 90, R = 80;
const MAX_G = 4.0;

let peakG = 0;
let peakDecayTimer = null;

function updateGForceDisplay(latG, longG, lfShock, rfShock, lrShock, rrShock, speed, lfSpd, rfSpd, lrSpd, rrSpd) {
  // --- G-force circle plot ---
  ctx.clearRect(0, 0, 180, 180);

  // Background rings
  ctx.strokeStyle = '#333';
  ctx.lineWidth = 1;
  [1, 2, 3, 4].forEach(g => {
    ctx.beginPath();
    ctx.arc(CX, CY, (g / MAX_G) * R, 0, Math.PI * 2);
    ctx.stroke();
  });

  // Crosshairs
  ctx.strokeStyle = '#444';
  ctx.beginPath(); ctx.moveTo(CX - R, CY); ctx.lineTo(CX + R, CY); ctx.stroke();
  ctx.beginPath(); ctx.moveTo(CX, CY - R); ctx.lineTo(CX, CY + R); ctx.stroke();

  // G-dot — latG maps to X, longG maps to Y (negative long = braking = downward)
  const dotX = CX + (latG / MAX_G) * R;
  const dotY = CY - (longG / MAX_G) * R;
  const totalG = Math.sqrt(latG * latG + longG * longG);

  const hue = Math.min(totalG / 3.0, 1.0);
  const color = `hsl(${120 - hue * 120}, 100%, 55%)`;

  ctx.beginPath();
  ctx.arc(dotX, dotY, 6, 0, Math.PI * 2);
  ctx.fillStyle = color;
  ctx.fill();

  // Update numeric readout
  document.getElementById('g-lat').textContent  = latG.toFixed(2);
  document.getElementById('g-long').textContent = longG.toFixed(2);

  // Peak hold
  if (totalG > peakG) {
    peakG = totalG;
    clearTimeout(peakDecayTimer);
    peakDecayTimer = setTimeout(() => { peakG = 0; }, 5000);
  }
  document.getElementById('g-peak').textContent = peakG.toFixed(2);

  // --- Suspension shock bars ---
  const MAX_SHOCK = 2.0; // m/s
  ['lf', 'rf', 'lr', 'rr'].forEach((corner, i) => {
    const vel = [lfShock, rfShock, lrShock, rrShock][i];
    const pct = Math.min(Math.abs(vel) / MAX_SHOCK, 1.0) * 100;
    const bar = document.getElementById(`shock-${corner}`);
    bar.style.height = `${pct}%`;
    bar.style.background = vel > 1.0 ? '#ff6b6b' : vel > 0.5 ? '#ffaa00' : '#00d4aa';
  });

  // --- Tire slip (wheelspin / lockup indicator) ---
  const wheelSpeeds = [lfSpd, rfSpd, lrSpd, rrSpd];
  const maxSlip = Math.max(...wheelSpeeds.map(w => Math.abs(w - speed)));
  // You can add a slip indicator element here if desired
}

// In ws.onmessage, call when type === 'telemetry':
// updateGForceDisplay(
//   data.latAccelG, data.longAccelG,
//   data.lfShockVel, data.rfShockVel, data.lrShockVel, data.rrShockVel,
//   data.speed, data.lfSpeed, data.rfSpeed, data.lrSpeed, data.rrSpeed
// );
```

### Dashboard CSS (additions)

```css
.gforce-hud {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 12px;
  background: #111;
  padding: 16px;
  border-radius: 12px;
  width: 220px;
}

.gforce-circle {
  position: relative;
}

.gforce-readout {
  position: absolute;
  bottom: -28px;
  left: 50%;
  transform: translateX(-50%);
  text-align: center;
  font-size: 1rem;
  font-weight: bold;
  color: #fff;
  white-space: nowrap;
}

.gforce-readout small {
  display: block;
  font-size: 0.65rem;
  color: #888;
  font-weight: normal;
}

.peak-hold {
  margin-top: 30px;
  font-size: 0.9rem;
  color: #ffd580;
}

.shock-bars {
  width: 100%;
}

.shock-label {
  font-size: 0.7rem;
  color: #888;
  text-transform: uppercase;
  letter-spacing: 1px;
  margin-bottom: 6px;
}

.shock-row {
  display: flex;
  gap: 8px;
  margin-bottom: 6px;
}

.shock-bar-wrap {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 4px;
}

.shock-bar-wrap span {
  font-size: 0.65rem;
  color: #666;
}

.shock-bar {
  width: 28px;
  height: 0%;
  max-height: 50px;
  border-radius: 2px;
  transition: height 0.05s, background 0.1s;
  align-self: flex-end;
  background: #00d4aa;
}
```
