# Log/Event Stream Addition — Summary Plan

Concise summary of the plan to add a unified log/event stream, with repo file references and actionable to-dos for presentation.

---

## Goal

Add a **Live Event Stream** panel showing in real time:
- **Plugin log entries** (Info, Warn, Error) from PluginLogger
- **Official incidents** (1x/2x/4x) from IncidentTracker (real-time push, not only throttled state)
- **Physics signals** (optional Phase 2): G-force spikes, tire lockup, etc.

---

## Current State (Repo References)

| Component | File | Current behavior |
|-----------|------|------------------|
| **PluginLogger** | `src/SimSteward.Plugin/PluginLogger.cs` | File-based only; no events/callbacks when writing |
| **DashboardBridge** | `src/SimSteward.Plugin/DashboardBridge.cs` | `BroadcastState(string json)` only; no generic broadcast |
| **IncidentTracker** | `src/SimSteward.Plugin/IncidentTracker.cs` | `AddIncident()` adds to internal list; no real-time push queue |
| **SimStewardPlugin** | `src/SimSteward.Plugin/SimStewardPlugin.cs` | Throttled state broadcast in `DataUpdate` (~200 ms via `BuildStateJson`); no log or event drain |
| **Dashboard** | `src/SimSteward.Dashboard/index.html` | `handleMessage` handles only `state`, `actionResult`, `pong`; incidents come from `state.incidents`; no event stream UI |
| **Interface** | `docs/INTERFACE.md` | §3 documents only `state`, `actionResult`, `pong`, `error`; no `logEvents` / `incidentEvents` / `physicsEvents` |

---

## Architecture After Implementation

```
Plugin (DataUpdate ~60 Hz + Logger writes)
├── PluginLogger.Write()      → raise LogWritten event
├── SimStewardPlugin.OnLogWritten → enqueue to _pendingLogs
├── Drain _pendingLogs         → _bridge.Broadcast(logEvents)
├── IncidentTracker.Update()  → AddIncident also enqueues to _pendingBroadcast
├── Drain new incidents       → _bridge.Broadcast(incidentEvents)
├── (Phase 2) PhysicsIncidentDetector → physicsEvents
└── Throttled state broadcast → unchanged

Dashboard
├── handleMessage()
│   ├── state          → applyState() (existing)
│   ├── logEvents      → handleLogEvents()   → renderEventLog()
│   ├── incidentEvents → handleIncidentEvents() → renderEventLog()
│   └── physicsEvents  → handlePhysicsEvents()  → renderEventLog()
└── Live Event Stream panel (unified feed)
```

---

## To-Dos (Implementation Order)

### Phase 1 — Log + Incident Stream (no physics)

| # | Task | Files |
|---|------|-------|
| 1 | Add `LogWritten` event to PluginLogger | `src/SimSteward.Plugin/PluginLogger.cs` |
| 2 | Add `Broadcast(string json)` to DashboardBridge | `src/SimSteward.Plugin/DashboardBridge.cs` |
| 3 | Wire `OnLogWritten` → `_pendingLogs` → broadcast `logEvents` in SimStewardPlugin | `src/SimSteward.Plugin/SimStewardPlugin.cs` |
| 4 | Add `_pendingBroadcast` and `DrainNewIncidents()` to IncidentTracker | `src/SimSteward.Plugin/IncidentTracker.cs` |
| 5 | Wire incident broadcast in SimStewardPlugin.DataUpdate (after `_tracker.Update()`) | `src/SimSteward.Plugin/SimStewardPlugin.cs` |
| 6 | Add event-panel HTML + CSS (Live Event Stream) | `src/SimSteward.Dashboard/index.html` |
| 7 | Add `handleLogEvents`, `handleIncidentEvents`, `renderEventLog`, and `handleMessage` cases | `src/SimSteward.Dashboard/index.html` |
| 8 | Replace or augment incidents panel with unified event stream | `src/SimSteward.Dashboard/index.html` |
| 9 | Update `docs/INTERFACE.md` with new message types | `docs/INTERFACE.md` |

### Phase 2 — Physics Events (optional)

| # | Task | Files |
|---|------|-------|
| 10 | Create PhysicsIncidentDetector class | `src/SimSteward.Plugin/PhysicsIncidentDetector.cs` (new) |
| 11 | Wire physics detector and `physicsEvents` broadcast | `src/SimSteward.Plugin/SimStewardPlugin.cs` |
| 12 | Add `handlePhysicsEvents`, extend `renderEventLog` | `src/SimSteward.Dashboard/index.html` |

---

## Key File Edits (Quick Reference)

### 1. PluginLogger.cs

```csharp
public event Action<string, string, DateTime> LogWritten;

private void Write(string level, string message)
{
    // ... existing file write ...
    try { LogWritten?.Invoke(level, message, DateTime.UtcNow); } catch { }
}
```

### 2. DashboardBridge.cs

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

### 3. IncidentTracker.cs

- Add `ConcurrentQueue<IncidentEvent> _pendingBroadcast`
- In `AddIncident`, also enqueue to `_pendingBroadcast`
- Add `List<IncidentEvent> DrainNewIncidents()` — returns and clears queue

### 4. New WebSocket Message Schemas (INTERFACE.md)

| Type | Shape |
|------|-------|
| `logEvents` | `{ "type": "logEvents", "events": [ { "level": "INFO"\|"WARN"\|"ERROR", "message": "...", "timestamp": "ISO8601" } ] }` |
| `incidentEvents` | `{ "type": "incidentEvents", "events": [ { id, sessionTime, sessionTimeFormatted, carIdx, driverName, carNumber, delta, totalAfter, type, source } ] }` |
| `physicsEvents` | `{ "type": "physicsEvents", "events": [ { sessionTime, signal, value, carIdx, ... } ] }` |

---

## Existing Planning Documents

- **Full log + event plan**: `docs/PLAN-log-event-stream.md`
- **Event stream UI + physics**: `docs/PLAN-event-stream-ui.md`

---

## Testing Checklist

- [ ] Log stream: Start SimHub + dashboard; verify plugin log entries appear in event stream
- [ ] Incident stream: Load iRacing replay; verify incidents appear in real time when stepping
- [ ] Reconnect: Close dashboard, reopen; stream rebuilds from live events
- [ ] Performance: DataUpdate stays fast; cap queues (e.g. 500 logs, 10 drain per tick)
