# Implementation Plan: Log / Event Stream and UI Panel

This plan implements the **Live Event Stream** feature as described in Example 8 of the SimHub skill (`.cursor/skills/simhub-dashboard-plugin/examples.md`): a unified scrolling event feed showing both official YAML incidents and physics-derived signals, correlated by session time.

---

## 1. Overview

### Goal
- **Plugin**: Emit `incidentEvents` and `physicsEvents` over WebSocket when new events occur (in addition to the existing throttled `state` broadcast).
- **Dashboard**: Add a "Live Event Stream" panel that displays a unified chronological feed (newest first) with:
  - Official incidents (1x off-track, 2x wall/spin, 4x contact) with severity styling
  - Physics signals (G-force spikes, tire lockup, wheelspin, kerb impact, etc.)
  - Optional correlation: nearby physics events shown under incident rows

### Current State
- **IncidentTracker** exists and produces incidents. Incidents are only in `state.incidents` (every ~200 ms). No real-time `incidentEvents` push.
- **DashboardBridge** has `BroadcastState` only; no generic `Broadcast` for ad-hoc message types.
- **PhysicsIncidentDetector** does not exist; physics telemetry (LongAccel, LatAccel, VertAccel, shock velocity, tire slip) is not sampled.
- **Dashboard** has an incidents panel showing incident cards; no unified event stream with physics.

---

## 2. Architecture

```
Plugin (DataUpdate ~60 Hz)
├── IncidentTracker.Update()     → new incidents queued
├── PhysicsIncidentDetector.Sample() → physics events queued
├── Drain new incidents         → broadcast incidentEvents
├── Drain physics events        → broadcast physicsEvents
└── Throttled state broadcast   → (unchanged)

Dashboard
├── handleMessage()
│   ├── state          → applyState() (existing)
│   ├── incidentEvents → handleIncidentEvents() → renderEventLog()
│   └── physicsEvents  → handlePhysicsEvents()  → renderEventLog()
└── event-panel HTML/CSS/JS
```

---

## 3. Plugin Changes

### 3.1 DashboardBridge — Add Generic Broadcast

**File**: `src/SimSteward.Plugin/DashboardBridge.cs`

- Add `Broadcast(string json)` (or `BroadcastJson`) that sends arbitrary JSON to all clients. Implementation is identical to `BroadcastState` — just a different method name/signature so the plugin can send `incidentEvents` and `physicsEvents` without going through state.
- Reuse the same lock and client snapshot logic as `BroadcastState`.

### 3.2 IncidentTracker — Drain New Incidents

**File**: `src/SimSteward.Plugin/IncidentTracker.cs`

- Add a `ConcurrentQueue<IncidentEvent> _pendingBroadcast` (or `List` with lock).
- In `AddIncident`, also enqueue to `_pendingBroadcast`.
- Add `List<IncidentEvent> DrainNewIncidents()` — dequeues all pending events and returns them. Called from `DataUpdate` each tick.
- Extend `IncidentEvent` for event-stream compatibility (optional, see §4):
  - `Lap` (int) — already implied by driver data; add if missing.
  - `IncidentType` (string) — `OffTrack`, `WallContact`, `Spin`, `HeavyContact`, `LightContact`, `Unknown` — derived from `Delta` and optionally physics correlation. Phase 1: derive from delta only (1→OffTrack, 2→Spin, 4→HeavyContact).
  - `PhysicsCause` (string) — filled when physics detector correlates; Phase 2.

### 3.3 PhysicsIncidentDetector — New Class

**File**: `src/SimSteward.Plugin/PhysicsIncidentDetector.cs` (new)

- Implement as in Example 7 of the skill:
  - `PhysicsIncidentEvent`: SessionTime, Signal, Value, Threshold, CarIdx, DriverName, Lap, LapDistPct.
  - `PhysicsIncidentDetector`:
    - Thresholds: LongAccel (29.4), LatAccel (29.4), VertAccel (19.6), ShockVel (1.5), TireLock (5.0), YawRate (1.2).
    - Cooldown per signal (e.g. 1.5 s) to avoid spam.
    - `Initialize(IRacingSdkData)` — cache `IRacingSdkDatum` refs for LongAccel, LatAccel, VertAccel, YawRate, LF/RF/LR/RR shock vel, wheel speeds, Speed, SessionTime, Lap, LapDistPct.
    - `Sample(IRacingSdkData, string playerName, int playerCarIdx)` — run all checks, fire events into queue.
    - `DrainEvents()` — return and clear queue.
