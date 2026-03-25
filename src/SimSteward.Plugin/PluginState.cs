using Newtonsoft.Json;

namespace SimSteward.Plugin
{
    /// <summary>Subsystem health and dependency status broadcast with state messages.</summary>
    public class PluginDiagnostics
    {
        [JsonProperty("irsdkStarted")]
        public bool IrsdkStarted { get; set; }

        [JsonProperty("irsdkConnected")]
        public bool IrsdkConnected { get; set; }

        [JsonProperty("wsRunning")]
        public bool WsRunning { get; set; }

        [JsonProperty("wsPort")]
        public int WsPort { get; set; }

        [JsonProperty("wsClients")]
        public int WsClients { get; set; }

        [JsonProperty("steamRunning")]
        public bool SteamRunning { get; set; }

        [JsonProperty("simHubHttpListening")]
        public bool SimHubHttpListening { get; set; }

        [JsonProperty("dashboardPing")]
        public string DashboardPing { get; set; } = "—";

        /// <summary>Seconds since last <c>host_resource_sample</c>; <c>-1</c> if none yet.</summary>
        [JsonProperty("resourceSampleAgeSec")]
        public double ResourceSampleAgeSec { get; set; } = -1;

        /// <summary>SimHub process CPU % (all logical processors), last sample.</summary>
        [JsonProperty("processCpuPct")]
        public double ProcessCpuPct { get; set; }

        [JsonProperty("processWorkingSetMb")]
        public double ProcessWorkingSetMb { get; set; }

        [JsonProperty("processPrivateMb")]
        public double ProcessPrivateMb { get; set; }

        [JsonProperty("gcHeapMb")]
        public double GcHeapMb { get; set; }

        [JsonProperty("diskRoot")]
        public string DiskRoot { get; set; } = "";

        [JsonProperty("diskUsedPct")]
        public double DiskUsedPct { get; set; }

        [JsonProperty("diskFreeGb")]
        public double DiskFreeGb { get; set; }

        /// <summary>True when <c>SIMSTEWARD_LOKI_URL</c> env var is configured (Grafana/Loki reachable).</summary>
        [JsonProperty("grafanaConfigured")]
        public bool GrafanaConfigured { get; set; }

        /// <summary>True if any race session in the loaded replay has reached Checkered or CoolDown state.</summary>
        [JsonProperty("replaySessionCompleted")]
        public bool ReplaySessionCompleted { get; set; }
    }

    /// <summary>Minimal snapshot for WebSocket state push.</summary>
    public class PluginSnapshot
    {
        /// <summary>Plugin build id (semver + git from AssemblyInformationalVersion).</summary>
        [JsonProperty("pluginVersion")]
        public string PluginVersion { get; set; } = "";

        [JsonProperty("pluginMode")]
        public string PluginMode { get; set; } = "Unknown";

        [JsonProperty("currentSessionTime")]
        public double CurrentSessionTime { get; set; }

        [JsonProperty("currentSessionTimeFormatted")]
        public string CurrentSessionTimeFormatted { get; set; } = "0:00";

        /// <summary>CarIdxLap for focus car; <see cref="SessionLogging.LapUnknown"/> if unknown.</summary>
        [JsonProperty("lap")]
        public int Lap { get; set; } = SessionLogging.LapUnknown;

        /// <summary>Current replay frame (<c>ReplayFrameNum</c>).</summary>
        [JsonProperty("frame")]
        public int Frame { get; set; }

        /// <summary>Replay end frame (<c>ReplayFrameNumEnd</c>); 0 if unknown.</summary>
        [JsonProperty("frameEnd")]
        public int FrameEnd { get; set; }

        /// <summary>Number of sessions in weekend session list; 0 if unknown.</summary>
        [JsonProperty("replaySessionCount")]
        public int ReplaySessionCount { get; set; }

        /// <summary>Current <c>SessionNum</c> from telemetry.</summary>
        [JsonProperty("replaySessionNum")]
        public int ReplaySessionNum { get; set; }

        /// <summary>Display name for current session from YAML; "—" if unknown.</summary>
        [JsonProperty("replaySessionName")]
        public string ReplaySessionName { get; set; } = "—";

        [JsonProperty("diagnostics")]
        public PluginDiagnostics Diagnostics { get; set; } = new PluginDiagnostics();

