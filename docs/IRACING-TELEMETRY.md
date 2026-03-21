# iRacing Telemetry — SDK Variable Reference

This document describes the telemetry data available from the iRacing SDK (via IRSDKSharper) and how it maps to this plugin's data pipeline.

---

## How variables are accessed

The SDK exposes all telemetry via string-keyed typed getters. The full list of available variables is **discovered at runtime** from the iRacing shared memory header — not hardcoded:

```csharp
// Enumerate everything iRacing is broadcasting right now
foreach (var kv in _irsdk.Data.TelemetryDataProperties)
{
    // kv.Key        = variable name, e.g. "Speed"
    // kv.Value.Desc = human description
    // kv.Value.Unit = "m/s", "pct", "C", etc.
    // kv.Value.Count  = 1 for scalar, 64 for CarIdx[] arrays
    // kv.Value.VarType = IRacingSdkVarType (float, int, bool, bitField, double, char)
}
```

Typed getters for scalars and per-car arrays:

```csharp
float  speed    = _irsdk.Data.GetFloat("Speed");
int    gear     = _irsdk.Data.GetInt("Gear");
double sesTime  = _irsdk.Data.GetDouble("SessionTime");
float  lapDist  = _irsdk.Data.GetFloat("CarIdxLapDistPct", carIdx); // per-car
```

For hot-path reads, cache the `IRacingSdkDatum` to skip the dictionary lookup:

```csharp
var speedDatum = _irsdk.Data.TelemetryDataProperties["Speed"];
float speed = _irsdk.Data.GetFloat(speedDatum); // faster, no string lookup
```

---

## Variable categories

### Car inputs

| Variable | Type | Unit | Description |
|---|---|---|---|
| `Throttle` | float | % (0–1) | Driver throttle application |
| `Brake` | float | % (0–1) | Driver brake application |
| `Clutch` | float | % (0–1) | Driver clutch application |
| `Gear` | int | — | Current gear (-1=reverse, 0=neutral) |
| `SteeringWheelAngle` | float | rad | Steering wheel angle |
| `SteeringWheelTorque` | float | N·m | Force feedback torque |
| `ShiftIndicatorPct` | float | % (0–1) | Shift light indicator |
| `HandbrakeRaw` | float | % (0–1) | Handbrake |

### Car motion / dynamics

| Variable | Type | Unit | Description |
|---|---|---|---|
| `Speed` | float | m/s | Car speed |
| `RPM` | float | r/min | Engine RPM |
| `Lat`, `Lon`, `Alt` | float | deg / m | GPS position |
| `VelocityX/Y/Z` | float | m/s | World-space velocity |
| `Yaw`, `Pitch`, `Roll` | float | rad | Orientation |
| `YawRate`, `PitchRate`, `RollRate` | float | rad/s | Rotation rates |
| `LatAccel` | float | m/s² | Lateral acceleration |
| `LongAccel` | float | m/s² | Longitudinal acceleration |
| `VertAccel` | float | m/s² | Vertical acceleration |

### Tires

Each corner has a prefix: `LF` (left-front), `RF`, `LR`, `RR`.

| Variable pattern | Type | Unit | Description |
|---|---|---|---|
| `{c}tempCL/CM/CR` | float | °C | Surface temp: inner / middle / outer |
| `{c}wearL/M/R` | float | % (0–1) | Wear: inner / middle / outer (0=new) |
| `{c}pressure` | float | kPa | Cold pressure |
| `{c}rideHeight` | float | m | Ride height at corner |
| `{c}shockDefl` | float | m | Shock deflection |
| `{c}shockVel` | float | m/s | Shock velocity |
| `{c}suspDefl` | float | m | Suspension deflection |
| `{c}brakeLinePress` | float | bar | Brake line pressure at corner |

Example: `LFtempCL` = left-front tire inner surface temp.

### Engine / fuel

| Variable | Type | Unit | Description |
|---|---|---|---|
| `FuelLevel` | float | l | Fuel remaining |
| `FuelLevelPct` | float | % (0–1) | Fuel remaining as % of capacity |
| `FuelUsePerHour` | float | l/hr | Current fuel consumption rate |
| `OilTemp` | float | °C | Oil temperature |
| `OilPress` | float | bar | Oil pressure |
| `WaterTemp` | float | °C | Coolant temperature |
| `ManifoldPress` | float | bar | Intake manifold pressure |
| `FuelPress` | float | bar | Fuel pressure |
| `Voltage` | float | V | Electrical voltage |

### Lap / track position

