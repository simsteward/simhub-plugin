# Replay capture workflow (MCP-driven)

Consistent procedure for capturing replay session data using the SimSteward MCP server and plugin actions. Use **small steps**: run each phase, check (build, linter, quick manual/MCP verification), then continue.

---

## 1. Detect replay vs live

Before seek or multi-speed processing, determine session mode:

- **MCP:** Call **simsteward_health** or **simsteward_diagnostics**. Read **Plugin Mode** (Replay/Live) or **simMode** (replay/live).
- **State:** If using WebSocket state, check `pluginMode` (`"Replay"` | `"Live"` | `"Unknown"`).

**If not replay:** Seek-to-session and multi-speed steps do not apply. Use a reduced flow: record snapshots at current time only, no seek.

**If replay:** Proceed to sessions list and seek/speed workflow.

---

## 2. Sessions list

- **MCP:** After plugin exposes `sessions[]`, read it from **simsteward_diagnostics** or state (simsteward://snapshot). Each entry: `sessionNum`, `sessionType`, `sessionName` (e.g. Practice, Qualify, Race).
- Use this list to iterate session start/end and to label snapshots (e.g. session_start:1, session_end:2).

---

## 3. Order of operations (replay)

1. **Detect** — simsteward_health or simsteward_diagnostics; confirm pluginMode = Replay.
2. **Sessions** — Read `sessions[]` from state/diagnostics.
3. **Seek to start of each session** — For each session N: **ReplaySeekSessionStart** with arg = N (e.g. "1", "2", "3"). Optionally **RecordSessionSnapshot** with trigger `session_start:N`.
4. **Seek to end of each session** (when implemented) — For each session N: **ReplaySeekToSessionEnd** with arg = N. Then **RecordSessionSnapshot** with trigger `session_end:N` (fingerprint). Optionally **CaptureSessionSummaryNow** to capture results if available.
5. **Speeds** — At each segment, run at 16x, then 8x, then 1x as needed. Record snapshots with trigger that includes speed, e.g. `session_1_16x`, `session_1_8x`, `session_1_1x`.
6. **Two drivers** — Record from primary (player) car, then from contact driver (switch view in iRacing, then snapshot). Use triggers `primary_driver` and `contact_driver` (or `driver_contact:<carIdx>`).

---

## 4. Standardized trigger labels

Use these trigger values for **RecordSessionSnapshot** so session-discovery.jsonl is consistent and queryable:

| Trigger pattern | Meaning |
|-----------------|--------|
| `session_start:N` | Snapshot at start of session N (e.g. session_start:1, session_start:2). |
| `session_end:N` | Snapshot at end of session N (fingerprint: what data is available at session end). |
| `primary_driver` | View on primary (player) car. |
| `contact_driver` or `driver_contact:<carIdx>` | View on contact driver (other car from incident). |
| `session_N_16x`, `session_N_8x`, `session_N_1x` | Snapshot at session N at given replay speed. |
| `far_chase` | Snapshot with standardized far chase view (set manually in iRacing). |

---

## 5. When checkered is not available

Replay clips often do not include the checkered flag. To maximize data:

1. **Replay metadata** — Ensure one snapshot includes full metadata (roster, sessions list, track, replay position, incident feed) via RecordSessionSnapshot with extended payload or CaptureReplayMetadata.
2. **Seek to end of each session** — ReplaySeekToSessionEnd(N), then RecordSessionSnapshot("session_end:N") and try CaptureSessionSummaryNow. iRacing may populate ResultsPositions at session end even without checkered.
3. **FinalizeThenCaptureSessionSummary** — Seek to replay end, wait for results, capture, restore. Use Loki (session_end_fingerprint, session_capture_skipped vs session_summary_captured) to see which session ends provided results.

See **docs/SESSION-DATA-AVAILABILITY.md** and the consistent replay capture workflow plan for full data inventory and no-checkered strategy.
