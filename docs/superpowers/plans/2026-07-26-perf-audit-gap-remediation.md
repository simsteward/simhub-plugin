# Performance Audit Gap Remediation — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the four concrete, low-risk gaps identified by the 2026-07-26 read-only performance audit — an unbounded incident-board rebroadcast, unconditional high-frequency probe logging, silently-swallowed per-car SDK read failures, and a per-tick string rebuild — without touching the correctness-critical detection/correlation logic itself.

**Architecture:** Each fix is a small, pure, unit-testable helper class in `src/SimSteward.Plugin/` (following the existing `LiveIncidentTotalsBackfill` pattern: stateless or minimally-stateful logic lives outside the `#if SIMHUB_SDK` partial-class files so it's testable without the SimHub SDK), wired into the existing partial-class call sites with a one- or two-line change. No detection, correlation, or scoring logic changes.

**Tech Stack:** .NET Framework 4.8, xUnit, Newtonsoft.Json (existing `JsonConvert.SerializeObject` usage unchanged).

## Global Constraints

- Target .NET Framework 4.8 (per CLAUDE.md).
- Every `catch` block with meaningful context must log — never silently swallow exceptions (per CLAUDE.md logging rules). This plan directly remediates three violations of this rule.
- Log levels: INFO = completed normally, WARN = unexpected but continued, DEBUG = high-frequency checks gated on `_logger.IsDebugMode` (per CLAUDE.md). Task 2 brings the YAML probe back into compliance with this.
- Deploy via `deploy.ps1`; must pass build (0 errors), `dotnet test`, and `tests/*.ps1`. Retry-once-then-stop on failure.
- Lints: 0 new errors.
- No new NuGet dependencies — all fixes use only `System`/`System.Collections.Generic`/`System.Text`, already referenced everywhere in this project.
- Do not change `IncidentSeverityCorrelator`, `ReplayIncidentIndexDetector`, `ReplayIncidentIndexDetection`, or `IncidentCauseMapping` — none of the four gaps require touching detection/correlation logic, and those files are shared by both the live and replay-build detector instances (see `docs/REVIEW-incident-points-implementation.md`).

---

## Background: what this plan does and does not cover

The 2026-07-26 read-only performance audit (three agents + direct verification) found several candidate issues. Two categories were explicitly **ruled out** after direct code inspection and are NOT in this plan:

- **`PluginLogger`'s 500ms flush timer** (`PluginLogger.cs:89`) already no-ops cheaply when idle — `Flush()` (`PluginLogger.cs:195-227`) returns immediately if `_writeBuffer.Count == 0` under the lock. It is not a real CPU/IO cost when nothing is happening. The one genuine always-on cost found is the unconditional 60-second host-resource sample (`SimStewardPlugin.cs:1915-1925`, `_resourceSampleIntervalSec = 60`, `SimStewardPlugin.cs:68`) that runs for the plugin's entire `Init()`/`End()`-scoped lifetime regardless of iRacing connection state. At 1 sample/log/push per minute this is minor; it is called out here for visibility but is **not** a task in this plan — fixing it would mean deciding whether host-resource telemetry should exist at all outside of sessions, which is a product decision, not a mechanical fix.
- **Cross-thread/cross-consumer per-car array read duplication** between the native-telemetry-thread incident detector (`SimStewardPlugin.LiveIncidentDetection.cs`), the main-thread leaderboard builder (`SimStewardPlugin.cs:338` `BuildLeaderboardRows()`), and the main-thread replay aggregator tick (`SimStewardPlugin.cs:2257` `ProcessLiveReplayAggregatorTick()`) is real, but merging any of these safely requires confirming the exact tick-cadence and ordering guarantees between call sites first — attempting it without that confirmation risks introducing staleness bugs into the incident-detection hot path. This needs its own investigation spike, not a blind merge, and is deliberately excluded here.
- **The replay fast-forward index build's read density** (`SimStewardPlugin.ReplayIncidentIndexBuild.cs:772` `ProcessFastForwardingLocked()`) is inherent to a user-triggered, one-shot operation. There is no throttle to add without extending build time, which the user did not ask for.

The four gaps below are the ones with a clear, safe, mechanical fix.

---

### Task 1: Cap and gate the incident-board broadcast payload

**Context:** `BroadcastLiveIncidentBoard()` (`SimStewardPlugin.LiveIncidentDetection.cs:488-499`) re-serializes and rebroadcasts the *entire* `_liveIncidentBoardEntries` list (`SimStewardPlugin.LiveIncidentDetection.cs:14`) to every connected dashboard client on every new incident or escalation (call site: `SimStewardPlugin.LiveIncidentDetection.cs:432-433`, gated on `boardChanged`). The list is never capped or pruned during a session. In a chaotic, incident-heavy session this is O(n²) work over the session and an unbounded per-message payload. It also serializes even when zero dashboard clients are connected (`_bridge.ClientCount` is not checked here, unlike the pattern already used in `ProcessLiveReplayAggregatorTick`, `SimStewardPlugin.cs:2319`). This is `docs/REVIEW-incident-points-implementation.md` IMPROVEMENT 8, confirmed still present and unfixed.

**Files:**
- Create: `src/SimSteward.Plugin/LiveIncidentBoardTrim.cs`
- Test: `src/SimSteward.Plugin.Tests/LiveIncidentBoardTrimTests.cs`
- Modify: `src/SimSteward.Plugin/SimStewardPlugin.LiveIncidentDetection.cs:488-499`

**Interfaces:**
- Produces: `LiveIncidentBoardTrim.DefaultMaxBroadcastEntries` (`const int`), `LiveIncidentBoardTrim.Trim(IReadOnlyList<LiveIncidentBoardEntry> entries, int maxEntries) : List<LiveIncidentBoardEntry>` — returns the most recent `maxEntries` entries in original order, or a copy of the full list if it's already at or under the cap.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Collections.Generic;
using SimSteward.Plugin;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class LiveIncidentBoardTrimTests
    {
        private static LiveIncidentBoardEntry Entry(string id) => new LiveIncidentBoardEntry { Id = id };

        [Fact]
        public void Trim_Null_ReturnsEmptyList()
        {
            var result = LiveIncidentBoardTrim.Trim(null, 3);
            Assert.Empty(result);
        }

        [Fact]
        public void Trim_FewerThanMax_ReturnsAllInOrder()
        {
            var entries = new List<LiveIncidentBoardEntry> { Entry("a"), Entry("b") };
            var result = LiveIncidentBoardTrim.Trim(entries, 5);
            Assert.Equal(new[] { "a", "b" }, new[] { result[0].Id, result[1].Id });
        }

        [Fact]
        public void Trim_ExactlyMax_ReturnsAll()
        {
            var entries = new List<LiveIncidentBoardEntry> { Entry("a"), Entry("b"), Entry("c") };
            var result = LiveIncidentBoardTrim.Trim(entries, 3);
            Assert.Equal(3, result.Count);
        }

        [Fact]
        public void Trim_MoreThanMax_ReturnsMostRecentOnlyInOrder()
        {
            var entries = new List<LiveIncidentBoardEntry> { Entry("a"), Entry("b"), Entry("c"), Entry("d") };
            var result = LiveIncidentBoardTrim.Trim(entries, 2);
            Assert.Equal(new[] { "c", "d" }, new[] { result[0].Id, result[1].Id });
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/SimSteward.Plugin.Tests --filter LiveIncidentBoardTrimTests`
Expected: FAIL (compile error) — `LiveIncidentBoardTrim` does not exist yet.

- [ ] **Step 3: Write the implementation**

```csharp
using System.Collections.Generic;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Bounds the Live tab's incident-board broadcast to the most recent N entries. The full,
    /// unbounded list is still held in memory in-process and remains available to the post-session
    /// Replay Index Build — this only limits what gets re-serialized and pushed to every connected
    /// dashboard client on each new incident/escalation.
    /// See docs/REVIEW-incident-points-implementation.md IMPROVEMENT 8.
    /// </summary>
    public static class LiveIncidentBoardTrim
    {
        public const int DefaultMaxBroadcastEntries = 150;

        public static List<LiveIncidentBoardEntry> Trim(IReadOnlyList<LiveIncidentBoardEntry> entries, int maxEntries)
        {
            if (entries == null || entries.Count == 0)
                return new List<LiveIncidentBoardEntry>();
            if (entries.Count <= maxEntries)
                return new List<LiveIncidentBoardEntry>(entries);

            int start = entries.Count - maxEntries;
            var trimmed = new List<LiveIncidentBoardEntry>(maxEntries);
            for (int i = start; i < entries.Count; i++)
                trimmed.Add(entries[i]);
            return trimmed;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/SimSteward.Plugin.Tests --filter LiveIncidentBoardTrimTests`
Expected: PASS (4/4)

- [ ] **Step 5: Wire into `BroadcastLiveIncidentBoard()`**

In `src/SimSteward.Plugin/SimStewardPlugin.LiveIncidentDetection.cs`, replace lines 488-499:

```csharp
        private void BroadcastLiveIncidentBoard()
        {
            if (_bridge == null || _bridge.ClientCount <= 0) return;
            try
            {
                var trimmed = LiveIncidentBoardTrim.Trim(_liveIncidentBoardEntries, LiveIncidentBoardTrim.DefaultMaxBroadcastEntries);
                var msg = new { type = "incidents", entries = trimmed };
                _bridge.Broadcast(JsonConvert.SerializeObject(msg), "incidents");
            }
            catch (Exception ex)
            {
                _logger?.Warn("live_incident_board broadcast: " + ex.Message);
            }
```
(closing brace on the existing next line is unchanged)

- [ ] **Step 6: Build and run the full test suite**

Run: `dotnet build && dotnet test`
Expected: 0 errors, all tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/SimSteward.Plugin/LiveIncidentBoardTrim.cs src/SimSteward.Plugin.Tests/LiveIncidentBoardTrimTests.cs src/SimSteward.Plugin/SimStewardPlugin.LiveIncidentDetection.cs
git commit -m "fix(perf): cap and gate the live incident-board broadcast payload"
```

---

### Task 2: Stop unconditional INFO logging on the 5-second YAML verification probe

**Context:** `PollLiveYamlIncidentsForVerificationLocked` (`SimStewardPlugin.LiveIncidentDetection.cs:200-249`) polls every 5 seconds (`LiveYamlProbePollInterval`, `SimStewardPlugin.LiveIncidentDetection.cs:49`) for the entire duration of every live session and, regardless of whether anything changed, always logs at INFO (lines 246-248) — roughly 720 log lines/hour/session carrying zero new information in the steady state. This is `docs/REVIEW-incident-points-implementation.md` IMPROVEMENT 7, confirmed still present. Per CLAUDE.md's own logging rules, "high-frequency checks" belong at DEBUG, gated on `_logger.IsDebugMode` — this brings the probe back into compliance.

**Files:**
- Create: `src/SimSteward.Plugin/LiveYamlProbeLogLevel.cs`
- Test: `src/SimSteward.Plugin.Tests/LiveYamlProbeLogLevelTests.cs`
- Modify: `src/SimSteward.Plugin/SimStewardPlugin.LiveIncidentDetection.cs:245-248`

**Interfaces:**
- Produces: `LiveYamlProbeLogLevel.IsInfoWorthy(bool parseOk, int deltaCount) : bool` — true only when the poll is actionable (parse failed, or at least one delta since last poll).

- [ ] **Step 1: Write the failing tests**

```csharp
using SimSteward.Plugin;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class LiveYamlProbeLogLevelTests
    {
        [Fact]
        public void IsInfoWorthy_ParseFailed_True()
        {
            Assert.True(LiveYamlProbeLogLevel.IsInfoWorthy(parseOk: false, deltaCount: 0));
        }

        [Fact]
        public void IsInfoWorthy_ParseOkWithDeltas_True()
        {
            Assert.True(LiveYamlProbeLogLevel.IsInfoWorthy(parseOk: true, deltaCount: 3));
        }

        [Fact]
        public void IsInfoWorthy_ParseOkNoDeltas_False()
        {
            Assert.False(LiveYamlProbeLogLevel.IsInfoWorthy(parseOk: true, deltaCount: 0));
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/SimSteward.Plugin.Tests --filter LiveYamlProbeLogLevelTests`
Expected: FAIL (compile error) — `LiveYamlProbeLogLevel` does not exist yet.

- [ ] **Step 3: Write the implementation**

```csharp
namespace SimSteward.Plugin
{
    /// <summary>
    /// Whether a live YAML incident-probe poll (fires every 5s for the life of a live session) is
    /// worth an INFO-level log line. A steady-state "nothing changed" poll is DEBUG-only — logging
    /// every poll at INFO unconditionally produced ~720 lines/hour/session with no new information.
    /// See docs/REVIEW-incident-points-implementation.md IMPROVEMENT 7.
    /// </summary>
    public static class LiveYamlProbeLogLevel
    {
        public static bool IsInfoWorthy(bool parseOk, int deltaCount)
        {
            return !parseOk || deltaCount > 0;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/SimSteward.Plugin.Tests --filter LiveYamlProbeLogLevelTests`
Expected: PASS (3/3)

- [ ] **Step 5: Wire into `PollLiveYamlIncidentsForVerificationLocked`**

In `src/SimSteward.Plugin/SimStewardPlugin.LiveIncidentDetection.cs`, replace lines 245-248:

```csharp
            MergeSessionAndRoutingFields(f);
            string probeMessage = "Live YAML incident probe: " +
                (parseOk ? currByCar.Count + " cars, " + deltaCount + " deltas" : "parse failed (" + parseErr + ")");
            if (LiveYamlProbeLogLevel.IsInfoWorthy(parseOk, deltaCount))
                _logger?.Structured("INFO", "simhub-plugin", EventLiveYamlProbe, probeMessage, f, "lifecycle", null);
            else
                _logger?.Debug(probeMessage, "simhub-plugin", EventLiveYamlProbe, f);
```

- [ ] **Step 6: Build and run the full test suite**

Run: `dotnet build && dotnet test`
Expected: 0 errors, all tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/SimSteward.Plugin/LiveYamlProbeLogLevel.cs src/SimSteward.Plugin.Tests/LiveYamlProbeLogLevelTests.cs src/SimSteward.Plugin/SimStewardPlugin.LiveIncidentDetection.cs
git commit -m "fix(logging): drop steady-state YAML probe polls to DEBUG, keep INFO for actionable ones"
```

---

### Task 3: Stop silently swallowing per-car SDK read failures

**Context:** `SafeGetIntPerCar`, `SafeGetFloatPerCar`, and `SafeGetBoolPerCar` (`SimStewardPlugin.ReplayIncidentIndexBuild.cs:1441-1466`) each wrap a per-car SDK read in `try { ... } catch { buffer[i] = <default>; }` — a bare catch with no log line at all, run up to 64 times per call, multiple calls per tick during replay/live detection. This directly violates CLAUDE.md's "never silently swallow exceptions" rule and means a genuinely broken field/index (or, in Release builds where IRSDKSharper's own bounds-checking is compiled out, a bad index reading adjacent memory) would be invisible today. The fix adds a throttled WARN so at least one log line reaches Loki per sustained failure window, without flooding the logger if a field is broken for an entire session.

**Files:**
- Create: `src/SimSteward.Plugin/PerCarReadFailureThrottle.cs`
- Test: `src/SimSteward.Plugin.Tests/PerCarReadFailureThrottleTests.cs`
- Modify: `src/SimSteward.Plugin/SimStewardPlugin.ReplayIncidentIndexBuild.cs:1441-1466`

**Interfaces:**
- Produces: `PerCarReadFailureThrottle(TimeSpan minInterval)` constructor, `PerCarReadFailureThrottle.ShouldLog(DateTime nowUtc) : bool` — true on first call, then true again only after `minInterval` has elapsed since the last true result.

- [ ] **Step 1: Write the failing tests**

```csharp
using System;
using SimSteward.Plugin;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class PerCarReadFailureThrottleTests
    {
        [Fact]
        public void ShouldLog_FirstCall_True()
        {
            var t = new PerCarReadFailureThrottle(TimeSpan.FromSeconds(10));
            Assert.True(t.ShouldLog(new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc)));
        }

        [Fact]
        public void ShouldLog_WithinInterval_False()
        {
            var t = new PerCarReadFailureThrottle(TimeSpan.FromSeconds(10));
            var t0 = new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc);
            t.ShouldLog(t0);
            Assert.False(t.ShouldLog(t0.AddSeconds(5)));
        }

        [Fact]
        public void ShouldLog_AfterInterval_True()
        {
            var t = new PerCarReadFailureThrottle(TimeSpan.FromSeconds(10));
            var t0 = new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc);
            t.ShouldLog(t0);
            Assert.True(t.ShouldLog(t0.AddSeconds(11)));
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/SimSteward.Plugin.Tests --filter PerCarReadFailureThrottleTests`
Expected: FAIL (compile error) — `PerCarReadFailureThrottle` does not exist yet.

- [ ] **Step 3: Write the implementation**

```csharp
using System;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Gates repeated per-car SDK-read failure logging so a persistently-bad field/index doesn't
    /// flood the logger at tick rate, while still guaranteeing at least one WARN reaches Loki instead
    /// of being silently swallowed.
    /// </summary>
    public sealed class PerCarReadFailureThrottle
    {
        private readonly TimeSpan _minInterval;
        private DateTime _lastLoggedUtc = DateTime.MinValue;

        public PerCarReadFailureThrottle(TimeSpan minInterval)
        {
            _minInterval = minInterval;
        }

        public bool ShouldLog(DateTime nowUtc)
        {
            if (nowUtc - _lastLoggedUtc < _minInterval)
                return false;
            _lastLoggedUtc = nowUtc;
            return true;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/SimSteward.Plugin.Tests --filter PerCarReadFailureThrottleTests`
Expected: PASS (3/3)

- [ ] **Step 5: Wire into the three `SafeGet*PerCar` helpers**

In `src/SimSteward.Plugin/SimStewardPlugin.ReplayIncidentIndexBuild.cs`, add three fields near the top of the partial class (alongside the other scratch/state fields already declared there) and replace lines 1440-1466:

```csharp
        private readonly PerCarReadFailureThrottle _safeGetIntPerCarFailureThrottle = new PerCarReadFailureThrottle(TimeSpan.FromSeconds(30));
        private readonly PerCarReadFailureThrottle _safeGetFloatPerCarFailureThrottle = new PerCarReadFailureThrottle(TimeSpan.FromSeconds(30));
        private readonly PerCarReadFailureThrottle _safeGetBoolPerCarFailureThrottle = new PerCarReadFailureThrottle(TimeSpan.FromSeconds(30));

        /// <summary>Read one int per car slot into <paramref name="buffer"/>, defaulting to 0 on any error.</summary>
        private void SafeGetIntPerCar(string field, int[] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                try { buffer[i] = _irsdk.Data.GetInt(field, i); }
                catch (Exception ex)
                {
                    buffer[i] = 0;
                    if (_safeGetIntPerCarFailureThrottle.ShouldLog(DateTime.UtcNow))
                        _logger?.Warn($"SafeGetIntPerCar field='{field}' idx={i}: {ex.Message}");
                }
            }
        }

        private void SafeGetFloatPerCar(string field, float[] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                try { buffer[i] = _irsdk.Data.GetFloat(field, i); }
                catch (Exception ex)
                {
                    buffer[i] = 0f;
                    if (_safeGetFloatPerCarFailureThrottle.ShouldLog(DateTime.UtcNow))
                        _logger?.Warn($"SafeGetFloatPerCar field='{field}' idx={i}: {ex.Message}");
                }
            }
        }

        private void SafeGetBoolPerCar(string field, bool[] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                try { buffer[i] = _irsdk.Data.GetBool(field, i); }
                catch (Exception ex)
                {
                    buffer[i] = false;
                    if (_safeGetBoolPerCarFailureThrottle.ShouldLog(DateTime.UtcNow))
                        _logger?.Warn($"SafeGetBoolPerCar field='{field}' idx={i}: {ex.Message}");
                }
            }
        }
```

- [ ] **Step 6: Build and run the full test suite**

Run: `dotnet build && dotnet test`
Expected: 0 errors, all tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/SimSteward.Plugin/PerCarReadFailureThrottle.cs src/SimSteward.Plugin.Tests/PerCarReadFailureThrottleTests.cs src/SimSteward.Plugin/SimStewardPlugin.ReplayIncidentIndexBuild.cs
git commit -m "fix(observability): stop silently swallowing per-car SDK read failures"
```

---

### Task 4: Cache the session-sequence string instead of rebuilding it every tick

**Context:** `SimStewardPlugin.cs:1928` calls `BuildSessionSeq(trackName)` (defined `SimStewardPlugin.cs:209-216`: sanitize track name char-by-char via a `StringBuilder`, then format with `DateTime.UtcNow`) unconditionally on every `DataUpdate` tick while connected (the surrounding block starting `SimStewardPlugin.cs:1887` has no change-detection gate before this call). Since `_currentSessionSeq` is constant for the whole session (same track, same calendar day), this is a wasted string allocation up to ~60x/second. `BuildSessionSeq` has exactly one call site, confirmed by repo-wide grep, so it can be replaced outright rather than kept alongside the new cache.

**Files:**
- Create: `src/SimSteward.Plugin/SessionSeqCache.cs`
- Test: `src/SimSteward.Plugin.Tests/SessionSeqCacheTests.cs`
- Modify: `src/SimSteward.Plugin/SimStewardPlugin.cs:209-216` (delete `BuildSessionSeq`), `SimStewardPlugin.cs:1928` (call the cache instead)

**Interfaces:**
- Produces: `SessionSeqCache.Resolve(string trackName, DateTime utcNow) : string` — same sanitize-and-date-stamp format `BuildSessionSeq` produced, but only recomputes when `trackName` or the UTC calendar day changes since the last call.

- [ ] **Step 1: Write the failing tests**

```csharp
using System;
using SimSteward.Plugin;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class SessionSeqCacheTests
    {
        [Fact]
        public void Resolve_EmptyTrackName_ReturnsEmpty()
        {
            var cache = new SessionSeqCache();
            Assert.Equal("", cache.Resolve("", new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc)));
        }

        [Fact]
        public void Resolve_FirstCall_BuildsSanitizedSeq()
        {
            var cache = new SessionSeqCache();
            var result = cache.Resolve("Road America!", new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal("Road_America__20260726", result);
        }

        [Fact]
        public void Resolve_SameTrackSameDay_ReturnsCachedInstance()
        {
            var cache = new SessionSeqCache();
            var utc = new DateTime(2026, 7, 26, 10, 0, 0, DateTimeKind.Utc);
            var first = cache.Resolve("Road America", utc);
            var second = cache.Resolve("Road America", utc.AddSeconds(1));
            Assert.Same(first, second);
        }

        [Fact]
        public void Resolve_TrackChanges_Rebuilds()
        {
            var cache = new SessionSeqCache();
            var utc = new DateTime(2026, 7, 26, 10, 0, 0, DateTimeKind.Utc);
            cache.Resolve("Road America", utc);
            var result = cache.Resolve("Watkins Glen", utc);
            Assert.Equal("Watkins_Glen_20260726", result);
        }

        [Fact]
        public void Resolve_DayChanges_Rebuilds()
        {
            var cache = new SessionSeqCache();
            var day1 = new DateTime(2026, 7, 26, 23, 59, 0, DateTimeKind.Utc);
            var day2 = new DateTime(2026, 7, 27, 0, 1, 0, DateTimeKind.Utc);
            var first = cache.Resolve("Road America", day1);
            var second = cache.Resolve("Road America", day2);
            Assert.NotEqual(first, second);
        }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test src/SimSteward.Plugin.Tests --filter SessionSeqCacheTests`
Expected: FAIL (compile error) — `SessionSeqCache` does not exist yet.

- [ ] **Step 3: Write the implementation**

```csharp
using System;
using System.Text;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Caches the session-sequence string (sanitized track name + UTC date) so it is rebuilt only
    /// when the track or calendar day actually changes, instead of on every ~60Hz DataUpdate tick.
    /// </summary>
    public sealed class SessionSeqCache
    {
        private string _lastTrackName;
        private string _lastDateStamp;
        private string _cached = "";

        public string Resolve(string trackName, DateTime utcNow)
        {
            if (string.IsNullOrEmpty(trackName))
            {
                _lastTrackName = null;
                _lastDateStamp = null;
                _cached = "";
                return _cached;
            }

            string dateStamp = utcNow.ToString("yyyyMMdd");
            if (trackName == _lastTrackName && dateStamp == _lastDateStamp)
                return _cached;

            _lastTrackName = trackName;
            _lastDateStamp = dateStamp;
            _cached = Build(trackName, dateStamp);
            return _cached;
        }

        private static string Build(string trackName, string dateStamp)
        {
            var safe = new StringBuilder();
            foreach (var c in trackName)
                safe.Append(char.IsLetterOrDigit(c) ? c : '_');
            return $"{safe}_{dateStamp}";
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test src/SimSteward.Plugin.Tests --filter SessionSeqCacheTests`
Expected: PASS (5/5)

- [ ] **Step 5: Wire into `SimStewardPlugin.cs`, delete `BuildSessionSeq`**

Delete lines 209-216 (`private static string BuildSessionSeq(string trackName) { ... }`).

Add a field near the other per-plugin-instance state (e.g. next to `_currentSessionSeq`'s declaration):

```csharp
        private readonly SessionSeqCache _sessionSeqCache = new SessionSeqCache();
```

Replace line 1928:

```csharp
                _currentSessionSeq = _sessionSeqCache.Resolve(trackName, DateTime.UtcNow);
```

- [ ] **Step 6: Build and run the full test suite**

Run: `dotnet build && dotnet test`
Expected: 0 errors, all tests pass. Confirm no other reference to `BuildSessionSeq` remains (`grep -rn BuildSessionSeq src/`).

- [ ] **Step 7: Commit**

```bash
git add src/SimSteward.Plugin/SessionSeqCache.cs src/SimSteward.Plugin.Tests/SessionSeqCacheTests.cs src/SimSteward.Plugin/SimStewardPlugin.cs
git commit -m "perf: cache session-seq string instead of rebuilding it every DataUpdate tick"
```

---

## Self-Review

**Spec coverage:** All four gaps carried forward from the performance audit that had a clear, safe, mechanical fix are covered — board cap/gate (Task 1), probe log volume (Task 2), silent exception swallowing (Task 3), per-tick string rebuild (Task 4). The two ruled-out items (PluginLogger timer, cross-thread read duplication) and the one deliberately-excluded item (replay FF sweep density) are documented in the Background section with the reasoning for exclusion, per the "no silent caps" principle — nothing is dropped without saying so.

**Placeholder scan:** No TBD/TODO markers; every step has complete, compilable code; every test has real assertions against real expected values (including a hand-traced sanitized string in Task 4).

**Type consistency:** `LiveIncidentBoardTrim.Trim` takes `IReadOnlyList<LiveIncidentBoardEntry>` and `List<LiveIncidentBoardEntry>` (from `_liveIncidentBoardEntries`) implements that interface, so the wiring in Task 1 Step 5 compiles as-is. `SessionSeqCache.Resolve` and `PerCarReadFailureThrottle.ShouldLog` signatures are used identically between their test files and wiring steps. `LiveYamlProbeLogLevel.IsInfoWorthy(bool, int)` matches the `parseOk`/`deltaCount` locals already in scope at the Task 2 wiring site.

## Out of scope (see Background)

- `PluginLogger` flush-timer idle cost (verified negligible — no fix needed).
- Host-resource-sample-and-log-and-Loki-push running for the plugin's full SimHub-open lifetime (minor, 1x/minute — a product decision on whether it should be session-scoped, not a mechanical fix).
- Cross-thread/cross-consumer per-car array read deduplication (needs a tick-cadence investigation spike first).
- Replay fast-forward sweep read density (inherent to a user-triggered one-shot operation).

## Execution outcome (2026-07-26, subagent-driven-development)

All four tasks landed via `docs/superpowers/plans/2026-07-26-perf-audit-gap-remediation.md`'s subagent-driven-development run, plus two rounds of review-driven fixes beyond what's written above. Commit range: `02bc01d..80b9576` (worktree `perf-audit-gap-remediation`, branched from `feat/incident-scoring-accuracy`).

**Task 3 deviation from the code block above:** the per-task review found the three shared `PerCarReadFailureThrottle` instances gated per CLR type, not per SDK field name — since `SafeGetIntPerCar` alone serves 8 distinct field names sharing one throttle, a persistently-broken field could starve a second, independently-broken field's WARN indefinitely. Fixed (human-approved, since it revises this plan's own design) by changing `PerCarReadFailureThrottle.ShouldLog` from `ShouldLog(DateTime nowUtc)` to `ShouldLog(string key, DateTime nowUtc)`, backed by `Dictionary<string, DateTime>` instead of a single `DateTime` field, with `field` passed as the key at all three call sites. The Task 3 section's code blocks above reflect the original (superseded) single-argument design — read `src/SimSteward.Plugin/PerCarReadFailureThrottle.cs` for the shipped version.

**Findings from the final whole-branch review, also fixed (commits `1cfe395`, `9cfe2eb`, `80b9576`):**
- `PerCarReadFailureThrottle`'s dictionary was mutated from two threads (native SDK telemetry thread and main `DataUpdate` thread) with no lock — a real corruption/hang risk on the exact failure path Task 3 exists to observe. Fixed with a `lock` around the check-then-update in `ShouldLog`.
- `GetIncidentsForNewClient` (`SimStewardPlugin.cs`) — the on-connect incident-board broadcast path — was still sending the full unbounded list; only `BroadcastLiveIncidentBoard` (Task 1) had been fixed. Now uses the same `LiveIncidentBoardTrim.Trim(...)` call.
- The YAML probe's DEBUG-level log branch (Task 2) dropped its `domain` field since `PluginLogger.Debug` has no domain parameter; switched to an explicit `IsDebugMode` gate + direct `Structured(...)` call carrying `"lifecycle"`, matching the INFO branch.

**Parked, non-blocking:** the dashboard's "Max 200 steps" tooltip and the walk-all-incidents buttons (`src/SimSteward.Dashboard/index.html:626,2317,2323`) are now stale given the 150-entry live cap — a presentation-only follow-up outside this plan's backend-perf scope.

Final test count: 310 passed, 0 failed, 1 skipped (pre-existing, unrelated, network-dependent Loki integration test).
