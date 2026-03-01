# Implementation Plan: Log + Event Stream (PluginLogger Event, Broadcast, UI Panel)

This plan adds a **unified log/event stream** to Sim Steward: PluginLogger events, DashboardBridge broadcast, and a UI panel showing incidents, physics signals, and plugin log entries in real time.

---

## 1. Overview

### Goal
- **PluginLogger**: Raise an event when each log entry is written, so the plugin can broadcast it to the dashboard.
- **DashboardBridge**: Add generic `Broadcast(string json)` for ad-hoc message types (`logEvents`, `incidentEvents`, `physicsEvents`).
- **Plugin**: Wire log events into a broadcast pipeline; optionally drain incident/physics events (per existing PLAN-event-stream-ui.md).
- **Dashboard**: Add a "Live Event Stream" panel displaying a unified chronological feed (newest first):
  - Plugin log entries (Info, Warn, Error)
  - Official incidents (1x/2x/4x)
  - Physics signals (G-force spikes, tire lockup, etc.)

### Current State
| Component | Status |
|-----------|--------|
| **PluginLogger** | File-based only; no events or callbacks when writing |
| **DashboardBridge** | `BroadcastState` only; no generic broadcast |
| **IncidentTracker** | Emits incidents into `state.incidents` (throttled ~200 ms); no real-time push |
| **Dashboard** | Incident feed from state; no log stream |

---

## 2. Architecture

```
Plugin (DataUpdate ~60 Hz + Logger writes)
├── PluginLogger.Write()      → raise LogWritten event
├── SimStewardPlugin.OnLogWritten → enqueue to _pendingLogs
├── Drain _pendingLogs         → broadcast logEvents (on each DataUpdate, or when queue non-empty)
├── IncidentTracker.Update()  → new incidents queued (existing plan)
├── PhysicsIncidentDetector    → physics events queued (existing plan)
├── Drain new incidents       → broadcast incidentEvents
├── Drain physics events      → broadcast physicsEvents
└── Throttled state broadcast → (unchanged)

Dashboard
├── handleMessage()
│   ├── state          → applyState() (existing)
│   ├── logEvents      → handleLogEvents()   → renderEventLog()
│   ├── incidentEvents → handleIncidentEvents() → renderEventLog()
│   └── physicsEvents  → handlePhysicsEvents()  → renderEventLog()
└── event-panel HTML/CSS/JS
```

---

## 3. Plugin Changes

### 3.1 PluginLogger — Add Event

**File**: `src/SimSteward.Plugin/PluginLogger.cs`

- Add `event Action<string level, string message, DateTime timestamp> LogWritten`.
- In `Write(string level, string message)`, after appending to file, invoke `LogWritten?.Invoke(level, message, DateTime.UtcNow)` (inside lock, or after; ensure minimal work in callback to avoid re-entrancy).
- Keep existing API: `Info`, `Warn`, `Error` — no breaking changes.

```csharp
public event Action<string, string, DateTime> LogWritten;

private void Write(string level, string message)
{
    // ... existing file write ...
    try { LogWritten?.Invoke(level, message, DateTime.UtcNow); } catch { }
}
```

### 3.2 DashboardBridge — Add Generic Broadcast

**File**: `src/SimSteward.Plugin/DashboardBridge.cs`

- Add `Broadcast(string json)` that sends arbitrary JSON to all clients.
- Reuse the same lock and client snapshot logic as `BroadcastState` (or factor into a private helper).
- `BroadcastState` can internally call `Broadcast` if desired, or remain separate for clarity.

```csharp
public void Broadcast(string json)
{
    if (string.IsNullOrEmpty(json)) return;
    List<IWebSocketConnection> snapshot;
    lock (_clientLock)
    {
        if (_clients.Count == 0) return;
        snapshot = new List<IWebSocketConnection>(_clients);
    }
    foreach (var client in snapshot)
    {
        try { client.Send(json); }
        catch { }
    }
}
```

### 3.3 SimStewardPlugin — Wire Log Events to Broadcast

**File**: `src/SimSteward.Plugin/SimStewardPlugin.cs`

- Add `ConcurrentQueue<LogEventDto> _pendingLogs` (or `List` with lock).
- In `Init`, subscribe: `_logger.LogWritten += OnLogWritten`.
- `OnLogWritten(string level, string message, DateTime timestamp)`:
  - Enqueue `{ level, message, timestamp: ISO8601 }` into `_pendingLogs`.
  - Cap queue size (e.g. 500) to avoid unbounded growth if dashboard disconnects.
- In `DataUpdate`, before or after existing logic:
  - Drain `_pendingLogs` up to N per tick (e.g. 10) to avoid blocking.
  - If any drained: serialize `{ type: "logEvents", events: [ ... ] }`, call `_bridge.Broadcast(json)`.
- In `End`: unsubscribe `_logger.LogWritten -= OnLogWritten`.

Log event schema for dashboard:
```json
{
  "type": "logEvents",
  "events": [
    { "level": "INFO", "message": "iRacing connected.", "timestamp": "2025-02-27T12:34:56.789Z" }
  ]
}
```

### 3.4 IncidentTracker — Drain New Incidents (existing plan)

**File**: `src/SimSteward.Plugin/IncidentTracker.cs`

- Add `ConcurrentQueue<IncidentEvent> _pendingBroadcast`.
- In `AddIncident`, also enqueue to `_pendingBroadcast`.
- Add `List<IncidentEvent> DrainNewIncidents()` — dequeues all pending and returns. Called from `DataUpdate`.

### 3.5 PhysicsIncidentDetector (existing plan, optional for Phase 1)

