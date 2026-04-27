# Test Run Dashboards — Design Spec
**Date:** 2026-04-10
**Status:** In progress

## Overview

Replace the existing `simsteward-test-runs.json` (which only tracked T1 speed sweep) with two dashboards:

- **Dashboard A** — `simsteward-test-runs.json` — Primary drill-down. One dedicated row per test stage in execution order.
- **Dashboard B** — `simsteward-test-runs-scoreboard.json` — At-a-glance quality scoreboard. Dense top stat row + compressed trend rows.

All queries target `{app="sim-steward"}` in Loki. Fields are nested under `fields.*`; unwrap with `| json field="fields.field_name"` before `| unwrap`.

---

## Required Code Change

**Add `elapsed_ms` to `sdk_capture_suite_complete`.**

In `SimStewardPlugin.DataCaptureSuite.cs`, `TransitionToLoki()` (~line 2090):

```csharp
var fields = BuildTestFields("T_done");
fields["loki_wait_ms"]  = DataCaptureSuiteConstants.LokiVerifyDelayMs;
fields["elapsed_ms"]    = _suiteStopwatch?.ElapsedMilliseconds ?? 0;   // ADD THIS
MergeSessionAndRoutingFields(fields);
```

This unlocks the **Avg Suite Duration** stat in Row 0 and a duration trend panel.

---

## Dashboard A — SimSteward Test Runs (Primary)

**UID:** `simsteward-test-runs`
**Default range:** `now-30d`
**Refresh:** `5m`

---

### Row 0 — Suite Health

7 stat panels (full width). All instant queries over `$__range`.

| # | Title | Event | Field/logic | Thresholds |
|---|---|---|---|---|
| 1 | Runs Completed | `sdk_capture_suite_complete` | count | purple → teal ≥5 |
| 2 | Runs Cancelled | `sdk_capture_suite_cancelled` | count | teal → red ≥1 |
| 3 | T1 Avg Det % @ 1x | `sdk_capture_speed_sample` `requested_speed=1` | avg `detection_rate_pct` | red <60 → yellow <85 → teal |
| 4 | T7 Reseek Pass Rate | `sdk_capture_incident_reseek` | count(`matches_within_60_frames=3`) / count × 100 | red <67 → yellow <100 → teal |
| 5 | T8 Index Success | `sdk_capture_ff_sweep_result` | count(`total_incidents_in_index>=1`) / count × 100 | red <100 → teal |
| 6 | Preflight Pass Rate | `sdk_capture_preflight_check` | count(`all_passed=true`) / count × 100 | red <100 → teal |
| 7 | Avg Suite Duration | `sdk_capture_suite_complete` | avg `elapsed_ms` (requires code change above) | purple fixed |

---

### Row 1 — T1: Speed Sweep

**Sub-row 1a — 4 stat boxes (one per speed, instant):**

| Title | Filter | Field | Thresholds |
|---|---|---|---|
| Det % @ 1x | `requested_speed=1` | avg `detection_rate_pct` | red <60 → yellow <85 → teal |
| Det % @ 4x | `requested_speed=4` | avg `detection_rate_pct` | same |
| Det % @ 8x | `requested_speed=8` | avg `detection_rate_pct` | same |
| Det % @ 16x | `requested_speed=16` | avg `detection_rate_pct` | red <0 → yellow <30 → teal (16x expected low) |

**Sub-row 1b — Detection Rate % by Speed (timeseries, full width):**
- Keep existing panel. Add `min=0 max=100`. Move legend to `right`.

**Sub-row 1c — GT Hit/Miss Ratio (stacked bar, full width):**
- Replace existing two split panels with one stacked timeseries: `ground_truth_hit_count` (teal) + `ground_truth_miss_count` (pink) per interval.

---

### Row 2 — T7: Incident Reseek

**Sub-row 2a — 3 stats (instant):**

| Title | Field/logic | Thresholds |
|---|---|---|
| Avg Matches / 3 | avg `matches_within_60_frames` | red <2 → yellow <3 → teal |
| Perfect Reseeks % | count(`matches=3`) / count × 100 | red <67 → yellow <100 → teal |
| Avg Any-Frame Matches | avg `any_frame_matches` | purple fixed (secondary signal) |

**Sub-row 2b — Matches Over Time (timeseries, full width):**
- `matches_within_60_frames` as bars. Threshold line at y=3 (perfect). Regressions appear as dips.

---

### Row 3 — T8: FF Sweep

*(to be completed)*

---

### Row 4 — T5b: Camera View Cycle

*(to be completed)*

---

### Row 5 — Preflight Health

*(to be completed)*

---

### Row 6 — Event Log

*(to be completed)*

---

## Dashboard B — SimSteward Test Runs Scoreboard

*(to be completed)*
