# Incident Capture Scorecard — Test Plan & Wish List

**Status:** planned, not yet executed. This is the "next session" calibration run referenced in memory
`project_incident_scorecard_plan`. Goal: validate accuracy of incident capture end-to-end — live
detection (event, cause, estimate, aggregate backfill) AND the Replay Index Build (per-incident points,
fingerprinting, persistence) — against a known set of intentionally-triggered ground-truth actions.

**Session type:** practice (per user). Drive yourself; also include an idle-in-garage phase.

**Ground truth method (user's choice):** scripted order below, executed as written — no marker
button, no verbal notes. Cross-reference happens after the fact against Loki + the persisted index.

**Pit-lane speeding:** tested as-is with current detection (no new detector built beforehand) — the
point of this run is partly to discover what (if anything) currently fires for it.

---

## Ordered signal script — execute in this order

Leave a few seconds of clean gap between each numbered step so events don't bleed into each other's
quick-succession window (6s) unless a step is explicitly testing that chaining.

### Phase 0 — Garage baseline (negative control + exploratory)
0.1. Load in, stay in the garage/pit stall. Don't go on track yet.
0.2. Sit idle ~3 min, no inputs.
0.3. While still garaged: rev the engine, cycle a few gears, turn the wheel lock to lock.
**Expected:** zero incidents of any kind. Any detection here is a false-positive bug.

### Phase 1 — Clean laps (negative control while moving)
1.1. Drive 2-3 fully clean laps — no off-tracks, no contact.
**Expected:** zero incidents. Validates ordinary driving/curbs don't misfire the detector.

### Phase 2 — Off-track (1x)
2.1. Deliberately put all four wheels off track once, briefly, then return to the track surface.
2.2. Repeat at 2 different corners, each several seconds apart.
**Expected:** 3 separate 1x off-track incidents, distinct fingerprints, distinct board rows.

### Phase 3 — Spin (2x)
3.1. Deliberately spin >90°, no contact, continue driving.
3.2. Repeat once, different location.
**Expected:** 2 separate 2x incidents (cause: spin — note live cause will show "unknown" or fall back
to a source guess since other cars can't get a live spin signal; your own car should resolve via
`PlayerCarMyIncidentCount`).

### Phase 4 — Wall contact, light then heavy
4.1. A deliberately gentle/light wall touch.
4.2. A deliberately harder wall hit (not enough for a fast repair).
**Expected:** 4.1 may register 0x (no incident) per iRacing's own rule — a legitimate "nothing logged"
result, not a bug. 4.2 should resolve 2x or 4x depending on iRacing's own severity judgment.

### Phase 5 — Heavy contact (4x)
5.1. A hard hit — enough to trigger visible damage / a fast repair prompt.
**Expected:** 4x, `fast_repair`/`repair_flag` source should also fire alongside the points resolution.

### Phase 6 — Quick-succession chaining (max-wins-not-additive rule)
6.1. In ONE continuous motion: spin immediately into heavy contact (within ~2s).
6.2. Off-track immediately into a spin (within ~2s).
**Expected:** 6.1 → single incident, 4x total (not 6x, not two rows). 6.2 → single incident, 2x total
(not 3x, not two rows). This directly tests `IncidentSeverityCorrelator`'s merge window.

### Phase 7 — Pit lane
7.1. Enter/exit pit lane at legal speed — baseline, should be silent.
7.2. Enter or drive pit lane deliberately over the speed limit.
7.3. Exit normally.
**Expected:** 7.1 silent. 7.2 outcome is the open question — record exactly what (if anything) appears:
a black flag (`black_flag` source)? Nothing at all? This determines whether the previously-scoped
"speeding in pit lane" cause detector is worth building.

### Phase 8 — Other cars (if any real drivers present)
8.1. Note approximate session time + what you visually observed for any other car's incident.
**Expected:** compare against what our detector caught for that car_idx — cause and timing should
roughly line up even though points won't resolve live for them.

### Phase 9 — Session-end / results
9.1. After your last on-track action, stay connected for a few minutes before disconnecting.
**Expected/open question:** does `live_incident_totals_resolved` ever fire in a mere **practice**
session, or does official-results posting only happen for races? This is untested — practice sessions
may never finalize `ResultsPositions[].Incidents` the way a race does. Record whether it fires.

### Phase 10 — Replay validation
10.1. Load the replay of this exact session.
10.2. Confirm the index auto-builds (no manual "Start" click) and completes.
10.3. Cross-check every phase 1-8 action appears as a row: correct cause, correct points, no
duplicates, nothing missing.
10.4. Reload/revisit the Replay Index tab — fingerprints and rows must stay stable (no duplicate rows
on a second load).
10.5. Check `replay_index_driver_gap` / `replay_index_build_completion_audit` Loki events — do the
detected-vs-expected counts match for every driver, not just you?

---

## Wish list — signals, scorecard ideas, validation techniques, calibration baseline

**Ground-truth tooling (deferred this round, worth building for round 2):**
- A "mark test event" dashboard action/hotkey that logs a precisely-timestamped marker
  (`test_event_marked`, with a free-text label) the instant it's pressed — turns "scripted order,
  cross-referenced after" into exact timestamp ground truth for a future, more rigorous calibration
  pass.

**Detection signals not yet built:**
- Pit-road speed limit + derived per-car speed (from `CarIdxLapDistPct` differentiation, already used
  elsewhere) cross-referenced against `CarIdxOnPitRoad` — the "speeding in pit lane" cause, scoped
  earlier this session but deliberately deferred until we see Phase 7's real result.
- Explicit "0x light contact, no penalty" surfacing — currently invisible (no incident logged at all
  for a 0x event), which is probably correct behavior but has never been decided on purpose.
- Dirt-session calibration — `IncidentSeverityCorrelator`'s 4x→2x dirt cap is flagged in its own code
  comment as "unvalidated heuristic, never confirmed against a real live dirt-oval session." This
  scorecard is pavement-only; a dirt-oval equivalent run is a distinct future wish-list item.

**Validation technique — automated scorecard diff:**
- Right now, matching ground truth against detections is manual (read Loki, read the index, eyeball
  it). Worth building a small script that ingests (a) this doc's ordered script as structured
  ground-truth data, (b) the session's Loki `live_incident_detection`/`live_incident_escalated`
  events, and (c) the persisted Replay Index JSON, and emits a pass/fail table per phase — turns this
  into a repeatable regression suite instead of a one-off manual check, so future detector changes can
  be validated against the same baseline automatically.

**Metrics worth tracking over time, once the above exists:**
- False-positive rate (Phase 0 + Phase 1 should always be zero).
- False-negative rate (fraction of Phase 2-7 intentional actions that produced zero detection).
- Fingerprint stability (Phase 10.4 — any duplicate/missing rows on reload).
- Driver-gap audit pass rate across all drivers, not just the player (Phase 10.5) — this is the
  strongest signal for whether the Replay Index Build's per-incident resolution is trustworthy for
  *other* cars, which is the harder, less-tested half of the system.

**Open questions this run should answer (not yet known):**
- ~~Does `live_incident_totals_resolved` ever fire in a practice session, or only in races?~~
  **ANSWERED 2026-07-19, outside this plan's own run** — confirmed live during an actual hosted
  session (subsession 87331584, Spa-Francorchamps): fired the moment `session_num` rolled `0→1`
  (practice segment ending, race/quali segment starting), not tied to the whole event finishing.
  18 drivers, 7 total points, correctly broadcast. Results go official per-*segment*, not just
  per-event.
- Does pit-lane speeding produce any SDK-visible signal at all via the fields we already read? (Phase 7)
- Does a genuinely light wall touch register as 0x (silently, correctly) or does our detector
  currently over-report it? (Phase 4.1)
