# Incident Cause Inference — Spin, Contact-Partner, Wall (design)

**Date:** 2026-07-25
**Status:** Approved (brainstorm), pending implementation plan
**Branch:** feat/incident-scoring-accuracy

## Problem

Today the "cause" label on an incident (`off-track` / `spin` / `contact` / `flagged` / `unknown` —
`IncidentCauseMapping.cs`) is only ever accurate for `off-track` (a direct read of
`CarIdxTrackSurface`) and for `spin`/`contact` **when a real points value resolves** (1x→off-track,
2x→spin, 4x→contact — `IncidentCauseMapping.Resolve`). Points only resolve live for the player's own
car (`PlayerCarMyIncidentCount` delta); for every other car, `spin` is structurally unreachable and
`contact` collapses to whatever the triggering SDK source was (`repair_flag`/`fast_repair`→contact,
`furled_flag`/`black_flag`→flagged), with no notion of *which other car* was involved.

Validated during research (this session, cross-checked against `irsdk_defines.h` and the
`mrbelowski/CrewChiefV4` source directly, not guessed):

- The iRacing SDK has **no per-car world position** for other cars — confirmed both by
  `docs/IRACING-DATA-AVAILABILITY.md` (no such field in any group) and structurally by CrewChief
  itself: its generic `Spotter` base class needs real X/Z opponent coordinates, and only
  `ACSSpotter.cs`/`R3ESpotterv2.cs`/`PCars2Spotterv2.cs` override the method that supplies them.
  `iRacingSpotter.cs` never does — it just relays iRacing's own native `CarLeftRight` telemetry
  value, confirmed against `irsdk_defines.h`'s `irsdk_CarLeftRight` enum
  (`Off/Clear/CarLeft/CarRight/CarLeftRight/2CarsLeft/2CarsRight`). `CarLeftRight` is player-only and
  carries no car identity.
- No SDK field or event represents "spin"/"loss of control" (all 32 `SessionFlags` bits enumerated
  in `docs/IRACING-CROSSWALK.md` Appendix A checked; none apply) or "wall" (surface-material enums
  only describe ground type: tarmac/grass/gravel/dirt/rumble — never a fixed barrier).
- `docs/IRACING-CROSSWALK.md` previously miscited CrewChief's `DamageReporting.cs` as doing
  `YawRate`-based spin detection — corrected during this session after a full-repo GitHub code
  search returned zero hits for `YawRate` anywhere in CrewChief.

Goal: close these three gaps as far as the available signals honestly allow, using **only Group 2
fields** (live + replay, every car, no admin) plus the one Group 3 player-only field (`CarLeftRight`)
that materially helps — without ever letting an inferred label be mistaken for a resolved one.

## Decisions (locked during brainstorm)

- **Attribution bar for contact partner:** show a best-guess `CarIdx`, always visibly labeled as
  inferred (e.g. "likely contact: car #12 (~4m)") — not a bare boolean, not a silent guess. Same
  honesty tier as the existing `EstimatedPoints` treatment.
- **A resolved points value always wins.** All three new signals are corroborating/fallback only —
  they must never override `IncidentCauseMapping.Resolve`'s existing points-override rule.
- **No new SDK polling.** Every field used (`CarIdxSteer`, `CarIdxGear`, `CarIdxTrackSurface`,
  `CarIdxLapDistPct`, `CarLeftRight`) is already read (or trivially added alongside an existing
  per-car read) in `SimStewardPlugin.LiveIncidentDetection.cs` / the replay sweep — no new tick-rate
  SDK calls.
- **Heuristics stay unvalidated-and-labeled-as-such**, same tier as the dirt 4x→2x cap — real
  accuracy validation happens later via the live-session scorecard process
  (`docs/INCIDENT-SCORECARD-TEST-PLAN.md`), not asserted from unit tests alone.

## Design

### New fields on `IncidentSample` (`ReplayIncidentIndexDetection.cs`)

All additive and nullable — absence must never break an existing consumer:

```
SuspectedContactCarIdx : int?    // best-guess nearby car, null if none found within threshold
ContactDistanceMeters  : float?  // distance to that car, for display/confidence context
LossOfControlScore     : float?  // 0.0-1.0 heuristic strength; null if not yet evaluated
PlayerCarLeftRight     : int?    // raw irsdk_CarLeftRight value; only ever set when CarIdx == playerCarIdx
```

### `IncidentProximityResolver.cs` (new, stateless)

```
static (int? carIdx, float? distanceMeters) FindNearestCar(
    int subjectCarIdx, float[] carIdxLapDistPct, float trackLengthMeters, float thresholdMeters)
```

- Converts every other car's `CarIdxLapDistPct` to a 1-D "meters around the lap" distance from the
  subject car, handling the lap-boundary wraparound (e.g. subject at 0.99, other car at 0.01).