| Variable | Type | Unit | Description |
|---|---|---|---|
| `LapDistPct` | float | % (0–1) | Player lap distance |
| `LapDist` | float | m | Absolute track position |
| `Lap` | int | — | Current lap (player) |
| `LapCurrentLapTime` | float | s | In-progress lap time |
| `LapLastLapTime` | float | s | Completed lap time |
| `LapBestLapTime` | float | s | Best lap this session |
| `LapDeltaToSessionBestLap` | float | s | Delta to session best |
| `LapDeltaToOptimalLap` | float | s | Delta to personal optimal |
| `LapLasNLapSeq` | int | — | Lap sequence counter |

### Session state

| Variable | Type | Description |
|---|---|---|
| `SessionTime` | double | Elapsed session time (seconds) |
| `SessionNum` | int | Current session number within event |
| `SessionState` | int | State enum (practice / qual / race) |
| `SessionFlags` | bitField | Green / yellow / checkered / etc. |
| `PaceMode` | int | Pace mode enum |
| `ReplayFrameNum` | int | Current replay frame |
| `ReplayFrameNumEnd` | int | Total replay frames (length) |
| `ReplayPlaySpeed` | int | Replay playback speed multiplier |
| `CamCarIdx` | int | Car index currently on camera |
| `PlayerCarIdx` | int | Player's own car index |

> **Note:** `ReplayFrameNum` and `ReplayFrameNumEnd` are **inverted** from their names in the plugin. The plugin field `frame` stores `ReplayFrameNum` (current position) and `frameEnd` stores `ReplayFrameNumEnd` (total length). Do not rename — this is a known SDK quirk that is documented and accepted.

### Per-car arrays (all cars, indexed 0–63)

These use `GetFloat(name, carIdx)` / `GetInt(name, carIdx)`:

| Variable | Type | Description |
|---|---|---|
| `CarIdxLapDistPct` | float | Lap distance % per car |
| `CarIdxLap` | int | Current lap per car |
| `CarIdxPosition` | int | Race position per car |
| `CarIdxClassPosition` | int | Class position per car |
| `CarIdxGear` | int | Current gear per car |
| `CarIdxRPM` | float | RPM per car |
| `CarIdxF2Time` | float | Estimated time behind leader |
| `CarIdxEstTime` | float | Estimated lap time |
| `CarIdxTrackSurface` | int | Surface type (enum: track/pit/off) |
| `CarIdxTrackSurfaceMaterial` | int | Surface material enum |
| `CarIdxOnPitRoad` | bool | On pit road flag |
| `CarIdxSteer` | float | Steering angle per car |
| `CarIdxThrottle` | float | Throttle per car |
| `CarIdxBrake` | float | Brake per car |
| `CarIdxP2P_Status` | bool | Push-to-pass active |
| `CarIdxP2P_Count` | int | Push-to-pass activations remaining |

### IRSDKSharper calculated events (event system)

These are not raw iRacing variables — they are computed by IRSDKSharper's event system and stored as event tracks:

| Track key | Description |
|---|---|
| `CarIdxGForce[n]` | G-force along track direction for car `n`; only values > ±2g are recorded. Used to detect collisions in replays. |

Access via `irsdk.EventSystem.Tracks["CarIdxGForce[3]"]`.

---

## What the plugin currently exposes via WebSocket

The plugin broadcasts a `state` message at ~5 Hz (200 ms throttle) with only:

```json
{
  "type": "state",
  "pluginMode": "Replay",
  "currentSessionTime": 1234.5,
  "currentSessionTimeFormatted": "20:34",
  "lap": 3,
  "frame": 42000,
  "frameEnd": 180000,
  "replaySessionCount": 4,
  "replaySessionNum": 1,
  "replaySessionName": "Qualify",
  "diagnostics": { ... }
}
```

To expose additional telemetry to the dashboard or external consumers, add extraction in `DataUpdate()` in [SimStewardPlugin.cs](../src/SimSteward.Plugin/SimStewardPlugin.cs) and include the new fields in `PluginSnapshot` / `BuildStateJson()`.

---

## SimHub vs direct SDK access

| | IRSDKSharper (in-plugin) | SimHub `GameData` |
|---|---|---|
| **Breadth** | Full SDK — all variables above | SimHub normalises a subset across all sims |
| **iRacing-specific vars** | All available | Not available (e.g. tire wear, CarIdx arrays, replay controls) |
| **Access point** | C# plugin process only | `DataUpdate(ref GameData data)` arg |
| **Latency** | Direct shared memory read | Same tick |

For iRacing-specific data (tires, CarIdx arrays, replay state, incidents), always use IRSDKSharper directly. SimHub's `GameData` is useful only for sim-agnostic properties (speed, RPM, gear) if multi-sim support is needed.
