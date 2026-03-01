---
name: simhub-dashboard-plugin
description: Develop SimHub HTML/JavaScript dashboards linked to C# plugins with iRacing shared memory expertise. Covers SimHub architecture, Dash Studio bindings (NCalc/Jint ES5.1), plugin SDK (.NET 4.8, AddProperty, AddAction, DataUpdate), HTML dashboard ↔ plugin WebSocket communication using Fleck, and iRacing SDK shared memory (telemetry variables, session info YAML, per-driver incident tracking at any replay speed including 16x). Use when working on SimHub, SimHub dashboard, SimHub plugin, Dash Studio, Web Page View, NCalc, telemetry, sim racing dash, sim steward features, iRacing, iRacing SDK, iRSDK, shared memory, incident points, incident tracking, race steward, race admin, or driver monitoring.
---

# SimHub Dashboard + Plugin Development

## SimHub Overview

SimHub is a WPF .NET Framework 4.8 application for sim racing. It aggregates game telemetry (shared memory, UDP), drives hardware (Arduino, LEDs, tactile feedback), and renders dashboards as PC overlays or on remote devices via a built-in web server.

- **Web server**: port 8888 (configurable). Serves dashboard list and static files.
- **No live telemetry API on 8888**: SimHub does not expose a WebSocket or REST API for live property values. The only documented data endpoint is `http://localhost:8888/DashTemplates/List` (JSON). Live data to an HTML page requires a **plugin-hosted server**.
- Custom HTML: place files under SimHub installation root in `Web\` (e.g. `Web\sim-steward-dash\` for SimSteward). Access at `http://localhost:8888/Web/sim-steward-dash/index.html`. Plugin DLLs deploy to the **SimHub root** (no subfolder); SimHub discovers them automatically.

## Two JavaScript Contexts — Never Confuse Them

| Context | Runs in | JS version | Data access |
|---------|---------|------------|-------------|
| **Jint formula engine** | SimHub process | ES5.1 ONLY | `$prop('name')`, `root["key"]`, no `window`/`document` |
| **Browser HTML dashboard** | Chrome / Safari / WebView | Full ES6+ | WebSocket / fetch to plugin server |

- Jint is used in Dash Studio property bindings, LED editor, and custom protocol INI files.
- The HTML dashboard runs in a real browser with full modern JavaScript.
- Do NOT use `$prop()` in browser JS. Do NOT use `fetch()` or `WebSocket` in Jint formulas.

## Dash Studio (Native Components)

Dash Studio is SimHub's visual editor for WPF-based dashboards. Components include circular gauges, linear gauges, text, images, track maps, rectangles, ellipses, and progress bars. Properties are bound to game data using NCalc or JavaScript formulas.

**Overlay constraints**: total pixel area W × H must be ≤ 480,000. Game must run in windowed or borderless windowed mode. Web Page View is clickable (fixed in V9).

**Preference**: Use HTML/JavaScript dashboards over native Dash Studio objects wherever possible.

## Jint Formula Bindings (ES5.1)

Used only inside SimHub's binding editor, LED editor, and custom protocol INI files.

**NCalc syntax**: `[DataCorePlugin.GameData.NewData.Rpms]` with operators and functions (`Round()`, `Truncate()`, `if()`, `changed()`, `format()`, `isnull()`).

**JavaScript (Jint)**: enable "use javascript" in binding editor, or prefix `js:` in INI.
- Read property: `$prop('DataCorePlugin.GameData.NewData.SpeedKmh')`
- Persist state: `root["myCounter"]`
- Must `return` a value. No classes, no `const`/`let`, no arrow functions.
- Shared helpers: place `.js` files in the `JavascriptExtensions` folder.
- Refresh rate: ~60 FPS; use `RenderingSkip` to throttle.

## Plugin Development (C# .NET 4.8)

### Requirements

