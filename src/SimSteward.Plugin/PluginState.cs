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

        [JsonProperty("diagnostics")]
        public PluginDiagnostics Diagnostics { get; set; } = new PluginDiagnostics();
    }
}
