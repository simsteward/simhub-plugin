using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace SimSteward.Plugin
{
    public enum DataCaptureSuitePhase { Idle, Running, AwaitingLoki, Complete, Cancelled }

    /// <summary>A single ground-truth incident captured at 1x speed during T0.</summary>
    public class GroundTruthIncident
    {
        public int IncidentIndex { get; set; }
        public int CarIdx { get; set; }
        public int ReplayFrameNum { get; set; }
        public double ReplaySessionTimeSec { get; set; }
        public int[] CarIdxSessionFlagsSnapshot { get; set; }
        public string DriverName { get; set; }
        public string CarNumber { get; set; }
        public string CustId { get; set; }
        public float LapDistPct { get; set; }
        public int LapNum { get; set; }
    }

    /// <summary>Per-test result for dashboard display and Loki verification.</summary>
    public class DataCaptureSuiteTestResult
    {
        [JsonProperty("testId")]    public string TestId    { get; set; }
        [JsonProperty("name")]      public string Name      { get; set; }
        /// <summary>pending / emitted / found / pass / fail / skip</summary>
        [JsonProperty("status")]    public string Status    { get; set; } = "pending";
        [JsonProperty("eventName")] public string EventName { get; set; }
        [JsonProperty("kpiLabel")]  public string KpiLabel  { get; set; }
        [JsonProperty("kpiValue")]  public string KpiValue  { get; set; }
        [JsonProperty("error")]     public string Error     { get; set; }
        [JsonProperty("lokiCount")] public int    LokiCount { get; set; }
        [JsonProperty("grafanaEventUrl")] public string GrafanaEventUrl { get; set; }
    }

    /// <summary>Summary of a selected ground-truth incident for dashboard display.</summary>
    public class SelectedIncidentSummary
    {
        [JsonProperty("index")]      public int    Index      { get; set; }
        [JsonProperty("frame")]      public int    Frame      { get; set; }
        [JsonProperty("lap")]        public int    Lap        { get; set; }
        [JsonProperty("driverName")] public string DriverName { get; set; }
        [JsonProperty("carNumber")]  public string CarNumber  { get; set; }
        [JsonProperty("custId")]     public string CustId     { get; set; }
        /// <summary>"different_lap", "first_available", or "fallback"</summary>
        [JsonProperty("reason")]     public string Reason     { get; set; }
    }

    /// <summary>Snapshot broadcast in <c>state.dataCaptureSuite</c> (WebSocket).</summary>
    public class DataCaptureSuiteSnapshot
    {
        [JsonProperty("phase")]           public string Phase           { get; set; } = "idle";
        [JsonProperty("testRunId")]       public string TestRunId       { get; set; }
        [JsonProperty("currentStep")]     public int    CurrentStep     { get; set; }
        [JsonProperty("totalSteps")]      public int    TotalSteps      { get; set; } = 10;
        [JsonProperty("currentStepName")] public string CurrentStepName { get; set; }
        [JsonProperty("elapsedMs")]       public long   ElapsedMs       { get; set; }
        [JsonProperty("testResults")]     public DataCaptureSuiteTestResult[] TestResults { get; set; }
        [JsonProperty("grafanaExploreUrl")] public string GrafanaExploreUrl { get; set; }
        [JsonProperty("selectedIncidents")] public SelectedIncidentSummary[] SelectedIncidents { get; set; }
    }

    /// <summary>Constants shared between the state machine and unit tests.</summary>
    public static class DataCaptureSuiteConstants
    {
        public const int LokiVerifyDelayMs       = 15_000;
        public static readonly int[] SpeedSweepSpeeds = { 1, 4, 8, 16 };
        /// <summary>Frames beyond the last GT incident to advance during T1 sweep.</summary>
        public const int SpeedSweepAdvanceFrames = 300;
        /// <summary>
        /// Combined flag mask used to detect incident rising edges during the T1 speed sweep.
        /// Matches <see cref="ReplayIncidentIndexDetection.FurledSessionFlag"/> |
        /// <see cref="ReplayIncidentIndexDetection.RepairSessionFlag"/>.
        /// </summary>
        public const int IncidentFlagMask = 0x80000 | 0x100000;   // furled | repair
        /// <summary>Ticks to wait after ReplaySearch(NextIncident) before reading frame/car data (~2.5 s at 60 Hz).</summary>
        public const int NextIncidentCooldownTicks = 150;
        /// <summary>Ticks to wait after CamSwitchPos before reading CamCarIdx (~1 s at 60 Hz).</summary>
        public const int CamSettleTicks = 60;
        /// <summary>Consecutive ticks with ReplayFrameNum ≤ 2 required to confirm frame-zero stability.</summary>
        public const int FrameZeroStableTicks = 4;
        /// <summary>Max ticks to wait for frame-zero before giving up.</summary>
        public const int SeekTimeoutTicks = 600;

        // ── T0 scan/select constants ──────────────────────────────────────────
        /// <summary>Max NextIncident calls during the T0 incident scan pass.</summary>
        public const int T0_ScanMaxIncidents    = 30;
        /// <summary>Skip incidents on laps ≤ this when possible (avoid first-lap drama).</summary>
        public const int T0_MinLapForSelection  = 1;
        /// <summary>Frame tolerance for seek-settle during T0 capture pass.</summary>
        public const int T0_SeekSettleTolerance = 30;

        // ── Structured log event names ──────────────────────────────────────────
        public const string EventGroundTruth        = "sdk_capture_ground_truth_incident";
        public const string EventSpeedSample        = "sdk_capture_speed_sample";
        public const string EventVariableInventory  = "sdk_capture_variable_inventory";
        public const string EventPlayerSnapshot     = "sdk_capture_player_snapshot";
        public const string EventDriverRoster       = "sdk_capture_driver_roster";
        public const string EventCameraSwitchDriver = "sdk_capture_camera_switch_driver";
        public const string EventCameraViewSample   = "sdk_capture_camera_view_sample";
        public const string EventCameraViewSummary  = "sdk_capture_camera_view_summary";
        public const string EventSessionResults     = "sdk_capture_session_results";
        public const string EventIncidentReseek     = "sdk_capture_incident_reseek";
        public const string EventFfSweepResult      = "sdk_capture_ff_sweep_result";
        public const string EventSuiteStarted       = "sdk_capture_suite_started";
        public const string EventSuiteComplete      = "sdk_capture_suite_complete";
        public const string EventDataDiscovery      = "sdk_capture_data_discovery";
        public const string Event60HzSummary        = "sdk_capture_60hz_summary";
        public const string EventPreflightCheck     = "sdk_capture_preflight_check";
        public const string EventPreflightProbe     = "sdk_capture_preflight_probe";
    }

    /// <summary>T0 incident selection algorithm — testable outside SIMHUB_SDK.</summary>
    public static class DataCaptureSuiteSelection
    {
        /// <summary>
        /// Selects up to 3 incident frames from candidates, preferring different laps and skipping lap 1.
        /// </summary>
        public static int[] SelectGroundTruthFrames(List<(int frame, int lap, int carIdx)> candidates)
        {
            var result = new List<int>();
            var usedLaps = new HashSet<int>();

            // Pass 1: different laps, lap > T0_MinLapForSelection
            foreach (var c in candidates.Where(c => c.lap > DataCaptureSuiteConstants.T0_MinLapForSelection).OrderBy(c => c.frame))
            {
                if (!usedLaps.Contains(c.lap))
                {
                    result.Add(c.frame);
                    usedLaps.Add(c.lap);
                    if (result.Count == 3) return result.ToArray();
                }
            }

            // Pass 2: fill from any remaining candidates
            foreach (var c in candidates.OrderBy(c => c.frame))
            {
                if (result.Count >= 3) break;
                if (!result.Contains(c.frame)) result.Add(c.frame);
            }

            return result.Take(3).ToArray();
        }
    }
}
