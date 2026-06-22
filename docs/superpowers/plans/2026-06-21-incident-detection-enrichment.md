# Incident Detection Enrichment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enrich incident detections with context fields, add missing detection signals, fix documentation inaccuracies — based on the SDK expert audit.

**Architecture:** Expand `IncidentSample` struct with track position and severity context. Add `CarIdxFastRepairsUsed` rising-edge + Black/DQ flag detection as new signals. Add `CarIdxTrackSurfaceMaterial` filtering to reduce off-track false positives on kerbs. Propagate new fields through the document model to JSON output.

**Tech Stack:** .NET Framework 4.8, C#, Xunit, IRSDKSharper, Newtonsoft.Json

## Global Constraints

- Target .NET Framework 4.8
- All new fields on `IncidentSample` are optional (nullable or defaulted) — existing callers must not break
- Fingerprint v1 format must NOT change — new fields are context, not identity
- Detection runs at 60Hz in the native IRSDK poll — no allocations in hot path beyond the `List<IncidentSample>` that already exists
- Every new detection source needs a `Source*` constant string in `ReplayIncidentIndexDetection`
- Every new JSON field needs a `[JsonProperty]` on the document model
- Tests use Xunit; run with `dotnet test`

---

### Task 1: Enrich `IncidentSample` with track position and context fields

**Files:**
- Modify: `src/SimSteward.Plugin/ReplayIncidentIndexDetection.cs` (IncidentSample struct)
- Modify: `src/SimSteward.Plugin/ReplayIncidentIndexDocumentModel.cs` (ReplayIncidentIndexIncidentRow)
- Modify: `src/SimSteward.Plugin/ReplayIncidentIndexDocumentModel.cs` (ReplayIncidentIndexDocumentBuilder.Build)
- Test: `src/SimSteward.Plugin.Tests/ReplayIncidentIndexDetectionTests.cs`

**Interfaces:**
- Consumes: nothing new
- Produces: `IncidentSample.LapDistPct` (float?), `IncidentSample.CarPosition` (int?) — consumed by Task 5 (document builder) and all later tasks that construct `IncidentSample`

- [ ] **Step 1: Write failing test for new IncidentSample fields**

```csharp
[Fact]
public void IncidentSample_NewContextFields_DefaultToNull()
{
    var s = new IncidentSample(carIdx: 1, sessionTimeMs: 5000, detectionSource: "test", incidentPoints: null, replayFrame: 100);
    Assert.Null(s.LapDistPct);
    Assert.Null(s.CarPosition);
}

[Fact]
public void IncidentSample_NewContextFields_RoundTrip()
{
    var s = new IncidentSample(carIdx: 1, sessionTimeMs: 5000, detectionSource: "test", incidentPoints: null, replayFrame: 100,
        lapDistPct: 0.45f, carPosition: 3);
    Assert.Equal(0.45f, s.LapDistPct);
    Assert.Equal(3, s.CarPosition);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/SimSteward.Plugin.Tests/ --filter "IncidentSample_NewContextFields" -v n`
Expected: FAIL — `IncidentSample` has no `LapDistPct` or `CarPosition` parameters/properties

- [ ] **Step 3: Add fields to IncidentSample**

In `src/SimSteward.Plugin/ReplayIncidentIndexDetection.cs`, update the struct:

```csharp
public readonly struct IncidentSample
{
    public const int SessionNumUnknown = -1;

    public IncidentSample(
        int carIdx,
        int sessionTimeMs,
        string detectionSource,
        int? incidentPoints,
        int replayFrame,
        int lap = SessionLogging.LapUnknown,
        int sessionNum = SessionNumUnknown,
        float? lapDistPct = null,
        int? carPosition = null)
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
}
```

- [ ] **Step 4: Add fields to ReplayIncidentIndexIncidentRow (JSON model)**

In `src/SimSteward.Plugin/ReplayIncidentIndexDocumentModel.cs`, add to `ReplayIncidentIndexIncidentRow`:

```csharp
[JsonProperty("lapDistPct", NullValueHandling = NullValueHandling.Ignore)]
public float? LapDistPct { get; set; }

[JsonProperty("carPosition", NullValueHandling = NullValueHandling.Ignore)]
public int? CarPosition { get; set; }
```

