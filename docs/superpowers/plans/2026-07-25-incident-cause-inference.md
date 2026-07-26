# Incident Cause Inference (Spin / Contact-Partner / Wall) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the "spin" and "wall" cause labels (already styled in the dashboard CSS but never
producible today) actually reachable, and add a best-guess "likely contact with car #N" annotation
to contact-cause incidents — all from no-admin, Group 2 SDK signals already being read every tick.

**Architecture:** Two new stateless/stateful helper classes (`IncidentProximityResolver`,
`IncidentSpinHeuristic`) feed three new nullable fields on the existing `IncidentSample` struct.
`IncidentCauseMapping` gains an additive `ResolveInferred` method that layers the new tiers on top of
the existing points-authoritative `Resolve` without changing its behavior. `IncidentSeverityCorrelator`
is extended (new pending-state arrays, one new rank tier) to carry the inferred data through its
existing quick-succession merge window. Everything is wired into the already-running live-detection
tick in `SimStewardPlugin.LiveIncidentDetection.cs` — no new SDK polling.

**Tech Stack:** C# / .NET Framework 4.8, xUnit, vanilla JS (dashboard).

## Global Constraints

- Target .NET Framework 4.8 (per `CLAUDE.md`).
- New pure logic classes (`IncidentProximityResolver`, `IncidentSpinHeuristic`) must NOT be wrapped in
  `#if SIMHUB_SDK` — they have no SimHub SDK dependency, matching `IncidentSeverityCorrelator.cs` /
  `IncidentCauseMapping.cs` / `ReplayIncidentIndexDetector.cs`, all unconditional today. This is what
  makes them unit-testable; `SimStewardPlugin.LiveIncidentDetection.cs` (SDK-gated) has no direct tests
  for this same reason.
- No new SDK telemetry polling — every field used (`CarIdxSteer`, `CarIdxGear`, `CarIdxTrackSurface`,
  `CarIdxLapDistPct`, `CarLeftRight`) is already read, or is a trivial addition alongside an existing
  per-car read, in `SimStewardPlugin.LiveIncidentDetection.cs`.
- A resolved points value (1/2/4, from `PlayerCarMyIncidentCount` or a YAML delta) always remains
  authoritative over every inferred signal added here — this must never regress.
- Every new/changed method needs unit test coverage in the matching existing test file, per project
  convention (one test file per class, e.g. `IncidentCauseMappingTests.cs`).
- `deploy.ps1` must pass: 0 build errors, `dotnet test` green, `tests/*.ps1` green. Retry-once-then-stop
  on any failure (per `CLAUDE.md`).
- Spec: `docs/superpowers/specs/2026-07-25-incident-cause-inference-design.md`.

---

### Task 1: Extend `IncidentSample` with three new inferred-context fields

**Files:**
- Modify: `src/SimSteward.Plugin/ReplayIncidentIndexDetection.cs:94-136` (the `IncidentSample` struct)
- Test: `src/SimSteward.Plugin.Tests/ReplayIncidentIndexDetectionTests.cs`