- **Telemetry access**: IRSDKSharper's `_irsdk.Data` is updated by its background thread. Call `Sample(_irsdk.Data, ...)` from `DataUpdate` after `_tracker.Update()` — no `OnTelemetryData` callback required if the shared-memory data is current.

### 3.4 SimStewardPlugin — Wire Events and Broadcast

**File**: `src/SimSteward.Plugin/SimStewardPlugin.cs`

- Add field: `PhysicsIncidentDetector _physicsDetector`.
- In `Init`: `_physicsDetector = new PhysicsIncidentDetector()`. On `_irsdk.OnConnected`, call `_physicsDetector.Initialize(_irsdk.Data)` (ensure Data is available; may need to defer to first `DataUpdate` when connected).
- In `DataUpdate`, after `_tracker.Update()`:
  1. `_physicsDetector.Sample(_irsdk.Data, playerName, playerCarIdx)` — get player name/carIdx from driver list.
  2. `var newIncidents = _tracker.DrainNewIncidents()`.
  3. If `newIncidents.Count > 0`: serialize `{ type: "incidentEvents", events: newIncidents }`, call `_bridge.Broadcast(json)`.
  4. `var physicsEvents = _physicsDetector.DrainEvents()`.
  5. If `physicsEvents.Count > 0`: serialize `{ type: "physicsEvents", events: physicsEvents }`, call `_bridge.Broadcast(json)`.
- In `_tracker.Reset()` path (iRacing disconnected): `_physicsDetector?.Reset()`.

### 3.5 IncidentEvent Schema for incidentEvents

The `incidentEvents` payload should match what the dashboard expects:

- Existing: `id`, `sessionTime`, `sessionTimeFormatted`, `carIdx`, `driverName`, `carNumber`, `delta`, `totalAfter`, `type`, `source`.
- Add: `lap` (int), `incidentType` (string: OffTrack|WallContact|Spin|HeavyContact|LightContact|Unknown), `physicsCause` (string, optional).

Phase 1: Map `type` (1x/2x/4x) to `incidentType`:
- 1x → OffTrack
- 2x → Spin (could refine with physics later: WallContact vs Spin)
- 4x → HeavyContact (or LightContact if heuristic; keep simple initially)

---

## 4. Dashboard Changes

### 4.1 HTML — Event Panel

**File**: `src/SimSteward.Dashboard/index.html`

- Add a new section (after incidents-panel or as an alternative/expansion):

```html
<section class="event-panel">
  <div class="event-panel-header">
    <h2>Live Event Stream</h2>
    <div class="legend">
      <span class="legend-inc">■ Official Inc</span>
      <span class="legend-phy">■ Physics Signal</span>
    </div>
  </div>
  <div id="event-stream" class="event-stream"></div>
</section>
```

- Decide placement: either replace the current "Incidents" panel with this unified stream, or add both (stream = richer view, incidents = compact). Recommendation: **Replace** the incidents panel with the event stream, since it supersedes it and avoids redundancy.

### 4.2 CSS — Event Stream Styling

**File**: `src/SimSteward.Dashboard/index.html` (inline styles)

- Add CSS for:
  - `.event-panel`, `.event-panel-header`, `.legend`, `.event-stream`
  - `.event-entry` (flex, gap, padding, border-left)
  - Severity: `.sev-high`, `.sev-mid`, `.sev-low`
  - Incident types: `.inc-offtrack`, `.inc-wall`, `.inc-spin`, `.inc-contact`, `.inc-light`, `.inc-unknown`
  - Physics: `.event-physics`, `.sig-impact`, `.sig-kerb`, `.sig-tire`, `.sig-weight`
  - `.evt-time`, `.evt-badge`, `.inc-badge`, `.phy-badge`, `.evt-driver`, `.evt-lap`, `.evt-total`, `.evt-detail`, `.evt-cause`, `.evt-corr`
- Match existing design tokens (e.g. `--bg`, `--surface`, `--accent`, `--red`, `--yellow`, `--green`) for consistency.

### 4.3 JavaScript — Event Handlers and Render

**File**: `src/SimSteward.Dashboard/index.html` (script block)

