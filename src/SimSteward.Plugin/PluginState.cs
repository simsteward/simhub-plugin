using System.Collections.Generic;
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

        /// <summary>True when the live (non-replay) field-wide incident detector has a baseline and is actively watching.</summary>
        [JsonProperty("liveDetectorActive")]
        public bool LiveDetectorActive { get; set; }

        /// <summary>Full git SHA of the running build (parsed from the informational version string).</summary>
        [JsonProperty("gitSha")]
        public string GitSha { get; set; } = "";

        /// <summary>UTC timestamp of the plugin DLL on disk — a proxy for build time, not a tracked build-system value.</summary>
        [JsonProperty("buildTimestampUtc")]
        public string BuildTimestampUtc { get; set; } = "";

        [JsonProperty("activeConfig")]
        public PluginActiveConfig ActiveConfig { get; set; } = new PluginActiveConfig();
    }

    /// <summary>Config values in effect for this running plugin instance — for "is this configured the way I think" checks.</summary>
    public sealed class PluginActiveConfig
    {
        /// <summary>Loki endpoint base URL (not a secret — auth is separate Basic-auth credentials, not embedded here); empty if not configured.</summary>
        [JsonProperty("lokiUrl")]
        public string LokiUrl { get; set; } = "";

        [JsonProperty("fastForwardSweepSpeed")]
        public int FastForwardSweepSpeed { get; set; }

        [JsonProperty("incidentFingerprintVersion")]
        public string IncidentFingerprintVersion { get; set; } = "v1";
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

        /// <summary>Live race running order, derived from total distance covered (see <see cref="RaceLeaderboardOrdering"/>), leader first.</summary>
        [JsonProperty("leaderboard")]
        public List<LeaderboardRow> Leaderboard { get; set; } = new List<LeaderboardRow>();
    }

    /// <summary>One row of the live-derived race leaderboard (WS <c>state.leaderboard[]</c>).</summary>
    public sealed class LeaderboardRow
    {
        [JsonProperty("carIdx")]
        public int CarIdx { get; set; }

        [JsonProperty("driverName")]
        public string DriverName { get; set; } = "";

        /// <summary>Live-derived rank (1 = leader), updates every tick — see <see cref="RaceLeaderboardOrdering"/>.</summary>
        [JsonProperty("liveRank")]
        public int LiveRank { get; set; }

        /// <summary>Raw CarIdxPosition — official, but only updates at the S/F line crossing.</summary>
        [JsonProperty("officialPosition")]
        public int OfficialPosition { get; set; }

        [JsonProperty("lap")]
        public int Lap { get; set; }

        [JsonProperty("lapDistPct")]
        public float LapDistPct { get; set; }

        [JsonProperty("lastLapTime")]
        public float LastLapTime { get; set; }

        [JsonProperty("bestLapTime")]
        public float BestLapTime { get; set; }

        /// <summary>
        /// CarIdxEstTime(car) - CarIdxEstTime(liveRank 1). CONFIRMED UNRELIABLE when cars are on
        /// different laps (live-tested 2026-07-19): CarIdxEstTime is time-to-next-S/F-crossing, a
        /// per-lap-cycle value, so subtracting across cars on different laps is not a real time gap.
        /// Do not present this to users as an accurate gap until replaced with a proper
        /// distance-based formula. Field kept for now so downstream code has a value, not omitted
        /// silently to avoid re-deriving the same wrong number twice.
        /// </summary>
        [JsonProperty("gapToLeaderSec")]
        public float GapToLeaderSec { get; set; }
    }

    /// <summary>
    /// One row of the dashboard's "Incidents" tab (WS <c>{type:"incidents", entries:[...]}</c>).
    /// Field names/shape match what index.html's incident renderer already expects (car, driver,
    /// cause, points, time, lap, frame, player) — this was previously never actually broadcast by
    /// any server code, discovered live when the tab showed nothing.
    /// </summary>
    public sealed class LiveIncidentBoardEntry
    {
        [JsonProperty("id")]
        public string Id { get; set; } = "";

        /// <summary>Pre-formatted "m:ss.s" session time — the client displays this directly, no reformatting.</summary>
        [JsonProperty("time")]
        public string Time { get; set; } = "";

        [JsonProperty("car")]
        public int Car { get; set; }

        [JsonProperty("driver")]
        public string Driver { get; set; } = "";

        /// <summary>1/2/4 per CLAUDE.md's incident-point convention (off-track/spin-wall/heavy-contact) — NOT a severity rating. Meaningless when <see cref="PointsResolved"/> is false (0 in that case is a display placeholder, not a confirmed zero).</summary>
        [JsonProperty("points")]
        public int Points { get; set; }

        /// <summary>
        /// True once a real incident-point value has resolved for this incident (player's own car via
        /// PlayerCarMyIncidentCount, or — confirmed empirically — other cars only after the fact via the
        /// Replay Index tab, never live; see docs/IRACING-DATA-AVAILABILITY.md). False means "not yet
        /// known", not "confirmed zero" — the dashboard must render these states differently.
        /// </summary>
        [JsonProperty("pointsResolved")]
        public bool PointsResolved { get; set; }

        /// <summary>One of "off-track" | "spin" | "contact" | "flagged" | "unknown" — matches index.html's `.cause-tag` CSS classes.</summary>
        [JsonProperty("cause")]
        public string Cause { get; set; } = "unknown";

        [JsonProperty("frame")]
        public int Frame { get; set; }

        [JsonProperty("lap")]
        public int Lap { get; set; }

        [JsonProperty("player")]
        public bool Player { get; set; }
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

        [JsonProperty("cursorIdx")]
        public int CursorIdx { get; set; } = -1;

        [JsonProperty("incidentCount")]
        public int IncidentCount { get; set; }

        [JsonProperty("autoWalkActive")]
        public bool AutoWalkActive { get; set; }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Test rig — replay_state_tick / replay_sweep_progress_tick wire payloads
    // (docs/RULES-TestRig-Contract.md). Field names use snake_case to match the
    // existing dashboard contract for these channels.
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Root payload for <c>replay_state_tick</c> (250 ms cadence).</summary>
    public sealed class ReplayStateTickPayload
    {
        [JsonProperty("type")]
        public string Type { get; set; } = "replay_state_tick";

        [JsonProperty("ts")]
        public string Ts { get; set; } = "";

        [JsonProperty("frame")]
        public int Frame { get; set; }

        [JsonProperty("frame_end")]
        public int FrameEnd { get; set; }

        [JsonProperty("session_time")]
        public double SessionTime { get; set; }

        [JsonProperty("paused")]
        public bool Paused { get; set; }

        [JsonProperty("speed")]
        public int Speed { get; set; }

        [JsonProperty("direction")]
        public string Direction { get; set; } = "forward";

        [JsonProperty("slow_motion")]
        public bool SlowMotion { get; set; }

        [JsonProperty("aggregates")]
        public ReplayStateAggregates Aggregates { get; set; } = new ReplayStateAggregates();

        [JsonProperty("drivers")]
        public List<ReplayStateDriverRow> Drivers { get; set; } = new List<ReplayStateDriverRow>();

        [JsonProperty("misfire")]
        public ReplayStateLastJump Misfire { get; set; } = new ReplayStateLastJump();
    }

    /// <summary>Wraps the two aggregate buckets (<c>ours</c> and <c>yaml</c>).</summary>
    public sealed class ReplayStateAggregates
    {
        [JsonProperty("ours")]
        public ReplayStateAggregateBucket Ours { get; set; } = new ReplayStateAggregateBucket();

        [JsonProperty("yaml")]
        public ReplayStateAggregateBucket Yaml { get; set; } = new ReplayStateAggregateBucket();
    }

    /// <summary>Counters bucket for <c>aggregates.ours</c> / <c>aggregates.yaml</c>.</summary>
    /// <remarks>Every field is nullable so the YAML-in-progress rule can emit explicit nulls
    /// (which the dashboard renders as "—" instead of misleading "0").</remarks>
    public sealed class ReplayStateAggregateBucket
    {
        [JsonProperty("incidents")]
        public int? Incidents { get; set; }

        [JsonProperty("points")]
        public int? Points { get; set; }

        [JsonProperty("off_tracks")]
        public int? OffTracks { get; set; }

        [JsonProperty("car_contacts")]
        public int? CarContacts { get; set; }
    }

    /// <summary>Per-driver row in the live aggregator table.</summary>
    public sealed class ReplayStateDriverRow
    {
        [JsonProperty("pos")]
        public int Pos { get; set; }

        [JsonProperty("car_idx")]
        public int CarIdx { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; } = "";

        [JsonProperty("cust_id")]
        public string CustId { get; set; } = "";

        [JsonProperty("our_inc")]
        public int OurInc { get; set; }

        [JsonProperty("yaml_inc")]
        public int? YamlInc { get; set; }

        [JsonProperty("our_pts")]
        public int OurPts { get; set; }

        [JsonProperty("off_tracks")]
        public int OffTracks { get; set; }

        [JsonProperty("car_contacts")]
        public int CarContacts { get; set; }
    }

    /// <summary>Misfire payload on <c>replay_state_tick.misfire</c>. All-null when inactive.</summary>
    public sealed class ReplayStateLastJump
    {
        [JsonProperty("active")]
        public bool Active { get; set; }

        [JsonProperty("direction")]
        public string Direction { get; set; }

        [JsonProperty("expected_frame")]
        public int? ExpectedFrame { get; set; }

        [JsonProperty("landed_frame")]
        public int? LandedFrame { get; set; }

        [JsonProperty("delta_frames")]
        public int? DeltaFrames { get; set; }

        [JsonProperty("delta_ms")]
        public int? DeltaMs { get; set; }

        [JsonProperty("expected_fingerprint")]
        public string ExpectedFingerprint { get; set; }

        [JsonProperty("nearest_fingerprint")]
        public string NearestFingerprint { get; set; }
    }

    /// <summary>Root payload for <c>replay_sweep_progress_tick</c> (~1 Hz during FF sweep).</summary>
    public sealed class ReplaySweepProgressPayload
    {
        [JsonProperty("type")]
        public string Type { get; set; } = "replay_sweep_progress_tick";

        [JsonProperty("ts")]
        public string Ts { get; set; } = "";

        [JsonProperty("frame")]
        public int Frame { get; set; }

        [JsonProperty("frame_end")]
        public int FrameEnd { get; set; }

        [JsonProperty("samples_so_far")]
        public int SamplesSoFar { get; set; }

        [JsonProperty("est_completion_pct")]
        public double EstCompletionPct { get; set; }

        [JsonProperty("est_remaining_ms")]
        public long EstRemainingMs { get; set; }

        [JsonProperty("telemetry_play_speed")]
        public int TelemetryPlaySpeed { get; set; }

        [JsonProperty("play_speed_requested")]
        public int PlaySpeedRequested { get; set; }
    }
}