- Same fundamental technique CrewChief's own `iRacingGameStateMapper.cs` uses for opponent-relative
  gap math (`DistanceRoundTrack = trackLength × CorrectedLapDistance`) — precedented, not novel, but
  still a 1-D proxy: it cannot see lateral separation, so it will misfire on wide straights (two cars
  far apart side-by-side reads as "close") and in tight corners (linear-distance assumption breaks
  down). This must be stated in the field's XML doc comment, not just this spec.
- Called **only** when a primary detection already fired (`repair_flag`, `fast_repair`,
  `track_surface`) — not every tick. Cheap, on-demand.

### `IncidentSpinHeuristic.cs` (new, stateful per car — same shape as `IncidentSeverityCorrelator`)

```
void Update(int carIdx, float steerRad, int gear, int trackSurface, double sessionTimeSec)
float? GetScore(int carIdx)
```

- Runs every tick (cheap array math) for every car, alongside the existing scratch-array reads in
  `SimStewardPlugin.LiveIncidentDetection.cs` (`CarIdxSteer`, `CarIdxGear`, `CarIdxTrackSurface` are
  already read there) — needs rolling history, not a single sample, so it cannot be computed
  on-demand like the proximity resolver.
- Score combines: steering-angle sign oscillation frequency (catching a slide), dwell time in neutral
  gear (0) outside expected shifting patterns, and on/off/on track-surface flicker count within a
  short rolling window (spin-and-recover) as distinct from a single clean off-track exit.
- Exact thresholds/weights are an implementation-time tuning question, not locked here — flag as
  "needs live-session tuning" in the plan.

### Wiring (`SimStewardPlugin.LiveIncidentDetection.cs` + replay sweep)

- `IncidentSpinHeuristic.Update` called every tick, right after the existing
  `CarIdxSteer`/`CarIdxGear`/`CarIdxTrackSurface` scratch reads (lines ~147-158 today) — no new SDK
  calls.
- `IncidentProximityResolver.FindNearestCar` called inside `LogLiveIncidentDetectionsLocked`, only
  for samples that just became a new/escalated board entry — reuses the already-read
  `_liveRaceScratchCarIdxLapDistPct`.
- `CarLeftRight` — one new `SafeGetInt("CarLeftRight")` call, gated `if (s.CarIdx == playerCarIdx)`,
  placed in `AddPlayerOnlyIncidentContext` alongside the existing Speed/RPM/G-force reads.

### Cause resolution hierarchy (`IncidentCauseMapping.cs`)

Extends, does not replace, the existing points-override rule:

1. Resolved points (1/2/4) → authoritative cause — **unchanged**.
2. No points, `LossOfControlScore` above threshold → cause = `spin`, tagged inferred. (Currently
   unreachable without points; this makes it reachable.)
3. No points, no spin score, `SuspectedContactCarIdx` present → cause = `contact`, carries the
   candidate CarIdx + distance, tagged inferred.
4. No points, no spin score, no nearby car, but a damage-adjacent event fired
   (`fast_repair`/`repair_flag`) → cause = `wall` (inferred by elimination — no direct wall signal
   exists at any SDK layer), tagged inferred.
5. Otherwise → `unknown`, same as today.

### Dashboard (`index.html`)

Same visual pattern as the existing `pointsBadgeHtml` (`~Nx est` vs. confirmed `Nx` — never let a
guess look like confirmed data): a new badge renders `contact: car #12 (~4m, inferred)` or
`spin (inferred)`, visually distinct from a resolved cause tag. No changes to the points badge itself.

### Tests

Matches the existing per-concern test file pattern (`IncidentSeverityCorrelatorTests.cs`,
`IncidentCauseMappingTests.cs`):

- `IncidentProximityResolverTests.cs` — lap-boundary wraparound (0.99↔0.01), multiple candidates
  picks the nearest, empty/all-absent field returns null, subject-car-excluded-from-its-own-search.
- `IncidentSpinHeuristicTests.cs` — synthetic steer-oscillation / neutral-gear-dwell / surface-flicker
  sequences, deterministic, no live telemetry required.
- `IncidentCauseMappingTests.cs` — extend with cases for the new hierarchy tiers (2-4 above),
  confirming points-override still wins when both a resolved value and an inferred signal are present.
- **Not** covered by unit tests: real-world accuracy of the heuristics themselves — that requires the
  live-session scorecard process, flagged explicitly so it isn't silently skipped.

## Out of scope (YAGNI)

- No true lateral/spatial position for other cars — confirmed structurally unavailable; not
  attempted.
- No camera-suggestion logic changes (`pickSuggestedCamera` in `index.html`) — this design only
  produces the underlying cause/contact-partner data; wiring it into camera suggestions is a
  separate follow-on, not bundled here.
- No admin-tier / official incident-count changes — this is entirely the no-admin Group 2 (+
  player-only `CarLeftRight`) signal set; Group 1's admin-gated official severity is untouched.
- No new SDK polling cadence or new WS message types — reuses existing per-tick reads and the
  existing `incidents` board broadcast.
- No threshold/weight tuning values locked in this doc — that's implementation+scorecard work, not a
  design decision.
