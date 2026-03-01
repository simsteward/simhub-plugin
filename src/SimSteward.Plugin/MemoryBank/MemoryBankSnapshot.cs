using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using SimSteward.Plugin;

namespace SimSteward.Plugin.MemoryBank
{
    /// <summary>
    /// Per-layer incident detection counters for the current plugin session.
    /// Resets when iRacing disconnects. Lets you see exactly which layers are
    /// firing and how many events each has produced.
    /// </summary>
    public class DetectionMetrics
    {
        /// <summary>Layer 1 — PlayerCarMyIncidentCount telemetry (player/focused car, 60 Hz, exact type).</summary>
        [JsonProperty("l1PlayerEvents")]
        public int L1PlayerEvents { get; set; }

        /// <summary>Layer 2 — CarIdxLapDistPct velocity → G-force (all cars, physics impact).</summary>
        [JsonProperty("l2PhysicsImpacts")]
        public int L2PhysicsImpacts { get; set; }

        /// <summary>Layer 3 — CarIdxTrackSurface OnTrack→OffTrack transitions (all cars).</summary>
        [JsonProperty("l3OffTrackEvents")]
        public int L3OffTrackEvents { get; set; }

        /// <summary>Layer 4 — Session YAML ResultsPositions.Incidents deltas (all drivers, batched).</summary>
        [JsonProperty("l4YamlEvents")]
        public int L4YamlEvents { get; set; }

        /// <summary>0x — Player contact or off-track detected by physics but no official count change.</summary>
        [JsonProperty("zeroXEvents")]
        public int ZeroXEvents { get; set; }

        /// <summary>All incidents added to the feed across all layers.</summary>
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