And in `ReplayIncidentIndexDocumentBuilder.Build`, inside the `foreach` loop where rows are created, add:

```csharp
LapDistPct = s.LapDistPct,
CarPosition = s.CarPosition,
```

- [ ] **Step 5: Run tests**

Run: `dotnet test src/SimSteward.Plugin.Tests/ -v n`
Expected: ALL PASS (existing tests pass because new params are optional with defaults)

- [ ] **Step 6: Commit**

```bash
git add src/SimSteward.Plugin/ReplayIncidentIndexDetection.cs src/SimSteward.Plugin/ReplayIncidentIndexDocumentModel.cs src/SimSteward.Plugin.Tests/ReplayIncidentIndexDetectionTests.cs
git commit -m "feat: add LapDistPct and CarPosition context fields to IncidentSample"
```

---

### Task 2: Pass track position and race position into detector

**Files:**
- Modify: `src/SimSteward.Plugin/ReplayIncidentIndexDetector.cs` (Process signature + all IncidentSample construction)
- Test: `src/SimSteward.Plugin.Tests/ReplayIncidentIndexDetectionTests.cs`

**Interfaces:**
- Consumes: `IncidentSample.LapDistPct`, `IncidentSample.CarPosition` from Task 1
- Produces: Updated `Process()` signature with `float[] carIdxLapDistPct = null, int[] carIdxPosition = null` — consumed by the build loop in `SimStewardPlugin.ReplayIncidentIndexBuild.cs`

- [ ] **Step 1: Write failing test**

```csharp
[Fact]
public void Process_RepairDetection_CapturesLapDistPctAndPosition()
{
    var d = new ReplayIncidentIndexDetector();
    d.Reset(Zeros64(), 0, 0);

    var flags = Zeros64();
    flags[3] = ReplayIncidentIndexDetection.RepairSessionFlag;

    var lapDistPct = new float[64];
    lapDistPct[3] = 0.72f;
    var positions = Zeros64();
    positions[3] = 5;

    var r = d.Process(10.0, flags, 0, 0, 100,
        carIdxLapDistPct: lapDistPct, carIdxPosition: positions);

    Assert.Single(r);
    Assert.Equal(0.72f, r[0].LapDistPct);
    Assert.Equal(5, r[0].CarPosition);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/SimSteward.Plugin.Tests/ --filter "CapturesLapDistPctAndPosition" -v n`
Expected: FAIL — `Process` has no `carIdxLapDistPct` or `carIdxPosition` parameter

- [ ] **Step 3: Add parameters and propagate to all IncidentSample constructors**

In `ReplayIncidentIndexDetector.Process`, add two optional parameters after `carIdxLap`:

```csharp
public List<IncidentSample> Process(
    double replaySessionTimeSec,
    int[] flags,
    int playerIncidents,
    int playerCarIdx,
    int replayFrame,
    int[] trackSurface = null,
    int[] carIdxLap = null,
    int sessionNum = IncidentSample.SessionNumUnknown,
    float[] carIdxLapDistPct = null,
    int[] carIdxPosition = null)
```

Then in every `new IncidentSample(...)` call inside `Process`, add:

```csharp
lapDistPct: carIdxLapDistPct != null && i < carIdxLapDistPct.Length ? (float?)carIdxLapDistPct[i] : null,
carPosition: carIdxPosition != null && i < carIdxPosition.Length && carIdxPosition[i] > 0 ? (int?)carIdxPosition[i] : null
```

For the player-incident `IncidentSample`, use `playerCarIdx` as the index instead of `i`.

- [ ] **Step 4: Run all tests**

Run: `dotnet test src/SimSteward.Plugin.Tests/ -v n`
Expected: ALL PASS

- [ ] **Step 5: Commit**

```bash
git add src/SimSteward.Plugin/ReplayIncidentIndexDetector.cs src/SimSteward.Plugin.Tests/ReplayIncidentIndexDetectionTests.cs
git commit -m "feat: pass CarIdxLapDistPct and CarIdxPosition through detector to IncidentSample"
```

---

