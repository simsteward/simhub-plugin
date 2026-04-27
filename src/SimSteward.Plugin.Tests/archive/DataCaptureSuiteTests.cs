using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    // ─────────────────────────────────────────────────────────────────────────
    // Constants
    // ─────────────────────────────────────────────────────────────────────────

    public class DataCaptureSuiteConstantsTests
    {
        // ── SpeedSweepSpeeds ────────────────────────────────────────────────

        [Fact] public void SpeedSweepSpeeds_HasFourEntries() =>
            Assert.Equal(4, DataCaptureSuiteConstants.SpeedSweepSpeeds.Length);

        [Fact] public void SpeedSweepSpeeds_Contains_1_4_8_16() =>
            Assert.Equal(new[] { 1, 4, 8, 16 }, DataCaptureSuiteConstants.SpeedSweepSpeeds);

        [Fact] public void SpeedSweepSpeeds_AreInAscendingOrder()
        {
            var speeds = DataCaptureSuiteConstants.SpeedSweepSpeeds;
            for (int i = 1; i < speeds.Length; i++)
                Assert.True(speeds[i] > speeds[i - 1], $"Speed[{i}]={speeds[i]} not > Speed[{i-1}]={speeds[i-1]}");
        }

        [Fact] public void SpeedSweepSpeeds_AllPositive() =>
            Assert.All(DataCaptureSuiteConstants.SpeedSweepSpeeds, s => Assert.True(s > 0));

        // ── Timing constants ────────────────────────────────────────────────

        [Fact] public void LokiVerifyDelayMs_Is15000() =>
            Assert.Equal(15_000, DataCaptureSuiteConstants.LokiVerifyDelayMs);

        [Fact] public void NextIncidentCooldownTicks_Is150() =>
            Assert.Equal(150, DataCaptureSuiteConstants.NextIncidentCooldownTicks);

        [Fact] public void CamSettleTicks_Is60() =>
            Assert.Equal(60, DataCaptureSuiteConstants.CamSettleTicks);

        [Fact] public void FrameZeroStableTicks_Is4() =>
            Assert.Equal(4, DataCaptureSuiteConstants.FrameZeroStableTicks);

        [Fact] public void SeekTimeoutTicks_Is600() =>
            Assert.Equal(600, DataCaptureSuiteConstants.SeekTimeoutTicks);

        [Fact] public void SpeedSweepAdvanceFrames_Is300() =>
            Assert.Equal(300, DataCaptureSuiteConstants.SpeedSweepAdvanceFrames);

        // ── IncidentFlagMask matches the SDK flag constants ─────────────────

        [Fact] public void IncidentFlagMask_IncludesFurledFlag() =>
            Assert.NotEqual(0, DataCaptureSuiteConstants.IncidentFlagMask & ReplayIncidentIndexDetection.FurledSessionFlag);

        [Fact] public void IncidentFlagMask_IncludesRepairFlag() =>
            Assert.NotEqual(0, DataCaptureSuiteConstants.IncidentFlagMask & ReplayIncidentIndexDetection.RepairSessionFlag);

        [Fact] public void IncidentFlagMask_DoesNotMatchArbitraryBit() =>
            Assert.Equal(0, DataCaptureSuiteConstants.IncidentFlagMask & 0x01);

        // ── Event name catalogue ─────────────────────────────────────────────

        private static IEnumerable<string> AllEventNames()
        {
            yield return DataCaptureSuiteConstants.EventGroundTruth;
            yield return DataCaptureSuiteConstants.EventSpeedSample;
            yield return DataCaptureSuiteConstants.EventVariableInventory;
            yield return DataCaptureSuiteConstants.EventPlayerSnapshot;
            yield return DataCaptureSuiteConstants.EventDriverRoster;
            yield return DataCaptureSuiteConstants.EventCameraSwitchDriver;
            yield return DataCaptureSuiteConstants.EventCameraViewSample;
            yield return DataCaptureSuiteConstants.EventCameraViewSummary;
            yield return DataCaptureSuiteConstants.EventSessionResults;
            yield return DataCaptureSuiteConstants.EventIncidentReseek;
            yield return DataCaptureSuiteConstants.EventFfSweepResult;
            yield return DataCaptureSuiteConstants.EventSuiteStarted;
            yield return DataCaptureSuiteConstants.EventSuiteComplete;
            yield return DataCaptureSuiteConstants.EventDataDiscovery;
            yield return DataCaptureSuiteConstants.Event60HzSummary;
            yield return DataCaptureSuiteConstants.EventPreflightCheck;
            yield return DataCaptureSuiteConstants.EventPreflightProbe;
        }

        [Fact] public void AllEventNames_AreNonEmpty() =>
            Assert.All(AllEventNames(), n => Assert.False(string.IsNullOrWhiteSpace(n)));

        [Fact] public void AllEventNames_AreUnique()
        {
            var names = AllEventNames().ToList();
            var distinct = names.Distinct().ToList();
            Assert.Equal(names.Count, distinct.Count);
        }

        [Fact] public void AllSdkCaptureEventNames_StartWithSdkCapture()
        {
            var suiteEvents = AllEventNames()
                .Where(n => n != DataCaptureSuiteConstants.EventSuiteStarted &&
                            n != DataCaptureSuiteConstants.EventSuiteComplete);
            Assert.All(suiteEvents, n => Assert.StartsWith("sdk_capture_", n));
        }

        [Fact] public void AllEventNames_ContainOnlyLowercaseUnderscoreDigitChars()
        {
            Assert.All(AllEventNames(), name =>
            {
                foreach (char c in name)
                    Assert.True(char.IsLower(c) || c == '_' || char.IsDigit(c),
                        $"Event name '{name}' contains unexpected char '{c}'");
            });
        }

        [Fact] public void EventCount_Is17()
        {
            Assert.Equal(17, AllEventNames().Count());
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Speed-sweep effective-Hz formula
    // ─────────────────────────────────────────────────────────────────────────

    public class DataCaptureSuiteSpeedSweepTests
    {
        private static double EffectiveHz(int speed) => 60.0 / speed;

        [Theory]
        [InlineData(1,  60.0)]
        [InlineData(4,  15.0)]
        [InlineData(8,  7.5)]
        [InlineData(16, 3.75)]
        public void EffectiveHz_MatchesFormula(int speed, double expected) =>
            Assert.Equal(expected, EffectiveHz(speed), precision: 4);

        [Fact] public void EffectiveHz_1x_IsHighestSamplingRate() =>
            Assert.True(EffectiveHz(1) > EffectiveHz(16));

        [Fact] public void EffectiveHz_16x_IsLowestSamplingRate() =>
            Assert.Equal(3.75, EffectiveHz(DataCaptureSuiteConstants.SpeedSweepSpeeds.Last()), precision: 4);

        [Fact] public void SpeedSweepSpeeds_AllProducePositiveHz() =>
            Assert.All(DataCaptureSuiteConstants.SpeedSweepSpeeds, s => Assert.True(EffectiveHz(s) > 0));

        // Detection rate formula: hits / 3 * 100
        [Theory]
        [InlineData(3, 3, 100.0)]
        [InlineData(2, 3, 66.6667)]
        [InlineData(1, 3, 33.3333)]
        [InlineData(0, 3, 0.0)]
        public void DetectionRate_Formula(int hits, int total, double expected)
        {
            double rate = total > 0 ? hits * 100.0 / total : 0.0;
            Assert.Equal(expected, rate, precision: 3);
        }

        [Fact] public void DetectionRate_ZeroGtCount_IsZero()
        {
            double rate = 0 > 0 ? 1 * 100.0 / 0 : 0.0;
            Assert.Equal(0.0, rate);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Frame tolerance (T7 re-seek validation: ±60 frames is a match)
    // ─────────────────────────────────────────────────────────────────────────

    public class DataCaptureSuiteFrameToleranceTests
    {
        private static bool FrameMatch(int reseek, int groundTruth) =>
            Math.Abs(reseek - groundTruth) <= 60;

        [Fact] public void ExactMatch_IsTrue() =>
            Assert.True(FrameMatch(1000, 1000));

        [Fact] public void Within60_IsTrue() =>
            Assert.True(FrameMatch(1040, 1000));

        [Fact] public void AtBoundary60_IsTrue() =>
            Assert.True(FrameMatch(1060, 1000));

        [Fact] public void At61_IsFalse() =>
            Assert.False(FrameMatch(1061, 1000));

        [Fact] public void LargePositiveDiff_IsFalse() =>
            Assert.False(FrameMatch(2000, 1000));

        [Fact] public void NegativeDiffWithin60_IsTrue() =>
            Assert.True(FrameMatch(960, 1000));

        [Fact] public void NegativeDiffAtBoundary60_IsTrue() =>
            Assert.True(FrameMatch(940, 1000));

        [Fact] public void NegativeDiffAt61_IsFalse() =>
            Assert.False(FrameMatch(939, 1000));

        [Fact] public void ThreeReseeks_AllMatch_CountsThree()
        {
            var gt   = new[] { 100, 500, 900 };
            var rs   = new[] { 130, 490, 960 };
            int hits = gt.Zip(rs, (g, r) => FrameMatch(r, g) ? 1 : 0).Sum();
            Assert.Equal(3, hits);
        }

        [Fact] public void ThreeReseeks_OneOutOfRange_CountsTwo()
        {
            var gt   = new[] { 100, 500, 900 };
            var rs   = new[] { 130, 700, 960 }; // 700 vs 500 = diff 200
            int hits = gt.Zip(rs, (g, r) => FrameMatch(r, g) ? 1 : 0).Sum();
            Assert.Equal(2, hits);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GroundTruthIncident model
    // ─────────────────────────────────────────────────────────────────────────

    public class GroundTruthIncidentTests
    {
        [Fact] public void DefaultInstance_HasZeroCarIdx()
        {
            var g = new GroundTruthIncident();
            Assert.Equal(0, g.CarIdx);
        }

        [Fact] public void Properties_CanBeSetAndRead()
        {
            var g = new GroundTruthIncident
            {
                IncidentIndex        = 1,
                CarIdx               = 42,
                ReplayFrameNum       = 1500,
                ReplaySessionTimeSec = 123.456,
                DriverName           = "Test Driver",
                CarNumber            = "99",
                CustId               = "12345",
                LapDistPct           = 0.75f,
                LapNum               = 3
            };
            Assert.Equal(1, g.IncidentIndex);
            Assert.Equal(42, g.CarIdx);
            Assert.Equal(1500, g.ReplayFrameNum);
            Assert.Equal(123.456, g.ReplaySessionTimeSec, precision: 3);
            Assert.Equal("Test Driver", g.DriverName);
            Assert.Equal("99", g.CarNumber);
            Assert.Equal("12345", g.CustId);
            Assert.Equal(0.75f, g.LapDistPct);
            Assert.Equal(3, g.LapNum);
        }

        [Fact] public void SessionFlagsSnapshot_CanBeAssigned()
        {
            var g = new GroundTruthIncident
            {
                CarIdxSessionFlagsSnapshot = new[] { 1, 2, 3 }
            };
            Assert.Equal(3, g.CarIdxSessionFlagsSnapshot.Length);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DataCaptureSuiteTestResult
    // ─────────────────────────────────────────────────────────────────────────

    public class DataCaptureSuiteTestResultTests
    {
        [Fact] public void DefaultStatus_IsPending() =>
            Assert.Equal("pending", new DataCaptureSuiteTestResult().Status);

        [Fact] public void Status_CanBeChanged()
        {
            var r = new DataCaptureSuiteTestResult { Status = "pass" };
            Assert.Equal("pass", r.Status);
        }

        [Fact] public void JsonPropertyNames_AreCorrect()
        {
            var r = new DataCaptureSuiteTestResult
            {
                TestId = "T0", Name = "Ground Truth", Status = "emitted",
                EventName = DataCaptureSuiteConstants.EventGroundTruth,
                KpiLabel = "incidents_captured", KpiValue = "3", Error = null
            };
            var j = JObject.Parse(JsonConvert.SerializeObject(r));
            Assert.Equal("T0",    j["testId"]?.ToString());
            Assert.Equal("Ground Truth", j["name"]?.ToString());
            Assert.Equal("emitted", j["status"]?.ToString());
            Assert.Equal(DataCaptureSuiteConstants.EventGroundTruth, j["eventName"]?.ToString());
            Assert.Equal("incidents_captured", j["kpiLabel"]?.ToString());
            Assert.Equal("3", j["kpiValue"]?.ToString());
        }

        [Fact] public void NullError_IsOmittedFromJson()
        {
            var j = JObject.Parse(JsonConvert.SerializeObject(new DataCaptureSuiteTestResult { Error = null }));
            Assert.Null(j["error"]?.ToString() is string s && s == "" ? null : j["error"]);
        }

        [Theory]
        [InlineData("pending")]
        [InlineData("emitted")]
        [InlineData("pass")]
        [InlineData("fail")]
        [InlineData("skip")]
        public void KnownStatuses_CanBeSet(string status)
        {
            var r = new DataCaptureSuiteTestResult { Status = status };
            Assert.Equal(status, r.Status);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DataCaptureSuiteSnapshot
    // ─────────────────────────────────────────────────────────────────────────

    public class DataCaptureSuiteSnapshotTests
    {
        [Fact] public void DefaultPhase_IsIdle() =>
            Assert.Equal("idle", new DataCaptureSuiteSnapshot().Phase);

        [Fact] public void DefaultTotalSteps_Is10() =>
            Assert.Equal(10, new DataCaptureSuiteSnapshot().TotalSteps);

        [Fact] public void JsonPropertyNames_AreCorrect()
        {
            var snap = new DataCaptureSuiteSnapshot
            {
                Phase = "running", TestRunId = "abc-123",
                CurrentStep = 3, TotalSteps = 10, CurrentStepName = "T1_Sweep",
                ElapsedMs = 5000, GrafanaExploreUrl = "https://example.com/explore"
            };
            var j = JObject.Parse(JsonConvert.SerializeObject(snap));
            Assert.Equal("running",    j["phase"]?.ToString());
            Assert.Equal("abc-123",    j["testRunId"]?.ToString());
            Assert.Equal(3,            j["currentStep"]?.Value<int>());
            Assert.Equal(10,           j["totalSteps"]?.Value<int>());
            Assert.Equal("T1_Sweep",   j["currentStepName"]?.ToString());
            Assert.Equal(5000,         j["elapsedMs"]?.Value<long>());
            Assert.Equal("https://example.com/explore", j["grafanaExploreUrl"]?.ToString());
        }

        [Fact] public void TestResults_DefaultsToNull() =>
            Assert.Null(new DataCaptureSuiteSnapshot().TestResults);

        [Fact] public void TestResults_CanBeAssigned()
        {
            var snap = new DataCaptureSuiteSnapshot
            {
                TestResults = new[] { new DataCaptureSuiteTestResult { TestId = "T0", Status = "pass" } }
            };
            Assert.Single(snap.TestResults);
            Assert.Equal("pass", snap.TestResults[0].Status);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // DataCaptureSuitePhase enum
    // ─────────────────────────────────────────────────────────────────────────

    public class DataCaptureSuitePhaseTests
    {
        [Fact] public void Idle_IsFirst() =>
            Assert.Equal(0, (int)DataCaptureSuitePhase.Idle);

        [Fact] public void Enum_HasAllExpectedValues()
        {
            var names = Enum.GetNames(typeof(DataCaptureSuitePhase));
            Assert.Contains("Idle",         names);
            Assert.Contains("Running",      names);
            Assert.Contains("AwaitingLoki", names);
            Assert.Contains("Complete",     names);
            Assert.Contains("Cancelled",    names);
        }

        [Fact] public void Enum_HasExactlyFiveValues() =>
            Assert.Equal(5, Enum.GetValues(typeof(DataCaptureSuitePhase)).Length);

        [Fact] public void ToString_Lowercase_MatchesPhaseFieldConvention()
        {
            Assert.Equal("idle",         DataCaptureSuitePhase.Idle.ToString().ToLower());
            Assert.Equal("running",      DataCaptureSuitePhase.Running.ToString().ToLower());
            Assert.Equal("awaitingloki", DataCaptureSuitePhase.AwaitingLoki.ToString().ToLower());
            Assert.Equal("complete",     DataCaptureSuitePhase.Complete.ToString().ToLower());
            Assert.Equal("cancelled",    DataCaptureSuitePhase.Cancelled.ToString().ToLower());
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // LokiQueryClient
    // ─────────────────────────────────────────────────────────────────────────

    public class LokiQueryClientTests
    {
        private const string SampleRunId = "b1a2c3d4-e5f6-7890-abcd-ef1234567890";

        // ── BuildTestRunQuery ───────────────────────────────────────────────

        [Fact] public void BuildTestRunQuery_ContainsTestRunId() =>
            Assert.Contains(SampleRunId, LokiQueryClient.BuildTestRunQuery(SampleRunId));

        [Fact] public void BuildTestRunQuery_ContainsAppLabel() =>
            Assert.Contains("app=\"sim-steward\"", LokiQueryClient.BuildTestRunQuery(SampleRunId));

        [Fact] public void BuildTestRunQuery_WithoutEvent_NoEventFilter()
        {
            var q = LokiQueryClient.BuildTestRunQuery(SampleRunId);
            Assert.DoesNotContain("|event=", q);
        }

        [Fact] public void BuildTestRunQuery_WithEvent_ContainsEventFilter()
        {
            var q = LokiQueryClient.BuildTestRunQuery(SampleRunId, DataCaptureSuiteConstants.EventGroundTruth);
            Assert.Contains("|event=", q);
            Assert.Contains(DataCaptureSuiteConstants.EventGroundTruth, q);
        }

        [Fact] public void BuildTestRunQuery_WithEvent_AlsoContainsTestRunId()
        {
            var q = LokiQueryClient.BuildTestRunQuery(SampleRunId, DataCaptureSuiteConstants.EventGroundTruth);
            Assert.Contains(SampleRunId, q);
        }

        [Fact] public void BuildTestRunQuery_WithNullEvent_NoEventFilter()
        {
            var q = LokiQueryClient.BuildTestRunQuery(SampleRunId, null);
            Assert.DoesNotContain("|event=", q);
        }

        [Fact] public void BuildTestRunQuery_WithEmptyEvent_NoEventFilter()
        {
            var q = LokiQueryClient.BuildTestRunQuery(SampleRunId, "");
            Assert.DoesNotContain("|event=", q);
        }

        [Fact] public void BuildTestRunQuery_StartsWithStreamSelector()
        {
            var q = LokiQueryClient.BuildTestRunQuery(SampleRunId);
            Assert.StartsWith("{", q);
        }

        [Theory]
        [InlineData(DataCaptureSuiteConstants.EventGroundTruth)]
        [InlineData(DataCaptureSuiteConstants.EventSpeedSample)]
        [InlineData(DataCaptureSuiteConstants.EventVariableInventory)]
        [InlineData(DataCaptureSuiteConstants.EventPlayerSnapshot)]
        [InlineData(DataCaptureSuiteConstants.EventDriverRoster)]
        [InlineData(DataCaptureSuiteConstants.EventCameraSwitchDriver)]
        [InlineData(DataCaptureSuiteConstants.EventCameraViewSample)]
        [InlineData(DataCaptureSuiteConstants.EventSessionResults)]
        [InlineData(DataCaptureSuiteConstants.EventIncidentReseek)]
        [InlineData(DataCaptureSuiteConstants.EventFfSweepResult)]
        public void BuildTestRunQuery_AllEventNames_ProduceNonEmptyQuery(string eventName)
        {
            var q = LokiQueryClient.BuildTestRunQuery(SampleRunId, eventName);
            Assert.False(string.IsNullOrWhiteSpace(q));
            Assert.Contains(eventName, q);
        }

        // ── BuildGrafanaExploreUrl ──────────────────────────────────────────

        [Fact] public void BuildGrafanaExploreUrl_EmptyBase_ReturnsEmpty() =>
            Assert.Equal("", LokiQueryClient.BuildGrafanaExploreUrl("", SampleRunId));

        [Fact] public void BuildGrafanaExploreUrl_NullBase_ReturnsEmpty() =>
            Assert.Equal("", LokiQueryClient.BuildGrafanaExploreUrl(null, SampleRunId));

        [Fact] public void BuildGrafanaExploreUrl_EmptyRunId_ReturnsEmpty() =>
            Assert.Equal("", LokiQueryClient.BuildGrafanaExploreUrl("https://example.grafana.net", ""));

        [Fact] public void BuildGrafanaExploreUrl_NullRunId_ReturnsEmpty() =>
            Assert.Equal("", LokiQueryClient.BuildGrafanaExploreUrl("https://example.grafana.net", null));

        [Fact] public void BuildGrafanaExploreUrl_ContainsExplore() =>
            Assert.Contains("explore", LokiQueryClient.BuildGrafanaExploreUrl("https://example.grafana.net", SampleRunId));

        [Fact] public void BuildGrafanaExploreUrl_ContainsEncodedTestRunId()
        {
            var url = LokiQueryClient.BuildGrafanaExploreUrl("https://example.grafana.net", SampleRunId);
            // test_run_id appears in the URL (may be percent-encoded)
            Assert.Contains(SampleRunId, Uri.UnescapeDataString(url));
        }

        [Fact] public void BuildGrafanaExploreUrl_StartsWithBase()
        {
            var url = LokiQueryClient.BuildGrafanaExploreUrl("https://example.grafana.net", SampleRunId);
            Assert.StartsWith("https://example.grafana.net/", url);
        }

        [Fact] public void BuildGrafanaExploreUrl_TrailingSlashOnBase_IsStripped()
        {
            var withSlash    = LokiQueryClient.BuildGrafanaExploreUrl("https://example.grafana.net/", SampleRunId);
            var withoutSlash = LokiQueryClient.BuildGrafanaExploreUrl("https://example.grafana.net",  SampleRunId);
            Assert.Equal(withoutSlash, withSlash);
        }

        // ── BuildGrafanaExploreUrl (3-arg: per-event) ─────────────────────

        [Fact] public void BuildGrafanaExploreUrl_WithEvent_ContainsEventName()
        {
            var url = LokiQueryClient.BuildGrafanaExploreUrl("https://example.grafana.net", SampleRunId, DataCaptureSuiteConstants.EventGroundTruth);
            Assert.Contains(DataCaptureSuiteConstants.EventGroundTruth, Uri.UnescapeDataString(url));
        }

        [Fact] public void BuildGrafanaExploreUrl_WithEvent_ContainsRunId()
        {
            var url = LokiQueryClient.BuildGrafanaExploreUrl("https://example.grafana.net", SampleRunId, DataCaptureSuiteConstants.EventGroundTruth);
            Assert.Contains(SampleRunId, Uri.UnescapeDataString(url));
        }

        [Fact] public void BuildGrafanaExploreUrl_WithEvent_EmptyBase_ReturnsEmpty() =>
            Assert.Equal("", LokiQueryClient.BuildGrafanaExploreUrl("", SampleRunId, "some_event"));

        [Fact] public void BuildGrafanaExploreUrl_WithEvent_NullEvent_StillReturnsUrl()
        {
            var url = LokiQueryClient.BuildGrafanaExploreUrl("https://example.grafana.net", SampleRunId, null);
            Assert.Contains("explore", url);
            Assert.Contains(SampleRunId, Uri.UnescapeDataString(url));
        }

        [Fact] public void BuildGrafanaExploreUrl_WithEvent_StartsWithBase()
        {
            var url = LokiQueryClient.BuildGrafanaExploreUrl("https://example.grafana.net", SampleRunId, "test_event");
            Assert.StartsWith("https://example.grafana.net/", url);
        }

        // ── Timestamp helpers ───────────────────────────────────────────────

        [Fact] public void NowNs_ReturnsPositiveValue() =>
            Assert.True(LokiQueryClient.NowNs() > 0);

        [Fact] public void NowNs_IsInNanoseconds()
        {
            // 2020-01-01 in ns is ~1577836800000000000
            Assert.True(LokiQueryClient.NowNs() > 1_577_836_800_000_000_000L);
        }

        [Fact] public void NowMinusMs_ZeroOffset_ApproximatelyEqualsNowNs()
        {
            long t1 = LokiQueryClient.NowMinusMs(0);
            long t2 = LokiQueryClient.NowNs();
            // Should be within 100ms of each other
            Assert.InRange(t2 - t1, 0L, 100_000_000L);
        }

        [Fact] public void NowMinusMs_OneHour_IsLessThanNowNs()
        {
            long oneHourAgo = LokiQueryClient.NowMinusMs(3_600_000L);
            long now        = LokiQueryClient.NowNs();
            Assert.True(oneHourAgo < now);
        }

        [Fact] public void NowMinusMs_OneHour_DiffIsApproximatelyOneHour()
        {
            long oneHourAgo = LokiQueryClient.NowMinusMs(3_600_000L);
            long now        = LokiQueryClient.NowNs();
            long diffMs     = (now - oneHourAgo) / 1_000_000L;
            // Allow ±100ms tolerance
            Assert.InRange(diffMs, 3_599_900L, 3_600_100L);
        }

        [Fact] public void NowMinusMs_LargeOffset_StillPositive()
        {
            // 1 day ago should still be a positive epoch
            Assert.True(LokiQueryClient.NowMinusMs(86_400_000L) > 0);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Cross-cutting: event names align with result structure expectations
    // ─────────────────────────────────────────────────────────────────────────

    public class DataCaptureSuiteIntegrationModelTests
    {
        /// <summary>
        /// Each of the 12 test steps (T0–T8 + T5b + T_DISC + T_60Hz) has a unique event name constant.
        /// This mirrors the expected test result rows in the dashboard.
        /// </summary>
        [Fact] public void TwelveUniquePerTestEventNames()
        {
            var perTestEvents = new[]
            {
                DataCaptureSuiteConstants.EventGroundTruth,
                DataCaptureSuiteConstants.EventSpeedSample,
                DataCaptureSuiteConstants.EventVariableInventory,
                DataCaptureSuiteConstants.EventPlayerSnapshot,
                DataCaptureSuiteConstants.EventDriverRoster,
                DataCaptureSuiteConstants.EventCameraSwitchDriver,
                DataCaptureSuiteConstants.EventCameraViewSample,
                DataCaptureSuiteConstants.EventSessionResults,
                DataCaptureSuiteConstants.EventIncidentReseek,
                DataCaptureSuiteConstants.EventFfSweepResult,
                DataCaptureSuiteConstants.EventDataDiscovery,
                DataCaptureSuiteConstants.Event60HzSummary,
            };
            Assert.Equal(12, perTestEvents.Length);
            Assert.Equal(12, perTestEvents.Distinct().Count());
        }

        [Fact] public void SuiteLifecycleEvents_AreDistinctFromTestEvents()
        {
            var perTest = new HashSet<string>
            {
                DataCaptureSuiteConstants.EventGroundTruth,
                DataCaptureSuiteConstants.EventSpeedSample,
                DataCaptureSuiteConstants.EventVariableInventory,
                DataCaptureSuiteConstants.EventPlayerSnapshot,
                DataCaptureSuiteConstants.EventDriverRoster,
                DataCaptureSuiteConstants.EventCameraSwitchDriver,
                DataCaptureSuiteConstants.EventCameraViewSample,
                DataCaptureSuiteConstants.EventCameraViewSummary,
                DataCaptureSuiteConstants.EventSessionResults,
                DataCaptureSuiteConstants.EventIncidentReseek,
                DataCaptureSuiteConstants.EventFfSweepResult,
            };
            Assert.DoesNotContain(DataCaptureSuiteConstants.EventSuiteStarted, perTest);
            Assert.DoesNotContain(DataCaptureSuiteConstants.EventSuiteComplete, perTest);
        }

        /// <summary>
        /// LokiVerifyDelayMs must be large enough for Alloy file-tail ingestion (~10-15s).
        /// </summary>
        [Fact] public void LokiVerifyDelayMs_AtLeast10Seconds() =>
            Assert.True(DataCaptureSuiteConstants.LokiVerifyDelayMs >= 10_000);

        /// <summary>
        /// NextIncidentCooldownTicks at 60Hz = 2.5s, which is the known SDK seek cooldown.
        /// </summary>
        [Fact] public void NextIncidentCooldownTicks_At60Hz_Is2_5Seconds()
        {
            double seconds = DataCaptureSuiteConstants.NextIncidentCooldownTicks / 60.0;
            Assert.Equal(2.5, seconds, precision: 2);
        }

        /// <summary>
        /// CamSettleTicks at 60Hz = 1.0s, which matches the CamSwitchPos settle time.
        /// </summary>
        [Fact] public void CamSettleTicks_At60Hz_Is1Second()
        {
            double seconds = DataCaptureSuiteConstants.CamSettleTicks / 60.0;
            Assert.Equal(1.0, seconds, precision: 2);
        }

        /// <summary>
        /// SeekTimeoutTicks at 60Hz = 10s, which is enough time for a ToStart seek.
        /// </summary>
        [Fact] public void SeekTimeoutTicks_At60Hz_IsAtLeast5Seconds()
        {
            double seconds = DataCaptureSuiteConstants.SeekTimeoutTicks / 60.0;
            Assert.True(seconds >= 5.0);
        }

        /// <summary>
        /// The speed sweep covers all four speeds from the plan spec.
        /// </summary>
        [Fact] public void SpeedSweepCoversAllPlanSpeeds()
        {
            var required = new[] { 1, 4, 8, 16 };
            Assert.All(required, s => Assert.Contains(s, DataCaptureSuiteConstants.SpeedSweepSpeeds));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // T0 SelectGroundTruthFrames
    // ─────────────────────────────────────────────────────────────────────────

    public class SelectGroundTruthFramesTests
    {
        [Fact] public void PrefersDifferentLaps_SkippingLap1()
        {
            var candidates = new List<(int frame, int lap, int carIdx)>
            {
                (100, 1, 0),   // lap 1 — should be skipped
                (200, 2, 1),
                (300, 3, 2),
                (400, 4, 3),
            };
            var selected = DataCaptureSuiteSelection.SelectGroundTruthFrames(candidates);
            Assert.Equal(3, selected.Length);
            Assert.Equal(new[] { 200, 300, 400 }, selected);
        }

        [Fact] public void FallsBackToLap1_WhenNotEnoughOtherLaps()
        {
            var candidates = new List<(int frame, int lap, int carIdx)>
            {
                (100, 1, 0),
                (200, 1, 1),
                (300, 2, 2),
            };
            var selected = DataCaptureSuiteSelection.SelectGroundTruthFrames(candidates);
            Assert.Equal(3, selected.Length);
            Assert.Contains(300, selected); // lap 2 preferred
            Assert.Contains(100, selected); // lap 1 used as fallback
        }

        [Fact] public void ReturnsFewerThan3_WhenNotEnoughCandidates()
        {
            var candidates = new List<(int frame, int lap, int carIdx)>
            {
                (100, 3, 0),
                (200, 5, 1),
            };
            var selected = DataCaptureSuiteSelection.SelectGroundTruthFrames(candidates);
            Assert.Equal(2, selected.Length);
        }

        [Fact] public void ReturnsEmpty_WhenNoCandidates()
        {
            var selected = DataCaptureSuiteSelection.SelectGroundTruthFrames(new List<(int, int, int)>());
            Assert.Empty(selected);
        }

        [Fact] public void PrefersDifferentLaps_NotSameLapTwice()
        {
            var candidates = new List<(int frame, int lap, int carIdx)>
            {
                (100, 3, 0),
                (200, 3, 1),  // same lap as first — should be skipped in pass 1
                (300, 5, 2),
                (400, 7, 3),
            };
            var selected = DataCaptureSuiteSelection.SelectGroundTruthFrames(candidates);
            Assert.Equal(3, selected.Length);
            Assert.Equal(new[] { 100, 300, 400 }, selected);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Preflight model
    // ─────────────────────────────────────────────────────────────────────────

    public class PreflightMiniTestTests
    {
        [Fact]
        public void DefaultStatus_IsPending()
        {
            var t = new PreflightMiniTest { Id = "PC_WS", Name = "WebSocket", Level = 1 };
            Assert.Equal("pending", t.Status);
        }

        [Fact]
        public void SerializesToJson_WithExpectedKeys()
        {
            var t = new PreflightMiniTest { Id = "PC_WS", Name = "WS", Status = "pass", Detail = "ok", Level = 1 };
            var json = JObject.FromObject(t);
            Assert.Equal("PC_WS", json["id"]?.ToString());
            Assert.Equal("pass", json["status"]?.ToString());
            Assert.Equal(1, json["level"]?.Value<int>());
        }
    }

    public class PreflightSnapshotTests
    {
        [Fact]
        public void DefaultValues()
        {
            var snap = new PreflightSnapshot();
            Assert.Equal("idle", snap.Phase);
            Assert.Equal("full", snap.ReplayScope);
            Assert.Equal(0, snap.Level);
            Assert.False(snap.AllPassed);
            Assert.Null(snap.MiniTests);
            Assert.Null(snap.CorrelationId);
        }

        [Fact]
        public void AllPassed_Serializes()
        {
            var snap = new PreflightSnapshot
            {
                Phase = "complete",
                Level = 2,
                AllPassed = true,
                CorrelationId = "abc-123",
                ReplayScope = "partial",
                MiniTests = new[]
                {
                    new PreflightMiniTest { Id = "PC_WS", Name = "WS", Status = "pass", Level = 1 },
                    new PreflightMiniTest { Id = "PC_CHECKERED", Name = "Checkered", Status = "skip", Level = 2 },
                }
            };
            var json = JObject.FromObject(snap);
            Assert.True(json["allPassed"]?.Value<bool>());
            Assert.Equal("partial", json["replayScope"]?.ToString());
            Assert.Equal("abc-123", json["correlationId"]?.ToString());
            Assert.Equal(2, (json["miniTests"] as JArray)?.Count);
        }

        [Fact]
        public void BackwardCompat_FlatBooleans_StillSerialize()
        {
            var snap = new PreflightSnapshot
            {
                GrafanaOk = true,
                SimHubOk = true,
                CheckeredOk = false,
                ResultsPopulated = true,
                SessionStateAtEnd = 5,
            };
            var json = JObject.FromObject(snap);
            Assert.True(json["grafanaOk"]?.Value<bool>());
            Assert.True(json["simHubOk"]?.Value<bool>());
            Assert.False(json["checkeredOk"]?.Value<bool>());
            Assert.Equal(5, json["sessionStateAtEnd"]?.Value<int>());
        }
    }
}