- Add:
  - `eventLog` array (unified, newest first; cap at 200).
  - `SIGNAL_LABELS` map for physics signals (LongAccelSpike, LatAccelSpike, VertAccelSpike, TireLockup, TireSpinout, SuspensionImpact, WeightTransferExtreme).
  - `INCIDENT_TYPE_LABELS` map (OffTrack, WallContact, Spin, HeavyContact, LightContact, Unknown) with label, css, icon.
  - `formatSessionTime(s)` helper (if not already present).
  - `handlePhysicsEvents(events)` — for each event, unshift into `eventLog` with `kind: 'physics'`; cap length; call `renderEventLog()`.
  - `handleIncidentEvents(events)` — for each event, unshift with `kind: 'incident'`; optionally correlate with nearby physics in `eventLog` to set `physicsCorrelation`; cap; `renderEventLog()`.
  - `renderEventLog()` — clear `#event-stream`, iterate `eventLog`, create `div.event-entry` per event with appropriate classes and innerHTML.
- In `handleMessage`, add:
  - `case 'incidentEvents': handleIncidentEvents(p.events); break;`
  - `case 'physicsEvents': handlePhysicsEvents(p.events); break;`
- Ensure incident rows are clickable for `SelectIncidentAndSeek` (reuse pattern from existing incident cards — `data-incident-id` and click handler).

### 4.4 Backward Compatibility

- `state.incidents` continues to be sent. The dashboard can:
  - **Option A**: Use only `incidentEvents` / `physicsEvents` for the event stream; keep `state` for drivers and replay state. Event stream is empty until first events arrive.
  - **Option B**: On first `state` apply, seed `eventLog` from `state.incidents` (converted to incident-like entries) so newly connected clients see recent history. Simpler: don't seed; stream starts fresh on connect.

Recommendation: **Option A** — stream builds from live events only. New clients get initial `state` with `incidents`; we could optionally seed `eventLog` from `state.incidents` on first `applyState` for continuity.

---

## 5. INTERFACE.md Updates

**File**: `docs/INTERFACE.md`

- In §3 (Message Types: Plugin → Dashboard), add:
  - `incidentEvents` — When new incident(s) detected. Shape: `{ "type": "incidentEvents", "events": [ { id, sessionTime, ... } ] }`.
  - `physicsEvents` — When new physics signal(s) detected. Shape: `{ "type": "physicsEvents", "events": [ { sessionTime, signal, value, carIdx, driverName, lap, lapDistPct, ... } ] }`.
- Document the schema for `incidentEvents[].incidentType` and `physicsEvents[].signal` if needed.

---

## 6. Implementation Order

| Step | Task | Depends On |
|------|------|------------|
| 1 | Add `Broadcast` (or `BroadcastJson`) to `DashboardBridge` | — |
| 2 | Add `DrainNewIncidents()` and `_pendingBroadcast` to `IncidentTracker` | — |
| 3 | Extend `IncidentEvent` with `lap`, `incidentType` (Phase 1: derive from delta) | — |
| 4 | Create `PhysicsIncidentDetector` and `PhysicsIncidentEvent` | — |
| 5 | Wire `_physicsDetector` and event broadcast in `SimStewardPlugin.DataUpdate` | 1–4 |
| 6 | Add event-panel HTML + CSS to dashboard | — |
| 7 | Add `handlePhysicsEvents`, `handleIncidentEvents`, `renderEventLog`, `handleMessage` cases | 6 |
| 8 | Update `INTERFACE.md` | 5, 7 |
| 9 | Replace or augment incidents panel (replace recommended) | 7 |

---

## 7. Testing

- **Unit**: N/A for C# (optional: mock IncidentTracker/PhysicsDetector for DrainNewIncidents).
- **Manual**:
  1. Start SimHub + plugin, open dashboard.
  2. Launch iRacing, load replay with incidents.
  3. Confirm `incidentEvents` appear when incidents occur (e.g. step through replay, trigger player incidents).
  4. Confirm `physicsEvents` appear during hard braking, kerb hits, wheelspin (tune thresholds if noisy).
  5. Verify event stream panel shows both incident and physics rows, newest first.
  6. Verify click-to-seek on incident rows works.
  7. Test reconnect: close/reopen dashboard, ensure stream repopulates from live events (or from state.incidents seed if implemented).

---

## 8. Future Enhancements (Out of Scope)

- **Physics correlation**: For player incidents, cross-reference with physics events at same session time; set `physicsCause` on `IncidentEvent` (e.g. "LatAccelSpike +2.8g").
- **All-car physics**: CarIdxLapDistPct velocity/G-force, CarIdxTrackSurface for non-player cars in replay — requires additional detector logic.
- **incidentType refinement**: Use physics to distinguish 2x WallContact vs Spin.
- **Filter controls**: Toggle incident-only, physics-only, or both in the UI.