- Target: **.NET Framework 4.8** (not .NET Core / .NET 5+).
- Tooling: Visual Studio 2022+.
- SDK demos: `C:\Program Files (x86)\SimHub\PluginSdk\`
  - `User.PluginSdkDemo` — properties and actions (primary reference).

### Lifecycle

- `Init(PluginManager pluginManager)` — called once. Register properties, actions, and start the WebSocket server.
- `DataUpdate(PluginManager pluginManager, ref GameData data)` — called ~60 Hz. Must complete in < 1/60s. Read game data, update properties, broadcast to WebSocket clients.
- `End(PluginManager pluginManager)` — cleanup. Stop WebSocket server.

### Key API

- `pluginManager.AddProperty(name, type, defaultValue)` — register a property visible to all bindings.
- `pluginManager.SetPropertyValue(name, type, value)` — update a property.
- `pluginManager.GetPropertyValue(name)` — read any SimHub property.
- `pluginManager.AddAction(name, type, handler)` — register an action triggerable from Dash Studio buttons or Control Mapper.
- `data.GameName`, `data.GameRunning`, `data.NewData`, `data.OldData` — game state.

### Plugin attributes

```csharp
[PluginName("SimSteward")]
[PluginDescription("Sim Steward dashboard bridge")]
[PluginAuthor("YourName")]
public class SimStewardPlugin : IPlugin, IDataPlugin, IWPFSettings
```

## Dashboard ↔ Plugin Communication

### Architecture

The plugin hosts **both** the HTML dashboard files and a **WebSocket server** on a single port (e.g. 9000) using **Fleck**. This eliminates CORS entirely.

```
Browser (tablet / phone / PC)
  ↕  WebSocket  ws://<ip>:9000
  ↕  HTTP GET   http://<ip>:9000/  (index.html, app.js, style.css)