### Task 3: Add `CarIdxFastRepairsUsed` rising-edge detection

**Files:**
- Modify: `src/SimSteward.Plugin/ReplayIncidentIndexDetection.cs` (add `SourceFastRepair` constant)
- Modify: `src/SimSteward.Plugin/ReplayIncidentIndexDetector.cs` (add `_prevFastRepairs` array + detection)
- Test: `src/SimSteward.Plugin.Tests/ReplayIncidentIndexDetectionTests.cs`

**Interfaces:**
- Consumes: `IncidentSample` from Task 1
- Produces: `ReplayIncidentIndexDetection.SourceFastRepair` constant, new `int[] carIdxFastRepairsUsed` parameter on `Process()`

- [ ] **Step 1: Write failing test**

```csharp
[Fact]
public void Process_FastRepairIncrement_EmitsFastRepairRow()
{
    var d = new ReplayIncidentIndexDetector();
    var baseFlags = Zeros64();
    var baseFastRepairs = Zeros64();
    d.Reset(baseFlags, 0, 0, baselineFastRepairs: baseFastRepairs);

    var flags = Zeros64();
    var fastRepairs = Zeros64();
    fastRepairs[4] = 1; // car 4 used one fast repair

    var r = d.Process(15.0, flags, 0, 0, 200, carIdxFastRepairsUsed: fastRepairs);

    Assert.Single(r);
    Assert.Equal(4, r[0].CarIdx);
    Assert.Equal("fast_repair", r[0].DetectionSource);
}

[Fact]
public void Process_FastRepairNoChange_EmitsNothing()
{
    var d = new ReplayIncidentIndexDetector();
    var baseFastRepairs = Zeros64();
    baseFastRepairs[4] = 1;
    d.Reset(Zeros64(), 0, 0, baselineFastRepairs: baseFastRepairs);

    var fastRepairs = Zeros64();
    fastRepairs[4] = 1; // same as baseline — no increment

    var r = d.Process(15.0, Zeros64(), 0, 0, 200, carIdxFastRepairsUsed: fastRepairs);
    Assert.Empty(r);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test src/SimSteward.Plugin.Tests/ --filter "FastRepair" -v n`
Expected: FAIL

- [ ] **Step 3: Add constant and detection logic**

In `ReplayIncidentIndexDetection.cs`:
```csharp
public const string SourceFastRepair = "fast_repair";
```

In `ReplayIncidentIndexDetector.cs`, add state array:
```csharp
private readonly int[] _prevFastRepairs = new int[ReplayIncidentIndexBuild.CarSlotCount];
private readonly double[] _lastFastRepairEmitSec = new double[ReplayIncidentIndexBuild.CarSlotCount];
```

Update `Reset` signature to accept `int[] baselineFastRepairs = null` and copy to `_prevFastRepairs`.

In `Process`, add parameter `int[] carIdxFastRepairsUsed = null` and detection block after the track-surface block:

```csharp
if (carIdxFastRepairsUsed != null && carIdxFastRepairsUsed.Length > i)
{
    int prevFR = _prevFastRepairs[i];
    int currFR = carIdxFastRepairsUsed[i];
    if (currFR > prevFR
        && TryTakePrimarySlot(_lastFastRepairEmitSec, i, replaySessionTimeSec))
    {
        results.Add(new IncidentSample(
            i,
            sessionTimeMs,
            ReplayIncidentIndexDetection.SourceFastRepair,
            null,
            replayFrame,
            carIdxLap != null && i < carIdxLap.Length ? carIdxLap[i] : SessionLogging.LapUnknown,
            sessionNum,
            lapDistPct: carIdxLapDistPct != null && i < carIdxLapDistPct.Length ? (float?)carIdxLapDistPct[i] : null,
            carPosition: carIdxPosition != null && i < carIdxPosition.Length && carIdxPosition[i] > 0 ? (int?)carIdxPosition[i] : null));
    }
    _prevFastRepairs[i] = currFR;
}
```

- [ ] **Step 4: Run all tests**

Run: `dotnet test src/SimSteward.Plugin.Tests/ -v n`
Expected: ALL PASS

- [ ] **Step 5: Commit**

