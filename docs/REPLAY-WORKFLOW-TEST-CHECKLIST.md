# Replay capture workflow — test checklist

Expected expectations and test cases for the replay capture workflow. Use this checklist for manual runs or as the spec for automated tests. See [docs/REPLAY-CAPTURE-WORKFLOW.md](REPLAY-CAPTURE-WORKFLOW.md) for the procedure.

---

## Prerequisites

- [ ] SimHub running with SimSteward plugin loaded
- [ ] **Replay tests:** iRacing running with a replay loaded (SessionInfo with at least one session)
- [ ] **Optional:** MCP (simsteward_health, simsteward_diagnostics, simsteward_action) or WebSocket client to send action JSON

**Snapshot file path:** `%LOCALAPPDATA%\SimHubWpf\PluginsData\SimSteward\session-discovery.jsonl`

---

## 1. Expected expectations (by step)

| Step | Input / condition | Expected outcome | MCP path |
|------|-------------------|------------------|---------|
| **Detect** | SimHub + plugin; iRacing connected (live or replay) | simsteward_health or simsteward_diagnostics returns; `pluginMode` in Replay, Live, or Unknown; `simMode` in replay/live when SessionInfo present. When replay: Plugin Mode = Replay, SimMode = replay. | Call **simsteward_health** or **simsteward_diagnostics**; assert pluginMode and simMode present. |
| **Sessions** | Replay loaded (SessionInfo available) | simsteward_diagnostics includes `sessions` array; each element has sessionNum, sessionType, sessionName; length >= 1. When not replay or no SessionInfo: `sessions` may be empty or absent. | Call **simsteward_diagnostics**; read `sessions` array and structure. |
| **ReplaySeekSessionStart(N)** | Replay loaded; N valid (e.g. "1", "2") | Action returns success: true, result: "ok", error: null. Replay position moves to start of session N. Invalid N or not replay: may return success: false with error. | **simsteward_action**(action=ReplaySeekSessionStart, arg=N). |
| **ReplaySeekToSessionEnd(N)** | Replay loaded; N valid | Action returns success: true, result: "ok". Replay position moves toward end of session N. Session not found: success: false, error e.g. "session_not_found". | **simsteward_action**(action=ReplaySeekToSessionEnd, arg=N). |
| **RecordSessionSnapshot(trigger)** | Replay or live; trigger string | Action returns success: true. One new line in session-discovery.jsonl. Line is valid JSON with type "sessionSnapshot", trigger, playerCarIdx, replayFrameNum, sessionDiagnostics. When SessionInfo available: replayMetadata present (sessionID, subSessionID, trackDisplayName, category, simMode, driverRoster, sessions, incidentFeed). When trigger contains "session_end": plugin emits session_end_fingerprint log. | **simsteward_action**(action=RecordSessionSnapshot, arg=trigger). Snapshot *content* check: read file or use a SimSteward snapshot resource if exposed. |
| **ReplaySetSpeed(arg)** | Replay loaded; arg e.g. "16", "8", "1" | Returns success: true. Replay speed changes. | **simsteward_action**(action=ReplaySetSpeed, arg=e.g. "16"). |
| **CaptureSessionSummaryNow** | Optional; after seek to session end | Success when ResultsPositions populated; else may return success: false / "results_not_ready". | **simsteward_action**(action=CaptureSessionSummaryNow). |

**MCP note:** Replay workflow tests can be driven via SimSteward MCP (health, diagnostics, action) when the plugin is running. Snapshot file structure validation (type, trigger, playerCarIdx, sessionDiagnostics, replayMetadata) still requires reading `session-discovery.jsonl` unless the MCP exposes a snapshot resource (e.g. last snapshot).

---

## 2. Snapshot payload expectations

**Per-line (minimum):**
- `type` = "sessionSnapshot"
- `trigger` = string (e.g. "dashboard:session_start:1")
- `pluginMode` = string
- `playerCarIdx` = number (-1 when unknown)
- `replayFrameNum`, `replayFrameNumEnd`, `replayPlaySpeed`, `replaySessionNum` = numbers
- `sessionDiagnostics` = object (simMode, sessionNum, sessions array, resultsReady, etc.)

**When SessionInfo available:** `replayMetadata` = object with sessionID, subSessionID, trackDisplayName, category, simMode, driverRoster (array), sessions (array), incidentFeed (array, capped).

**Trigger patterns:** session_start:N, session_end:N, primary_driver, contact_driver, session_N_16x, etc.

---

## 3. Test cases (checklist)

### Test case 1 — Detect (health/diagnostics)

- [ ] **Action:** Call simsteward_health or simsteward_diagnostics
- [ ] **Expect:** Response contains plugin mode and simMode; when replay loaded, pluginMode = Replay, simMode = replay
- [ ] **Pass:** Response parsed and mode/simMode present; in replay scenario, values match
- [ ] **Fail:** No response, or missing fields, or replay loaded but mode not Replay

---

### Test case 2 — Sessions list (replay only)

- [ ] **Condition:** Replay loaded
- [ ] **Action:** Call simsteward_diagnostics; read `sessions`
- [ ] **Expect:** `sessions` is array; each entry has sessionNum, sessionType, sessionName; length >= 1
- [ ] **Pass:** sessions present and structure valid
- [ ] **Fail:** sessions missing when SessionInfo expected, or invalid structure

---

### Test case 3 — Seek start + snapshot (replay only)

- [ ] **Action:** ReplaySeekSessionStart("1"); RecordSessionSnapshot with trigger "session_start:1" (or "dashboard:session_start:1")
- [ ] **Expect:** Both actions return success; one new line in session-discovery.jsonl with trigger containing "session_start" and "1", type "sessionSnapshot", playerCarIdx and sessionDiagnostics present
- [ ] **Pass:** Actions ok and new snapshot line exists with expected shape
- [ ] **Fail:** Action failure or no new line or wrong payload shape

---

### Test case 4 — Seek end + snapshot (replay only)

- [ ] **Action:** ReplaySeekToSessionEnd("2"); RecordSessionSnapshot with trigger "session_end:2"
- [ ] **Expect:** Both actions return success; one new line in session-discovery.jsonl with trigger containing "session_end" and "2"; session_end_fingerprint log emitted (optional check via Loki)
- [ ] **Pass:** Actions ok and new snapshot line with session_end trigger
- [ ] **Fail:** Action failure or snapshot missing or trigger mismatch

---

### Test case 5 — Snapshot payload shape (replay with SessionInfo)

- [ ] **Action:** After at least one RecordSessionSnapshot in replay with SessionInfo
- [ ] **Expect:** Latest snapshot line has `replayMetadata` object; replayMetadata has sessionID, subSessionID, trackDisplayName, category, simMode, driverRoster, sessions, incidentFeed
- [ ] **Pass:** replayMetadata present and required fields present
- [ ] **Fail:** replayMetadata missing or required fields missing

---

### Test case 6 — Speed + trigger (replay only)

- [ ] **Action:** ReplaySetSpeed("16"); RecordSessionSnapshot with trigger "session_2_16x"
- [ ] **Expect:** Both success; new snapshot line with trigger containing "16x" and replayPlaySpeed consistent (e.g. 16)
- [ ] **Pass:** Actions ok and snapshot trigger/speed match
- [ ] **Fail:** Action failure or snapshot missing or speed/trigger mismatch

---

## 4. Automated test

See **tests/ReplayWorkflowTest.ps1** for a script that verifies state shape and snapshot file structure (same PASS/FAIL, exit 0/1 style as WebSocketConnectTest.ps1). Run after deploy when SimHub (and optionally a replay) is available.
