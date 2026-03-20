using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using SimSteward.Plugin;

namespace SimSteward.Plugin.MemoryBank
{
    /// <summary>
    /// Incident detection counters for the current plugin session.
    /// Resets when iRacing disconnects.
    /// </summary>
    public class DetectionMetrics
    {
        /// <summary>YAML CurDriverIncidentCount deltas (all drivers).</summary>
        [JsonProperty("yamlIncidentEvents")]
        public int YamlIncidentEvents { get; set; }

        /// <summary>All incidents added to the feed.</summary>
        [JsonProperty("totalEvents")]
        public int TotalEvents { get; set; }

        /// <summary>Number of times session YAML was parsed (SessionInfoUpdate ticks).</summary>
        [JsonProperty("yamlUpdates")]
        public int YamlUpdates { get; set; }

        /// <summary>Session time of the most recently detected incident, or -1 if none yet.</summary>
        [JsonProperty("lastDetectionSessionTime")]
        public double LastDetectionSessionTime { get; set; } = -1;
    }

    /// <summary>
    /// Snapshot of plugin subsystem health broadcast with every state message.
    /// Gives the dashboard (and AI memory bank) real-time visibility into what
    /// is running and what is not.
    /// </summary>
    public class PluginDiagnostics
    {
        /// <summary>IRSDKSharper was constructed and started without exception.</summary>
        [JsonProperty("irsdkStarted")]
        public bool IrsdkStarted { get; set; }

        /// <summary>iRacing shared memory is currently connected (IRacingSdk.IsConnected).</summary>
        [JsonProperty("irsdkConnected")]
        public bool IrsdkConnected { get; set; }

        /// <summary>Fleck WebSocket server is running and accepting connections.</summary>
        [JsonProperty("wsRunning")]
        public bool WsRunning { get; set; }

        /// <summary>TCP port the WebSocket server is bound to.</summary>
        [JsonProperty("wsPort")]
        public int WsPort { get; set; }

        /// <summary>Number of currently connected WebSocket dashboard clients.</summary>
        [JsonProperty("wsClients")]
        public int WsClients { get; set; }

        /// <summary>MemoryBankClient initialised and able to write files.</summary>
        [JsonProperty("memoryBankAvailable")]
        public bool MemoryBankAvailable { get; set; }

        /// <summary>Absolute path the memory bank is writing to.</summary>
        [JsonProperty("memoryBankPath")]
        public string MemoryBankPath { get; set; }

        /// <summary>Player car index from iRacing; -1 means not yet known.</summary>
        [JsonProperty("playerCarIdx")]
        public int PlayerCarIdx { get; set; } = -1;
    }

    public class MemoryBankSnapshot
    {
        [JsonProperty("timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        [JsonProperty("pluginMode")]
        public string PluginMode { get; set; } = "Unknown";

        [JsonProperty("currentSessionTime")]
        public double CurrentSessionTime { get; set; }

        [JsonProperty("currentSessionTimeFormatted")]
        public string CurrentSessionTimeFormatted { get; set; } = "0:00";

        [JsonProperty("replayIsPlaying")]
        public bool ReplayIsPlaying { get; set; }

        [JsonProperty("replayFrameNum")]
        public int ReplayFrameNum { get; set; }

        [JsonProperty("replayFrameNumEnd")]
        public int ReplayFrameNumEnd { get; set; }

        [JsonProperty("replayPlaySpeed")]
        public int ReplayPlaySpeed { get; set; }

        [JsonProperty("replayPlaySlowMotion")]
        public bool ReplayPlaySlowMotion { get; set; }

        [JsonProperty("replaySessionNum")]
        public int ReplaySessionNum { get; set; }

        [JsonProperty("playerCarIdx")]
        public int PlayerCarIdx { get; set; } = -1;

        [JsonProperty("playerIncidentCount")]
        public int PlayerIncidentCount { get; set; }

        [JsonProperty("drivers")]
        public List<DriverRecord> Drivers { get; set; } = new List<DriverRecord>();

        [JsonProperty("incidents")]
        public List<IncidentEvent> Incidents { get; set; } = new List<IncidentEvent>();

        [JsonProperty("hasLiveIncidentData")]
        public bool HasLiveIncidentData { get; set; }

        [JsonProperty("trackName")]
        public string TrackName { get; set; } = "";

        [JsonProperty("trackCategory")]
        public string TrackCategory { get; set; } = "Road";

        [JsonProperty("trackLengthM")]
        public float TrackLengthM { get; set; }

        [JsonProperty("metrics")]
        public DetectionMetrics Metrics { get; set; } = new DetectionMetrics();

        [JsonProperty("diagnostics")]
        public PluginDiagnostics Diagnostics { get; set; } = new PluginDiagnostics();

        [JsonProperty("projectMarkers")]
        public ProjectMarkers ProjectMarkers { get; set; } = new ProjectMarkers();
    }

    public class ProjectMarkers
    {
        [JsonProperty("currentTaskId")]
        public string CurrentTaskId { get; set; }

        [JsonProperty("currentTaskDescription")]
        public string CurrentTaskDescription { get; set; }

        [JsonProperty("complexityLevel")]
        public string ComplexityLevel { get; set; }

        [JsonProperty("lastAction")]
        public string LastAction { get; set; }

        [JsonProperty("lastActionTimestamp")]
        public DateTime? LastActionTimestamp { get; set; }

        [JsonProperty("notes")]
        public string Notes { get; set; }
    }
}