**File**: `src/SimSteward.Plugin/PhysicsIncidentDetector.cs` (new)

- Per PLAN-event-stream-ui.md; can be Phase 2 if focusing first on logs + incidents.

### 3.6 SimStewardPlugin — Wire Incident Broadcast

- In `DataUpdate`, after `_tracker.Update()`:
  - `var newIncidents = _tracker.DrainNewIncidents()`.
  - If `newIncidents.Count > 0`: `_bridge.Broadcast(JsonConvert.SerializeObject(new { type = "incidentEvents", events = newIncidents }))`.

---

## 4. Dashboard Changes

### 4.1 HTML — Event Panel

**File**: `src/SimSteward.Dashboard/index.html`

- Add section (replace or augment incidents-panel per preference):

```html
<section class="event-panel">
  <div class="event-panel-header">
    <h2>Live Event Stream</h2>
    <div class="legend">
      <span class="legend-log">■ Log</span>
      <span class="legend-inc">■ Incident</span>
      <span class="legend-phy">■ Physics</span>
    </div>
  </div>
  <div id="event-stream" class="event-stream"></div>
</section>
```

- **Placement**: Replace incidents panel with unified stream (recommended), or add both (stream = richer, incidents = compact).

### 4.2 CSS — Event Stream Styling

- `.event-panel`, `.event-panel-header`, `.legend`, `.event-stream`
- `.event-entry` — flex, gap, padding, border-left
- Log levels: `.log-info`, `.log-warn`, `.log-error` (use --green, --yellow, --red)
- Incident severity: `.sev-high`, `.sev-mid`, `.sev-low`
- Physics: `.event-physics`
- `.evt-time`, `.evt-badge`, `.evt-message`, `.evt-driver`, etc.
- Match existing design tokens (`--bg`, `--surface`, `--accent`, `--red`, `--yellow`, `--green`).

### 4.3 JavaScript — Event Handlers and Render

- `eventLog` array (unified, newest first; cap at 200).
- `handleLogEvents(events)` — for each `{ level, message, timestamp }`, unshift into `eventLog` with `kind: 'log'`; cap; `renderEventLog()`.
- `handleIncidentEvents(events)` — unshift with `kind: 'incident'`; cap; `renderEventLog()`.
- `handlePhysicsEvents(events)` — unshift with `kind: 'physics'`; cap; `renderEventLog()` (when physics detector exists).
- `renderEventLog()` — clear `#event-stream`, iterate `eventLog`, create `div.event-entry` per event.
- In `handleMessage`:
  - `case 'logEvents': handleLogEvents(p.events); break;`
  - `case 'incidentEvents': handleIncidentEvents(p.events); break;`
  - `case 'physicsEvents': handlePhysicsEvents(p.events); break;`
- Incident rows: `data-incident-id`, click → `send("SelectIncidentAndSeek", id)`.

---

## 5. Implementation Order

| Step | Task | Depends On |
|------|------|------------|
| 1 | Add `LogWritten` event to `PluginLogger` | — |
| 2 | Add `Broadcast(string json)` to `DashboardBridge` | — |
| 3 | Wire `OnLogWritten` → `_pendingLogs` → `Broadcast(logEvents)` in `SimStewardPlugin` | 1, 2 |
| 4 | Add event-panel HTML + CSS to dashboard | — |
| 5 | Add `handleLogEvents`, `renderEventLog`, `handleMessage` case for `logEvents` | 4 |
| 6 | Add `DrainNewIncidents` + `_pendingBroadcast` to `IncidentTracker` | — |
| 7 | Wire incident broadcast in `SimStewardPlugin.DataUpdate` | 2, 6 |
| 8 | Add `handleIncidentEvents`, extend `renderEventLog` for incidents | 5, 7 |
| 9 | Replace incidents panel with unified event stream (or augment) | 8 |
| 10 | (Phase 2) PhysicsIncidentDetector + physicsEvents | — |
| 11 | Update `INTERFACE.md` | 3, 7, 10 |

---

## 6. INTERFACE.md Updates

**File**: `docs/INTERFACE.md`

Add to §3 (Plugin → Dashboard message types):

| Type | When | Shape |
|------|------|-------|
| `logEvents` | When plugin writes log entries | `{ "type": "logEvents", "events": [ { "level": "INFO"\|"WARN"\|"ERROR", "message": "...", "timestamp": "ISO8601" } ] }` |
| `incidentEvents` | When new incident(s) detected | `{ "type": "incidentEvents", "events": [ { id, sessionTime, ... } ] }` |
| `physicsEvents` | When physics signal(s) detected | `{ "type": "physicsEvents", "events": [ { sessionTime, signal, value, ... } ] }` |

---

## 7. Testing

- **Log stream**:
  1. Start SimHub + plugin, open dashboard.
  2. Verify log entries (e.g. "iRacing connected", "DashboardBridge: client connected") appear in event stream.
  3. Trigger Warn/Error (e.g. disconnect iRacing) — confirm styling.
  4. Reconnect dashboard — stream rebuilds from live events.
- **Incident stream**: Per PLAN-event-stream-ui.md §7.
- **Performance**: Ensure draining logs/incidents doesn't stall DataUpdate; cap queue and per-tick drain.

---

## 8. Options and Variations

- **Log verbosity**: Add `SIMSTEWARD_LOG_STREAM_LEVEL` env var (INFO|WARN|ERROR) to filter what gets broadcast.
- **Seed on connect**: Optionally seed `eventLog` from `state.incidents` when a new client connects for continuity.
- **Filter toggles**: UI toggles for log-only, incident-only, physics-only, or all.