Plugin (C# .NET 4.8, inside SimHub)
  ↔  PluginManager  (AddProperty, GetPropertyValue, SetPropertyValue)
  ↔  DataCorePlugin (game telemetry at ~60Hz)
```

### Why Fleck, NOT HttpListener

`System.Net.HttpListener` requires **Windows administrator privileges** (or a pre-registered `netsh urlacl`) to listen on any port. SimHub does NOT run as admin by default. A plugin using HttpListener would throw `AccessDeniedException` without admin.

**Fleck** uses `TcpListener` internally — no admin rights needed. It is a lightweight WebSocket server library compatible with .NET Framework 4.8.

- NuGet package: `Fleck`
- GitHub: [statianzo/Fleck](https://github.com/statianzo/Fleck)

### Plugin → Dashboard (push telemetry)

In `DataUpdate`, serialize telemetry and broadcast via WebSocket to all connected browsers:

```csharp
private void BroadcastData(object telemetryPayload)
{
    var json = JsonConvert.SerializeObject(telemetryPayload);
    foreach (var client in _clients)
        client.Send(json);
}
```

### Dashboard → Plugin (button clicks / actions)

Browser sends JSON messages via WebSocket when buttons are clicked:

```javascript
document.getElementById('btn-pit').addEventListener('click', () => {
  ws.send(JSON.stringify({ action: 'RequestPit' }));
});
```

Plugin receives and handles in `OnMessage`:

```csharp
socket.OnMessage = msg => {
    var cmd = JsonConvert.DeserializeObject<ActionCommand>(msg);
    HandleAction(pluginManager, cmd);
};
```

### Static file serving

The plugin also serves the HTML/CSS/JS files. Use a `TcpListener`-based HTTP handler alongside Fleck on the same port, or on a second port. Fleck handles WebSocket upgrades; non-upgrade HTTP requests serve static files.

Alternatively, place HTML files in SimHub's `Web` folder (served at :8888) and run only the WebSocket on the plugin port. In that case, the WebSocket connection URL in JS must point to the plugin port, and the Fleck server must not restrict origins.

## Remote Rendering vs Browser Access

These are **completely different**:

| Mode | How it works | Touch/click | Resolution |
|------|-------------|-------------|------------|
| **Remote rendering** | SimHub renders JPEG frames, streams to device | No touch | Fixed 800×480 |
| **Browser WiFi access** | Real HTML served to real browser on device | Full touch/click | Any |

The HTML dashboard approach uses **browser access**, never remote rendering.

## iRacing Shared Memory Model

### Overview

iRacing exposes data via Windows shared memory (iRSDK). Two categories of data:

1. **Live telemetry** — updated 60 times/second. Flat key-value pairs: speed, RPM, gear, lap, flags, and per-car-index arrays (`CarIdx*`). Accessible even in replays at any speed.
2. **Session info YAML** — a YAML string updated less frequently (when drivers join, results post, etc.). Contains `WeekendInfo`, `DriverInfo` (all drivers list), `SessionInfo` (session list with `ResultsPositions` per session), and `QualifyResultsInfo`.

### Critical: incident data locations and their hard limitations

| Data | Where | Scope | Live race | Replay |
|------|-------|-------|-----------|--------|
| `PlayerCarMyIncidentCount` | Live telemetry | **Player only** | YES, 60 Hz | YES, but only player |
| `PlayerCarTeamIncidentCount` | Live telemetry | Player's team | YES | YES, but only player's team |
| `PlayerCarDriverIncidentCount` | Live telemetry | Player's driver (team racing) | YES | YES, but only player |
| `ResultsPositions[].Incidents` | Session info YAML | **All drivers** | **ADMIN ONLY** during race | YES during replay |
| `CurDriverIncidentCount` | Session info YAML, `DriverInfo.Drivers[]` | **All drivers** | **ADMIN ONLY** during race | YES during replay |

### THE MOST CRITICAL LIMITATION — ADMIN REQUIREMENT

**Confirmed by iRacingSdkWrapper author Nick Thissen (2021):**

> "The incidents for each driver in the session info yaml will only update if you are an **admin**, or **after the race**. During the race, they will stay at 0 unless you are an admin. This is done by iRacing, nothing I can do."

This is an iRacing server-side policy, not a library bug. The `ResultsPositions[].Incidents` and `DriverInfo.Drivers[].CurDriverIncidentCount` fields in the session YAML **remain 0 for all other drivers during a live race unless the connected client is a server admin**.

**Implications for the dashboard:**

- **Race steward / admin use case**: The plugin user must be the **session admin**. If they are, all-driver incidents flow normally via session YAML.
- **Non-admin use case**: Only the player's own incidents are available via `PlayerCarMyIncidentCount`. Other drivers' totals are not accessible during a live race.
- **Post-race / replay use case**: After the race ends (or during a replay), the session YAML is fully populated with all drivers' incident totals. This is where replay tracking becomes valuable.

### How the shared memory updates during replay

This is sourced from the official iRacing SDK header (`irsdk_defines.h`):

- Live data is written to shared memory at **60 ticks per second** (`tickRate = 60`).
- The sim writes a new data line every **16ms** and signals listeners.
- During replay, `SessionTime` reflects the **simulated** time being replayed, not wall-clock time. At 16x speed, each real-second advances 16 simulated seconds.
- The **TickRate field in the shared memory header stays at 60** — the sim still writes data 60 times per real second. However, at 16x replay speed, each tick advances `16/60 ≈ 0.267` simulated seconds.
- The SDK provides a triple-buffer of the latest telemetry frames. At 16x replay speed, if your polling loop runs slower than 60 Hz wall-clock, you will miss intermediate frames (drop them). IRSDKSharper tracks this with `Data.FramesDropped`.

### Session info YAML update behavior during replay

The YAML string is event-driven — it is not updated on a fixed schedule. `SessionInfoUpdate` (a counter in the shared memory header) increments whenever iRacing writes a new session string. During a replay:

- At 1x speed, the YAML may update every few seconds as iRacing processes the replay.
- At 16x speed, **all incidents that occurred between two YAML writes are batched into a single delta**. You lose per-incident granularity but still capture the cumulative total.
- The YAML correctly reflects all driver incidents during replay (unlike the live race admin restriction).
- In `WeekendInfo.SimMode`, the value will be `"replay"` during a replay and `"full"` for a live session. **Always check this** to understand what data you can trust.

### Multi-layer incident detection strategy

The plugin uses **four complementary layers** to detect incidents. No single layer is complete alone; together they provide per-incident, all-driver coverage even at 16x replay speed.

#### iRacing incident point types — official definitions

The delta value directly encodes the incident type. This is not a heuristic — it is how iRacing defines the system:

| Delta | Type | Physical cause | Signal confirmation |
|-------|------|---------------|---------------------|
| **0x** | Light contact | Minor brush with wall or car | 0x noted but count doesn't increase |
| **1x** | Off-track | Car's geometric center crosses illegal track surface (outside white lines + curbing) | `CarIdxTrackSurface` transitions `OnTrack(3)` → `OffTrack(0)` |
| **2x** | Wall/barrier contact OR loss of control (spin) | Hard contact with wall/barrier, or car spins | G-force spike (wall) OR high yaw rate / velocity reversal (spin) |
| **4x** | Heavy car-to-car contact (paved) | Significant contact between two cars | G-force spike on two cars at similar `LapDistPct` simultaneously |

**Quick succession rule**: If multiple incidents happen rapidly, only the highest is counted. A 2x spin that leads to a 4x heavy contact shows as "2x → 4x" but only the 4x is tallied. This means a delta of 4 might replace an earlier 2x (the total jumps by 4, not 6).

**Dirt exception**: In Dirt Oval and Dirt Road, heavy contact is 2x instead of 4x. Check `WeekendInfo.Category` for `"DirtOval"` or `"DirtRoad"`.

#### Layer 1: `PlayerCarMyIncidentCount` — player per-incident, type-identified (works at 16x)

This is a live telemetry int, updated at 60 Hz (real-clock). At 16x replay, incident moments that are 1 simulated second apart are still ~4 real-time frames apart. The value increments by the exact delta at the exact frame the incident occurs. **The delta IS the incident type**:

```
Frame 1800: PlayerCarMyIncidentCount = 2
Frame 1804: PlayerCarMyIncidentCount = 4   → delta = 2 → 2x (wall contact or spin)
Frame 2300: PlayerCarMyIncidentCount = 5   → delta = 1 → 1x (off-track)
Frame 3100: PlayerCarMyIncidentCount = 9   → delta = 4 → 4x (heavy car contact)
```

Cross-reference with physics signals for the exact cause:
- Delta = 1 → confirm with `CarIdxTrackSurface` (was there an off-track transition?)
- Delta = 2 → check G-force spike (wall contact) vs. yaw rate spike (spin/loss of control)
- Delta = 4 → G-force spike + another car at the same track position → car-to-car contact

**This gives per-incident, per-type, physics-corroborated granularity for the player's own car at any replay speed.**

#### Layer 2: `CarIdxLapDistPct` velocity / G-force — all cars, per-frame (works at 16x)

`CarIdxLapDistPct` is a float[64] array updated at 60 Hz for **every car** in the session. By computing the velocity from position deltas between consecutive frames, and then the acceleration from velocity deltas, you can detect **sudden deceleration (impact) events** for all 64 cars:

```
velocity[carIdx] = (lapDistPct[t] - lapDistPct[t-1]) * trackLengthMeters / deltaTime
gForce[carIdx]   = (velocity[t] - velocity[t-1]) / deltaTime / 9.80665
if abs(gForce) > 2.0g → incident/impact detected for this car
```

IRSDKSharper's event system already implements this as `CarIdxGForce[n]` calculated tracks (see `EventSystem.CalculatedTracks.cs`). At 16x, each tick covers ~0.267 simulated seconds, so very brief micro-impacts may be missed, but any significant crash/contact that decelerates a car by 2g+ over a quarter-second is still visible.

**This is the workaround for the YAML batching problem**: even though the YAML might batch +6 incidents into one update, the per-frame G-force data gives you the **exact simulated time and car** of each impact.

#### Layer 3: `CarIdxTrackSurface` — all cars, per-frame (works at 16x)

An int[64] array for all cars:
- `3 (OnTrack)` → `0 (OffTrack)` transition = car left the track surface (off-track incident, possible 1x).
- `3 (OnTrack)` → `-1 (NotInWorld)` transition = car towed or disconnected.
- Rapid `OnTrack → OffTrack → OnTrack` within a few frames = brief off-track (likely 1x).

Combined with Layer 2 (G-force spike at the same time as a track surface change), this strongly indicates a crash vs. a simple off-track excursion.

#### Layer 4: Session YAML `ResultsPositions[].Incidents` — all cars, official totals (batched at 16x)

The authoritative count from iRacing. In replay mode, this is available for all drivers. At 16x, multiple incidents may batch into a single delta. Use this as the **ground truth for totals** and cross-reference the session time with Layer 2/3 events to decompose the batch.

### Correlating the layers — incident type inference

For the **player car**, the delta value from Layer 1 IS the incident type. Cross-reference with physics to get the cause:

```
Player delta = 1 → 1x OFF-TRACK
  Confirm: CarIdxTrackSurface transition at this frame?
  Physics: low G-force, car drove onto grass/gravel

Player delta = 2 → 2x WALL CONTACT or SPIN
  If G-force spike > 2g at this frame → wall/barrier contact
  If YawRate spike > 1.2 rad/s, no wall G → spin / loss of control

Player delta = 4 → 4x HEAVY CAR CONTACT
  G-force spike + another CarIdx with G-force spike at similar LapDistPct
  → identify the other car involved
```

For **other drivers** (YAML batch at 16x), decompose using Layers 2+3:

```
YAML delta for CarIdx N = +6 between T=12:30 and T=14:15:
  Layer 2 events in window: G-spike at T=12:45 (3.2g), G-spike at T=13:58 (2.7g)
  Layer 3 events in window: OffTrack at T=13:12

  Decomposition estimate:
    T=12:45 → probable 4x (heavy G + another car nearby) OR 2x (wall)
    T=13:12 → probable 1x (off-track, no G-spike)
    T=13:58 → probable 2x (moderate G, no nearby car → wall or spin)
    Sum: 4+1+2 = 7 ≠ 6 → adjust: first was likely 2x not 4x
    Revised: 2+1+2 = 5... still off → possibly a batched 2x at another time

  Present in dashboard with confidence level:
    "Car #42 +6 incidents between 12:30–14:15
     → Impact at 12:45 (3.2g, likely 2x–4x)
     → Off-track at 13:12 (likely 1x)
     → Impact at 13:58 (2.7g, likely 2x)"
```

The point total from the YAML is always **exact**. The per-incident type decomposition for other drivers is a best-effort heuristic — but often quite accurate because the physics signals clearly distinguish off-tracks (1x) from impacts (2x/4x).

### iRacing broadcast API: Navigate to incidents in replay

The SDK's broadcast message system (`irsdk_broadcastMsg`) includes:
- `irsdk_BroadcastReplaySearch` with `irsdk_RpySrch_PrevIncident` / `irsdk_RpySrch_NextIncident` — navigate the replay to the previous or next incident frame. iRacing knows every incident frame internally and steps through them one by one.
- `irsdk_BroadcastReplaySearchSessionTime(sessionNum, sessionTimeMS)` — seek to a specific session time.

This means the plugin can: (a) track incidents during 16x fast-forward, (b) offer a "Jump to Incident" button that seeks the replay to the exact moment for review at 1x.

### 16x replay speed — realistic expectations

| Capability | At 1x | At 16x | Strategy |
|-----------|-------|--------|----------|
| Player incident type (1x/2x/4x) | Exact, per-frame | **Exact, per-frame** | Layer 1: delta = type (1=off-track, 2=wall/spin, 4=contact) |
| Player incident cause (wall vs. spin) | Exact, physics-corroborated | **Exact, physics-corroborated** | Layer 1 delta + G-force/yaw/track surface |
| All-car impact detection | Exact G-force | **Good** (~0.27s resolution) | Layer 2: `CarIdxLapDistPct` velocity |
| All-car off-track detection | Exact per-frame | **Good** (~0.27s resolution) | Layer 3: `CarIdxTrackSurface` transitions |
| All-car official totals | Exact | Exact (batched timing) | Layer 4: YAML `ResultsPositions` |
| Other-car type decomposition | Heuristic (G+surface+proximity) | Heuristic (slightly coarser) | Physics signals + point arithmetic |
| Jump to incident frame | YES | YES | Broadcast API `RpySrch_NextIncident` |
| Suspension / tire / physics (player) | Full 60 Hz | Full 60 Hz | See Example 7 physics detector |

### Hard limitations that cannot be worked around

1. **Other drivers' detailed physics** (tire slip, shock, acceleration, yaw) are NOT available — only player car. For other cars, only `CarIdxLapDistPct`-derived G-force and `CarIdxTrackSurface` transitions are available.
2. **Incident type decomposition for other drivers at 16x**: The YAML delta gives the total (e.g., +6) but not the breakdown. The physics layers can usually distinguish 1x (off-track = track surface change, no G) from 2x/4x (G-force spike), but distinguishing a 2x wall hit from a 4x car-to-car contact requires checking whether another car had a simultaneous G-force event at a similar track position. This heuristic is good but not guaranteed.
3. **Quick succession rule**: iRacing's "2x → 4x" promotion means the actual delta might appear lower than the sum of individual events. A spin (2x) followed immediately by heavy contact (4x) shows as a single +4 delta, not +6. The physics layers will see both the spin and the impact, so the dashboard can annotate the full sequence even when the point total reflects only the highest.
4. **Very brief micro-incidents at 16x**: A 1x off-track lasting < 0.2 simulated seconds may land between two telemetry ticks at 16x and be missed by Layer 2/3. The YAML total (Layer 4) will still be correct.

### Primary use case: the driving user's own incidents

The plugin's primary purpose is catching incidents for the **driving user**. For this case, coverage is excellent:

- **`PlayerCarMyIncidentCount` delta IS the incident type**: delta=1 → 1x off-track, delta=2 → 2x wall/spin, delta=4 → 4x heavy contact. No heuristics needed for classification.
- **Physics signals disambiguate within a type**: A 2x could be wall contact (G-force spike, no yaw) or a spin (high yaw rate, lower G). Cross-referencing gives the exact cause.
- **Full physics telemetry** (G-force, suspension, tires, yaw, weight transfer) is available for the player car at 60 Hz, even at 16x replay.
- **Quick-succession rule awareness**: iRacing promotes lower incidents to higher (2x → 4x shows as delta=4 only). The physics layers still see both events (the spin AND the impact), so the dashboard can show the full sequence.
- In replay mode, the user can fast-forward at 16x to scan the entire race, then click "Jump to Incident" to review any moment at 1x with full physics detail.

### Accessing iRacing data from a SimHub plugin

SimHub exposes iRacing data through `GameRawData`, but with a critical limitation: **`GameRawData.SessionData.DriverInfo` and `GameRawData.SessionData.SessionInfo` are only partially parsed** (only `drivers01` and `session01`). For full access to all drivers and all sessions, the plugin must **directly access iRacing's shared memory** using a C# SDK wrapper.

**Recommended approach for the SimSteward plugin:**

1. Use **IRSDKSharper** (NuGet: `IRSDKSharper`, supports .NET Framework 4.7.1+) directly inside the SimHub plugin.
2. In `Init()`, create an `IRacingSdk` instance with `OnSessionInfo` and `OnTelemetryData` callbacks.
3. On each `OnSessionInfo` callback, check `WeekendInfo.SimMode` to detect live vs. replay. Parse `Data.SessionInfo` to extract all drivers' incident counts from `ResultsPositions` and `DriverInfo.Drivers[]`.
4. During live race: only update if admin (verify via attempting to read non-zero values). During replay: data is always available.
5. Compare against previous snapshot; emit incident events with session time, lap, car number, driver name, and delta.
6. Broadcast incident events to the HTML dashboard via WebSocket.

### C# SDK options for iRacing shared memory

| Library | NuGet | .NET | Notes |
|---------|-------|------|-------|
| **IRSDKSharper** | `IRSDKSharper` | net471 / net6+ | Best performance; separate session info + telemetry threads; event system for replays; actively maintained |
| **iRacingSdkWrapper** | — (source only) | net48 | Popular; confirmed: incidents stay 0 without admin during live race |
| **iRacingTelemetrySDK** | `SVappsLAB.iRacingTelemetrySDK` | net8+ | Modern; targets net8+, NOT compatible with SimHub's net48 |

**Use IRSDKSharper**: targets net471 (compatible with SimHub's net48), has separate session info + telemetry processing threads (avoids frame drops during YAML parsing), and has a dedicated event system designed for replay analysis.

### Session info YAML structure (key sections)

```yaml
DriverInfo:
  Drivers:
    - CarIdx: 0
      UserName: "John Doe"
      CarNumber: "42"
      CurDriverIncidentCount: 3
      TeamIncidentCount: 5
    - CarIdx: 1
      UserName: "Jane Smith"
      CarNumber: "7"
      CurDriverIncidentCount: 1
      TeamIncidentCount: 1

SessionInfo:
  Sessions:
    - SessionNum: 0
      SessionType: "Race"
      ResultsPositions:
        - CarIdx: 0
          Position: 1
          Incidents: 3
          LapsComplete: 12
          FastestTime: 85.432
        - CarIdx: 1
          Position: 2
          Incidents: 1
          LapsComplete: 12
          FastestTime: 85.789
```

### Live telemetry variables (per-car-index arrays)

These are arrays indexed by `CarIdx` (0 to ~63), updated at 60 Hz:

- `CarIdxLap` — current lap number for each car
- `CarIdxLapDistPct` — % around track for each car
- `CarIdxPosition` — race position for each car
- `CarIdxClassPosition` — in-class position
- `CarIdxTrackSurface` — on track, in pit, off world
- `CarIdxOnPitRoad` — boolean, on pit road
- `CarIdxEstTime` — estimated time around track
- `CarIdxSessionFlags` — per-car flags (not all games)
- `CarIdxGear` — current gear
- `CarIdxRPM` — current RPM
- `CarIdxSteer` — steering angle

Player-only incident variables (NOT per-CarIdx):
- `PlayerCarMyIncidentCount` — player's own incidents
- `PlayerCarTeamIncidentCount` — player's team total
- `PlayerCarDriverIncidentCount` — current driver's incidents (team racing)

## Community References

- [SimHubPropertyServer](https://github.com/pre-martin/SimHubPropertyServer) — production plugin hosting a TCP server, pushes property changes, accepts SimHub Control triggers. Primary architecture reference.
- [StreamDeckSimHubPlugin](https://github.com/pre-martin/StreamDeckSimHubPlugin) — triggers SimHub actions from Stream Deck buttons via SimHubPropertyServer. Proves the action-triggering pattern end-to-end.
- [CalcLngWheelSlip](https://github.com/viper4gh/SimHub-Plugin-CalcLngWheelSlip) — full plugin with AddProperty, DataUpdate, IWPFSettings.
- [RaceAdmin](https://github.com/GameDotPlay/RaceAdmin) — C# WinForms app tracking all-driver incidents from iRacing SDK. Demonstrates delta-based incident detection, per-driver tracking, caution logic. **Key reference for incident tracking design.**
- [IRSDKSharper](https://github.com/mherbold/IRSDKSharper) — high-performance C# iRacing SDK wrapper. Separate session info + telemetry threads, YAML parsing, event system for replay analysis.
- [iRacingSdkWrapper](https://github.com/NickThissen/iRacingSdkWrapper) — popular C# wrapper (older, some known issues with incidents).
- SDK demos: `C:\Program Files (x86)\SimHub\PluginSdk`

## MCP Tools

- **Context7**: use `resolve-library-id` then `query-docs` to fetch up-to-date C# / .NET / Fleck / Newtonsoft.Json documentation and code examples.
- **cursor-ide-browser**: navigate to `http://localhost:9000` (plugin server) or `http://localhost:8888` (SimHub web server) to test dashboards. Use `browser_navigate` → `browser_lock` → `browser_snapshot` → interact → `browser_unlock`.

## Additional Resources

For detailed property name patterns, NCalc function reference, Fleck setup guide, and full code samples, see [reference.md](reference.md).

For worked examples (speed gauge, pit request button, gear indicator), see [examples.md](examples.md).

## Official Documentation

- [SimHub Wiki](https://github.com/SHWotever/SimHub/wiki)
- [Plugin SDK](https://github.com/SHWotever/SimHub/wiki/Plugin-and-extensions-SDKs)
- [Dash Studio Bindings](https://github.com/SHWotever/SimHub/wiki/Dash-Studio---Bindings)
- [Javascript Formula Engine](https://github.com/zegreatclan/SimHub/wiki/Javascript-Formula-Engine)
- [NCalc scripting](https://github.com/zegreatclan/SimHub/wiki/NCalc-scripting---Introduction)
- [Dash Studio Overlays](https://github.com/SHWotever/SimHub/wiki/Dash-Studio-Overlays)
- [Troubleshoot web access](https://github.com/SHWotever/SimHub/wiki/Troubleshoot-Dashstudio-Web-access)
- [Fleck WebSocket library](https://github.com/statianzo/Fleck)
