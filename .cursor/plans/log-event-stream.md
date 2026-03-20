# Log/Event Stream Plan

> Consolidated plan for implementing the unified log + incident event stream with Live Event Stream UI.  
> Source docs: `docs/plans/PLAN-log-event-stream.md`, `docs/plans/PLAN-log-event-stream-SUMMARY.md`, `docs/plans/PLAN-event-stream-ui.md`

---

## Goals

Add a **Live Event Stream** panel to Sim Steward that displays in real time:

1. **Plugin log entries** (Info, Warn, Error) — real-time tail of PluginLogger output
2. **Official incidents** (1x/2x/4x) — push-based incident events (not only throttled state)
3. **Physics signals** (Phase 2) — G-force spikes, tire lockup, suspension impact, etc.

**Current gap**: Incidents arrive only via state (~200 ms throttled); log entries stay file-only. This plan wires both into a push-based event stream and adds a unified dashboard panel.

---

## Steps (Implementation Order)

| # | Task | Depends On | Phase |
|---|------|------------|-------|
| 1 | Add `LogWritten` event to PluginLogger | — | Log tail |
| 2 | Add `Broadcast(string json)` to DashboardBridge | — | Both |
| 3 | Wire `OnLogWritten` → `_pendingLogs` → broadcast `logEvents` | 1, 2 | Log tail |
| 4 | Add `_pendingBroadcast` and `DrainNewIncidents()` to IncidentTracker | — | Incident stream |
| 5 | Wire incident broadcast in SimStewardPlugin.DataUpdate | 2, 4 | Incident stream |
| 6 | Add event-panel HTML + CSS (Live Event Stream) | — | UI |
| 7 | Add `handleLogEvents`, `handleIncidentEvents`, `renderEventLog`, `handleMessage` cases | 6 | UI |
| 8 | Replace or augment incidents panel with unified event stream | 7 | UI |
| 9 | Update `docs/INTERFACE.md` with new message types | 3, 5 | Docs |
| 10 | (Phase 2) PhysicsIncidentDetector + physicsEvents | — | Physics |

---

## References

| Doc | Purpose |
|-----|---------|
| `docs/plans/PLAN-log-event-stream.md` | Full implementation plan (PluginLogger, Bridge, IncidentTracker, dashboard) |
| `docs/plans/PLAN-log-event-stream-SUMMARY.md` | Concise summary with file references |
| `docs/plans/PLAN-event-stream-ui.md` | Event stream UI panel + PhysicsIncidentDetector |
| `docs/INTERFACE.md` | WebSocket message contract (§3, §4.1) |
| `.cursor/skills/simhub-dashboard-plugin/examples.md` | Example 8: Live Event Stream, Example 7: Physics detector |
| `.cursor/skills/simhub-dashboard-plugin/reference.md` | IncidentEvent, DrainEvents pattern |

### Key Files

| Component | File |
|-----------|------|
| PluginLogger | `src/SimSteward.Plugin/PluginLogger.cs` |
| DashboardBridge | `src/SimSteward.Plugin/DashboardBridge.cs` |
| IncidentTracker | `src/SimSteward.Plugin/IncidentTracker.cs` |
| SimStewardPlugin | `src/SimSteward.Plugin/SimStewardPlugin.cs` |
| Dashboard | `src/SimSteward.Dashboard/index.html` |

---

## Tasks (Phased)

### Phase 1 — Log Tail + Incident Stream (no physics)

- Plugin: LogWritten event → pending queue → `logEvents` broadcast
- Plugin: IncidentTracker drain → `incidentEvents` broadcast
- Dashboard: Live Event Stream panel, handlers for `logEvents` and `incidentEvents`
- Docs: INTERFACE.md message types

### Phase 2 — Physics Events (optional)

- PhysicsIncidentDetector class, thresholds, cooldown
- `physicsEvents` broadcast and dashboard handler
- Optional: correlate physics with incidents for `physicsCause`

---

## To-Dos: Log Tail

- [ ] **L1** Add `LogWritten` event to PluginLogger — `src/SimSteward.Plugin/PluginLogger.cs`
  - `event Action<string level, string message, DateTime timestamp> LogWritten`
  - Invoke after file write in `Write()`
- [ ] **L2** Add `ConcurrentQueue<LogEventDto> _pendingLogs` and `OnLogWritten` subscription in SimStewardPlugin
- [ ] **L3** In `DataUpdate`, drain `_pendingLogs` (cap 10/tick, queue cap 500), broadcast `logEvents` via `_bridge.Broadcast()`
- [ ] **L4** Add `handleLogEvents(events)` and `case 'logEvents'` in dashboard `handleMessage`
- [ ] **L5** Extend `renderEventLog()` for `kind: 'log'` entries (level badge, message, timestamp)
- [ ] **L6** (Optional) Add `SIMSTEWARD_LOG_STREAM_LEVEL` env var (INFO|WARN|ERROR) to filter broadcast

**Schema**: `{ "type": "logEvents", "events": [ { "level": "INFO"|"WARN"|"ERROR", "message": "...", "timestamp": "ISO8601" } ] }`

---

## To-Dos: Incident Event Streaming

- [ ] **I1** Add `Broadcast(string json)` to DashboardBridge — `src/SimSteward.Plugin/DashboardBridge.cs`
- [ ] **I2** Add `ConcurrentQueue<IncidentEvent> _pendingBroadcast` to IncidentTracker
- [ ] **I3** In `AddIncident`, enqueue to `_pendingBroadcast` in addition to internal list
- [ ] **I4** Add `List<IncidentEvent> DrainNewIncidents()` — dequeue all, return, clear queue
- [ ] **I5** In SimStewardPlugin.DataUpdate, after `_tracker.Update()`: call `DrainNewIncidents()`, if non-empty broadcast `incidentEvents`
- [ ] **I6** Add `handleIncidentEvents(events)` and `case 'incidentEvents'` in dashboard
- [ ] **I7** Extend `renderEventLog()` for `kind: 'incident'` with `data-incident-id`, click → `SelectIncidentAndSeek`
- [ ] **I8** (Optional) Extend IncidentEvent with `lap`, `incidentType` (OffTrack|WallContact|Spin|HeavyContact)

**Schema**: `{ "type": "incidentEvents", "events": [ { id, sessionTime, sessionTimeFormatted, carIdx, driverName, carNumber, delta, totalAfter, type, source } ] }`

---

## Action Integration

Incident rows in the event stream must support:

| Action | Purpose |
|--------|---------|
| `SelectIncidentAndSeek` | Click incident row → seek replay to its session time |
| `NextIncident` | Navigate to next incident (iRacing broadcast) |
| `PrevIncident` | Navigate to previous incident (iRacing broadcast) |

Use `data-incident-id` on incident rows for click handling.

---

## Testing Checklist

- [ ] **Log stream**: Start SimHub + dashboard; verify plugin log entries appear in event stream
- [ ] **Incident stream**: Load iRacing replay; verify incidents appear in real time when stepping
- [ ] **Incident click**: Row click triggers `SelectIncidentAndSeek` and seeks replay
- [ ] **Reconnect**: Close dashboard, reopen; stream rebuilds from live events
- [ ] **Performance**: DataUpdate stays &lt; 1/60s; cap queues (e.g. 500 logs, 10 drain per tick)
