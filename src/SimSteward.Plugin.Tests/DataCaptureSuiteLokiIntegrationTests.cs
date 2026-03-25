using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Newtonsoft.Json.Linq;
using SimSteward.Observability;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    /// <summary>
    /// End-to-end integration tests that query Loki after a real Data Capture Suite run.
    /// Requires iRacing open with a full race replay loaded, the suite run to completion, and Alloy to have ingested logs.
    ///
    /// Enable with env vars:
    ///   RUN_CAPTURE_SUITE_LOKI_ASSERT=1        (master gate — all tests skip when absent)
    ///   LOKI_QUERY_URL or SIMSTEWARD_LOKI_URL  (Loki base URL, e.g. http://localhost:3100)
    ///   CAPTURE_SUITE_TEST_RUN_ID              (optional GUID — narrows all queries to a specific run)
    /// </summary>
    public class DataCaptureSuiteLokiIntegrationTests
    {
        // ── Infrastructure ──────────────────────────────────────────────────────

        private const int FrameTolerance = 60;

        private static readonly string _masterGate =
            Environment.GetEnvironmentVariable("RUN_CAPTURE_SUITE_LOKI_ASSERT");
        private static readonly string _baseUrl =
            (Environment.GetEnvironmentVariable("LOKI_QUERY_URL")
             ?? Environment.GetEnvironmentVariable("SIMSTEWARD_LOKI_URL") ?? "").TrimEnd('/');
        private static readonly string _testRunId =
            Environment.GetEnvironmentVariable("CAPTURE_SUITE_TEST_RUN_ID") ?? "";

        // Lazy cache for T0 lines — queried once, reused by many tests
        private List<JObject> _t0Cache;

        private void SkipIfDisabled()
        {
            Skip.IfNot(string.Equals(_masterGate, "1", StringComparison.Ordinal),
                "Set RUN_CAPTURE_SUITE_LOKI_ASSERT=1 and SIMSTEWARD_LOKI_URL to enable.");
            if (string.IsNullOrWhiteSpace(_baseUrl))
                Assert.Fail("RUN_CAPTURE_SUITE_LOKI_ASSERT=1 requires LOKI_QUERY_URL or SIMSTEWARD_LOKI_URL.");
        }

        private List<JObject> QueryLines(string eventName)
        {
            var runFilter = string.IsNullOrEmpty(_testRunId)
                ? ""
                : $" | test_run_id = \"{_testRunId}\"";
            var logql =
                $"{{app=\"sim-steward\", component=\"simhub-plugin\"}} | json | event = \"{eventName}\"{runFilter}";
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            if (!LokiQueryRangeClient.TryQueryRange(_baseUrl, logql, client, TimeSpan.FromMinutes(60),
                    out var lines, out var err))
                Assert.Fail($"Loki query failed for event={eventName}: {err}");
            var parsed = new List<JObject>();
            foreach (var line in lines)
            {
                try { parsed.Add(JObject.Parse(line)); }
                catch { /* ignore malformed */ }
            }
            return parsed;
        }

        private List<JObject> GetT0() => _t0Cache ??= QueryLines(DataCaptureSuiteConstants.EventGroundTruth)
            .OrderBy(j => FieldInt(j, "incident_index") ?? 999).ToList();

        private JObject T0ByIndex(int idx) =>
            GetT0().FirstOrDefault(j => FieldInt(j, "incident_index") == idx);

        private static string Field(JObject j, string name) =>
            j?[name]?.ToString();

        private static int? FieldInt(JObject j, string name)
        {
            var s = Field(j, name);
            return int.TryParse(s, out var v) ? v : (int?)null;
        }

        private static double? FieldDouble(JObject j, string name)
        {
            var s = Field(j, name);
            return double.TryParse(s, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : (double?)null;
        }

        // Parses field as JArray or comma/semicolon-separated string → list of trimmed strings
        private static List<string> FieldArray(JObject j, string name)
        {
            var token = j?[name];
            if (token == null) return new List<string>();
            if (token is JArray arr) return arr.Select(t => t.ToString()).ToList();
            var s = token.ToString();
            return s.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                     .Select(x => x.Trim()).ToList();
        }

        // Collect all events across all 13 types for common-field assertions
        private List<JObject> AllEvents()
        {
            var all = new List<JObject>();
            foreach (var ev in new[]
            {
                DataCaptureSuiteConstants.EventSuiteStarted,
                DataCaptureSuiteConstants.EventSuiteComplete,
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
            })
                all.AddRange(QueryLines(ev));
            return all;
        }

        // ── Suite Lifecycle ──────────────────────────────────────────────────────

        [SkippableFact]
        public void Loki_SuiteStarted_IsPresent()
        {
            SkipIfDisabled();
            var lines = QueryLines(DataCaptureSuiteConstants.EventSuiteStarted);
            Assert.True(lines.Count >= 1,
                $"Expected sdk_capture_suite_started in Loki; got {lines.Count}.");
        }

        [SkippableFact]
        public void Loki_SuiteStarted_ExactlyOne()
        {
            SkipIfDisabled();
            var lines = QueryLines(DataCaptureSuiteConstants.EventSuiteStarted);
            Assert.Single(lines);
        }

        [SkippableFact]
        public void Loki_SuiteStarted_HasValidGuidTestRunId()
        {
            SkipIfDisabled();
            var lines = QueryLines(DataCaptureSuiteConstants.EventSuiteStarted);
            Skip.If(lines.Count == 0, "No suite_started event — run the suite first.");
            var id = Field(lines[0], "test_run_id");
            Assert.True(Guid.TryParse(id, out _),
                $"test_run_id '{id}' is not a valid GUID.");
        }

        [SkippableFact]
        public void Loki_SuiteStarted_TotalStepsIsTen()
        {
            SkipIfDisabled();
            var lines = QueryLines(DataCaptureSuiteConstants.EventSuiteStarted);
            Skip.If(lines.Count == 0, "No suite_started event.");
            Assert.Equal(10, FieldInt(lines[0], "total_steps") ?? 0);
        }

        [SkippableFact]
        public void Loki_SuiteComplete_IsPresent()
        {
            SkipIfDisabled();
            var lines = QueryLines(DataCaptureSuiteConstants.EventSuiteComplete);
            Assert.True(lines.Count >= 1,
                $"Expected sdk_capture_suite_complete; got {lines.Count}.");
        }

        [SkippableFact]
        public void Loki_SuiteComplete_TestRunIdMatchesStarted()
        {
            SkipIfDisabled();
            var started = QueryLines(DataCaptureSuiteConstants.EventSuiteStarted);
            var complete = QueryLines(DataCaptureSuiteConstants.EventSuiteComplete);
            Skip.If(started.Count == 0 || complete.Count == 0, "Need both started and complete events.");
            Assert.Equal(Field(started[0], "test_run_id"), Field(complete[0], "test_run_id"));
        }

        // ── All-Events Common Fields ─────────────────────────────────────────────

        [SkippableFact]
        public void Loki_AllThirteenEventTypesPresent()
        {
            SkipIfDisabled();
            var missing = new List<string>();
            foreach (var ev in new[]
            {
                DataCaptureSuiteConstants.EventSuiteStarted,
                DataCaptureSuiteConstants.EventSuiteComplete,
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
            })
                if (QueryLines(ev).Count == 0) missing.Add(ev);
            Assert.True(missing.Count == 0, $"Missing events in Loki: {string.Join(", ", missing)}");
        }

        [SkippableFact]
        public void Loki_AllEvents_HaveTestRunId()
        {
            SkipIfDisabled();
            var bad = AllEvents().Where(j => string.IsNullOrEmpty(Field(j, "test_run_id"))).ToList();
            Assert.True(bad.Count == 0, $"{bad.Count} events missing test_run_id.");
        }

        [SkippableFact]
        public void Loki_AllEvents_TestRunIdIsConsistent()
        {
            SkipIfDisabled();
            var all = AllEvents();
            var ids = all.Select(j => Field(j, "test_run_id")).Distinct().ToList();
            Assert.True(ids.Count == 1, $"Expected 1 unique test_run_id; got {ids.Count}: {string.Join(", ", ids)}");
        }

        [SkippableFact]
        public void Loki_AllEvents_TestRunIdIsValidGuid()
        {
            SkipIfDisabled();
            var bad = AllEvents()
                .Where(j => !Guid.TryParse(Field(j, "test_run_id") ?? "", out _))
                .ToList();
            Assert.True(bad.Count == 0, $"{bad.Count} events have non-GUID test_run_id.");
        }

        [SkippableFact]
        public void Loki_AllEvents_HaveTestingFlag()
        {
            SkipIfDisabled();
            var bad = AllEvents().Where(j => Field(j, "testing") != "true").ToList();
            Assert.True(bad.Count == 0, $"{bad.Count} events missing testing=true.");
        }

        [SkippableFact]
        public void Loki_AllEvents_HaveDomainTest()
        {
            SkipIfDisabled();
            var bad = AllEvents().Where(j => Field(j, "domain") != "test").ToList();
            Assert.True(bad.Count == 0, $"{bad.Count} events missing domain=test.");
        }

        [SkippableFact]
        public void Loki_AllEvents_HaveSubsessionId()
        {
            SkipIfDisabled();
            var bad = AllEvents().Where(j => string.IsNullOrEmpty(Field(j, "subsession_id"))).ToList();
            Assert.True(bad.Count == 0, $"{bad.Count} events missing subsession_id.");
        }

        [SkippableFact]
        public void Loki_AllEvents_SubsessionIdIsConsistent()
        {
            SkipIfDisabled();
            var ids = AllEvents().Select(j => Field(j, "subsession_id")).Distinct().ToList();
            Assert.True(ids.Count == 1, $"Expected 1 unique subsession_id; got {ids.Count}.");
        }

        [SkippableFact]
        public void Loki_AllEvents_HaveTrackDisplayName()
        {
            SkipIfDisabled();
            var bad = AllEvents().Where(j => string.IsNullOrEmpty(Field(j, "track_display_name"))).ToList();
            Assert.True(bad.Count == 0, $"{bad.Count} events missing track_display_name.");
        }

        [SkippableFact]
        public void Loki_AllEvents_TrackDisplayNameIsConsistent()
        {
            SkipIfDisabled();
            var names = AllEvents().Select(j => Field(j, "track_display_name")).Distinct().ToList();
            Assert.True(names.Count == 1, $"Expected 1 unique track_display_name; got {names.Count}.");
        }

        [SkippableFact]
        public void Loki_AllEvents_HaveTestTag()
        {
            SkipIfDisabled();
            var bad = AllEvents().Where(j => string.IsNullOrEmpty(Field(j, "test_tag"))).ToList();
            Assert.True(bad.Count == 0, $"{bad.Count} events missing test_tag.");
        }

        // ── T0 — Ground Truth ────────────────────────────────────────────────────

        [SkippableFact]
        public void Loki_GroundTruth_HasThreeIncidents()
        {
            SkipIfDisabled();
            Assert.Equal(3, GetT0().Count);
        }

        [SkippableFact]
        public void Loki_GroundTruth_IncidentIndexesAreSequential()
        {
            SkipIfDisabled();
            var indexes = GetT0().Select(j => FieldInt(j, "incident_index")).OrderBy(x => x).ToList();
            Assert.Equal(new int?[] { 0, 1, 2 }, indexes);
        }

        [SkippableFact]
        public void Loki_GroundTruth_ReplayFramesAreUnique()
        {
            SkipIfDisabled();
            var frames = GetT0().Select(j => Field(j, "replay_frame")).ToList();
            Assert.Equal(frames.Count, frames.Distinct().Count());
        }

        [SkippableFact]
        public void Loki_GroundTruth_ReplayFramesArePositive()
        {
            SkipIfDisabled();
            var bad = GetT0().Where(j => (FieldInt(j, "replay_frame") ?? -1) <= 0).ToList();
            Assert.True(bad.Count == 0, $"{bad.Count} ground truth incidents have replay_frame <= 0.");
        }

        [SkippableFact]
        public void Loki_GroundTruth_CarIdxIsValid()
        {
            SkipIfDisabled();
            var bad = GetT0().Where(j => (FieldInt(j, "car_idx") ?? -1) < 0).ToList();
            Assert.True(bad.Count == 0, $"{bad.Count} ground truth incidents have invalid car_idx.");
        }

        [SkippableFact]
        public void Loki_GroundTruth_CarIdxesAreDistinct()
        {
            SkipIfDisabled();
            var carIdxes = GetT0().Select(j => Field(j, "car_idx")).ToList();
            Assert.Equal(carIdxes.Count, carIdxes.Distinct().Count());
        }

        [SkippableFact]
        public void Loki_GroundTruth_SessionTimeIsPositive()
        {
            SkipIfDisabled();
            var bad = GetT0().Where(j => (FieldDouble(j, "replay_session_time_sec") ?? -1) <= 0).ToList();
            Assert.True(bad.Count == 0, $"{bad.Count} ground truth incidents have session_time <= 0.");
        }

        [SkippableFact]
        public void Loki_GroundTruth_SessionTimesAreIncreasing()
        {
            SkipIfDisabled();
            var t0 = GetT0();
            Skip.If(t0.Count < 3, "Need 3 T0 events.");
            var times = t0.Select(j => FieldDouble(j, "replay_session_time_sec") ?? 0).ToList();
            Assert.True(times[0] < times[1] && times[1] < times[2],
                $"Session times not increasing: {times[0]} {times[1]} {times[2]}");
        }

        [SkippableFact]
        public void Loki_GroundTruth_LapDistPctInRange()
        {
            SkipIfDisabled();
            var bad = GetT0().Where(j =>
            {
                var v = FieldDouble(j, "lap_dist_pct");
                return v == null || v < 0.0 || v > 1.0;
            }).ToList();
            Assert.True(bad.Count == 0, $"{bad.Count} ground truth incidents have lap_dist_pct out of [0,1].");
        }

        [SkippableFact]
        public void Loki_GroundTruth_LapNumNonNegative()
        {
            SkipIfDisabled();
            var bad = GetT0().Where(j => (FieldInt(j, "lap_num") ?? -1) < 0).ToList();
            Assert.True(bad.Count == 0, $"{bad.Count} ground truth incidents have lap_num < 0.");
        }

        [SkippableFact]
        public void Loki_GroundTruth_DriverNameNonEmpty()
        {
            SkipIfDisabled();
            var bad = GetT0().Where(j => string.IsNullOrEmpty(Field(j, "driver_name"))).ToList();
            Assert.True(bad.Count == 0, $"{bad.Count} ground truth incidents missing driver_name.");
        }

        [SkippableFact]
        public void Loki_GroundTruth_CarNumberNonEmpty()
        {
            SkipIfDisabled();
            var bad = GetT0().Where(j => string.IsNullOrEmpty(Field(j, "car_number"))).ToList();
            Assert.True(bad.Count == 0, $"{bad.Count} ground truth incidents missing car_number.");
        }

        [SkippableFact]
        public void Loki_GroundTruth_CustIdNonEmpty()
        {
            SkipIfDisabled();
            var bad = GetT0().Where(j => string.IsNullOrEmpty(Field(j, "cust_id"))).ToList();
            Assert.True(bad.Count == 0, $"{bad.Count} ground truth incidents missing cust_id.");
        }

        // ── T1 — Speed Sweep ─────────────────────────────────────────────────────

        [SkippableFact]
        public void Loki_SpeedSweep_HasFourSamples()
        {
            SkipIfDisabled();
            Assert.Equal(4, QueryLines(DataCaptureSuiteConstants.EventSpeedSample).Count);
        }

        [SkippableFact]
        public void Loki_SpeedSweep_CoversAllFourSpeeds()
        {
            SkipIfDisabled();
            var speeds = QueryLines(DataCaptureSuiteConstants.EventSpeedSample)
                .Select(j => FieldInt(j, "requested_speed"))
                .OrderBy(x => x).ToList();
            Assert.Equal(new int?[] { 1, 4, 8, 16 }, speeds);
        }

        [SkippableFact]
        public void Loki_SpeedSweep_NoDuplicateSpeeds()
        {
            SkipIfDisabled();
            var speeds = QueryLines(DataCaptureSuiteConstants.EventSpeedSample)
                .Select(j => Field(j, "requested_speed")).ToList();
            Assert.Equal(speeds.Count, speeds.Distinct().Count());
        }

        [SkippableFact]
        public void Loki_SpeedSweep_EffectiveHzMatchesFormula()
        {
            SkipIfDisabled();
            foreach (var j in QueryLines(DataCaptureSuiteConstants.EventSpeedSample))
            {
                var speed = FieldInt(j, "requested_speed") ?? 0;
                var hz = FieldDouble(j, "effective_session_hz") ?? -1;
                var expected = 60.0 / speed;
                Assert.True(Math.Abs(hz - expected) < 0.01,
                    $"speed={speed}: expected hz={expected} but got {hz}");
            }
        }

        [SkippableFact]
        public void Loki_SpeedSweep_EffectiveHzDecreasesWithSpeed()
        {
            SkipIfDisabled();
            var samples = QueryLines(DataCaptureSuiteConstants.EventSpeedSample)
                .OrderBy(j => FieldInt(j, "requested_speed") ?? 0).ToList();
            Skip.If(samples.Count < 4, "Need 4 speed samples.");
            for (int i = 0; i < samples.Count - 1; i++)
            {
                var hz1 = FieldDouble(samples[i], "effective_session_hz") ?? 0;
                var hz2 = FieldDouble(samples[i + 1], "effective_session_hz") ?? 0;
                Assert.True(hz1 > hz2,
                    $"Hz should decrease with speed; got hz[{i}]={hz1} >= hz[{i+1}]={hz2}");
            }
        }

        [SkippableFact]
        public void Loki_SpeedSweep_DetectionRateInRange()
        {
            SkipIfDisabled();
            var bad = QueryLines(DataCaptureSuiteConstants.EventSpeedSample).Where(j =>
            {
                var r = FieldDouble(j, "detection_rate_pct") ?? -1;
                return r < 0 || r > 100;
            }).ToList();
            Assert.True(bad.Count == 0, $"{bad.Count} speed samples have detection_rate_pct out of [0,100].");
        }

        [SkippableFact]
        public void Loki_SpeedSweep_HitPlusMissEqualsThree()
        {
            SkipIfDisabled();
            foreach (var j in QueryLines(DataCaptureSuiteConstants.EventSpeedSample))
            {
                var hit = FieldInt(j, "ground_truth_hit_count") ?? -1;
                var miss = FieldInt(j, "ground_truth_miss_count") ?? -1;
                Assert.Equal(3, hit + miss);
            }
        }

        [SkippableFact]
        public void Loki_SpeedSweep_TickCountPositive()
        {
            SkipIfDisabled();
            var bad = QueryLines(DataCaptureSuiteConstants.EventSpeedSample)
                .Where(j => (FieldInt(j, "tick_count") ?? 0) <= 0).ToList();
            Assert.True(bad.Count == 0, $"{bad.Count} speed samples have tick_count <= 0.");
        }

        [SkippableFact]
        public void Loki_SpeedSweep_IncidentsDetectedNonNegative()
        {
            SkipIfDisabled();
            var bad = QueryLines(DataCaptureSuiteConstants.EventSpeedSample)
                .Where(j => (FieldInt(j, "incidents_detected") ?? -1) < 0).ToList();
            Assert.True(bad.Count == 0, $"{bad.Count} speed samples have incidents_detected < 0.");
        }

        [SkippableFact]
        public void Loki_SpeedSweep_1x_AllHitsDetected()
        {
            SkipIfDisabled();
            var sample1x = QueryLines(DataCaptureSuiteConstants.EventSpeedSample)
                .FirstOrDefault(j => FieldInt(j, "requested_speed") == 1);
            Skip.If(sample1x == null, "No speed=1 sample found.");
            Assert.Equal(3, FieldInt(sample1x, "ground_truth_hit_count") ?? -1);
        }

        [SkippableFact]
        public void Loki_SpeedSweep_1x_DetectionRate100Pct()
        {
            SkipIfDisabled();
            var sample1x = QueryLines(DataCaptureSuiteConstants.EventSpeedSample)
                .FirstOrDefault(j => FieldInt(j, "requested_speed") == 1);
            Skip.If(sample1x == null, "No speed=1 sample found.");
            var rate = FieldDouble(sample1x, "detection_rate_pct") ?? -1;
            Assert.True(Math.Abs(rate - 100.0) < 0.01, $"1x detection_rate_pct expected 100; got {rate}");
        }

        [SkippableFact]
        public void Loki_SpeedSweep_1x_MissCountIsZero()
        {
            SkipIfDisabled();
            var sample1x = QueryLines(DataCaptureSuiteConstants.EventSpeedSample)
                .FirstOrDefault(j => FieldInt(j, "requested_speed") == 1);
            Skip.If(sample1x == null, "No speed=1 sample found.");
            Assert.Equal(0, FieldInt(sample1x, "ground_truth_miss_count") ?? -1);
        }

        [SkippableFact]
        public void Loki_SpeedSweep_TickCountDecreasesWithSpeed()
        {
            SkipIfDisabled();
            var samples = QueryLines(DataCaptureSuiteConstants.EventSpeedSample)
                .OrderBy(j => FieldInt(j, "requested_speed") ?? 0).ToList();
            Skip.If(samples.Count < 4, "Need 4 speed samples.");
            for (int i = 0; i < samples.Count - 1; i++)
            {
                var tc1 = FieldInt(samples[i], "tick_count") ?? 0;
                var tc2 = FieldInt(samples[i + 1], "tick_count") ?? 0;
                Assert.True(tc1 > tc2,
                    $"tick_count should decrease with speed; got tick_count[{i}]={tc1} <= tick_count[{i+1}]={tc2}");
            }
        }

        [SkippableFact]
        public void Loki_SpeedSweep_AllSpeedsMatchPlanConstants()
        {
            SkipIfDisabled();
            var expected = new HashSet<int>(DataCaptureSuiteConstants.SpeedSweepSpeeds);
            foreach (var j in QueryLines(DataCaptureSuiteConstants.EventSpeedSample))
            {
                var s = FieldInt(j, "requested_speed") ?? -1;
                Assert.True(expected.Contains(s),
                    $"requested_speed={s} is not in DataCaptureSuiteConstants.SpeedSweepSpeeds.");
            }
        }

        // ── T2 — Variable Inventory ──────────────────────────────────────────────

        [SkippableFact]
        public void Loki_VariableInventory_IsPresent()
        {
            SkipIfDisabled();
            Assert.True(QueryLines(DataCaptureSuiteConstants.EventVariableInventory).Count >= 1,
                "sdk_capture_variable_inventory not found in Loki.");
        }

        [SkippableFact]
        public void Loki_VariableInventory_CountAboveThreshold()
        {
            SkipIfDisabled();
            var lines = QueryLines(DataCaptureSuiteConstants.EventVariableInventory);
            Skip.If(lines.Count == 0, "No variable inventory event.");
            Assert.True((FieldInt(lines[0], "variable_count") ?? 0) > 50,
                $"variable_count expected > 50; got {Field(lines[0], "variable_count")}");
        }

        [SkippableFact]
        public void Loki_VariableInventory_KnownVarsPresent()
        {
            SkipIfDisabled();
            var lines = QueryLines(DataCaptureSuiteConstants.EventVariableInventory);
            Skip.If(lines.Count == 0, "No variable inventory event.");
            var names = FieldArray(lines[0], "variable_names");
            foreach (var expected in new[] { "Speed", "RPM", "ReplayFrameNum" })
                Assert.True(names.Contains(expected),
                    $"Expected SDK variable '{expected}' in variable_names; got: {string.Join(", ", names.Take(20))}");
        }

        // ── T3 — Player Snapshot ─────────────────────────────────────────────────

        [SkippableFact]
        public void Loki_PlayerSnapshot_IsPresent()
        {
            SkipIfDisabled();
            Assert.True(QueryLines(DataCaptureSuiteConstants.EventPlayerSnapshot).Count >= 1,
                "sdk_capture_player_snapshot not found in Loki.");
        }

        [SkippableFact]
        public void Loki_PlayerSnapshot_HasSpeedMps()
        {
            SkipIfDisabled();
            var lines = QueryLines(DataCaptureSuiteConstants.EventPlayerSnapshot);
            Skip.If(lines.Count == 0, "No player snapshot event.");
            Assert.NotNull(FieldDouble(lines[0], "speed_mps"));
        }

        [SkippableFact]
        public void Loki_PlayerSnapshot_SpeedMpsNonNegative()
        {
            SkipIfDisabled();
            var lines = QueryLines(DataCaptureSuiteConstants.EventPlayerSnapshot);
            Skip.If(lines.Count == 0, "No player snapshot event.");
            Assert.True((FieldDouble(lines[0], "speed_mps") ?? -1) >= 0,
                "speed_mps must be >= 0.");
        }

        [SkippableFact]
        public void Loki_PlayerSnapshot_HasGear()
        {
            SkipIfDisabled();
            var lines = QueryLines(DataCaptureSuiteConstants.EventPlayerSnapshot);
            Skip.If(lines.Count == 0, "No player snapshot event.");
            Assert.NotNull(FieldInt(lines[0], "gear"));
        }

        [SkippableFact]
        public void Loki_PlayerSnapshot_HasRpm()
        {
            SkipIfDisabled();
            var lines = QueryLines(DataCaptureSuiteConstants.EventPlayerSnapshot);
            Skip.If(lines.Count == 0, "No player snapshot event.");
            Assert.NotNull(FieldDouble(lines[0], "rpm"));
        }

        [SkippableFact]
        public void Loki_PlayerSnapshot_LapDistPctInRange()
        {
            SkipIfDisabled();
            var lines = QueryLines(DataCaptureSuiteConstants.EventPlayerSnapshot);
            Skip.If(lines.Count == 0, "No player snapshot event.");
            var v = FieldDouble(lines[0], "lap_dist_pct") ?? -1;
            Assert.InRange(v, 0.0, 1.0);
        }

        // ── T4 — Driver Roster ───────────────────────────────────────────────────

        [SkippableFact]
        public void Loki_DriverRoster_IsPresent()
        {
            SkipIfDisabled();
            Assert.True(QueryLines(DataCaptureSuiteConstants.EventDriverRoster).Count >= 1,
                "sdk_capture_driver_roster not found in Loki.");
        }

        [SkippableFact]
        public void Loki_DriverRoster_DriverCountAtLeastTwo()
        {
            SkipIfDisabled();
            var lines = QueryLines(DataCaptureSuiteConstants.EventDriverRoster);
            Skip.If(lines.Count == 0, "No driver roster event.");
            Assert.True((FieldInt(lines[0], "driver_count") ?? 0) >= 2,
                $"driver_count expected >= 2; got {Field(lines[0], "driver_count")}");
        }

        [SkippableFact]
        public void Loki_DriverRoster_ContainsT0CarIdx0()
        {
            SkipIfDisabled();
            var roster = QueryLines(DataCaptureSuiteConstants.EventDriverRoster);
            Skip.If(roster.Count == 0 || T0ByIndex(0) == null, "Need roster and T0[0].");
            var carIdxes = FieldArray(roster[0], "car_idxes");
            Assert.Contains(Field(T0ByIndex(0), "car_idx"), carIdxes);
        }

        [SkippableFact]
        public void Loki_DriverRoster_ContainsT0CarIdx1()
        {
            SkipIfDisabled();
            var roster = QueryLines(DataCaptureSuiteConstants.EventDriverRoster);
            Skip.If(roster.Count == 0 || T0ByIndex(1) == null, "Need roster and T0[1].");
            Assert.Contains(Field(T0ByIndex(1), "car_idx"), FieldArray(roster[0], "car_idxes"));
        }

        [SkippableFact]
        public void Loki_DriverRoster_ContainsT0CarIdx2()
        {
            SkipIfDisabled();
            var roster = QueryLines(DataCaptureSuiteConstants.EventDriverRoster);
            Skip.If(roster.Count == 0 || T0ByIndex(2) == null, "Need roster and T0[2].");
            Assert.Contains(Field(T0ByIndex(2), "car_idx"), FieldArray(roster[0], "car_idxes"));
        }

        // ── T5 — Camera Switch ───────────────────────────────────────────────────

        [SkippableFact]
        public void Loki_CameraSwitch_IsPresent()
        {
            SkipIfDisabled();
            Assert.True(QueryLines(DataCaptureSuiteConstants.EventCameraSwitchDriver).Count >= 1,
                "sdk_capture_camera_switch_driver not found in Loki.");
        }

        [SkippableFact]
        public void Loki_CameraSwitch_CamCarIdxIsValid()
        {
            SkipIfDisabled();
            var lines = QueryLines(DataCaptureSuiteConstants.EventCameraSwitchDriver);
            Skip.If(lines.Count == 0, "No camera switch event.");
            Assert.True((FieldInt(lines[0], "cam_car_idx") ?? -1) >= 0,
                "cam_car_idx must be >= 0.");
        }

        [SkippableFact]
        public void Loki_CameraSwitch_ConfirmedMatchIsTrue()
        {
            SkipIfDisabled();
            var lines = QueryLines(DataCaptureSuiteConstants.EventCameraSwitchDriver);
            Skip.If(lines.Count == 0, "No camera switch event.");
            Assert.Equal("true", Field(lines[0], "confirmed_match"));
        }

        [SkippableFact]
        public void Loki_CameraSwitch_CarIdxMatchesT0Index0()
        {
            SkipIfDisabled();
            var lines = QueryLines(DataCaptureSuiteConstants.EventCameraSwitchDriver);
            Skip.If(lines.Count == 0 || T0ByIndex(0) == null, "Need camera switch and T0[0].");
            Assert.Equal(Field(T0ByIndex(0), "car_idx"), Field(lines[0], "cam_car_idx"));
        }

        [SkippableFact]
        public void Loki_CameraSwitch_GroundTruthIncidentIndexIsZero()
        {
            SkipIfDisabled();
            var lines = QueryLines(DataCaptureSuiteConstants.EventCameraSwitchDriver);
            Skip.If(lines.Count == 0, "No camera switch event.");
            Assert.Equal("0", Field(lines[0], "ground_truth_incident_index"));
        }

        // ── T5b — Camera View Cycle ──────────────────────────────────────────────

        [SkippableFact]
        public void Loki_CameraViewSamples_Present()
        {
            SkipIfDisabled();
            Assert.True(QueryLines(DataCaptureSuiteConstants.EventCameraViewSample).Count >= 1,
                "sdk_capture_camera_view_sample not found in Loki.");
        }

        [SkippableFact]
        public void Loki_CameraViewSamples_GroupNamesVary()
        {
            SkipIfDisabled();
            var names = QueryLines(DataCaptureSuiteConstants.EventCameraViewSample)
                .Select(j => Field(j, "cam_group_name")).Distinct().ToList();
            Assert.True(names.Count >= 2,
                $"Expected >= 2 distinct cam_group_name values; got {names.Count}.");
        }

        [SkippableFact]
        public void Loki_CameraViewSamples_NoDuplicateGroupNums()
        {
            SkipIfDisabled();
            var nums = QueryLines(DataCaptureSuiteConstants.EventCameraViewSample)
                .Select(j => Field(j, "cam_group_num")).ToList();
            Assert.Equal(nums.Count, nums.Distinct().Count());
        }

        [SkippableFact]
        public void Loki_CameraViewSamples_AllHaveGroundTruthIndex0()
        {
            SkipIfDisabled();
            var bad = QueryLines(DataCaptureSuiteConstants.EventCameraViewSample)
                .Where(j => Field(j, "ground_truth_incident_index") != "0").ToList();
            Assert.True(bad.Count == 0,
                $"{bad.Count} camera view samples have ground_truth_incident_index != 0.");
        }

        [SkippableFact]
        public void Loki_CameraViewSamples_CamCarIdxConsistentAcrossSamples()
        {
            SkipIfDisabled();
            var ids = QueryLines(DataCaptureSuiteConstants.EventCameraViewSample)
                .Select(j => Field(j, "cam_car_idx")).Distinct().ToList();
            Assert.True(ids.Count == 1,
                $"Expected consistent cam_car_idx across samples; got {ids.Count} distinct values.");
        }

        [SkippableFact]
        public void Loki_CameraViewSamples_CamCarIdxMatchesT0()
        {
            SkipIfDisabled();
            var samples = QueryLines(DataCaptureSuiteConstants.EventCameraViewSample);
            Skip.If(samples.Count == 0 || T0ByIndex(0) == null, "Need camera view samples and T0[0].");
            Assert.Equal(Field(T0ByIndex(0), "car_idx"), Field(samples[0], "cam_car_idx"));
        }

        [SkippableFact]
        public void Loki_CameraViewSamples_HaveSpeedMpsField()
        {
            SkipIfDisabled();
            var bad = QueryLines(DataCaptureSuiteConstants.EventCameraViewSample)
                .Where(j => Field(j, "speed_mps") == null).ToList();
            Assert.True(bad.Count == 0, $"{bad.Count} camera view samples missing speed_mps.");
        }

        [SkippableFact]
        public void Loki_CameraViewSamples_HaveGearField()
        {
            SkipIfDisabled();
            var bad = QueryLines(DataCaptureSuiteConstants.EventCameraViewSample)
                .Where(j => Field(j, "gear") == null).ToList();
            Assert.True(bad.Count == 0, $"{bad.Count} camera view samples missing gear.");
        }

        [SkippableFact]
        public void Loki_CameraViewSummary_IsPresent()
        {
            SkipIfDisabled();
            Assert.True(QueryLines(DataCaptureSuiteConstants.EventCameraViewSummary).Count >= 1,
                "sdk_capture_camera_view_summary not found in Loki.");
        }

        [SkippableFact]
        public void Loki_CameraViewSummary_GroupsTestedPositive()
        {
            SkipIfDisabled();
            var lines = QueryLines(DataCaptureSuiteConstants.EventCameraViewSummary);
            Skip.If(lines.Count == 0, "No camera view summary.");
            Assert.True((FieldInt(lines[0], "groups_tested") ?? 0) > 0,
                "groups_tested must be > 0.");
        }

        [SkippableFact]
        public void Loki_CameraViewSummary_ConfirmedMatchesPositive()
        {
            SkipIfDisabled();
            var lines = QueryLines(DataCaptureSuiteConstants.EventCameraViewSummary);
            Skip.If(lines.Count == 0, "No camera view summary.");
            Assert.True((FieldInt(lines[0], "confirmed_matches") ?? 0) > 0,
                "confirmed_matches must be > 0.");
        }

        [SkippableFact]
        public void Loki_CameraViewSummary_GroupsTestedMatchesSampleCount()
        {
            SkipIfDisabled();
            var summary = QueryLines(DataCaptureSuiteConstants.EventCameraViewSummary);
            var samples = QueryLines(DataCaptureSuiteConstants.EventCameraViewSample);
            Skip.If(summary.Count == 0, "No camera view summary.");
            Assert.Equal(samples.Count, FieldInt(summary[0], "groups_tested") ?? -1);
        }

        [SkippableFact]
        public void Loki_CameraViewSummary_GroupNamesFieldPresent()
        {
            SkipIfDisabled();
            var lines = QueryLines(DataCaptureSuiteConstants.EventCameraViewSummary);
            Skip.If(lines.Count == 0, "No camera view summary.");
            Assert.True(FieldArray(lines[0], "group_names").Count > 0,
                "group_names field should be non-empty.");
        }

        // ── T6 — Session Results ─────────────────────────────────────────────────

        [SkippableFact]
        public void Loki_SessionResults_IsPresent()
        {
            SkipIfDisabled();
            Assert.True(QueryLines(DataCaptureSuiteConstants.EventSessionResults).Count >= 1,
                "sdk_capture_session_results not found in Loki.");
        }

        [SkippableFact]
        public void Loki_SessionResults_ContainsT0CarIdx0()
        {
            SkipIfDisabled();
            var results = QueryLines(DataCaptureSuiteConstants.EventSessionResults);
            Skip.If(results.Count == 0 || T0ByIndex(0) == null, "Need session results and T0[0].");
            var carIdxes = FieldArray(results[0], "car_idxes")
                .Concat(FieldArray(results[0], "driver_car_idxes")).ToList();
            Assert.Contains(Field(T0ByIndex(0), "car_idx"), carIdxes);
        }

        [SkippableFact]
        public void Loki_SessionResults_ContainsT0CarIdx1()
        {
            SkipIfDisabled();
            var results = QueryLines(DataCaptureSuiteConstants.EventSessionResults);
            Skip.If(results.Count == 0 || T0ByIndex(1) == null, "Need session results and T0[1].");
            var carIdxes = FieldArray(results[0], "car_idxes")
                .Concat(FieldArray(results[0], "driver_car_idxes")).ToList();
            Assert.Contains(Field(T0ByIndex(1), "car_idx"), carIdxes);
        }

        [SkippableFact]
        public void Loki_SessionResults_ContainsT0CarIdx2()
        {
            SkipIfDisabled();
            var results = QueryLines(DataCaptureSuiteConstants.EventSessionResults);
            Skip.If(results.Count == 0 || T0ByIndex(2) == null, "Need session results and T0[2].");
            var carIdxes = FieldArray(results[0], "car_idxes")
                .Concat(FieldArray(results[0], "driver_car_idxes")).ToList();
            Assert.Contains(Field(T0ByIndex(2), "car_idx"), carIdxes);
        }

        // ── T7 — Incident Re-Seek ────────────────────────────────────────────────

        [SkippableFact]
        public void Loki_IncidentReseek_HasThreeEvents()
        {
            SkipIfDisabled();
            Assert.Equal(3, QueryLines(DataCaptureSuiteConstants.EventIncidentReseek).Count);
        }

        [SkippableFact]
        public void Loki_IncidentReseek_IncidentIndexesAreSequential()
        {
            SkipIfDisabled();
            var indexes = QueryLines(DataCaptureSuiteConstants.EventIncidentReseek)
                .Select(j => FieldInt(j, "incident_index")).OrderBy(x => x).ToList();
            Assert.Equal(new int?[] { 0, 1, 2 }, indexes);
        }

        [SkippableFact]
        public void Loki_IncidentReseek_ReplayFramesPositive()
        {
            SkipIfDisabled();
            var bad = QueryLines(DataCaptureSuiteConstants.EventIncidentReseek)
                .Where(j => (FieldInt(j, "replay_frame") ?? -1) <= 0).ToList();
            Assert.True(bad.Count == 0, $"{bad.Count} reseek events have replay_frame <= 0.");
        }

        [SkippableFact]
        public void Loki_IncidentReseek_FrameWithinToleranceOfT0_Index0()
        {
            SkipIfDisabled();
            var reseek = QueryLines(DataCaptureSuiteConstants.EventIncidentReseek)
                .FirstOrDefault(j => FieldInt(j, "incident_index") == 0);
            Skip.If(reseek == null || T0ByIndex(0) == null, "Need reseek[0] and T0[0].");
            var diff = Math.Abs((FieldInt(reseek, "replay_frame") ?? 0) - (FieldInt(T0ByIndex(0), "replay_frame") ?? 0));
            Assert.True(diff <= FrameTolerance,
                $"Reseek[0] frame diff {diff} exceeds tolerance {FrameTolerance}.");
        }

        [SkippableFact]
        public void Loki_IncidentReseek_FrameWithinToleranceOfT0_Index1()
        {
            SkipIfDisabled();
            var reseek = QueryLines(DataCaptureSuiteConstants.EventIncidentReseek)
                .FirstOrDefault(j => FieldInt(j, "incident_index") == 1);
            Skip.If(reseek == null || T0ByIndex(1) == null, "Need reseek[1] and T0[1].");
            var diff = Math.Abs((FieldInt(reseek, "replay_frame") ?? 0) - (FieldInt(T0ByIndex(1), "replay_frame") ?? 0));
            Assert.True(diff <= FrameTolerance,
                $"Reseek[1] frame diff {diff} exceeds tolerance {FrameTolerance}.");
        }

        [SkippableFact]
        public void Loki_IncidentReseek_FrameWithinToleranceOfT0_Index2()
        {
            SkipIfDisabled();
            var reseek = QueryLines(DataCaptureSuiteConstants.EventIncidentReseek)
                .FirstOrDefault(j => FieldInt(j, "incident_index") == 2);
            Skip.If(reseek == null || T0ByIndex(2) == null, "Need reseek[2] and T0[2].");
            var diff = Math.Abs((FieldInt(reseek, "replay_frame") ?? 0) - (FieldInt(T0ByIndex(2), "replay_frame") ?? 0));
            Assert.True(diff <= FrameTolerance,
                $"Reseek[2] frame diff {diff} exceeds tolerance {FrameTolerance}.");
        }

        [SkippableFact]
        public void Loki_IncidentReseek_CarIdxMatchesT0_Index0()
        {
            SkipIfDisabled();
            var reseek = QueryLines(DataCaptureSuiteConstants.EventIncidentReseek)
                .FirstOrDefault(j => FieldInt(j, "incident_index") == 0);
            Skip.If(reseek == null || T0ByIndex(0) == null, "Need reseek[0] and T0[0].");
            Assert.Equal(Field(T0ByIndex(0), "car_idx"), Field(reseek, "car_idx"));
        }

        [SkippableFact]
        public void Loki_IncidentReseek_CarIdxMatchesT0_Index1()
        {
            SkipIfDisabled();
            var reseek = QueryLines(DataCaptureSuiteConstants.EventIncidentReseek)
                .FirstOrDefault(j => FieldInt(j, "incident_index") == 1);
            Skip.If(reseek == null || T0ByIndex(1) == null, "Need reseek[1] and T0[1].");
            Assert.Equal(Field(T0ByIndex(1), "car_idx"), Field(reseek, "car_idx"));
        }

        [SkippableFact]
        public void Loki_IncidentReseek_CarIdxMatchesT0_Index2()
        {
            SkipIfDisabled();
            var reseek = QueryLines(DataCaptureSuiteConstants.EventIncidentReseek)
                .FirstOrDefault(j => FieldInt(j, "incident_index") == 2);
            Skip.If(reseek == null || T0ByIndex(2) == null, "Need reseek[2] and T0[2].");
            Assert.Equal(Field(T0ByIndex(2), "car_idx"), Field(reseek, "car_idx"));
        }

        // ── T8 — FF Sweep ────────────────────────────────────────────────────────

        [SkippableFact]
        public void Loki_FfSweep_IsPresent()
        {
            SkipIfDisabled();
            Assert.True(QueryLines(DataCaptureSuiteConstants.EventFfSweepResult).Count >= 1,
                "sdk_capture_ff_sweep_result not found in Loki.");
        }

        [SkippableFact]
        public void Loki_FfSweep_IncidentsFoundPositive()
        {
            SkipIfDisabled();
            var lines = QueryLines(DataCaptureSuiteConstants.EventFfSweepResult);
            Skip.If(lines.Count == 0, "No FF sweep event.");
            Assert.True((FieldInt(lines[0], "incidents_found_count") ?? 0) > 0,
                "incidents_found_count must be > 0.");
        }

        [SkippableFact]
        public void Loki_FfSweep_ContainsT0CarIdx0()
        {
            SkipIfDisabled();
            var lines = QueryLines(DataCaptureSuiteConstants.EventFfSweepResult);
            Skip.If(lines.Count == 0 || T0ByIndex(0) == null, "Need FF sweep and T0[0].");
            Assert.Contains(Field(T0ByIndex(0), "car_idx"), FieldArray(lines[0], "detected_car_idxes"));
        }

        [SkippableFact]
        public void Loki_FfSweep_ContainsT0CarIdx1()
        {
            SkipIfDisabled();
            var lines = QueryLines(DataCaptureSuiteConstants.EventFfSweepResult);
            Skip.If(lines.Count == 0 || T0ByIndex(1) == null, "Need FF sweep and T0[1].");
            Assert.Contains(Field(T0ByIndex(1), "car_idx"), FieldArray(lines[0], "detected_car_idxes"));
        }

        [SkippableFact]
        public void Loki_FfSweep_ContainsT0CarIdx2()
        {
            SkipIfDisabled();
            var lines = QueryLines(DataCaptureSuiteConstants.EventFfSweepResult);
            Skip.If(lines.Count == 0 || T0ByIndex(2) == null, "Need FF sweep and T0[2].");
            Assert.Contains(Field(T0ByIndex(2), "car_idx"), FieldArray(lines[0], "detected_car_idxes"));
        }

        [SkippableFact]
        public void Loki_FfSweep_BuildCompletedSuccessfully()
        {
            SkipIfDisabled();
            var lines = QueryLines(DataCaptureSuiteConstants.EventFfSweepResult);
            Skip.If(lines.Count == 0, "No FF sweep event.");
            // Accept either build_status="complete" or success="true"
            var status = Field(lines[0], "build_status");
            var success = Field(lines[0], "success");
            Assert.True(
                string.Equals(status, "complete", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(success, "true", StringComparison.OrdinalIgnoreCase),
                $"FF sweep not marked complete: build_status={status}, success={success}");
        }

        // ── Cross-Test Consistency ───────────────────────────────────────────────

        [SkippableFact]
        public void Loki_CrossTest_T0AndT7AllThreeFramesWithinTolerance()
        {
            SkipIfDisabled();
            var reseeks = QueryLines(DataCaptureSuiteConstants.EventIncidentReseek)
                .ToDictionary(j => FieldInt(j, "incident_index") ?? -1, j => j);
            Skip.If(GetT0().Count < 3 || reseeks.Count < 3, "Need 3 T0 and 3 reseek events.");
            for (int i = 0; i < 3; i++)
            {
                if (!reseeks.TryGetValue(i, out var r) || T0ByIndex(i) == null) continue;
                var diff = Math.Abs((FieldInt(r, "replay_frame") ?? 0) - (FieldInt(T0ByIndex(i), "replay_frame") ?? 0));
                Assert.True(diff <= FrameTolerance,
                    $"Cross-test: incident {i} reseek frame diff {diff} > tolerance {FrameTolerance}.");
            }
        }

        [SkippableFact]
        public void Loki_CrossTest_T5CamCarIdxMatchesT0Index0()
        {
            SkipIfDisabled();
            var camSwitch = QueryLines(DataCaptureSuiteConstants.EventCameraSwitchDriver);
            Skip.If(camSwitch.Count == 0 || T0ByIndex(0) == null, "Need T5 and T0[0].");
            Assert.Equal(Field(T0ByIndex(0), "car_idx"), Field(camSwitch[0], "cam_car_idx"));
        }

        [SkippableFact]
        public void Loki_CrossTest_T8ContainsAllT0Cars()
        {
            SkipIfDisabled();
            var sweep = QueryLines(DataCaptureSuiteConstants.EventFfSweepResult);
            Skip.If(sweep.Count == 0 || GetT0().Count < 3, "Need T8 and T0.");
            var detected = FieldArray(sweep[0], "detected_car_idxes");
            for (int i = 0; i < 3; i++)
            {
                var t0 = T0ByIndex(i);
                Skip.If(t0 == null, $"No T0[{i}].");
                Assert.Contains(Field(t0, "car_idx"), detected);
            }
        }

        [SkippableFact]
        public void Loki_CrossTest_SpeedSweepCountMatchesPlanSpeeds()
        {
            SkipIfDisabled();
            Assert.Equal(
                DataCaptureSuiteConstants.SpeedSweepSpeeds.Length,
                QueryLines(DataCaptureSuiteConstants.EventSpeedSample).Count);
        }

        [SkippableFact]
        public void Loki_CrossTest_T4RosterContainsAllT0Cars()
        {
            SkipIfDisabled();
            var roster = QueryLines(DataCaptureSuiteConstants.EventDriverRoster);
            Skip.If(roster.Count == 0 || GetT0().Count < 3, "Need T4 and T0.");
            var carIdxes = FieldArray(roster[0], "car_idxes");
            for (int i = 0; i < 3; i++)
            {
                var t0 = T0ByIndex(i);
                Skip.If(t0 == null, $"No T0[{i}].");
                Assert.Contains(Field(t0, "car_idx"), carIdxes);
            }
        }
    }
}
