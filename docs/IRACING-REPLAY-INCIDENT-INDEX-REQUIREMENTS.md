# Technical Requirements
## iRacing Replay Incident Index Capture via SDK Fast-Forward Sampling
**Version 0.7 — Draft**

---

## 1. Overview

This document defines the technical requirements for a test implementation that constructs a complete incident index from an iRacing replay file, operating without any iRacing `/data` REST API calls or iRacing account registration. Telemetry and replay control use the local SDK only.

The approach exploits the iRacing SDK broadcast command system to fast-forward a loaded replay at maximum speed while sampling the raw memory-mapped telemetry at 60Hz natively (bypassing SimHub's throttled update loops), detecting incident events in real time and recording their session timestamps and associated car indices.

**Grafana / Loki:** When the implementation runs as the SimSteward SimHub plugin (or reuses its logging stack), structured logs SHOULD be emitted to the same **Loki → Grafana** pipeline documented in [GRAFANA-LOGGING.md](GRAFANA-LOGGING.md) so index-build phases, detections, and validation outcomes are visible in Grafana Explore and dashboards. Loki push is **optional** and **off** unless `SIMSTEWARD_LOKI_URL` is configured; it does not replace the on-disk JSON index (TR-019).

**SimHub web dashboard:** The built incident index and validation summary MUST be **surfaceable in a new HTML/JavaScript page** served by SimHub’s built-in HTTP server (same model as the existing SimSteward dashboard under `Web/sim-steward-dash/`), communicating with the plugin over the existing WebSocket bridge so users can inspect results **without** Grafana. Full requirements: §4.8.

---

## 2. Background & Constraints

### 2.1 Why Local-Only

The iRacing `/data` REST API provides per-lap incident flags server-side, but requires OAuth client credentials registered with iRacing. This approach targets a fully self-contained workflow where no internet connection, no API registration, and no user credentials are required beyond having iRacing installed and a replay loaded.

### 2.2 Key SDK Limitations Driving This Approach

- The incident index inside `.rpy` is a proprietary undocumented binary — it cannot be read directly
- `BroadcastReplaySearch(NextIncident)` has no per-car filtering — it searches globally across all cars
- `BroadcastReplaySearch` has a minimum ~2.5 second cooldown before reliably accepting another command
- All SDK broadcast commands are fire-and-forget with no return value — feedback is observation-only
- `CarIdxThrottlePct`, `CarIdxBrakePct`, `CarIdxClutchPct` were deliberately removed from the SDK

### 2.3 Signal Sources for Incident Detection

| Variable | Scope | Incident Signal |
|---|---|---|
| `PlayerCarMyIncidentCount` | Player car only | Increments on each incident point award. Delta from previous frame = new incident. |
| `CarIdxSessionFlags[n]` | All cars | `repair` (0x100000) or `furled` (0x80000) bit rising edge = confirmed incident for that car. |
| `CarIdxFastRepairsUsed[n]` | All cars | Value increment = damage confirmed. Less precise timing than flags. |
| `ReplaySessionTime` | Replay state | Current playback position in session seconds. Recorded at moment of detection. |
| `CamCarIdx` | Replay camera | Car the camera switches to after `NextIncident` seek. Used for post-seek car identification. |

### 2.4 Observability (Grafana) & Data Finding Mission

**CRITICAL:** We are currently on a **data finding mission** to understand how the SDK behaves during accelerated replay playback. Because we do not yet know the limits of the SDK or the precise behavior of specific variables (see §7 Open Questions), **verbose logging is highly desirable** during the initial implementation.
* When in doubt, **log it**.
* When in doubt about what to include in the payload, **expand and log it**.

Index-build telemetry for operators and research SHOULD appear in Grafana via **structured logs** ingested by Loki, using the project’s four-label schema and JSON body fields ([GRAFANA-LOGGING.md](GRAFANA-LOGGING.md)). While you should not spam Loki with logs on *every single* 60Hz poll for the entire run, you should aggressively log **event-driven** milestones, state transitions, unexpected variable behaviors, and each detected incident (with as much context as possible) to help us answer the open questions. Local stack setup: [observability-local.md](observability-local.md).

### 2.5 SimHub web dashboard

Operator-facing UI runs in the **browser** (ES6+), not Dash Studio WPF. The plugin exposes data and commands through the **Fleck** WebSocket server and optional broadcast messages; static assets live under SimHub `Web/` per project conventions (see `.cursor/rules/SimHub.mdc`). Grafana remains optional; the new page is the primary local UX for the replay incident index when shipped inside SimSteward.

### 2.6 Data Capture Layer (Hybrid Approach Decision)

While SimSteward generally standardises telemetry capture through SimHub, this fast-forward incident indexing test requires a **hybrid approach**. SimHub abstracts away or fails to expose several critical pieces of raw SDK data needed for this specific task. 

**Direct SDK access MUST be used for:**
- **Replay control broadcast commands:** `BroadcastReplaySearch`, `BroadcastReplaySetPlaySpeed`, `BroadcastReplaySearchSessionTime` (SimHub does not expose these).
- **Raw `CarIdxSessionFlags` bitfields:** Needed for per-car incident detection (SimHub only exposes flag state for the player, not the raw `repair`/`furled` bits for all cars).
- **Raw frame counters:** `ReplayFrameNum` / `ReplayFrameNumEnd`.
- **Live high-frequency arrays:** `CarIdxRPM`, `CarIdxGear`, `CarIdxSteer` for all 63 cars at 60Hz during fast-forward (SimHub's opponent model filters this to nearby cars and may not be reliable at 16x speed).
- **Raw YAML session string:** Needed to extract unmapped fields like `ResultsPositions` entries during a replay.
- **Session YAML fingerprint in structured logs:** When `IRacingSdk.Data.SessionInfoYaml` is available, the plugin computes `session_yaml_fingerprint_sha256_16` (SHA-256 prefix; same helper as replay-incident index events) and merges it into logs that call `MergeSessionAndRoutingFields` (e.g. `action_dispatched`, `action_result`, `incident_detected`, dashboard bridge events). The fingerprint is recomputed when `SessionInfoUpdate` changes, not every `DataUpdate` tick.
- **`PlayerCarMyIncidentCount`:** Polled directly to ensure delta signals are not missed during fast-forward playback.

**SimHub is used for everything else:** Plugin lifecycle, WebSocket server, HTML dashboard hosting, YAML `DriverInfo` enrichment (where sufficient), and exposing our built index/channels back out as SimHub properties.

### 2.7 Effective sampling vs. session time at fast-forward (Decision)

The SDK memory map updates at **60Hz real time**. During replay fast-forward at **16×**, each real-time tick advances ~16× session time, so the **effective sample rate relative to replay session time** is approximately **60 ÷ 16 ≈ 3.75 Hz**. **This is acceptable** for building the incident index at that speed: we do not require a higher effective session-time sampling rate than this combination yields.

---

## 3. Test Objectives

1. A replay can be fast-forwarded programmatically via the SDK broadcast system
2. Incident events for all cars can be detected during fast-forward by monitoring `CarIdxSessionFlags`
3. `ReplaySessionTime` accurately captures the session timestamp of each detected incident
4. The resulting incident index matches the known incident record for the session (validated against final `Incidents` count from the YAML session string)
5. Total time to build a complete index for a full race replay is measured and recorded
6. When integrated with SimSteward logging, index-build lifecycle and incident detections are queryable in Grafana (Loki) without exceeding project volume and cardinality rules
7. The same index and validation summary are visible and usable from a **new SimHub-hosted web dashboard** (table, summary, and build status) without requiring Grafana

---

## 4. Technical Requirements

### 4.1 SDK Connection

| Req ID | Priority | Requirement | Acceptance Criteria |
|---|---|---|---|
| TR-001 | MUST | Establish a direct connection to the iRacing memory-mapped file (`Local\IRSDKMemMapFileName`) using a raw SDK reader (e.g., `IRSDKSharper` or `iRacingSdkWrapper`). Do NOT rely solely on SimHub's `DataUpdate` cycle for telemetry, as SimHub does not expose the raw arrays needed for this specific feature. | Raw SDK connection established within the SimHub plugin context; `IsConnected = true` verified. |
| TR-002 | MUST | Confirm the session is in replay mode by reading `WeekendInfo.SimMode = 'replay'` from the raw YAML session string before proceeding. | `SimMode` value logged and asserted as `'replay'`. |
| TR-003 | MUST | Read and store the `SubSessionID` from the raw YAML session string for use as a reference key for the resulting index. | `SubSessionID` logged and present in output. |

### 4.2 Baseline State Capture

| Req ID | Priority | Requirement | Acceptance Criteria |
|---|---|---|---|
| TR-004 | MUST | Before initiating fast-forward, seek the replay to the beginning using `BroadcastReplaySearch(ToStart)` and wait for `ReplayFrameNum` to stabilise at 0. | `ReplayFrameNum = 0` confirmed before fast-forward begins. |
| TR-005 | MUST | Capture a baseline snapshot of `CarIdxSessionFlags` for all car indices at frame 0 to correctly detect rising edges rather than false-triggering on flags present at session start. | Baseline flags array logged for all car indices. |
| TR-006 | MUST | Capture baseline value of `PlayerCarMyIncidentCount` at frame 0. | Baseline incident count logged. |
| TR-007 | SHOULD | Record the total frame count from `ReplayFrameNumEnd` for use in progress estimation. | `ReplayFrameNumEnd` logged. |

### 4.3 Fast-Forward Execution

| Req ID | Priority | Requirement | Acceptance Criteria |
|---|---|---|---|
| TR-008 | MUST | Initiate fast-forward by sending `BroadcastReplaySetPlaySpeed` with the maximum speed multiplier. Start at 16x and increase empirically to find the reliable ceiling. | Replay begins advancing at faster than real-time confirmed by `ReplaySessionTime` increasing. |
| TR-009 | MUST | Collect data via the direct SDK connection at its native 60Hz update frequency for the duration of the fast-forward. Do not artificially throttle or rely on SimHub's UI thread `DataUpdate` interval, as frames will be missed at 16x speed. | High-frequency polling loop established natively to the memory-mapped file. |
| TR-010 | MUST | Monitor `IsReplayPlaying`. When it transitions to `false`, treat the replay as complete and stop sampling. | Completion correctly detected. No polling after replay ends. |
| TR-011 | SHOULD | Log elapsed wall-clock time from fast-forward start to completion for performance measurement. | Elapsed time recorded in test output. |

### 4.4 Incident Detection Logic

| Req ID | Priority | Requirement | Acceptance Criteria |
|---|---|---|---|
| TR-012 | MUST | On each native 60Hz SDK sample, compare current raw `CarIdxSessionFlags[n]` against the previous frame for all 63 car indices. Detect any frame where the `repair` bit (0x100000) transitions from 0 to 1. | Rising edge correctly detected. No false positives on pre-existing flags from baseline. |
| TR-013 | MUST | On each native 60Hz SDK sample, detect any frame where the `furled`/meatball bit (0x80000) transitions from 0 to 1 for any car index. | Rising edge correctly detected independently of repair bit detection. |
| TR-014 | MUST | On each native 60Hz SDK sample, compare the current raw `PlayerCarMyIncidentCount` against the previous value. Detect any frame where the count increments. | All increments detected. Delta value (1, 2, or 4 points) recorded. |
| TR-015 | MUST | At the moment of any detection, record the current `ReplaySessionTime` as the incident timestamp. | Timestamp within one frame (1/60s ≈ 16.7ms) of the actual incident moment. |
| TR-016 | MUST | At the moment of any detection, record the `carIdx` of the affected car. | `carIdx` correctly identifies the car involved in each incident. |
| TR-017 | SHOULD | Detect `CarIdxFastRepairsUsed[n]` increments as a secondary confirmation signal. Record separately and cross-reference with flag-based detections. | Fast repair increments logged and correlated with flag events where applicable. |
| TR-018 | MUST | Handle flag bit resets — when a repair or furled bit clears and later re-sets, treat the re-set as a new incident detection. Do not deduplicate within the same car unless within a 1-second window. | Multiple incidents on the same car correctly recorded as separate events. |

### 4.5 Output Format

Each **data point** (incident index row) MUST carry a **`fingerprint`** so the same logical detection can be correlated across JSON on disk, structured logs (§4.7), the web dashboard (§4.8), and downstream storage without relying on row order. The fingerprint is a **deterministic id** derived only from stable fields (not wall-clock or build id).

**Fingerprint (v1) — canonical string**

1. Let `points` be the JSON literal for `incidentPoints`: either a decimal integer string, or the two-character sequence `null` when the value is unknown.
2. Build a single UTF-8 string (exact separators, no spaces):  
   `v1|{subSessionId}|{carIdx}|{sessionTimeMs}|{detectionSource}|{points}`  
   where `subSessionId` is the same integer as the file summary, `detectionSource` is one of `repair_flag`, `furled_flag`, `player_incident_count`.
3. Set **`fingerprint`** to the **lowercase hexadecimal** SHA-256 digest of that UTF-8 string (64 hex characters).

If a future format needs to change inputs, bump the leading `v1` token and document a new version (analogous to `fingerprint_version` in the broader Sim Steward data model).

| Req ID | Priority | Requirement | Acceptance Criteria |
|---|---|---|---|
| TR-019 | MUST | Produce a structured incident index as a JSON array upon completion. | Valid JSON file written to disk at test completion. |
| TR-020 | MUST | Each entry must contain: `fingerprint` (string, 64-char lowercase hex SHA-256 per **Fingerprint (v1)** above), `carIdx` (int), `sessionTimeMs` (int), `detectionSource` (string: `repair_flag` \| `furled_flag` \| `player_incident_count`), `incidentPoints` (int or null). | All five fields present and correctly typed in every entry; `fingerprint` matches the v1 digest of that row’s other fields plus `subSessionId`. |
| TR-021 | MUST | Entries must be sorted ascending by `sessionTimeMs`. | Output array is in chronological order. |
| TR-022 | SHOULD | Include a summary block: `subSessionId`, `totalRaceIncidents`, `incidentCountByCarIdx`, `indexBuildTimeMs`. | Summary block present and values correct. |

**Example output:**
```json
{
  "subSessionId": 12345678,
  "indexBuildTimeMs": 34200,
  "totalRaceIncidents": 22,
  "incidentCountByCarIdx": { "3": 2, "7": 1, "12": 4 },
  "incidents": [
    {
      "fingerprint": "a1b2c3d4e5f6789012345678901234567890abcdef1234567890abcdef123456",
      "carIdx": 3,
      "sessionTimeMs": 184320,
      "detectionSource": "repair_flag",
      "incidentPoints": null
    },
    {
      "fingerprint": "fedcba098765432109876543210fedcba098765432109876543210fedcba09",
      "carIdx": 0,
      "sessionTimeMs": 312800,
      "detectionSource": "player_incident_count",
      "incidentPoints": 2
    }
  ]
}
```

*(Example `fingerprint` values are placeholders; implementations MUST emit the real SHA-256 hex for each row.)*

### 4.6 Validation

| Req ID | Priority | Requirement | Acceptance Criteria |
|---|---|---|---|
| TR-023 | MUST | After index build, read the final driver `Incidents` value from `ResultsPositions` in the raw YAML session string. | Per-driver final incident totals extracted correctly from YAML. |
| TR-024 | MUST | Cross-reference incident events detected per `carIdx` against final `ResultsPositions.Incidents` count. Log any discrepancies. | Discrepancy report produced. Zero discrepancies is the pass condition, but discrepancies are research data not an automatic failure. |
| TR-025 | SHOULD | Seek to each detected incident timestamp using `BroadcastReplaySearchSessionTime` and confirm via `CamCarIdx` that iRacing's camera switches to the expected car. Allow 2.5 seconds per seek. | Camera car matches expected `carIdx`. Match rate logged as a percentage. |

### 4.7 Grafana / Loki structured logging

Requirements align with [GRAFANA-LOGGING.md](GRAFANA-LOGGING.md): **four labels only** (`app`, `env`, `component`, `level`); **no** high-cardinality values in labels (`subsession_id`, `car_idx`, correlation ids stay in the JSON body).

| Req ID | Priority | Requirement | Acceptance Criteria |
|---|---|---|---|
| TR-026 | SHOULD | When using the SimSteward plugin logging path, emit structured log lines for replay incident index build: at minimum `replay_incident_index_started` (or equivalent `event` name), `replay_incident_index_baseline_ready`, `replay_incident_index_fast_forward_started`, `replay_incident_index_fast_forward_complete`, `replay_incident_index_validation_summary`. | Corresponding events appear in Loki (if push enabled) and in `plugin-structured.jsonl` per project pipeline. |
| TR-027 | SHOULD | While we are on a data finding mission, do not blindly emit logs on every single 60Hz SDK cycle across a 45-minute race (to avoid overwhelming the sink). However, **verbose logging of state transitions, unexpected values, and key intervals is highly desired**. When in doubt, log. | Flexible volume suitable for debugging and answering Open Questions without causing OOM or Loki rejections. |
| TR-028 | SHOULD | For each incident detected during fast-forward, emit one structured line with `event` discriminating replay index build (e.g. `replay_incident_index_detection`) including **`fingerprint`** (same value as TR-020 / **Fingerprint (v1)**), `car_idx`, `session_time_ms` or `replay_session_time`, `detection_source` (`repair_flag` \| `furled_flag` \| `player_incident_count`), `incident_points` when known, `subsession_id`, `replay_frame` when available, and the same session spine fields used elsewhere (`track_display_name`, `log_env`, `loki_push_target` where applicable). | Each JSON index entry has a traceable log line in Grafana for debugging and cross-check; log `fingerprint` matches the on-disk row. |
| TR-029 | SHOULD | Log validation outcomes (TR-023–TR-025): discrepancy counts, camera seek match rate, and `index_build_time_ms` / `total_detected` summary fields in the body of `replay_incident_index_validation_summary` (or split events if size limits require). | Grafana Explore can filter by `subsession_id` in JSON and compare to file output. |
| TR-030 | MUST | If Loki URL is unset or push fails, the index build MUST still complete and write TR-019 JSON; logging failures MUST NOT abort the test. | Graceful degradation; behaviour matches existing Loki sink semantics. |

**Taxonomy note:** Register new `event` names in [GRAFANA-LOGGING.md](GRAFANA-LOGGING.md) when implemented so LogQL and dashboards stay canonical.

### 4.8 SimHub web dashboard (new page)

Deliver a **dedicated** dashboard page (separate HTML entry or clearly named sub-view) so the replay incident index is not buried-only in logs or disk files. Follow SimHub dashboard rules: **HTML/CSS/JavaScript** in `Web/sim-steward-dash/` (or an adjacent path documented in deploy), loaded via SimHub’s HTTP port (e.g. `http://<host>:8888/Web/...`).

| Req ID | Priority | Requirement | Acceptance Criteria |
|---|---|---|---|
| TR-031 | MUST | Add a new web dashboard surface that displays the latest completed index **summary** (`subSessionId`, `indexBuildTimeMs`, `totalRaceIncidents`, per-`carIdx` counts per TR-022) when available. | Summary fields visible after a successful build; empty/disabled state when no index exists for the current context. |
| TR-032 | MUST | Display the **incidents** array in a sortable, scannable table (or equivalent list UI) with columns matching TR-020: `fingerprint`, `carIdx`, `sessionTimeMs`, `detectionSource`, `incidentPoints`. | All five fields shown for every row (fingerprint may use truncation + tooltip/copy affordance if space is tight); chronological default sort matches TR-021. |
| TR-033 | SHOULD | Show **in-progress** index-build status: phase (e.g. baseline, fast-forward, validation), elapsed time, and non-high-frequency progress hints (e.g. `ReplaySessionTime` or frame-derived estimate) without spamming the UI or WebSocket. | User can tell build is running vs idle vs failed. |
| TR-034 | SHOULD | Provide navigation from the existing SimSteward dashboard to this page (link, tab, or menu entry) and a stable document URL path in the spec/README once chosen. | Discoverable entry point without typing a raw path from memory. |
| TR-035 | MUST | Load index payload from the plugin via the **existing WebSocket** bridge (broadcast snapshot and/or action request/response JSON). Do not require a second HTTP server inside the plugin. | Data matches TR-019/TR-020 semantics on the wire; one bridge connection model. |
| TR-036 | SHOULD | Allow **seek/jump** to a selected incident from the table (plugin action calling `BroadcastReplaySearchSessionTime` or equivalent) when replay mode is active, with clear feedback if seek is unavailable. | Row action triggers seek; errors surfaced in UI. |
| TR-037 | MUST | If the iRacing SDK is disconnected or `SimMode` is not `replay`, the dashboard MUST show a clear message and MUST NOT imply an index is being built. | Same guardrails as TR-001/TR-002, reflected in UI. |
| TR-038 | MUST | Provide a large "Record" button. When clicked, it activates high-frequency (60Hz) telemetry logging for deep data collection (the "data finding mission"). When clicked again, it stops logging. | Button exists, toggles state visually, and controls the plugin's raw high-frequency logging output. |

### 4.9 Grafana Insights Dashboard

Since this implementation is a data finding mission, we need a dedicated Grafana dashboard to visualize the results, measure success rates, and identify SDK limits across multiple test runs.

| Req ID | Priority | Requirement | Acceptance Criteria |
|---|---|---|---|
| TR-039 | MUST | Create a Grafana Dashboard JSON model (`docs/dashboards/replay-insights.json` or similar) dedicated to these tests. | Dashboard file is committed to the repository and can be imported into a local Grafana instance. |
| TR-040 | MUST | The dashboard MUST include panels visualizing: index build times vs. replay length (to deduce max fast-forward speed limits), discrepancy counts (detected vs. actual incidents), and high-frequency logging volume when the "Record" mode is active. | Panels correctly query Loki using the `replay_incident_index_*` events and display meaningful aggregations. |

### 4.10 Milestone documentation & automated tests

| Req ID | Priority | Requirement | Acceptance Criteria |
|---|---|---|---|
| TR-041 | MUST | For each milestone **M1–M9**, when the milestone is marked **complete**, add a **milestone summary** in §9 (same pattern as *M1 acceptance review*): what shipped, which requirement IDs are satisfied, and evidence (source paths, test class names, structured log `event` names). | Every completed milestone has a dated summary block under §9; claims map to verifiable artifacts. |
| TR-042 | MUST | Build the **automated test suite** for the replay incident index: unit tests with mocks/fixtures where the SDK is unavailable, integration or golden-data tests where appropriate; expectations MUST trace to this document, not ad-hoc behavior. | Tests exist, run via CI or a documented local command, and cover the behaviors implied by the linked TR/NFR IDs for M8. |
| TR-043 | MUST | **All** feature tests for the replay incident index **pass** locally and in CI. Resolve failures by fixing **implementation** or, when the written spec was wrong, by **updating this document** with rationale—**not** by weakening assertions, deleting cases, broadening tolerances without justification, or matching expectations to incorrect behavior. | Green suite; test edits only alongside corrected spec or post-fix tightened assertions. |

---

## 5. Non-Functional Requirements

| Req ID | Priority | Requirement | Acceptance Criteria |
|---|---|---|---|
| NFR-001 | MUST | Index construction MUST NOT use the iRacing `/data` API or other iRacing cloud credentials. **Optional:** HTTPS POST to the configured Loki endpoint (`SIMSTEWARD_LOKI_URL`) for Grafana is permitted when enabled; with Loki **disabled**, no outbound observability traffic is required for the test to pass. | No iRacing REST/OAuth traffic. Loki traffic only when explicitly configured. |
| NFR-002 | MUST | The test must fail gracefully with a clear error message if the iRacing SDK is not connected. | Graceful failure message on no connection. |
| NFR-003 | SHOULD | Total index build time for a 45-minute race replay must be measured and documented. Target is under 120 seconds — observation not a pass/fail criterion. | Build time recorded in output regardless of duration. |
| NFR-004 | SHOULD | After completion, restore the replay to its original position using the saved `ReplayFrameNum` from before fast-forward began. | Replay position restored to pre-test position. |
| NFR-005 | MUST | Implement as a SimHub C# plugin OR standalone C# console application using IRSDKSharper or iRacingSdkWrapper. | Executable runs on Windows without additional runtime dependencies beyond .NET and iRacing. |
| NFR-006 | SHOULD | Document or reuse LogQL examples for the new `replay_incident_index_*` events. Since this is a data finding mission, ensure logging payloads are expansive enough to answer questions about flag reliability and speed limits. | Queries reproducible from Grafana Explore; cross-ref [GRAFANA-LOGGING.md](GRAFANA-LOGGING.md) § LogQL. |
| NFR-007 | SHOULD | The new dashboard page should remain usable on a LAN client (WebSocket host derived from `window.location.hostname`, same pattern as the main SimSteward dash). | Remote browser on the same network can open the page and receive data. |
| NFR-008 | MUST | Treat **~3.75 Hz effective sampling vs. session time** (60Hz real-time SDK polls at **16×** replay speed) as **acceptable** for incident detection and index build. Document the chosen play-speed multiplier and implied effective rate in test output when reporting build methodology. | Spec and run logs state multiplier; no requirement to exceed SDK real-time 60Hz or to add synthetic higher session-time sampling. |

---

## 6. SDK Broadcast Command Reference

| Command | Parameters | Purpose |
|---|---|---|
| `BroadcastReplaySearch` | `irsdk_RpySrch_ToStart` | Seek to frame 0 before fast-forward |
| `BroadcastReplaySetPlaySpeed` | `speed` (int), `slowMotion` (bool = false) | Initiate fast-forward at maximum speed |
| `BroadcastReplaySetPlayPosition` | `irsdk_RpyPos_Begin`, `frameNumber` | Restore original playback position after test |
| `BroadcastReplaySearchSessionTime` | `sessionNum` (int), `sessionTimeMS` (int) | Validation: seek to detected incident timestamps |

---

## 7. Open Questions

- **Resolved (sampling):** At **16×** with **60Hz** real-time SDK polling, **~3.75 Hz** effective vs. session time is **acceptable** for this project (see §2.7, NFR-008). What is the maximum reliable play speed multiplier before the SDK starts dropping frames or returning stale `CarIdxSessionFlags` values?
- Does iRacing emit `repair`/`furled` flag bits reliably at high playback speeds, or are flag transitions dropped if the relevant frame is not rendered?
- Is `CarIdxSessionFlags` populated correctly during replay, or are these bits only set during real-time sessions? **Must be confirmed empirically.**
- How does `ReplaySessionTime` relate to `SessionTime` in the `/data` API lap records — same time base and units?
- Do incidents during caution laps, pit lane, or pre-race warm-up appear in `CarIdxSessionFlags` during replay?

---

## 8. Suggested Test Setup

To maximise test value, use a replay that satisfies the following:

- Full race session (not a clip) with a known incident count
- At least 3 different drivers with incidents (validates per-`carIdx` detection)
- At least one driver with multiple incidents on the same lap (validates deduplication logic)
- At least one DNF driver (`ReasonOutStr` populated)
- Ideally a race you participated in, so `PlayerCarMyIncidentCount` provides a second validation channel

---

## 9. Implementation Milestones

This implementation is broken down into the following milestones (tracked in ContextStream). **TR-041** applies to **every** milestone when marked complete (milestone summary in §9). Full criteria: §4.10.

| Milestone | Requirements | Description | Status |
|---|---|---|---|
| **M1: Project Setup & SDK Connection** | TR-001 – TR-003, NFR-005, TR-041 | Setup plugin structure, connect SDK, verify replay mode, extract `SubSessionID`. | Complete |
| **M2: Fast-Forward & Baseline Capture** | TR-004 – TR-011, NFR-008, TR-041 | Seek to start, capture baseline flags, trigger 16× fast-forward, hook raw native 60Hz polling (~3.75 Hz vs. session time acceptable per §2.7), handle completion. | Complete |
| **M3: Incident Detection Logic** | TR-012 – TR-018, TR-041 | Detect repair/furled bit rising edges, detect player incident increments, record timestamps and `carIdx` with 1-second debounce. | Complete |
| **M4: Validation & JSON Output** | TR-019 – TR-025, NFR-004, TR-041 | Write chronological JSON index, validate against YAML final incidents, test camera seek matching, restore replay position. | Complete |
| **M5: Observability Logging** | TR-026 – TR-030, TR-041 | Emit 4-label Loki structured logs for lifecycle phases, detections, and validation summary without tick spam. | Complete |
| **M6: SimHub Web Dashboard** | TR-031 – TR-038, TR-041 | Create HTML/JS page under `Web/`, stream data via WebSocket, display summary/table, add row seek actions, implement the "Record" button toggle. | ⏳ Not Started |
| **M7: Grafana Insights Dashboard** | TR-039 – TR-040, TR-041 | Create and commit a Grafana Dashboard JSON model specifically for analyzing test data (build speeds, discrepancies, log volumes). | ⏳ Not Started |
| **M8: Test suite construction** | TR-041, TR-042 | Automated tests for the replay incident index (mocks/fixtures, golden data as needed); expectations trace to this spec. | Complete |
| **M9: Tests passing (implementation alignment)** | TR-041, TR-043 | All feature tests pass locally and in CI; fix implementation or spec—not tests—to resolve failures. | Complete |

### M8 / M9 acceptance review (completed)

Milestones **M8** and **M9** are **Complete** for the current shipped surface (M1–M5 code paths). **TR-042** coverage MUST expand as **M6+** lands (dashboard, Grafana JSON, etc.).

| Item | Evidence |
|------|----------|
| **TR-041** | This subsection is the M8/M9 milestone summary (scope, requirement mapping, evidence pointers). |
| **TR-042** | `ReplayIncidentIndexPrerequisitesTests` (TR-001–TR-003 / §4.1); `ReplayIncidentIndexBuildTests` (TR-004–TR-011, NFR-008 / §4.2–§4.3, M5 event constants / TR-028 taxonomy); `ReplayIncidentIndexDetectionTests` (TR-012–TR-018 / §4.4); M4: `ReplayIncidentIndexFingerprintTests`, `ReplayIncidentIndexDocumentBuilderTests`, `ReplayIncidentIndexResultsYamlTests`, `ReplayIncidentIndexValidationComparerTests`, `ReplayIncidentIndexOutputPathsTests` (TR-019–TR-024, fingerprint §4.5); M5 Loki: `AssertLokiQueries` + optional `ReplayIncidentIndexLokiIntegrationTests` with `RUN_REPLAY_INDEX_LOKI_ASSERT` ([observability-testing.md](observability-testing.md)). Test classes reference the spec in XML docs. |
| **TR-043** | `dotnet test` for `SimSteward.Plugin.Tests` (net48) passes with zero failures; project policy: resolve failures by fixing implementation or updating this document—not by weakening tests. Same suite is enforced by deploy scripts per SimHub development rules. |

### M1 acceptance review (completed)

Milestone **M1** is **Complete**; TR-001–TR-003, NFR-005, TR-041, and raw session YAML fingerprinting for §2.6 are implemented as follows.

| Item | Evidence |
|------|----------|
| **TR-041** | This subsection is the M1 milestone summary (scope, requirement mapping, evidence pointers). |
| **TR-001** | `IRacingSdk` (IRSDKSharper) in plugin; structured event `replay_incident_index_sdk_ready` on iRacing connect (`irsdk_connected`, `update_interval_ms`). |
| **TR-002** | Event `replay_incident_index_session_context` logs `sim_mode` / `is_replay_mode` from parsed session YAML (`WeekendInfo`); **WARN** when a subsession is active but mode is not replay. |
| **TR-003** | Same event logs `subsession_id` (string, same convention as other plugin logs) for use as the index reference key. |
| **§2.6 raw YAML** | `IRacingSdk.Data.SessionInfoYaml` fingerprint: `session_yaml_fingerprint_sha256_16` (SHA-256 prefix), `session_yaml_length`, `session_info_update` (`SessionInfoUpdate`). Same fingerprint key merged into **all** spine/routing logs when YAML is available (`MergeSessionAndRoutingFields` in `DataUpdate`). |
| **NFR-005** | SimHub C# plugin targeting .NET Framework 4.8 with IRSDKSharper (NuGet). |

**Code:** `ReplayIncidentIndexPrerequisites.cs`, `SimStewardPlugin.ReplayIncidentIndex.cs`, `SimStewardPlugin.cs` / `OnIrsdkSessionInfo`. **Tests:** `ReplayIncidentIndexPrerequisitesTests`. **Log taxonomy:** [GRAFANA-LOGGING.md](GRAFANA-LOGGING.md) (`replay_incident_index_*`).

### M2 acceptance review (completed)

Milestone **M2** is **Complete**; TR-004–TR-011, NFR-008, and TR-041 are implemented as follows.

| Item | Evidence |
|------|----------|
| **TR-041** | This subsection is the M2 milestone summary (scope, requirement mapping, evidence pointers). Milestone status synced in ContextStream when marked complete. |
| **TR-004** | `ReplaySearch(ToStart)` from `TryBeginReplayIncidentIndexBuildLocked`; `ReplayFrameNum` stabilized at 0 (`FrameZeroStableConsecutiveSamples` consecutive `OnTelemetryData` ticks); seek failure → `replay_incident_index_build_error` (`seek_start_timeout`). **Code:** `SimStewardPlugin.ReplayIncidentIndexBuild.cs` (`ProcessSeekingStartLocked`). |
| **TR-005** | Baseline `CarIdxSessionFlags` for all **64** slots via `Data.GetInt("CarIdxSessionFlags", i)`; emitted on `replay_incident_index_baseline_ready` as `car_idx_session_flags` (full array). |
| **TR-006** | `PlayerCarMyIncidentCount` at baseline as `player_car_my_incident_count_baseline` on `replay_incident_index_baseline_ready`. |
| **TR-007** | `ReplayFrameNumEnd` recorded as `replay_frame_num_end` on baseline and completion events. |
| **TR-008** | `ReplaySetPlaySpeed(16, false)`; requested vs telemetry `ReplayPlaySpeed` on `replay_incident_index_fast_forward_started`. |
| **TR-009** | Native IRSDKSharper `OnTelemetryData` handler (`OnIrsdkTelemetryDataForReplayIndex`); `UpdateInterval = 1` (60Hz); not SimHub `DataUpdate`. |
| **TR-010** | Fast-forward loop ends when `IsReplayPlaying` is false; `completion_reason` (`replay_finished` \| `paused_or_stopped`) via `InferCompletionReason`; playback restored to 1×. |
| **TR-011** | Wall-clock `index_build_time_ms` on `replay_incident_index_fast_forward_complete` (`Stopwatch` from fast-forward start); `fast_forward_telemetry_samples` counted per `OnTelemetryData` tick in FF phase. |
| **NFR-008** | `effective_sample_hz_vs_session_time` (= 60 / play speed) on FF start/complete logs; play speed 16× documented in telemetry fields. |

**Code:** `ReplayIncidentIndexBuild.cs` (helpers/constants), `SimStewardPlugin.ReplayIncidentIndexBuild.cs`, `SimStewardPlugin.cs` (`OnTelemetryData` subscribe/unsubscribe, `DispatchAction` → `replay_incident_index_build`, `ReplayIncidentIndexOnIracingDisconnected`). **Tests:** `ReplayIncidentIndexBuildTests`. **Actions:** WebSocket `replay_incident_index_build` args `start` \| `cancel`. **Log taxonomy:** [GRAFANA-LOGGING.md](GRAFANA-LOGGING.md) (`replay_incident_index_started`, `replay_incident_index_baseline_ready`, `replay_incident_index_fast_forward_started`, `replay_incident_index_fast_forward_complete`, `replay_incident_index_build_error`, `replay_incident_index_build_cancelled`).

### M3 acceptance review (completed)

Milestone **M3** is **Complete**; TR-012–TR-018 and TR-041 are implemented as follows.

| Item | Evidence |
|------|----------|
| **TR-041** | This subsection is the M3 milestone summary (scope, requirement mapping, evidence pointers). |
| **TR-012** | `ReplayIncidentIndexDetection.IsRisingEdge` / `RepairSessionFlag` (`0x100000`); `ReplayIncidentIndexDetector.Process` compares each tick vs previous `CarIdxSessionFlags` after `Reset` with TR-005 baseline. |
| **TR-013** | Same for `FurledSessionFlag` (`0x80000`); independent of repair (same tick can emit both). |
| **TR-014** | Positive delta on `PlayerCarMyIncidentCount` vs previous sample → `IncidentSample` with `detectionSource` `player_incident_count`; `incidentPoints` set when delta is 1, 2, or 4, else null. |
| **TR-015** | `Process(replaySessionTimeSec, …)` uses **`ReplaySessionTime`** (seconds) with `SessionTime` fallback; `IncidentSample.SessionTimeMs` via `ToSessionTimeMs`. |
| **TR-016** | `carIdx` from affected slot (flags) or `PlayerCarIdx` for player channel. |
| **TR-017** | `CarIdxFastRepairsUsed` increments append to `ReplayIncidentIndexDetector.FastRepairDeltas` (separate from primary `IncidentSample` list, not TR-020 rows). Baseline captured at frame 0 with flags/player count. |
| **TR-018** | Rising edges after bit clear handled by per-frame comparison; `PrimaryDebounceSessionTimeSec` (1s) on replay session time per car × primary source (`repair` / `furled` / `player`) via `TryTakePrimarySlot`. |

**Runtime wiring:** After baseline (`CaptureBaselineAndStartFastForwardLocked`), the plugin calls `ReplayIncidentIndexDetector.Reset` with baseline `CarIdxSessionFlags`, `PlayerCarMyIncidentCount`, `PlayerCarIdx`, and per-slot `CarIdxFastRepairsUsed`. Each `OnTelemetryData` tick in **FastForwarding** (`ProcessFastForwardingLocked` while `IsReplayPlaying`) invokes `Process`; primary rows accumulate in `_replayIndexIncidentSamples`, then M4 persists TR-019 JSON and runs validation (see M4 acceptance review). **Completion log:** `replay_incident_index_fast_forward_complete` includes `detected_incident_samples` and `fast_repair_delta_events`.

**Code:** `ReplayIncidentIndexDetection.cs` (`IncidentSample`, `FastRepairDelta`, bitmasks), `ReplayIncidentIndexDetector.cs`, `SimStewardPlugin.ReplayIncidentIndexBuild.cs` (baseline fast-repair snapshot, `Reset`, per-tick `Process`). **Tests:** `ReplayIncidentIndexDetectionTests`.

### M4 acceptance review (completed)

Milestone **M4** is **Complete**; TR-019–TR-025, NFR-004, and TR-041 are implemented as follows.

| Item | Evidence |
|------|----------|
| **TR-041** | This subsection is the M4 milestone summary (scope, requirement mapping, evidence pointers). |
| **TR-019** | UTF-8 JSON written under `%LocalAppData%\SimSteward\replay-incident-index\{subSessionId}.json` via `ReplayIncidentIndexOutputPaths` (atomic temp + replace). |
| **TR-020** | `ReplayIncidentIndexFingerprint` (v1 canonical string + SHA-256 hex); rows in `ReplayIncidentIndexDocumentModel` / `ReplayIncidentIndexDocumentBuilder`. |
| **TR-021** | `ReplayIncidentIndexDocumentBuilder.Build` sorts by `sessionTimeMs`, then `carIdx`, then `detectionSource` (ordinal). |
| **TR-022** | Root object includes `subSessionId`, `indexBuildTimeMs` (wall clock for full build including post-FF), `totalRaceIncidents`, `incidentCountByCarIdx`, `incidents`; optional `validation` and `outputPath`. |
| **TR-023** | `ReplayIncidentIndexResultsYaml.TryParseOfficialIncidentsByCarIdx` reads `ResultsPositions` from raw `SessionInfoYaml` (prefers telemetry `SessionNum` captured at baseline; falls back to last non-empty block). |
| **TR-024** | `ReplayIncidentIndexValidationComparer.BuildDiscrepancies` compares per-car **detected event counts** (TR-020 row counts) to YAML `Incidents`; list stored in JSON `validation.discrepancies`. |
| **TR-025** | After fast-forward, `CameraValidating` phase: `ReplaySearchSessionTime(SessionNum, sessionTimeMs)` per sorted row, `CameraValidationCooldownTelemetryTicks` (150 ≈ 2.5s @ 60Hz), then `CamCarIdx` vs expected `carIdx`; `camera_seek_match_percent` in JSON and `replay_incident_index_validation_summary`. |
| **NFR-004** | `TryRestoreReplayIndexSavedFrameLocked`: `ReplaySetPlayPosition(Begin, saved frame)` + 1× speed after finalize, cancel, disconnect, seek timeout, fast-forward speed failure, and `ReplaySearch(ToStart)` failure. |

**Code:** `ReplayIncidentIndexFingerprint.cs`, `ReplayIncidentIndexDocumentModel.cs`, `ReplayIncidentIndexOutputPaths.cs`, `ReplayIncidentIndexResultsYaml.cs`, `ReplayIncidentIndexValidationComparer.cs`, `ReplayIncidentIndexBuild.cs` (`EventValidationSummary`, cooldown constant), `SimStewardPlugin.ReplayIncidentIndexBuild.cs` (post-FF pipeline, `FinalizeReplayIndexBuildLocked`, `ProcessCameraValidatingLocked`). **Tests:** `ReplayIncidentIndexFingerprintTests`, `ReplayIncidentIndexDocumentBuilderTests`, `ReplayIncidentIndexResultsYamlTests`, `ReplayIncidentIndexValidationComparerTests`, `ReplayIncidentIndexOutputPathsTests`. **Structured log:** `replay_incident_index_validation_summary` ([GRAFANA-LOGGING.md](GRAFANA-LOGGING.md)); JSON write failure → `replay_incident_index_build_error` (`json_write_failed`).

### M5 acceptance review (completed)

Milestone **M5** is **Complete**; TR-026–TR-030, TR-041, and per-detection observability are implemented as follows.

| Item | Evidence |
|------|----------|
| **TR-041** | This subsection is the M5 milestone summary (scope, requirement mapping, evidence pointers). |
| **TR-026** | Lifecycle events (M2–M3) plus M4 `replay_incident_index_validation_summary`; M5 adds `replay_incident_index_detection` so the minimum SHOULD set in §4.7 is satisfied. |
| **TR-027** | Detection logs are **event-driven** (one line per accepted primary incident), not per 60Hz tick. |
| **TR-028** | `ReplayIncidentIndexBuild.EventDetection`; `LogReplayIncidentIndexDetectionsLocked` in `SimStewardPlugin.ReplayIncidentIndexBuild.cs` emits `fingerprint` via `ReplayIncidentIndexFingerprint.ComputeHexV1` (same inputs as `ReplayIncidentIndexDocumentBuilder`), `car_idx`, `session_time_ms`, `detection_source`, `incident_points`, `replay_frame`, `replay_session_time`, and `MergeSessionAndRoutingFields` spine. |
| **TR-029** | Validation outcomes remain on `replay_incident_index_validation_summary` (M4); no split events required. |
| **TR-030** | Per-detection `Structured` calls wrapped in try/catch so logging failures do not abort the build; Loki optional per existing pipeline. |

**Code:** `ReplayIncidentIndexBuild.cs` (`EventDetection`), `SimStewardPlugin.ReplayIncidentIndexBuild.cs` (`LogReplayIncidentIndexDetectionsLocked`). **Tests:** `ReplayIncidentIndexBuildTests` (event name constants). Fingerprint parity with JSON rows: `ReplayIncidentIndexDocumentBuilderTests.Fingerprint_MatchesPerRowCanonicalDigest`. **Grafana/Loki verification:** `harness/SimSteward.GrafanaTestHarness` emits harness `replay_incident_index_detection` lines (TR-020 fingerprints); `tests/observability/AssertLokiQueries` **fails** unless Loki returns those events; optional `ReplayIncidentIndexLokiIntegrationTests` queries Loki when `RUN_REPLAY_INDEX_LOKI_ASSERT=1` (see [observability-testing.md](observability-testing.md)). **Log taxonomy:** [GRAFANA-LOGGING.md](GRAFANA-LOGGING.md) (`replay_incident_index_detection`).

---

*iRacing Replay Incident Index — Technical Requirements v0.7 — Draft*