        /// <summary>M6 dashboard: replay incident index build status and last TR-019 index for current subsession.</summary>
        [JsonProperty("replayIncidentIndex")]
        public ReplayIncidentIndexDashboardSnapshot ReplayIncidentIndex { get; set; }

        /// <summary>Data capture suite state (test harness).</summary>
        [JsonProperty("dataCaptureSuite")]
        public DataCaptureSuiteSnapshot DataCaptureSuite { get; set; }

        /// <summary>Active preflight check result (seek-to-end, Grafana ping, etc.).</summary>
        [JsonProperty("preflight")]
        public PreflightSnapshot Preflight { get; set; }
    }

    /// <summary>A single mini-test within the preflight check.</summary>
    public class PreflightMiniTest
    {
        [JsonProperty("id")]     public string Id     { get; set; }
        [JsonProperty("name")]   public string Name   { get; set; }
        [JsonProperty("status")] public string Status { get; set; } = "pending"; // pending, running, pass, fail, skip
        [JsonProperty("detail")] public string Detail { get; set; }
        [JsonProperty("level")]  public int    Level  { get; set; }
    }

    /// <summary>Session info extracted from replay YAML during preflight.</summary>
    public class PreflightSessionInfo
    {
        [JsonProperty("sessionNum")]  public int    SessionNum  { get; set; }
        [JsonProperty("sessionName")] public string SessionName { get; set; }
        [JsonProperty("sessionType")] public string SessionType { get; set; }
        [JsonProperty("sessionState")] public string SessionState { get; set; }
        [JsonProperty("resultsOfficial")] public bool ResultsOfficial { get; set; }
    }

    /// <summary>Result of the active preflight check triggered by the "Pre-test conditions" button.</summary>
    public class PreflightSnapshot
    {
        [JsonProperty("phase")]
        public string Phase { get; set; } = "idle";

        [JsonProperty("level")]
        public int Level { get; set; }

        [JsonProperty("replayScope")]
        public string ReplayScope { get; set; } = "full";

        [JsonProperty("correlationId")]
        public string CorrelationId { get; set; }

        [JsonProperty("allPassed")]
        public bool AllPassed { get; set; }

        [JsonProperty("miniTests")]
        public PreflightMiniTest[] MiniTests { get; set; }

        // Legacy flat fields — kept for backward compat
        [JsonProperty("grafanaOk")]
        public bool GrafanaOk { get; set; }

        [JsonProperty("simHubOk")]
        public bool SimHubOk { get; set; }

        [JsonProperty("checkeredOk")]
        public bool CheckeredOk { get; set; }

        [JsonProperty("sessionStateAtEnd")]
        public int SessionStateAtEnd { get; set; }

        [JsonProperty("resultsPopulated")]
        public bool ResultsPopulated { get; set; }

        [JsonProperty("error")]
        public string Error { get; set; }

        /// <summary>Sessions found in the replay YAML (populated during L1 check).</summary>
        [JsonProperty("sessions")]
        public PreflightSessionInfo[] Sessions { get; set; }

        /// <summary>Total replay frame count at time of preflight.</summary>
        [JsonProperty("replayFrameTotal")]
        public int ReplayFrameTotal { get; set; }
    }

    /// <summary>WebSocket <c>state.replayIncidentIndex</c> (TR-031–TR-033, TR-037–TR-038).</summary>
    public sealed class ReplayIncidentIndexDashboardSnapshot
    {
        [JsonProperty("phase")]
        public string Phase { get; set; } = "idle";

        [JsonProperty("buildElapsedMs")]
        public long BuildElapsedMs { get; set; }

        [JsonProperty("replaySessionTime")]
        public double ReplaySessionTime { get; set; }

        [JsonProperty("replayFrameNum")]
        public int ReplayFrameNum { get; set; }

        [JsonProperty("replayFrameEnd")]
        public int ReplayFrameEnd { get; set; }

        [JsonProperty("sessionNum")]
        public int SessionNum { get; set; }

        [JsonProperty("subSessionId")]
        public int SubSessionId { get; set; }

        [JsonProperty("isReplayMode")]
        public bool IsReplayMode { get; set; }

        [JsonProperty("irsdkConnected")]
        public bool IrsdkConnected { get; set; }

        [JsonProperty("recordMode")]
        public bool RecordMode { get; set; }

        [JsonProperty("recordSamplePath")]
        public string RecordSamplePath { get; set; }

        [JsonProperty("index")]
        public ReplayIncidentIndexFileRoot Index { get; set; }
    }
}
