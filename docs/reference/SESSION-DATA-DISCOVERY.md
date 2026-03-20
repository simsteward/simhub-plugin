## Session-end data discovery (iRacing Live vs Replay)

This doc is a practical checklist to determine **when iRacing emits “final” session data** (results table + per-driver incident totals) and whether we can capture it **without** seeking to checkered/end in replay.

### What to look at (in the dashboard)

Open the SimSteward dashboard and expand **Diagnostics & Metrics**. Watch these pills:

- **Session**: `simMode` + `SessionState` + `SessionNum` + `SessionInfoUpdate` + `SessionFlags`
- **Results**: `ResultsPositionsCount` and `READY/not ready`
- **YAML incidents**: how many active drivers have non-zero incidents (and whether they’re all zero)
- **Last capture**: result of your last capture attempt (error codes matter)

Use these buttons:

- **Capture summary**: tries to capture immediately (fails with `results_not_ready` if `ResultsPositions` isn’t populated)
- **Finalize + capture**: replay-only; pauses → seeks to end → captures once ready → restores your position
- **Record snapshot**: appends one JSON line to `PluginsData/SimSteward/session-discovery.jsonl`

### Where snapshots are written

Snapshots are appended to:

- `%LOCALAPPDATA%\SimHubWpf\PluginsData\SimSteward\session-discovery.jsonl`

Each line is JSON (`type: "sessionSnapshot"`) containing replay position + `sessionDiagnostics`.

### Scenarios to run (the “matrix”)

For each scenario below, use **Record snapshot** at the marked moments. If you’re testing capture behavior, also click **Capture summary** and record another snapshot immediately after.

#### Scenario 1: Live session (non-admin) → checkered → cooldown

Record snapshots:

- **T0**: mid-race (SessionState racing)
- **T1**: immediately when checkered is shown (SessionState transitions ≥ 5)
- **T2**: a few seconds into cooldown

Goal: confirm whether `ResultsPositionsCount` becomes non-zero only at/after checkered, and whether non-admin all-driver incidents stay 0 until post-race.

#### Scenario 2: Full-length replay loaded from iRacing UI

Record snapshots:

- **R0**: immediately after the replay loads (before pressing play)
- **R1**: near the beginning (playhead close to start)
- **R2**: mid-session
- **R3**: near the end / checkered

Goal: determine whether replay load alone populates final results/incident totals, or whether reaching end/checkered is required.

#### Scenario 3: Short replay clip (e.g. ~24 seconds)

Record snapshots:

- **S0**: immediately after load
- **S1**: after playing the full clip to the end (if possible)
- **S2**: after clicking **Finalize + capture** (and it restores)

Goal: determine whether short clips ever populate final `ResultsPositions`, and whether “seek to end” is required to force emission.

#### Scenario 4: Live → Replay transition within same subsession

Record snapshots:

- **L0**: live (mid-session)
- **L1**: immediately after switching to replay
- **L2**: after switching back (if you do)

Goal: observe whether `simMode` flips `full→replay` while still being the same `SubSessionID` (you’ll see SubSessionID only in the captured summary today; if needed we can add it to `sessionDiagnostics` later).

### Interpreting outcomes (what we’ll decide)

- If **Results are READY at replay load** (Scenario 2 R0), we can capture without seeking.
- If **Results are not READY until end/checkered**, then replay capture must be **user-initiated finalize + capture**, with restore (current behavior).
- If **short clips never become READY unless seek-to-end works**, we treat short clips as “missing finalization context” and require finalize workflow (or accept partial capture).