```bash
git add src/SimSteward.Plugin/ReplayIncidentIndexDetection.cs src/SimSteward.Plugin/ReplayIncidentIndexDetector.cs src/SimSteward.Plugin.Tests/ReplayIncidentIndexDetectionTests.cs
git commit -m "feat: add CarIdxFastRepairsUsed rising-edge detection (fast_repair source)"
```

---

### Task 4: Add Black Flag and DQ detection

**Files:**
- Modify: `src/SimSteward.Plugin/ReplayIncidentIndexDetection.cs` (add flag constants + source strings)
- Modify: `src/SimSteward.Plugin/ReplayIncidentIndexDetector.cs` (add detection blocks + debounce arrays)
- Test: `src/SimSteward.Plugin.Tests/ReplayIncidentIndexDetectionTests.cs`

**Interfaces:**
- Consumes: `IncidentSample` from Task 1
- Produces: `SourceBlackFlag`, `SourceDisqualify` constants; detections emitted from existing `CarIdxSessionFlags` read

- [ ] **Step 1: Write failing test**

```csharp
[Fact]
public void Process_BlackFlagRisingEdge_EmitsBlackFlagRow()
{
    var d = new ReplayIncidentIndexDetector();
    d.Reset(Zeros64(), 0, 0);

    var flags = Zeros64();
    flags[2] = 0x00010000; // Black flag bit
    var r = d.Process(20.0, flags, 0, 0, 300);

    Assert.Single(r);
    Assert.Equal(2, r[0].CarIdx);
    Assert.Equal("black_flag", r[0].DetectionSource);
}

[Fact]
public void Process_DisqualifyRisingEdge_EmitsDisqualifyRow()
{
    var d = new ReplayIncidentIndexDetector();
    d.Reset(Zeros64(), 0, 0);

    var flags = Zeros64();
    flags[6] = 0x00020000; // Disqualify bit
    var r = d.Process(25.0, flags, 0, 0, 400);

    Assert.Single(r);
    Assert.Equal(6, r[0].CarIdx);
    Assert.Equal("disqualify", r[0].DetectionSource);
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test src/SimSteward.Plugin.Tests/ --filter "BlackFlag|Disqualify" -v n`
Expected: FAIL

- [ ] **Step 3: Add constants and detection**

In `ReplayIncidentIndexDetection.cs`:
```csharp
public const int BlackSessionFlag = 0x00010000;
public const int DisqualifySessionFlag = 0x00020000;
public const string SourceBlackFlag = "black_flag";
public const string SourceDisqualify = "disqualify";
```

In `ReplayIncidentIndexDetector.cs`, add debounce arrays:
```csharp
private readonly double[] _lastBlackEmitSec = new double[ReplayIncidentIndexBuild.CarSlotCount];
private readonly double[] _lastDqEmitSec = new double[ReplayIncidentIndexBuild.CarSlotCount];
```

Initialize them to `-1` in `Reset`.

In `Process`, add after the furled detection block (before `_prevFlags[i] = curr;`):

```csharp
if (ReplayIncidentIndexDetection.IsRisingEdge(prev, curr, ReplayIncidentIndexDetection.BlackSessionFlag)
    && TryTakePrimarySlot(_lastBlackEmitSec, i, replaySessionTimeSec))
{
    results.Add(new IncidentSample(
        i, sessionTimeMs, ReplayIncidentIndexDetection.SourceBlackFlag, null, replayFrame,
        carIdxLap != null && i < carIdxLap.Length ? carIdxLap[i] : SessionLogging.LapUnknown,
        sessionNum,
        lapDistPct: carIdxLapDistPct != null && i < carIdxLapDistPct.Length ? (float?)carIdxLapDistPct[i] : null,
        carPosition: carIdxPosition != null && i < carIdxPosition.Length && carIdxPosition[i] > 0 ? (int?)carIdxPosition[i] : null));
}

if (ReplayIncidentIndexDetection.IsRisingEdge(prev, curr, ReplayIncidentIndexDetection.DisqualifySessionFlag)
    && TryTakePrimarySlot(_lastDqEmitSec, i, replaySessionTimeSec))
{
    results.Add(new IncidentSample(
        i, sessionTimeMs, ReplayIncidentIndexDetection.SourceDisqualify, null, replayFrame,
        carIdxLap != null && i < carIdxLap.Length ? carIdxLap[i] : SessionLogging.LapUnknown,
        sessionNum,
        lapDistPct: carIdxLapDistPct != null && i < carIdxLapDistPct.Length ? (float?)carIdxLapDistPct[i] : null,
        carPosition: carIdxPosition != null && i < carIdxPosition.Length && carIdxPosition[i] > 0 ? (int?)carIdxPosition[i] : null));
}
```

