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
    }

    /// <summary>Minimal snapshot for WebSocket state push.</summary>
    public class PluginSnapshot
    {
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
    }
}
