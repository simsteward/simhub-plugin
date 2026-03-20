# Code review: Incident Pipeline Visibility & downstream

## Scope

- **Visibility implementation**: startup log, waiting log, baseline broadcast, `BaselineJustEstablished`, status pill, context-aware empty-state, `clearDiagnostics` clearing pill.
- **Downstream**: all consumers of `hasLiveIncidentData`, `diagnostics`, `lastIncidentContext`; new-client flow; memory bank; disconnect path.

---

## Data flow (verified)

| Source | Consumer | Notes |
|--------|----------|--------|
| `BuildMemoryBankSnapshot()` | `BuildStateJson(snapshot)` → WebSocket state, `GetStateForNewClient()` | `HasLiveIncidentData = irConnected && _tracker.BaselineEstablished`; `Diagnostics` includes `IrsdkConnected`. |
| `BuildStateJson(snapshot)` | Dashboard `handleMessage` → `applyState(p)` | State includes `hasLiveIncidentData`, `diagnostics`, `metrics`. |
| `applyState()` | `lastIncidentContext` (only when `dataChanged \|\| throttleElapsed`) | `lastIncidentContext = { hasLiveIncidentData: !!s.hasLiveIncidentData, irsdkConnected: !!s.diagnostics?.irsdkConnected }`. |
| `lastIncidentContext` | `renderIncidents()` empty-state branch | Used to choose: "Waiting for iRacing…", "Connected — establishing baseline…", "No incidents detected yet.", "No incidents match this filter." |
| `applyDiagnostics(diag, metrics)` | Status bar `#ir-status`, infra dots, detection counts | Sets pill text/class from `diag.irsdkConnected`. |
| `clearDiagnostics()` | Called from `onClose` | Clears infra dots, pill, labels, detection counts. **Does not** reset `lastIncidentContext` or re-render incident feed. |
| `_tracker.BaselineJustEstablished` | Plugin after `_tracker.Update()` | One-time `logEvents` broadcast when true. Cleared at start of next `Update()`. |
| Memory bank | `MemoryBankClient` builds JSON/markdown | Uses `snapshot.Diagnostics`, `HasLiveIncidentData`; consistent with plugin state. |

---

## Inconsistencies and issues

### 1. **Disconnect: incident feed empty-state not reset (inconsistency)**

**Location:** Dashboard `onClose` → `clearDiagnostics()`.

**Issue:** On WebSocket disconnect we clear the diagnostics UI (infra dots, iRacing pill, detection counts) but we do **not** reset `lastIncidentContext` or re-render the incident feed. So with 0 incidents the feed can still show "Connected — establishing baseline…" or "No incidents detected yet." while the status bar and diagnostics show disconnected state.

**Downstream:** `renderIncidents()` uses `lastIncidentContext` only when rendering the empty state; it is not re-run on disconnect, so the message stays stale until the next `applyState` (i.e. after reconnect).

**Fix:** In `onClose`, after `clearDiagnostics()`, set `lastIncidentContext = { hasLiveIncidentData: false, irsdkConnected: false }` and call `renderIncidents(filterIncidents(allIncidents))` so the empty-state message becomes "Waiting for iRacing to connect…" when there are no incidents.

---

### 2. **Non-SIMHUB build: state lacks `hasLiveIncidentData` and `diagnostics`**

**Location:** `GetStateForNewClient()` → `BuildStateJson()` (no-arg overload when `#else`).

**Behaviour:** State object has no `hasLiveIncidentData` or `diagnostics`. Dashboard uses `!!s.hasLiveIncidentData` and `!!s.diagnostics?.irsdkConnected`, both false. Empty-state shows "Waiting for iRacing to connect…". Safe default; no bug.

---

### 3. **Filter change uses stale `lastIncidentContext`**

**Location:** Filter chip click → `renderIncidents(filterIncidents(allIncidents))`.

**Behaviour:** We do not update `lastIncidentContext` when only the filter changes. If there are 0 total incidents, the empty message is whatever context we had from the last state. If there are incidents but the filter yields 0, we show "No incidents match this filter." (correct). Only edge case: 0 total incidents and stale context (e.g. still "Connected — establishing baseline…" from before). Acceptable; context is still from the last known state.

---

### 4. **applyState throttle: context updated every 400 ms**

**Location:** `applyState()` block `if (dataChanged || throttleElapsed)`.

**Behaviour:** When only `throttleElapsed` is true we still refresh `lastIncidentContext`, `renderDrivers`, and `renderIncidents`. So connection/baseline state and empty-state message stay in sync with latest state at most 400 ms behind. No issue.

---

### 5. **BaselineJustEstablished lifecycle**

**Location:** `IncidentTracker`: set in `RefreshFromYaml`, cleared at start of `Update()`, reset in `Reset()`.

**Behaviour:** Plugin only sees it true on the same tick it was set; next tick it is cleared. After disconnect, `Reset()` clears it. No leak. Correct.

---

### 6. **New client: state + log tail**

**Location:** `DashboardBridge` OnOpen → `GetStateForNewClient()`, `GetLogTailForNewClient()`.

**Behaviour:** New dashboard clients get full state (including `hasLiveIncidentData`, `diagnostics`) and last 50 log entries. `applyState` will run and set `lastIncidentContext` and pill. Consistent.

---

## Summary

- **Fix applied:** On WebSocket disconnect, reset `lastIncidentContext` and re-render the incident feed so the empty-state message matches the cleared diagnostics ("Waiting for iRacing to connect…" when there are no incidents).
- **No change needed:** Non-SIMHUB state, filter-change staleness, throttle behaviour, BaselineJustEstablished lifecycle, new-client flow. Memory bank and plugin state are consistent.