- [ ] **Step 4: Run all tests**

Run: `dotnet test src/SimSteward.Plugin.Tests/ -v n`
Expected: ALL PASS

- [ ] **Step 5: Commit**

```bash
git add src/SimSteward.Plugin/ReplayIncidentIndexDetection.cs src/SimSteward.Plugin/ReplayIncidentIndexDetector.cs src/SimSteward.Plugin.Tests/ReplayIncidentIndexDetectionTests.cs
git commit -m "feat: add black flag and disqualify detection from CarIdxSessionFlags"
```

---

### Task 5: Filter false-positive off-tracks using `CarIdxTrackSurfaceMaterial`

**Files:**
- Modify: `src/SimSteward.Plugin/ReplayIncidentIndexDetection.cs` (add material constants)
- Modify: `src/SimSteward.Plugin/ReplayIncidentIndexDetector.cs` (add material filter to off-track detection)
- Test: `src/SimSteward.Plugin.Tests/ReplayIncidentIndexDetectionTests.cs`

**Interfaces:**
- Consumes: existing off-track detection in `Process()`
- Produces: `int[] carIdxTrackSurfaceMaterial` optional parameter on `Process()`. Off-track detections are suppressed when material is rumble strip (11-14).

- [ ] **Step 1: Write failing tests**

```csharp
[Fact]
public void Process_OffTrackOntoRumbleStrip_Suppressed()
{
    var d = new ReplayIncidentIndexDetector();
    var baseSurface = new int[64];
    for (int i = 0; i < 64; i++) baseSurface[i] = ReplayIncidentIndexDetection.TrackSurfaceOnTrack;
    d.Reset(Zeros64(), 0, 0, baselineTrackSurface: baseSurface);

    var surface = new int[64];
    surface[2] = ReplayIncidentIndexDetection.TrackSurfaceOffTrack;
    var material = new int[64];
    material[2] = 11; // Rumble1Material

    var r = d.Process(10.0, Zeros64(), 0, 0, 100, trackSurface: surface, carIdxTrackSurfaceMaterial: material);
    Assert.Empty(r); // rumble strip = not a real off-track
}

[Fact]
public void Process_OffTrackOntoGrass_NotSuppressed()
{
    var d = new ReplayIncidentIndexDetector();
    var baseSurface = new int[64];
    for (int i = 0; i < 64; i++) baseSurface[i] = ReplayIncidentIndexDetection.TrackSurfaceOnTrack;
    d.Reset(Zeros64(), 0, 0, baselineTrackSurface: baseSurface);

    var surface = new int[64];
    surface[2] = ReplayIncidentIndexDetection.TrackSurfaceOffTrack;
    var material = new int[64];
    material[2] = 15; // Grass1Material

    var r = d.Process(10.0, Zeros64(), 0, 0, 100, trackSurface: surface, carIdxTrackSurfaceMaterial: material);
    Assert.Single(r);
    Assert.Equal(ReplayIncidentIndexDetection.SourceTrackSurface, r[0].DetectionSource);
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test src/SimSteward.Plugin.Tests/ --filter "OffTrackOnto" -v n`
Expected: FAIL — `Process` has no `carIdxTrackSurfaceMaterial` parameter

- [ ] **Step 3: Add material constants and filter**

In `ReplayIncidentIndexDetection.cs`:
```csharp
/// <summary>Rumble strip material range (11-14). Off-track transitions onto rumble strips are suppressed as false positives.</summary>
public const int Rumble1Material = 11;
public const int Rumble4Material = 14;

public static bool IsRumbleStrip(int material) => material >= Rumble1Material && material <= Rumble4Material;
```