**Interfaces:**
- Produces: `IncidentSample.LossOfControlScore : float?`, `IncidentSample.SuspectedContactCarIdx : int?`,
  `IncidentSample.ContactDistanceMeters : float?` — all default `null` via new trailing optional
  constructor parameters, so every existing call site (`ReplayIncidentIndexDetector.cs`,
  `IncidentSeverityCorrelatorTests.cs`'s `Sample(...)` helper, etc.) compiles unchanged.

- [ ] **Step 1: Write the failing test**

Add to `src/SimSteward.Plugin.Tests/ReplayIncidentIndexDetectionTests.cs` (inside the existing
`ReplayIncidentIndexDetectionTests` class):

```csharp
[Fact]
public void IncidentSample_InferredFields_DefaultToNull()
{
    var s = new IncidentSample(5, 1000, ReplayIncidentIndexDetection.SourceTrackSurface, null, 0);

    Assert.Null(s.LossOfControlScore);
    Assert.Null(s.SuspectedContactCarIdx);
    Assert.Null(s.ContactDistanceMeters);
}

[Fact]
public void IncidentSample_InferredFields_CarryThroughWhenSet()
{
    var s = new IncidentSample(
        5, 1000, ReplayIncidentIndexDetection.SourceFastRepair, null, 0,
        lossOfControlScore: 0.75f,
        suspectedContactCarIdx: 12,
        contactDistanceMeters: 4.2f);

    Assert.Equal(0.75f, s.LossOfControlScore);
    Assert.Equal(12, s.SuspectedContactCarIdx);
    Assert.Equal(4.2f, s.ContactDistanceMeters);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~IncidentSample_Inferred"`
Expected: FAIL — compile error, `IncidentSample` has no such constructor parameters/properties yet.

- [ ] **Step 3: Implement the field/constructor additions**

In `src/SimSteward.Plugin/ReplayIncidentIndexDetection.cs`, replace the `IncidentSample` constructor
and property list (currently lines 94-136) with:

```csharp
        public IncidentSample(
            int carIdx,
            int sessionTimeMs,
            string detectionSource,
            int? incidentPoints,
            int replayFrame,
            int lap = SessionLogging.LapUnknown,
            int sessionNum = SessionNumUnknown,
            float? lapDistPct = null,
            int? carPosition = null,
            bool isAggregateDelta = false,
            float? lossOfControlScore = null,
            int? suspectedContactCarIdx = null,
            float? contactDistanceMeters = null)
        {
            CarIdx = carIdx;
            SessionTimeMs = sessionTimeMs;
            DetectionSource = detectionSource ?? "";
            IncidentPoints = incidentPoints;
            ReplayFrame = replayFrame;
            Lap = lap;
            SessionNum = sessionNum;
            LapDistPct = lapDistPct;
            CarPosition = carPosition;
            IsAggregateDelta = isAggregateDelta;
            LossOfControlScore = lossOfControlScore;
            SuspectedContactCarIdx = suspectedContactCarIdx;
            ContactDistanceMeters = contactDistanceMeters;
        }

        public int CarIdx { get; }
        public int SessionTimeMs { get; }
        public string DetectionSource { get; }
        public int? IncidentPoints { get; }
        public int ReplayFrame { get; }
        public int Lap { get; }
        public int SessionNum { get; }
        /// <summary>Track position 0.0-1.0 at detection time (from CarIdxLapDistPct).</summary>
        public float? LapDistPct { get; }
        /// <summary>Race position at detection time (from CarIdxPosition). 0 = not classified.</summary>
        public int? CarPosition { get; }
        /// <summary>
        /// True when <see cref="IncidentPoints"/> was resolved by capping a YAML-poll delta that spanned
        /// more than one iRacing-scored event between snapshots (not a clean single-event 1/2/4 read) —
        /// see <see cref="ReplayIncidentYamlDiff"/>. Downstream consumers can use this to distinguish a
        /// confirmed single-event value from a capped aggregate.
        /// </summary>
        public bool IsAggregateDelta { get; }
        /// <summary>
        /// 0.0-1.0 best-effort "is this car currently losing control" score from <see cref="IncidentSpinHeuristic"/>,
        /// or null when not evaluated. Never a confirmed spin — see docs/IRACING-DATA-AVAILABILITY.md
        /// and docs/IRACING-CROSSWALK.md: no SDK field or event represents loss-of-control for any car.
        /// </summary>
        public float? LossOfControlScore { get; }
        /// <summary>
        /// Best-guess CarIdx of a nearby car at detection time, from <see cref="IncidentProximityResolver"/>,
        /// or null if none found within threshold. A proximity coincidence (1-D lap-distance proxy), never
        /// a confirmed contact partner — the SDK has no real per-car world position.
        /// </summary>
        public int? SuspectedContactCarIdx { get; }
        /// <summary>Distance in meters to <see cref="SuspectedContactCarIdx"/>, for display/confidence context.</summary>
        public float? ContactDistanceMeters { get; }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~IncidentSample_Inferred"`
Expected: PASS (2 tests).

- [ ] **Step 5: Run the full existing suite to confirm no regression**

Run: `dotnet test --filter "FullyQualifiedName~ReplayIncidentIndexDetectionTests|FullyQualifiedName~ReplayIncidentIndexDetectorTests"`
Expected: PASS, same count as before this change.

- [ ] **Step 6: Commit**

```bash
git add src/SimSteward.Plugin/ReplayIncidentIndexDetection.cs src/SimSteward.Plugin.Tests/ReplayIncidentIndexDetectionTests.cs
git commit -m "feat(incidents): add inferred-context fields to IncidentSample"
```

---

### Task 2: `IncidentProximityResolver` — nearest-car lookup

**Files:**
- Create: `src/SimSteward.Plugin/IncidentProximityResolver.cs`
- Test: `src/SimSteward.Plugin.Tests/IncidentProximityResolverTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `IncidentProximityResolver.FindNearestCar(int subjectCarIdx, float[] carIdxLapDistPct, float trackLengthMeters, float thresholdMeters) : (int? carIdx, float? distanceMeters)` — used by Task 6.

- [ ] **Step 1: Write the failing tests**

Create `src/SimSteward.Plugin.Tests/IncidentProximityResolverTests.cs`:

```csharp
using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class IncidentProximityResolverTests
    {
        [Fact]
        public void FindNearestCar_OneCarWithinThreshold_ReturnsIt()
        {
            var pct = new float[] { 0.500f, 0.503f, -1f, -1f };
            var (carIdx, distance) = IncidentProximityResolver.FindNearestCar(0, pct, trackLengthMeters: 1000f, thresholdMeters: 10f);

            Assert.Equal(1, carIdx);
            Assert.Equal(3f, distance.Value, 3);
        }

        [Fact]
        public void FindNearestCar_NoCarsWithinThreshold_ReturnsNull()
        {
            var pct = new float[] { 0.500f, 0.900f };
            var (carIdx, distance) = IncidentProximityResolver.FindNearestCar(0, pct, trackLengthMeters: 1000f, thresholdMeters: 10f);

            Assert.Null(carIdx);
            Assert.Null(distance);
        }

        [Fact]
        public void FindNearestCar_MultipleCandidates_PicksNearest()
        {
            var pct = new float[] { 0.500f, 0.520f, 0.505f };
            var (carIdx, distance) = IncidentProximityResolver.FindNearestCar(0, pct, trackLengthMeters: 1000f, thresholdMeters: 50f);

            Assert.Equal(2, carIdx);
            Assert.Equal(5f, distance.Value, 3);
        }

        [Fact]
        public void FindNearestCar_LapBoundaryWraparound_TakesShortWayAround()
        {
            // Subject at 0.995, other car at 0.005 — physically 10m apart the short way,
            // not 990m the naive way.
            var pct = new float[] { 0.995f, 0.005f };
            var (carIdx, distance) = IncidentProximityResolver.FindNearestCar(0, pct, trackLengthMeters: 1000f, thresholdMeters: 20f);

            Assert.Equal(1, carIdx);
            Assert.Equal(10f, distance.Value, 3);
        }

        [Fact]
        public void FindNearestCar_SubjectExcludedFromItsOwnSearch()
        {
            var pct = new float[] { 0.500f };
            var (carIdx, distance) = IncidentProximityResolver.FindNearestCar(0, pct, trackLengthMeters: 1000f, thresholdMeters: 10f);

            Assert.Null(carIdx);
            Assert.Null(distance);
        }

        [Fact]
        public void FindNearestCar_SubjectNotInWorld_ReturnsNull()
        {
            var pct = new float[] { -1f, 0.500f };
            var (carIdx, distance) = IncidentProximityResolver.FindNearestCar(0, pct, trackLengthMeters: 1000f, thresholdMeters: 10f);

            Assert.Null(carIdx);
            Assert.Null(distance);
        }

        [Fact]
        public void FindNearestCar_InvalidTrackLengthOrThreshold_ReturnsNull()
        {
            var pct = new float[] { 0.500f, 0.501f };
            Assert.Null(IncidentProximityResolver.FindNearestCar(0, pct, 0f, 10f).carIdx);
            Assert.Null(IncidentProximityResolver.FindNearestCar(0, pct, 1000f, 0f).carIdx);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~IncidentProximityResolverTests"`
Expected: FAIL — compile error, `IncidentProximityResolver` doesn't exist yet.

- [ ] **Step 3: Implement**

Create `src/SimSteward.Plugin/IncidentProximityResolver.cs`:

```csharp
using System;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Best-effort "who else was nearby" lookup for incident context. The iRacing SDK exposes no real
    /// world position for other cars (confirmed: docs/IRACING-DATA-AVAILABILITY.md, and structurally
    /// via CrewChiefV4 — only its ACS/R3E/PCars2 spotters override real X/Z opponent coordinates;
    /// iRacingSpotter.cs never does) — only CarIdxLapDistPct (0.0-1.0 around the lap). This converts
    /// that to a 1-D "distance around the lap" proxy — the same technique CrewChiefV4's own
    /// iRacingGameStateMapper.cs uses for opponent-relative gap math (DistanceRoundTrack) — and finds
    /// the closest other car within a threshold.
    ///
    /// This is a 1-D proxy: it cannot see lateral separation, so two cars far apart side-by-side on a
    /// wide straight can read as "close," and the linear-distance assumption weakens in tight corners.
    /// A result here is a proximity coincidence, never a confirmed contact partner.
    /// </summary>
    public static class IncidentProximityResolver
    {
        public static (int? carIdx, float? distanceMeters) FindNearestCar(
            int subjectCarIdx, float[] carIdxLapDistPct, float trackLengthMeters, float thresholdMeters)
        {
            if (carIdxLapDistPct == null || subjectCarIdx < 0 || subjectCarIdx >= carIdxLapDistPct.Length)
                return (null, null);
            if (trackLengthMeters <= 0f || thresholdMeters <= 0f)
                return (null, null);

            float subjectPct = carIdxLapDistPct[subjectCarIdx];
            if (subjectPct < 0f) // NotInWorld / unknown sentinel
                return (null, null);

            int? bestCarIdx = null;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < carIdxLapDistPct.Length; i++)
            {
                if (i == subjectCarIdx) continue;
                float otherPct = carIdxLapDistPct[i];
                if (otherPct < 0f) continue; // car not in world / not present this session

                float pctDelta = Math.Abs(subjectPct - otherPct);
                if (pctDelta > 0.5f) pctDelta = 1f - pctDelta; // shorter way around the lap loop

                float distanceMeters = pctDelta * trackLengthMeters;
                if (distanceMeters <= thresholdMeters && distanceMeters < bestDistance)
                {
                    bestDistance = distanceMeters;
                    bestCarIdx = i;
                }
            }

            return bestCarIdx.HasValue ? (bestCarIdx, (float?)bestDistance) : (null, null);
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~IncidentProximityResolverTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/SimSteward.Plugin/IncidentProximityResolver.cs src/SimSteward.Plugin.Tests/IncidentProximityResolverTests.cs
git commit -m "feat(incidents): add IncidentProximityResolver nearest-car lookup"
```

---

### Task 3: `IncidentSpinHeuristic` — rolling loss-of-control score

**Files:**
- Create: `src/SimSteward.Plugin/IncidentSpinHeuristic.cs`
- Test: `src/SimSteward.Plugin.Tests/IncidentSpinHeuristicTests.cs`

**Interfaces:**
- Consumes: `ReplayIncidentIndexDetection.TrackSurfaceOnTrack`/`TrackSurfaceOffTrack`,
  `ReplayIncidentIndexBuild.CarSlotCount` (both already exist).
- Produces: `IncidentSpinHeuristic.Update(int carIdx, float steerRad, int gear, int trackSurface, double sessionTimeSec) : void`,
  `IncidentSpinHeuristic.GetScore(int carIdx, double sessionTimeSec) : float?`, `IncidentSpinHeuristic.Reset() : void` —
  used by Task 6 (constructed once, `Reset()` called at session boundary alongside the existing
  detector/correlator resets).

- [ ] **Step 1: Write the failing tests**

Create `src/SimSteward.Plugin.Tests/IncidentSpinHeuristicTests.cs`:

```csharp
using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class IncidentSpinHeuristicTests
    {
        [Fact]
        public void GetScore_NeverUpdated_ReturnsNull()
        {
            var h = new IncidentSpinHeuristic();
            Assert.Null(h.GetScore(5, 10.0));
        }

        [Fact]
        public void GetScore_SteadyStraightLineDriving_ScoresZero()
        {
            var h = new IncidentSpinHeuristic();
            for (double t = 0; t <= 2.0; t += 0.1)
                h.Update(5, steerRad: 0.05f, gear: 3, trackSurface: ReplayIncidentIndexDetection.TrackSurfaceOnTrack, sessionTimeSec: t);

            Assert.Equal(0f, h.GetScore(5, 2.0).Value, 3);
        }

        [Fact]
        public void GetScore_RapidSteerReversals_RaisesScore()
        {
            var h = new IncidentSpinHeuristic();
            float[] steerPattern = { 0.6f, -0.6f, 0.6f, -0.6f, 0.6f, -0.6f };
            double t = 0;
            foreach (var steer in steerPattern)
            {
                h.Update(5, steer, gear: 2, trackSurface: ReplayIncidentIndexDetection.TrackSurfaceOnTrack, sessionTimeSec: t);
                t += 0.2;
            }

            Assert.True(h.GetScore(5, t).Value >= 0.5f);
        }

        [Fact]
        public void GetScore_SustainedNeutralGear_ContributesToScore()
        {
            var h = new IncidentSpinHeuristic();
            h.Update(5, 0f, gear: 0, trackSurface: ReplayIncidentIndexDetection.TrackSurfaceOnTrack, sessionTimeSec: 0.0);

            Assert.True(h.GetScore(5, 1.5).Value > 0f); // 1.5s of neutral dwell since it started
        }

        [Fact]
        public void GetScore_OffTrackOnTrackFlicker_ContributesToScore()
        {
            var h = new IncidentSpinHeuristic();
            h.Update(5, 0f, gear: 2, trackSurface: ReplayIncidentIndexDetection.TrackSurfaceOnTrack, sessionTimeSec: 0.0);
            h.Update(5, 0f, gear: 2, trackSurface: ReplayIncidentIndexDetection.TrackSurfaceOffTrack, sessionTimeSec: 0.2);
            h.Update(5, 0f, gear: 2, trackSurface: ReplayIncidentIndexDetection.TrackSurfaceOnTrack, sessionTimeSec: 0.4);

            Assert.True(h.GetScore(5, 0.4).Value > 0f);
        }

        [Fact]
        public void GetScore_OldEventsOutsideWindow_DoNotContribute()
        {
            var h = new IncidentSpinHeuristic();
            float[] steerPattern = { 0.6f, -0.6f, 0.6f, -0.6f };
            double t = 0;
            foreach (var steer in steerPattern)
            {
                h.Update(5, steer, gear: 2, trackSurface: ReplayIncidentIndexDetection.TrackSurfaceOnTrack, sessionTimeSec: t);
                t += 0.2;
            }
            // Let the window (3s) fully elapse with steady driving.
            for (double drift = t; drift <= t + IncidentSpinHeuristic.WindowSec + 0.5; drift += 0.5)
                h.Update(5, 0.05f, gear: 2, trackSurface: ReplayIncidentIndexDetection.TrackSurfaceOnTrack, sessionTimeSec: drift);

            Assert.Equal(0f, h.GetScore(5, t + IncidentSpinHeuristic.WindowSec + 0.5).Value, 3);
        }

        [Fact]
        public void Reset_ClearsAllPerCarState()
        {
            var h = new IncidentSpinHeuristic();
            h.Update(5, 0.6f, gear: 0, trackSurface: ReplayIncidentIndexDetection.TrackSurfaceOffTrack, sessionTimeSec: 1.0);
            Assert.NotNull(h.GetScore(5, 1.0));

            h.Reset();

            Assert.Null(h.GetScore(5, 1.0));
        }

        [Fact]
        public void Update_CarIdxOutOfRange_DoesNotThrow()
        {
            var h = new IncidentSpinHeuristic();
            h.Update(-1, 0f, 0, 0, 0.0);
            h.Update(999, 0f, 0, 0, 0.0);
            Assert.Null(h.GetScore(-1, 0.0));
            Assert.Null(h.GetScore(999, 0.0));
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~IncidentSpinHeuristicTests"`
Expected: FAIL — compile error, `IncidentSpinHeuristic` doesn't exist yet.

- [ ] **Step 3: Implement**

Create `src/SimSteward.Plugin/IncidentSpinHeuristic.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Best-effort "is this car currently losing control / spinning" score, built entirely from
    /// symptom correlation — the iRacing SDK has no direct spin/yaw-rate signal for any car (confirmed:
    /// docs/IRACING-CROSSWALK.md — YawRate exists only as a player-only field, is not present anywhere
    /// in CrewChiefV4 despite a prior doc miscitation, and has no per-car array equivalent). Combines
    /// three Group 2 proxies (any car, no admin, all already read every tick):
    /// <list type="bullet">
    /// <item>rapid steering-angle sign reversals within the rolling window ("catching a slide")</item>
    /// <item>continuous dwell time in neutral gear (0)</item>
    /// <item>off-track-then-back-on-track flicker within the window (spin-and-recover, distinct from a
    /// single clean off-track exit)</item>
    /// </list>
    /// Score is 0.0-1.0, never a confirmed detection — same honesty tier as
    /// <see cref="IncidentPointsEstimate"/>. Thresholds are a starting point; real accuracy needs
    /// live-session validation (docs/INCIDENT-SCORECARD-TEST-PLAN.md), not just these unit tests.
    /// </summary>
    public sealed class IncidentSpinHeuristic
    {
        /// <summary>Rolling window (seconds) over which reversal/flicker events are counted.</summary>
        public const double WindowSec = 3.0;
        private const int ReversalsForFullSignal = 4;
        private const double NeutralDwellForFullSignalSec = 1.0;
        private const int FlickersForFullSignal = 2;

        private readonly List<double>[] _steerReversalTimes = new List<double>[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly List<double>[] _flickerTimes = new List<double>[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly int[] _lastSteerSign = new int[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly double[] _neutralSinceSec = new double[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly int[] _lastTrackSurface = new int[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly bool[] _hasSample = new bool[ReplayIncidentIndexBuild.CarSlotCount];

        public IncidentSpinHeuristic()
        {
            Reset();
        }

        /// <summary>Clears all per-car rolling state. Call at session boundaries alongside the detector/correlator resets.</summary>
        public void Reset()
        {
            for (int i = 0; i < ReplayIncidentIndexBuild.CarSlotCount; i++)
            {
                _steerReversalTimes[i] = new List<double>();
                _flickerTimes[i] = new List<double>();
                _lastSteerSign[i] = 0;
                _neutralSinceSec[i] = -1;
                _lastTrackSurface[i] = ReplayIncidentIndexDetection.TrackSurfaceNotInWorld;
                _hasSample[i] = false;
            }
        }

        /// <summary>Feed one tick's telemetry for one car. Call every tick for every car — this needs rolling history, not a single sample.</summary>
        public void Update(int carIdx, float steerRad, int gear, int trackSurface, double sessionTimeSec)
        {
            if (carIdx < 0 || carIdx >= ReplayIncidentIndexBuild.CarSlotCount)
                return;

            if (_hasSample[carIdx])
            {
                int sign = Math.Sign(steerRad);
                int lastSign = _lastSteerSign[carIdx];
                if (sign != 0 && lastSign != 0 && sign != lastSign)
                    _steerReversalTimes[carIdx].Add(sessionTimeSec);
                if (sign != 0)
                    _lastSteerSign[carIdx] = sign;

                if (_lastTrackSurface[carIdx] == ReplayIncidentIndexDetection.TrackSurfaceOffTrack
                    && trackSurface == ReplayIncidentIndexDetection.TrackSurfaceOnTrack)
                {
                    _flickerTimes[carIdx].Add(sessionTimeSec);
                }
            }
            else
            {
                _lastSteerSign[carIdx] = Math.Sign(steerRad);
            }

            _lastTrackSurface[carIdx] = trackSurface;
            _hasSample[carIdx] = true;

            if (gear == 0)
            {
                if (_neutralSinceSec[carIdx] < 0)
                    _neutralSinceSec[carIdx] = sessionTimeSec;
            }
            else
            {
                _neutralSinceSec[carIdx] = -1;
            }

            PruneOld(_steerReversalTimes[carIdx], sessionTimeSec);
            PruneOld(_flickerTimes[carIdx], sessionTimeSec);
        }

        private static void PruneOld(List<double> times, double nowSec)
        {
            int i = 0;
            while (i < times.Count && nowSec - times[i] > WindowSec) i++;
            if (i > 0) times.RemoveRange(0, i);
        }

        /// <summary>0.0-1.0 combined score, or null if <see cref="Update"/> was never called for this car.</summary>
        public float? GetScore(int carIdx, double sessionTimeSec)
        {
            if (carIdx < 0 || carIdx >= ReplayIncidentIndexBuild.CarSlotCount)
                return null;
            if (!_hasSample[carIdx])
                return null;

            float reversalScore = Math.Min(1f, _steerReversalTimes[carIdx].Count / (float)ReversalsForFullSignal);

            float neutralScore = 0f;
            if (_neutralSinceSec[carIdx] >= 0)
            {
                double dwellSec = sessionTimeSec - _neutralSinceSec[carIdx];
                neutralScore = (float)Math.Min(1.0, Math.Max(0.0, dwellSec) / NeutralDwellForFullSignalSec);
            }

            float flickerScore = Math.Min(1f, _flickerTimes[carIdx].Count / (float)FlickersForFullSignal);

            return 0.5f * reversalScore + 0.25f * neutralScore + 0.25f * flickerScore;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~IncidentSpinHeuristicTests"`
Expected: PASS (8 tests). If `GetScore_RapidSteerReversals_RaisesScore` doesn't clear 0.5, check the
reversal count produced by the 6-sample alternating pattern (should be 4 reversals -> reversalScore=1.0
-> total >= 0.5) before adjusting the test's pattern length rather than loosening the assertion.

- [ ] **Step 5: Commit**

```bash
git add src/SimSteward.Plugin/IncidentSpinHeuristic.cs src/SimSteward.Plugin.Tests/IncidentSpinHeuristicTests.cs
git commit -m "feat(incidents): add IncidentSpinHeuristic rolling loss-of-control score"
```

---

### Task 4: `IncidentCauseMapping.ResolveInferred` + `CauseWall`

**Files:**
- Modify: `src/SimSteward.Plugin/IncidentCauseMapping.cs`
- Test: `src/SimSteward.Plugin.Tests/IncidentCauseMappingTests.cs`

**Interfaces:**
- Consumes: nothing new from earlier tasks (takes primitives only — decoupled from `IncidentSample`
  so it stays a pure, minimal-surface function).
- Produces: `IncidentCauseMapping.CauseWall = "wall"`, `IncidentCauseMapping.LossOfControlScoreThreshold = 0.6f`,
  `IncidentCauseMapping.ResolveInferred(string detectionSource, int? incidentPoints, float? lossOfControlScore = null, int? suspectedContactCarIdx = null) : string` —
  used by Task 5. Existing `Resolve(string, int?)` is untouched.

- [ ] **Step 1: Write the failing tests**

Add to `src/SimSteward.Plugin.Tests/IncidentCauseMappingTests.cs` (inside the existing class):

```csharp
        // ── ResolveInferred: additive tiers on top of the untouched Resolve() ──────
        [Fact]
        public void ResolveInferred_PointsResolved_DelegatesToResolve_IgnoringInferredSignals()
        {
            // A resolved points value must win even when a spin score / contact partner also happen to be present.
            var cause = IncidentCauseMapping.ResolveInferred(
                ReplayIncidentIndexDetection.SourceTrackSurface, incidentPoints: 4,
                lossOfControlScore: 0.9f, suspectedContactCarIdx: 7);

            Assert.Equal("contact", cause);
        }

        [Fact]
        public void ResolveInferred_NoPoints_HighLossOfControlScore_ReturnsSpin()
        {
            var cause = IncidentCauseMapping.ResolveInferred(
                ReplayIncidentIndexDetection.SourceTrackSurface, incidentPoints: null,
                lossOfControlScore: 0.85f);

            Assert.Equal("spin", cause);
        }

        [Fact]
        public void ResolveInferred_NoPoints_LowLossOfControlScore_DoesNotOverrideSource()
        {
            var cause = IncidentCauseMapping.ResolveInferred(
                ReplayIncidentIndexDetection.SourceTrackSurface, incidentPoints: null,
                lossOfControlScore: 0.1f);

            Assert.Equal("off-track", cause);
        }

        [Theory]
        [InlineData(ReplayIncidentIndexDetection.SourceFastRepair)]
        [InlineData(ReplayIncidentIndexDetection.SourceRepairFlag)]
        public void ResolveInferred_DamageEvent_NoNearbyCarFound_ReturnsWall(string source)
        {
            var cause = IncidentCauseMapping.ResolveInferred(source, incidentPoints: null, suspectedContactCarIdx: null);
            Assert.Equal("wall", cause);
        }

        [Theory]
        [InlineData(ReplayIncidentIndexDetection.SourceFastRepair)]
        [InlineData(ReplayIncidentIndexDetection.SourceRepairFlag)]
        public void ResolveInferred_DamageEvent_NearbyCarFound_ReturnsContact(string source)
        {
            var cause = IncidentCauseMapping.ResolveInferred(source, incidentPoints: null, suspectedContactCarIdx: 12);
            Assert.Equal("contact", cause);
        }

        [Fact]
        public void ResolveInferred_NoInferredSignalsAtAll_MatchesPlainResolve()
        {
            // Backward-compat guarantee: every existing 2-arg Resolve() call site behaves identically
            // when routed through ResolveInferred with the new params left at their defaults.
            foreach (var source in new[] {
                ReplayIncidentIndexDetection.SourceTrackSurface, ReplayIncidentIndexDetection.SourceFurledFlag,
                ReplayIncidentIndexDetection.SourceBlackFlag, ReplayIncidentIndexDetection.SourceDisqualify,
                ReplayIncidentIndexDetection.SourcePlayerIncidentCount, "unrecognized" })
            {
                Assert.Equal(IncidentCauseMapping.Resolve(source, null), IncidentCauseMapping.ResolveInferred(source, null));
            }
        }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~IncidentCauseMappingTests"`
Expected: FAIL — compile error, `ResolveInferred`/`CauseWall` don't exist yet.

- [ ] **Step 3: Implement**

In `src/SimSteward.Plugin/IncidentCauseMapping.cs`, add the new constant and method (after the
existing `CauseUnknown` constant and after the existing `Resolve` method respectively):

```csharp
        public const string CauseUnknown = "unknown";
        /// <summary>Inferred by elimination — a damage event fired but IncidentProximityResolver found no nearby car. No SDK field represents "wall" at any layer; see docs/IRACING-CROSSWALK.md.</summary>
        public const string CauseWall = "wall";

        /// <summary>Tuning value — revisit after live-session scorecard validation (docs/INCIDENT-SCORECARD-TEST-PLAN.md).</summary>
        public const float LossOfControlScoreThreshold = 0.6f;
```

```csharp
        /// <summary>
        /// Extends <see cref="Resolve"/> with two additive, heuristic-only tiers — used when
        /// <see cref="IncidentSpinHeuristic"/> / <see cref="IncidentProximityResolver"/> have
        /// something to say. A resolved <paramref name="incidentPoints"/> value always wins and
        /// delegates straight to <see cref="Resolve"/>, unchanged. When no inferred signal is
        /// present, behaves identically to <see cref="Resolve"/> (backward compatible).
        /// </summary>
        public static string ResolveInferred(
            string detectionSource,
            int? incidentPoints,
            float? lossOfControlScore = null,
            int? suspectedContactCarIdx = null)
        {
            if (incidentPoints.HasValue)
                return Resolve(detectionSource, incidentPoints);

            if (lossOfControlScore.HasValue && lossOfControlScore.Value >= LossOfControlScoreThreshold)
                return CauseSpin;

            switch ((detectionSource ?? "").Trim().ToLowerInvariant())
            {
                case ReplayIncidentIndexDetection.SourceFastRepair:
                case ReplayIncidentIndexDetection.SourceRepairFlag:
                    return suspectedContactCarIdx.HasValue ? CauseContact : CauseWall;
                default:
                    return Resolve(detectionSource, null);
            }
        }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~IncidentCauseMappingTests"`
Expected: PASS — all prior tests plus the new ones (16 total).

- [ ] **Step 5: Commit**

```bash
git add src/SimSteward.Plugin/IncidentCauseMapping.cs src/SimSteward.Plugin.Tests/IncidentCauseMappingTests.cs
git commit -m "feat(incidents): add ResolveInferred spin/wall/contact cause tiers"
```

---

### Task 5: Wire spin/wall/contact-partner into `IncidentSeverityCorrelator`

**Files:**
- Modify: `src/SimSteward.Plugin/IncidentSeverityCorrelator.cs`
- Test: `src/SimSteward.Plugin.Tests/IncidentSeverityCorrelatorTests.cs`

**Interfaces:**
- Consumes: `IncidentSample.LossOfControlScore`/`.SuspectedContactCarIdx`/`.ContactDistanceMeters`
  (Task 1), `IncidentCauseMapping.ResolveInferred`/`.CauseWall` (Task 4).
- Produces: `CorrelationResult.Merged` now carries the inferred fields forward through the
  quick-succession merge window — used by Task 6 to populate the board entry.

- [ ] **Step 1: Write the failing tests**

Add to `src/SimSteward.Plugin.Tests/IncidentSeverityCorrelatorTests.cs` (inside the existing class;
reuses the file's existing `Sample(...)` private helper — extend it with the 3 new optional params
first):

```csharp
        private static IncidentSample Sample(
            int carIdx, string source, int? points, double sessionTimeSec = 0, bool aggregate = false,
            float? lossOfControlScore = null, int? suspectedContactCarIdx = null, float? contactDistanceMeters = null)
        {
            return new IncidentSample(
                carIdx,
                ReplayIncidentIndexDetection.ToSessionTimeMs(sessionTimeSec),
                source,
                points,
                replayFrame: 0,
                isAggregateDelta: aggregate,
                lossOfControlScore: lossOfControlScore,
                suspectedContactCarIdx: suspectedContactCarIdx,
                contactDistanceMeters: contactDistanceMeters);
        }

        [Fact]
        public void Correlate_HighLossOfControlScore_NoPoints_ReportsSpinCause()
        {
            var c = new IncidentSeverityCorrelator();
            var s = Sample(5, ReplayIncidentIndexDetection.SourceTrackSurface, null, 10.0, lossOfControlScore: 0.9f);

            var r = c.Correlate(s, 10.0, isDirtSurface: false);

            Assert.True(r.IsNewIncident);
            Assert.Equal("spin", r.Cause);
            Assert.Equal(0.9f, r.Merged.LossOfControlScore);
        }

        [Fact]
        public void Correlate_FastRepair_NoNearbyCarNoPoints_ReportsWallCause()
        {
            var c = new IncidentSeverityCorrelator();
            var s = Sample(5, ReplayIncidentIndexDetection.SourceFastRepair, null, 10.0);

            var r = c.Correlate(s, 10.0, isDirtSurface: false);

            Assert.Equal("wall", r.Cause);
        }

        [Fact]
        public void Correlate_FastRepair_NearbyCarNoPoints_ReportsContactCauseAndCarriesPartner()
        {
            var c = new IncidentSeverityCorrelator();
            var s = Sample(5, ReplayIncidentIndexDetection.SourceFastRepair, null, 10.0,
                suspectedContactCarIdx: 12, contactDistanceMeters: 4.2f);

            var r = c.Correlate(s, 10.0, isDirtSurface: false);

            Assert.Equal("contact", r.Cause);
            Assert.Equal(12, r.Merged.SuspectedContactCarIdx);
            Assert.Equal(4.2f, r.Merged.ContactDistanceMeters);
        }

        [Fact]
        public void Correlate_WallThenPointsArriveLater_PointsOverrideWallCause()
        {
            var c = new IncidentSeverityCorrelator();

            var s1 = Sample(5, ReplayIncidentIndexDetection.SourceFastRepair, null, 10.0);
            var r1 = c.Correlate(s1, 10.0, isDirtSurface: false);
            Assert.Equal("wall", r1.Cause);

            var s2 = Sample(5, ReplayIncidentIndexDetection.SourcePlayerIncidentCount, 4, 11.0);
            var r2 = c.Correlate(s2, 11.0, isDirtSurface: false);

            Assert.True(r2.IsEscalation);
            Assert.Equal("contact", r2.Cause); // resolved points always wins, even over an already-reported "wall"
            Assert.Equal(4, r2.Merged.IncidentPoints);
        }

        [Fact]
        public void Correlate_ContactPartnerCarriesForwardAcrossEscalation()
        {
            var c = new IncidentSeverityCorrelator();

            var s1 = Sample(5, ReplayIncidentIndexDetection.SourceFastRepair, null, 10.0,
                suspectedContactCarIdx: 12, contactDistanceMeters: 3.0f);
            c.Correlate(s1, 10.0, isDirtSurface: false);

            // A later same-window sample with a lower cause rank (off-track) and no partner info of
            // its own must not blank out the already-known contact partner.
            var s2 = Sample(5, ReplayIncidentIndexDetection.SourceTrackSurface, null, 10.5);
            var r2 = c.Correlate(s2, 10.5, isDirtSurface: false);

            Assert.Equal(12, r2.Merged.SuspectedContactCarIdx);
            Assert.Equal(3.0f, r2.Merged.ContactDistanceMeters);
        }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~IncidentSeverityCorrelatorTests"`
Expected: FAIL — `Sample(...)` compile error (new params) and/or wrong cause strings ("unknown"/"contact"
instead of "spin"/"wall") since the correlator doesn't call `ResolveInferred` yet.

- [ ] **Step 3: Implement**

In `src/SimSteward.Plugin/IncidentSeverityCorrelator.cs`:

1. Add three new pending-state arrays alongside the existing ones:

```csharp
        private readonly int?[] _pendingBestContactCarIdx = new int?[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly float?[] _pendingBestContactDistance = new float?[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly float?[] _pendingBestLossOfControlScore = new float?[ReplayIncidentIndexBuild.CarSlotCount];
```

2. In `Reset()`, add to the loop body:

```csharp
                _pendingBestContactCarIdx[i] = null;
                _pendingBestContactDistance[i] = null;
                _pendingBestLossOfControlScore[i] = null;
```

3. Replace the body of `Correlate` with:

```csharp
        public CorrelationResult Correlate(IncidentSample sample, double sessionTimeSec, bool isDirtSurface, double windowSec = DefaultWindowSec)
        {
            int carIdx = sample.CarIdx;
            bool inRange = carIdx >= 0 && carIdx < ReplayIncidentIndexBuild.CarSlotCount;

            int? cappedPoints = ApplyDirtCap(sample.IncidentPoints, isDirtSurface);
            string sampleCause = IncidentCauseMapping.ResolveInferred(
                sample.DetectionSource, cappedPoints, sample.LossOfControlScore, sample.SuspectedContactCarIdx);
            int sampleCauseRank = CauseSeverityRank(sampleCause);

            double lastSec = inRange ? _pendingLastSampleTimeSec[carIdx] : NoPending;
            bool hasPending = lastSec >= 0 && (sessionTimeSec - lastSec) <= windowSec;

            int prevBestPoints = hasPending ? _pendingBestPoints[carIdx] : NoPoints;
            int prevBestCauseRank = hasPending ? _pendingBestCauseRank[carIdx] : 0;

            int newBestPoints = Math.Max(prevBestPoints, cappedPoints ?? NoPoints);
            int newBestCauseRank = Math.Max(prevBestCauseRank, sampleCauseRank);
            // Ties prefer the newest sample so the reported source/context stays traceable to what just happened.
            bool sampleWinsTie = !hasPending || sampleCauseRank >= prevBestCauseRank;
            string newBestSource = sampleWinsTie ? sample.DetectionSource : _pendingBestSource[carIdx];
            int? newBestContactCarIdx = sampleWinsTie ? sample.SuspectedContactCarIdx : (inRange ? _pendingBestContactCarIdx[carIdx] : null);
            float? newBestContactDistance = sampleWinsTie ? sample.ContactDistanceMeters : (inRange ? _pendingBestContactDistance[carIdx] : null);
            float? newBestLossOfControlScore = sampleWinsTie ? sample.LossOfControlScore : (inRange ? _pendingBestLossOfControlScore[carIdx] : null);

            int? newPoints = newBestPoints == NoPoints ? (int?)null : newBestPoints;
            string newCause = newPoints.HasValue
                ? IncidentCauseMapping.Resolve(newBestSource, newPoints) // points override — source irrelevant here
                : CauseFromRank(newBestCauseRank);

            bool isNew = !hasPending;
            bool changed = hasPending && (newBestPoints != prevBestPoints || newBestCauseRank != prevBestCauseRank);

            if (inRange)
            {
                _pendingLastSampleTimeSec[carIdx] = sessionTimeSec;
                _pendingBestPoints[carIdx] = newBestPoints;
                _pendingBestCauseRank[carIdx] = newBestCauseRank;
                _pendingBestSource[carIdx] = newBestSource;
                _pendingBestContactCarIdx[carIdx] = newBestContactCarIdx;
                _pendingBestContactDistance[carIdx] = newBestContactDistance;
                _pendingBestLossOfControlScore[carIdx] = newBestLossOfControlScore;
            }

            var merged = new IncidentSample(
                sample.CarIdx, sample.SessionTimeMs, newBestSource, newPoints, sample.ReplayFrame,
                sample.Lap, sample.SessionNum, sample.LapDistPct, sample.CarPosition, sample.IsAggregateDelta,
                lossOfControlScore: newBestLossOfControlScore,
                suspectedContactCarIdx: newBestContactCarIdx,
                contactDistanceMeters: newBestContactDistance);

            return new CorrelationResult(isNew, changed, merged, newCause);
        }
```

4. Extend `CauseSeverityRank`/`CauseFromRank` with a new top tier for "wall" (kept above "contact" —
   not because a wall hit is definitionally worse, but because it's the more specific inference of the
   two once a nearby-car check has already come back empty; same rank-vs-cause conflation the existing
   scheme already has, tracked as RISK 4 in `docs/REVIEW-incident-points-implementation.md`):

```csharp
        private static int CauseSeverityRank(string cause)
        {
            if (cause == IncidentCauseMapping.CauseOffTrack) return 1;
            if (cause == IncidentCauseMapping.CauseFlagged) return 2;
            if (cause == IncidentCauseMapping.CauseSpin) return 3;
            if (cause == IncidentCauseMapping.CauseContact) return 4;
            if (cause == IncidentCauseMapping.CauseWall) return 5;
            return 0; // unknown
        }

        private static string CauseFromRank(int rank)
        {
            switch (rank)
            {
                case 1: return IncidentCauseMapping.CauseOffTrack;
                case 2: return IncidentCauseMapping.CauseFlagged;
                case 3: return IncidentCauseMapping.CauseSpin;
                case 4: return IncidentCauseMapping.CauseContact;
                case 5: return IncidentCauseMapping.CauseWall;
                default: return IncidentCauseMapping.CauseUnknown;
            }
        }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~IncidentSeverityCorrelatorTests"`
Expected: PASS — all pre-existing tests plus the 5 new ones.

- [ ] **Step 5: Run the full test suite to confirm no cross-file regression**

Run: `dotnet test`
Expected: PASS, 0 failures.

- [ ] **Step 6: Commit**

```bash
git add src/SimSteward.Plugin/IncidentSeverityCorrelator.cs src/SimSteward.Plugin.Tests/IncidentSeverityCorrelatorTests.cs
git commit -m "feat(incidents): carry spin/wall/contact-partner through the severity correlator"
```

---

### Task 6: Live wiring — board entry fields + `SimStewardPlugin.LiveIncidentDetection.cs`

**Files:**
- Modify: `src/SimSteward.Plugin/PluginState.cs` (`LiveIncidentBoardEntry`, ~line 196-244)
- Modify: `src/SimSteward.Plugin/SimStewardPlugin.LiveIncidentDetection.cs`

**Interfaces:**
- Consumes: `IncidentProximityResolver.FindNearestCar` (Task 2), `IncidentSpinHeuristic` (Task 3),
  `CorrelationResult.Merged.{LossOfControlScore,SuspectedContactCarIdx,ContactDistanceMeters}` (Task 5).
- Produces: `LiveIncidentBoardEntry.InferredContactCarIdx : int?`, `.InferredContactDistanceMeters : float?` —
  used by Task 7 (dashboard).

This file is `#if SIMHUB_SDK`-gated and has no direct unit tests (matches existing project convention —
verified via build + `PluginSmokeTests.cs` + the manual `deploy.ps1` flow per `CLAUDE.md`).

- [ ] **Step 1: Add the two new fields to `LiveIncidentBoardEntry`**

In `src/SimSteward.Plugin/PluginState.cs`, inside `LiveIncidentBoardEntry` (after the existing
`Player` property):

```csharp
        [JsonProperty("player")]
        public bool Player { get; set; }

        /// <summary>Best-guess CarIdx of a nearby car at detection time (see IncidentProximityResolver) — null if none found. A proximity coincidence, never a confirmed contact partner.</summary>
        [JsonProperty("inferredContactCarIdx")]
        public int? InferredContactCarIdx { get; set; }

        /// <summary>Distance in meters to <see cref="InferredContactCarIdx"/>, for display/confidence context.</summary>
        [JsonProperty("inferredContactDistanceMeters")]
        public float? InferredContactDistanceMeters { get; set; }
```

- [ ] **Step 2: Add per-tick spin heuristic state + a tuning constant**

In `src/SimSteward.Plugin/SimStewardPlugin.LiveIncidentDetection.cs`, alongside the existing
`_liveIncidentCorrelator` field declaration (~line 12):

```csharp
        private readonly IncidentSeverityCorrelator _liveIncidentCorrelator = new IncidentSeverityCorrelator();
        /// <summary>Rolling per-car loss-of-control score, updated every tick — see IncidentSpinHeuristic.</summary>
        private readonly IncidentSpinHeuristic _liveSpinHeuristic = new IncidentSpinHeuristic();
        /// <summary>Nearest-car proximity threshold for the "contact" vs "wall" inference tier — tuning value, revisit after live-session scorecard validation.</summary>
        private const float ContactProximityThresholdMeters = 12.0f;
```

- [ ] **Step 3: Reset the spin heuristic at session boundaries**

In `ProcessLiveIncidentDetectionTick`, inside the `if (needReset)` block, alongside the existing
`_liveIncidentCorrelator.Reset();` call:

```csharp
                _liveIncidentCorrelator.Reset();
                _liveSpinHeuristic.Reset();
                _livePendingIncidentFingerprintByCar.Clear();
```

- [ ] **Step 4: Update the spin heuristic every tick**

Still in `ProcessLiveIncidentDetectionTick`, immediately after the existing block of per-tick scratch
reads (right after `SafeGetIntPerCar("CarIdxTireCompound", _liveRaceScratchCarIdxTireCompound);` and
before `int playerIncidents = ...`):

```csharp
            SafeGetIntPerCar("CarIdxTireCompound", _liveRaceScratchCarIdxTireCompound);

            for (int i = 0; i < ReplayIncidentIndexBuild.CarSlotCount; i++)
            {
                _liveSpinHeuristic.Update(
                    i,
                    _liveRaceScratchCarIdxSteer[i],
                    _liveRaceScratchCarIdxGear[i],
                    _liveRaceScratchCarIdxTrackSurface[i],
                    sessionTimeSec);
            }

            int playerIncidents = 0;
```

- [ ] **Step 5: Enrich each raw sample before correlation**

In `LogLiveIncidentDetectionsLocked`, inside the `foreach (var raw in samples)` loop, immediately
after the existing `try` block opens and before `var result = _liveIncidentCorrelator.Correlate(raw, sessionTimeSec, _liveRaceIsDirtSession);`:

```csharp
                try
                {
                    var (contactCarIdx, contactDistance) = IncidentProximityResolver.FindNearestCar(
                        raw.CarIdx, _liveRaceScratchCarIdxLapDistPct, trackLengthMeters, ContactProximityThresholdMeters);
                    var enriched = new IncidentSample(
                        raw.CarIdx, raw.SessionTimeMs, raw.DetectionSource, raw.IncidentPoints, raw.ReplayFrame,
                        raw.Lap, raw.SessionNum, raw.LapDistPct, raw.CarPosition, raw.IsAggregateDelta,
                        lossOfControlScore: _liveSpinHeuristic.GetScore(raw.CarIdx, sessionTimeSec),
                        suspectedContactCarIdx: contactCarIdx,
                        contactDistanceMeters: contactDistance);

                    var result = _liveIncidentCorrelator.Correlate(enriched, sessionTimeSec, _liveRaceIsDirtSession);
```

(This replaces the original `var result = _liveIncidentCorrelator.Correlate(raw, sessionTimeSec, _liveRaceIsDirtSession);`
line — `raw` upstream of this point is untouched, only the local `enriched` copy is used from here on.
`trackLengthMeters` is already computed earlier in this same method, right above the `foreach` loop.)

- [ ] **Step 6: Populate the new board-entry fields**

Still in `LogLiveIncidentDetectionsLocked`, in the `if (result.IsNewIncident)` branch, inside the
`new LiveIncidentBoardEntry { ... }` initializer, add the two new properties (after `Player = ...`):

```csharp
                            Player = s.CarIdx == playerCarIdx,
                            InferredContactCarIdx = s.SuspectedContactCarIdx,
                            InferredContactDistanceMeters = s.ContactDistanceMeters
```

And in the `else // IsEscalation` branch, alongside the existing `entry.Cause = result.Cause;` line:

```csharp
                        entry.Cause = result.Cause;
                        entry.InferredContactCarIdx = s.SuspectedContactCarIdx;
                        entry.InferredContactDistanceMeters = s.ContactDistanceMeters;
```

- [ ] **Step 7: Add `CarLeftRight` as a corroborating log-only field for the player**

In `AddPlayerOnlyIncidentContext`, alongside the existing `player_lat_accel`/`player_long_accel`/
`player_vert_accel` reads:

```csharp
            try { fields["player_lat_accel"] = _irsdk.Data.GetFloat("LatAccel"); } catch { }
            try { fields["player_long_accel"] = _irsdk.Data.GetFloat("LongAccel"); } catch { }
            try { fields["player_vert_accel"] = _irsdk.Data.GetFloat("VertAccel"); } catch { }
            try { fields["player_car_left_right"] = _irsdk.Data.GetInt("CarLeftRight"); } catch { }
```

Also add the two new fields to `BuildLiveIncidentLogFields`'s returned dictionary (alongside the
existing `["car_tire_compound"]` line), so every detection log line carries them regardless of cause:

```csharp
                ["car_tire_compound"] = _liveRaceScratchCarIdxTireCompound[s.CarIdx],
                ["loss_of_control_score"] = s.LossOfControlScore.HasValue ? (object)s.LossOfControlScore.Value : null,
                ["suspected_contact_car_idx"] = s.SuspectedContactCarIdx.HasValue ? (object)s.SuspectedContactCarIdx.Value : null,
                ["contact_distance_meters"] = s.ContactDistanceMeters.HasValue ? (object)s.ContactDistanceMeters.Value : null
```

- [ ] **Step 8: Build and run the full test suite**

Run: `dotnet build` (expect 0 errors) then `dotnet test` (expect all green, same/higher pass count than
Task 5's end state).

- [ ] **Step 9: Commit**

```bash
git add src/SimSteward.Plugin/PluginState.cs src/SimSteward.Plugin/SimStewardPlugin.LiveIncidentDetection.cs
git commit -m "feat(incidents): wire spin/wall/contact-partner inference into the live detection tick"
```

---

### Task 7: Dashboard — show the inferred contact partner

**Files:**
- Modify: `src/SimSteward.Dashboard/index.html`

**Interfaces:**
- Consumes: `entries[].cause` (`"wall"`/`"spin"` now reachable), `entries[].inferredContactCarIdx`,
  `entries[].inferredContactDistanceMeters` (Task 6) — via the existing `{type:"incidents", entries:[...]}`
  WS message, no message-shape change needed.

No CSS changes needed — `.cause-tag.wall` and `.cause-tag.spin` are already defined
(`index.html:237-238`), just never reachable until this plan; verified during design research.

- [ ] **Step 1: Add a shared contact-partner suffix helper**

In `src/SimSteward.Dashboard/index.html`, add this function near `pointsBadgeHtml` (which it
complements — same "never let an inferred value look confirmed" rule):

```javascript
/**
 * Small suffix appended next to a "contact" cause tag when IncidentProximityResolver found a
 * candidate nearby car — e.g. "car #12 (~4m)". Always parenthetical/inferred, never implies the
 * SDK confirmed who was involved (see docs/IRACING-CROSSWALK.md — no per-car world position exists).
 * Returns '' when there's nothing to show (non-contact causes, or no candidate found).
 */
function contactPartnerSuffixHtml(i) {
  if (String(i.cause) !== 'contact') return '';
  const carIdx = i.inferredContactCarIdx;
  if (carIdx == null) return '';
  const dist = typeof i.inferredContactDistanceMeters === 'number' ? ` ~${i.inferredContactDistanceMeters.toFixed(0)}m` : '';
  return ` <span class="contact-partner-hint" title="Best-guess contact partner from track-position proximity — not confirmed by iRacing.">likely car #${escapeHtmlForCaptured(carIdx)}${escapeHtmlForCaptured(dist)}</span>`;
}
```

- [ ] **Step 2: Add minimal styling for the hint**

Alongside the existing `.cause-tag` CSS rules (`index.html:235-241`):

```css
.contact-partner-hint { font-size: 0.62rem; color: var(--muted); font-style: italic; }
```

- [ ] **Step 3: Wire the suffix into both incident renderers**

In `incidentCardHtml` (~`index.html:1584`), change the cause-tag line to:

```javascript
      <span class="cause-tag ${escapeHtmlForCaptured(cause)}">${escapeHtmlForCaptured(cause.replace('-', ' '))}</span>${contactPartnerSuffixHtml(i)}
```

In `incidentTableRowHtml` (~`index.html:1609`), change the cause-tag `<td>` to:

```javascript
      <td><span class="cause-tag ${escapeHtmlForCaptured(cause)}">${escapeHtmlForCaptured(cause.replace('-', ' '))}</span>${contactPartnerSuffixHtml(i)}</td>
```

- [ ] **Step 4: Manual verification (per CLAUDE.md's UI-change rule)**

Run `deploy.ps1`, open the dashboard in a browser, and confirm on the Incidents tab:
- A synthetic/live "contact" entry with `inferredContactCarIdx` set renders the "likely car #N ~Xm"
  hint next to the cause tag.
- A "wall" or "spin" cause entry renders with its existing (already-defined) yellow tag styling, no
  console errors.
- An entry with no `inferredContactCarIdx` renders exactly as it did before this change (no empty
  hint span, no layout shift).

- [ ] **Step 5: Commit**

```bash
git add src/SimSteward.Dashboard/index.html
git commit -m "feat(dashboard): show inferred contact-partner hint on contact-cause incidents"
```

---

### Task 8: Full build/test/deploy pass

**Files:** none (verification only)

- [ ] **Step 1: Full solution build**

Run: `dotnet build` — expect 0 errors, 0 new warnings.

- [ ] **Step 2: Full test suite**

Run: `dotnet test` — expect all green.

- [ ] **Step 3: PowerShell test scripts**

Run each script under `tests/*.ps1` — expect all green.

- [ ] **Step 4: Deploy**

Run: `deploy.ps1` (kills SimHub, copies DLLs, relaunches SimHub on success).

- [ ] **Step 5: Retry-once-then-stop**

If any of steps 1-4 fail, fix the root cause and retry the whole sequence **once**. If it fails again,
stop and report — do not retry further (per `CLAUDE.md`).

- [ ] **Step 6: Flag remaining validation as a separate, later step**

Do not claim the spin/wall/contact heuristics are "accurate" from this pass alone — that requires the
live-session scorecard process (`docs/INCIDENT-SCORECARD-TEST-PLAN.md`), which is out of scope for
this implementation plan (design-time tuning only, per the spec's Decisions section).

## Out of scope (YAGNI)

- No changes to the replay fast-forward sweep / Replay Index tab (`SimStewardPlugin.ReplayIncidentIndexBuild.cs`,
  `ReplayIncidentIndexResultsYaml.cs`) — separate detector instance, separate consumer, not touched.
- No changes to `pickSuggestedCamera` / camera-suggestion logic — a natural follow-on, not bundled here.
- No threshold/weight retuning beyond the starting constants defined in Tasks 3/6
  (`LossOfControlScoreThreshold`, `ContactProximityThresholdMeters`, `ReversalsForFullSignal`, etc.) —
  tuning is a live-session-validation activity, not a code-review activity.
- No admin-tier / official per-car incident severity changes.
- No new WS message types — reuses the existing `{type:"incidents", entries:[...]}` shape.