In `ReplayIncidentIndexDetector.Process`, add `int[] carIdxTrackSurfaceMaterial = null` parameter. Then wrap the off-track emit with a material check:

```csharp
if (prevSurf == ReplayIncidentIndexDetection.TrackSurfaceOnTrack
    && currSurf == ReplayIncidentIndexDetection.TrackSurfaceOffTrack
    && TryTakePrimarySlot(_lastSurfaceEmitSec, i, replaySessionTimeSec))
{
    // Suppress false positives when car lands on rumble strip (kerb)
    bool isRumble = carIdxTrackSurfaceMaterial != null
        && i < carIdxTrackSurfaceMaterial.Length
        && ReplayIncidentIndexDetection.IsRumbleStrip(carIdxTrackSurfaceMaterial[i]);

    if (!isRumble)
    {
        results.Add(new IncidentSample( /* ... existing fields plus new context fields ... */ ));
    }
}
```

- [ ] **Step 4: Run all tests**

Run: `dotnet test src/SimSteward.Plugin.Tests/ -v n`
Expected: ALL PASS

- [ ] **Step 5: Commit**

```bash
git add src/SimSteward.Plugin/ReplayIncidentIndexDetection.cs src/SimSteward.Plugin/ReplayIncidentIndexDetector.cs src/SimSteward.Plugin.Tests/ReplayIncidentIndexDetectionTests.cs
git commit -m "feat: filter false-positive off-tracks on rumble strips via CarIdxTrackSurfaceMaterial"
```

---

### Task 6: Wire new fields into the fast-forward build loop

**Files:**
- Modify: `src/SimSteward.Plugin/SimStewardPlugin.ReplayIncidentIndexBuild.cs` (read new telemetry arrays, pass to `Process()`)

**Interfaces:**
- Consumes: Updated `Process()` signature from Tasks 2-5 (new optional params: `carIdxLapDistPct`, `carIdxPosition`, `carIdxFastRepairsUsed`, `carIdxTrackSurfaceMaterial`)
- Produces: Live telemetry flowing through to `IncidentSample` context fields and new detection channels

- [ ] **Step 1: Locate the fast-forward poll loop**

The native IRSDK poll callback that calls `_replayIndexDetector.Process(...)` is in `SimStewardPlugin.ReplayIncidentIndexBuild.cs`. Find the existing call and the surrounding telemetry reads.

- [ ] **Step 2: Add telemetry reads for new arrays**

In the same scope where `CarIdxSessionFlags`, `CarIdxTrackSurface`, and `CarIdxLap` are read into scratch arrays, add:

```csharp
// Read new arrays for enriched detection
for (int i = 0; i < ReplayIncidentIndexBuild.CarSlotCount; i++)
{
    _scratchCarIdxLapDistPct[i] = _irsdk.Data.GetFloat("CarIdxLapDistPct", i);
    _scratchCarIdxPosition[i]   = _irsdk.Data.GetInt("CarIdxPosition", i);
    _scratchCarIdxFastRepairs[i] = _irsdk.Data.GetInt("CarIdxFastRepairsUsed", i);
    _scratchCarIdxSurfaceMaterial[i] = _irsdk.Data.GetInt("CarIdxTrackSurfaceMaterial", i);
}
```

Declare the scratch arrays as class fields:
```csharp
private readonly float[] _scratchCarIdxLapDistPct = new float[ReplayIncidentIndexBuild.CarSlotCount];
private readonly int[] _scratchCarIdxPosition = new int[ReplayIncidentIndexBuild.CarSlotCount];
private readonly int[] _scratchCarIdxFastRepairs = new int[ReplayIncidentIndexBuild.CarSlotCount];
private readonly int[] _scratchCarIdxSurfaceMaterial = new int[ReplayIncidentIndexBuild.CarSlotCount];
```

- [ ] **Step 3: Pass new arrays to Process()**

Update the `_replayIndexDetector.Process(...)` call to include the new parameters:

```csharp
var detections = _replayIndexDetector.Process(
    replaySessionTimeSec,
    _scratchCarIdxFlags,
    playerIncidents,
    playerCarIdx,
    replayFrame,
    trackSurface: _scratchCarIdxTrackSurface,
    carIdxLap: _scratchCarIdxLap,
    sessionNum: currentSessionNum,
    carIdxLapDistPct: _scratchCarIdxLapDistPct,
    carIdxPosition: _scratchCarIdxPosition,
    carIdxFastRepairsUsed: _scratchCarIdxFastRepairs,
    carIdxTrackSurfaceMaterial: _scratchCarIdxSurfaceMaterial);
```

- [ ] **Step 4: Update baseline Reset() call**

Where `_replayIndexDetector.Reset(...)` is called, pass the fast-repairs baseline:

```csharp
_replayIndexDetector.Reset(baselineFlags, baselinePlayerIncidents, playerCarIdx,
    baselineTrackSurface: baselineTrackSurface,
    baselineFastRepairs: _scratchCarIdxFastRepairs);
```

- [ ] **Step 5: Build and run tests**

Run: `dotnet build src/SimSteward.Plugin/ && dotnet test src/SimSteward.Plugin.Tests/ -v n`
Expected: Build succeeds, ALL tests PASS

- [ ] **Step 6: Commit**

```bash
git add src/SimSteward.Plugin/SimStewardPlugin.ReplayIncidentIndexBuild.cs
git commit -m "feat: wire new detection signals and context fields into fast-forward build loop"
```

---

### Task 7: Fix documentation inaccuracies

**Files:**
- Modify: `docs/IRACING-DATA-AVAILABILITY.md` (fix `PlayerCarMyIncidentCount` availability)
- Modify: `docs/IRACING-CROSSWALK.md` (fix aspirational SimSteward Usage claims)

**Interfaces:**
- Consumes: nothing
- Produces: Corrected documentation

- [ ] **Step 1: Fix PlayerCarMyIncidentCount in IRACING-DATA-AVAILABILITY.md**

Move `PlayerCarMyIncidentCount` from Group 1 ("Live Race Only") to Group 2 ("Live Race + Replay") with a note:

```markdown
| `PlayerCarMyIncidentCount` | Running total for your own car. Available in replay (jumps 0→N at start — see crosswalk Appendix B). |
```

Remove it from Group 1 and add a note that Group 1 retains `PlayerCarDriverIncidentCount` (which IS live-only — it requires the driver-swap context that replays don't preserve).

- [ ] **Step 2: Fix aspirational claims in IRACING-CROSSWALK.md**

For the following fields, change SimSteward Usage from aspirational descriptions to accurate ones based on what was implemented in Tasks 1-6:

After Tasks 1-6 are complete, update the SimSteward Usage column for:
- `CarIdxLapDistPct`: "Incident index — track position captured at detection time"
- `CarIdxPosition`: "Incident index — race position captured at detection time"
- `CarIdxFastRepairsUsed`: "ReplayIncidentIndexDetector — rising-edge detection (fast_repair source)"
- `CarIdxTrackSurfaceMaterial`: "ReplayIncidentIndexDetector — rumble strip filter for off-track false positives"

For fields that are still NOT implemented (Speed, LatAccel, LongAccel, YawRate as player-only context), change to "not yet used" with a note: "Player-only; future severity classification."

- [ ] **Step 3: Commit**

```bash
git add docs/IRACING-DATA-AVAILABILITY.md docs/IRACING-CROSSWALK.md
git commit -m "docs: fix PlayerCarMyIncidentCount availability and aspirational crosswalk claims"
```

---

## Verification

After all tasks are complete:

1. **Unit tests:** `dotnet test src/SimSteward.Plugin.Tests/ -v n` — all pass
2. **Build:** `dotnet build src/SimSteward.Plugin/` — 0 errors, 0 new warnings
3. **Deploy + smoke test:** Run `deploy.ps1`, load an iRacing replay, trigger index build, verify JSON output includes new fields (`lapDistPct`, `carPosition`) and new detection sources (`fast_repair`, `black_flag`, `disqualify`)
4. **False-positive check:** Verify a replay at a track with aggressive kerbs (e.g., Monza, Spa) produces fewer spurious `track_surface` detections than before the rumble strip filter
