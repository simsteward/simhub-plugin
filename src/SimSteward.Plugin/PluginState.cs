using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Incident detection counters for the current plugin session (Layer 4 YAML only).
    /// Resets when iRacing disconnects.
    /// </summary>
    public class DetectionMetrics
    {
        [JsonProperty("l4YamlEvents")]
        public int L4YamlEvents { get; set; }

        [JsonProperty("totalEvents")]
        public int TotalEvents { get; set; }

        [JsonProperty("yamlUpdates")]
        public int YamlUpdates { get; set; }

        /// <summary>Session time of the most recently detected incident, or -1 if none yet.</summary>
        [JsonProperty("lastDetectionSessionTime")]
        public double LastDetectionSessionTime { get; set; } = -1;
    }

    /// <summary>
    /// Snapshot of plugin subsystem health broadcast with every state message.
    /// </summary>
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

        /// <summary>Player car index from iRacing; -1 means not yet known.</summary>
        [JsonProperty("playerCarIdx")]
        public int PlayerCarIdx { get; set; } = -1;
    }

    /// <summary>
    /// Snapshot of iRacing session/YAML readiness for discovery and debugging.
    /// </summary>
    public class SessionDataDiagnostics
    {
        [JsonProperty("simMode")]
        public string SimMode { get; set; } = "Unknown";

        [JsonProperty("irSessionId")]
        public int IrSessionId { get; set; }

        [JsonProperty("irSubSessionId")]
        public int IrSubSessionId { get; set; }

        [JsonProperty("sessionState")]
        public int SessionState { get; set; }

        [JsonProperty("sessionNum")]
        public int SessionNum { get; set; }

        [JsonProperty("sessionInfoUpdate")]
        public int SessionInfoUpdate { get; set; }

        /// <summary>Bitmask; 0 if unavailable.</summary>
        [JsonProperty("sessionFlags")]
        public int SessionFlags { get; set; }

        [JsonProperty("hasSessionInfo")]
        public bool HasSessionInfo { get; set; }

        [JsonProperty("selectedResultsSessionNum")]
        public int SelectedResultsSessionNum { get; set; } = -1;

        [JsonProperty("selectedResultsSessionType")]
        public string SelectedResultsSessionType { get; set; } = "";

        [JsonProperty("resultsPositionsCount")]
        public int ResultsPositionsCount { get; set; }

        [JsonProperty("resultsLapsComplete")]
        public int ResultsLapsComplete { get; set; }

        [JsonProperty("resultsOfficial")]
        public int ResultsOfficial { get; set; }

        [JsonProperty("resultsReady")]
        public bool ResultsReady { get; set; }

        [JsonProperty("activeDriverCount")]
        public int ActiveDriverCount { get; set; }

        [JsonProperty("driversWithNonZeroIncidents")]
        public int DriversWithNonZeroIncidents { get; set; }

        [JsonProperty("maxDriverIncidents")]
        public int MaxDriverIncidents { get; set; }

        [JsonProperty("allNonSpectatorIncidentsZero")]
        public bool AllNonSpectatorIncidentsZero { get; set; }

        [JsonProperty("lastSummaryCapture")]
        public SessionSummaryCaptureStatus LastSummaryCapture { get; set; } = new SessionSummaryCaptureStatus();

        /// <summary>List of sessions from SessionInfo.Sessions (sessionNum, sessionType, sessionName). Empty when SessionInfo not loaded.</summary>
        [JsonProperty("sessions")]
        public List<SessionInfoEntry> Sessions { get; set; } = new List<SessionInfoEntry>();
    }

    /// <summary>One session from SessionInfo.Sessions (practice, qualify, race).</summary>
    public class SessionInfoEntry
    {
        [JsonProperty("sessionNum")]
        public int SessionNum { get; set; }

        [JsonProperty("sessionType")]
        public string SessionType { get; set; } = "";

        [JsonProperty("sessionName")]
        public string SessionName { get; set; } = "";
    }

    public class SessionSummaryCaptureStatus
    {
        [JsonProperty("attemptedAtUtc")]
        public string AttemptedAtUtc { get; set; }

        [JsonProperty("trigger")]
        public string Trigger { get; set; }

        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("error")]
        public string Error { get; set; }

        [JsonProperty("details")]
        public string Details { get; set; }
    }

    public class PluginSnapshot
    {
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

        /// <summary>
        /// Stable identifier for the current session: "{trackName}_{weekendDate}".
        /// Used by OBS clip naming.
        /// </summary>
        [JsonProperty("sessionId")]
        public string SessionId { get; set; } = "";

        [JsonProperty("metrics")]
        public DetectionMetrics Metrics { get; set; } = new DetectionMetrics();

        [JsonProperty("diagnostics")]
        public PluginDiagnostics Diagnostics { get; set; } = new PluginDiagnostics();

        [JsonProperty("sessionDiagnostics")]
        public SessionDataDiagnostics SessionDiagnostics { get; set; } = new SessionDataDiagnostics();

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

    /// <summary>
    /// Session summary captured at checkered flag: results table (iRacing ResultsPositions),
    /// session identity, and the plugin's incident feed. Broadcast as sessionComplete with
    /// both results and incidentFeed for the dashboard "View results" drawer.
    /// </summary>
    public class SessionSummary
    {
        [JsonProperty("sessionId")]
        public string SessionId { get; set; }

        [JsonProperty("subSessionID")]
        public int SubSessionID { get; set; }

        [JsonProperty("sessionID")]
        public int SessionID { get; set; }

        [JsonProperty("seriesID")]
        public int SeriesID { get; set; }

        [JsonProperty("seasonID")]
        public int SeasonID { get; set; }

        [JsonProperty("leagueID")]
        public int LeagueID { get; set; }

        [JsonProperty("sessionNum")]
        public int SessionNum { get; set; }

        [JsonProperty("sessionType")]
        public string SessionType { get; set; }

        [JsonProperty("eventType")]
        public string EventType { get; set; }

        [JsonProperty("trackName")]
        public string TrackName { get; set; }

        [JsonProperty("trackID")]
        public int TrackID { get; set; }

        [JsonProperty("trackConfigName")]
        public string TrackConfigName { get; set; }

        [JsonProperty("trackCity")]
        public string TrackCity { get; set; }

        [JsonProperty("trackCountry")]
        public string TrackCountry { get; set; }

        [JsonProperty("category")]
        public string Category { get; set; }

        [JsonProperty("numCautionFlags")]
        public int NumCautionFlags { get; set; }

        [JsonProperty("numCautionLaps")]
        public int NumCautionLaps { get; set; }

        [JsonProperty("numLeadChanges")]
        public int NumLeadChanges { get; set; }

        [JsonProperty("totalLapsComplete")]
        public int TotalLapsComplete { get; set; }

        [JsonProperty("averageLapTime")]
        public float AverageLapTime { get; set; }

        [JsonProperty("isOfficial")]
        public bool IsOfficial { get; set; }

        [JsonProperty("simMode")]
        public string SimMode { get; set; }

        [JsonProperty("capturedAt")]
        public string CapturedAt { get; set; }

        [JsonProperty("sessionTimeSec")]
        public double SessionTimeSec { get; set; }

        /// <summary>Session name from SessionInfo.Sessions[].SessionName (e.g. "RACE").</summary>
        [JsonProperty("sessionName")]
        public string SessionName { get; set; }

        /// <summary>Session laps from SessionInfo.Sessions[].SessionLaps (e.g. "50" or "Fixed").</summary>
        [JsonProperty("sessionLaps")]
        public string SessionLaps { get; set; }

        /// <summary>Session time from SessionInfo.Sessions[].SessionTime (e.g. "30 min").</summary>
        [JsonProperty("sessionTimeStr")]
        public string SessionTimeStr { get; set; }

        /// <summary>Track length string from WeekendInfo (e.g. "2.5 km").</summary>
        [JsonProperty("trackLength")]
        public string TrackLength { get; set; }

        /// <summary>Incident limit from WeekendOptions (steward/session rules).</summary>
        [JsonProperty("incidentLimit")]
        public string IncidentLimit { get; set; }

        /// <summary>Fast repairs limit from WeekendOptions.</summary>
        [JsonProperty("fastRepairsLimit")]
        public string FastRepairsLimit { get; set; }

        /// <summary>Green-white-checkered limit from WeekendOptions.</summary>
        [JsonProperty("greenWhiteCheckeredLimit")]
        public string GreenWhiteCheckeredLimit { get; set; }

        /// <summary>Telemetry at capture: SessionState, SessionInfoUpdate, SessionFlags, replay position.</summary>
        [JsonProperty("telemetryAtCapture")]
        public TelemetryAtCapture TelemetryAtCapture { get; set; }

        [JsonProperty("results")]
        public List<DriverResult> Results { get; set; } = new List<DriverResult>();

        [JsonProperty("incidentFeed")]
        public List<IncidentEvent> IncidentFeed { get; set; } = new List<IncidentEvent>();
    }

    /// <summary>Snapshot of live telemetry at the moment of session summary capture.</summary>
    public class TelemetryAtCapture
    {
        [JsonProperty("sessionState")]
        public int SessionState { get; set; }

        [JsonProperty("sessionNum")]
        public int SessionNum { get; set; }

        [JsonProperty("sessionInfoUpdate")]
        public int SessionInfoUpdate { get; set; }

        [JsonProperty("sessionFlags")]
        public int SessionFlags { get; set; }

        [JsonProperty("sessionTime")]
        public double SessionTime { get; set; }

        [JsonProperty("replayFrameNum")]
        public int ReplayFrameNum { get; set; }

        [JsonProperty("replayFrameNumEnd")]
        public int ReplayFrameNumEnd { get; set; }

        [JsonProperty("replayPlaySpeed")]
        public int ReplayPlaySpeed { get; set; }

        [JsonProperty("replaySessionNum")]
        public int ReplaySessionNum { get; set; }
    }

    /// <summary>One row in the session results table at checkered flag.</summary>
    public class DriverResult
    {
        [JsonProperty("carIdx")]
        public int CarIdx { get; set; }

        [JsonProperty("driverName")]
        public string DriverName { get; set; }

        [JsonProperty("carNumber")]
        public string CarNumber { get; set; }

        [JsonProperty("carClass")]
        public string CarClass { get; set; }

        [JsonProperty("position")]
        public int Position { get; set; }

        [JsonProperty("classPosition")]
        public int ClassPosition { get; set; }

        [JsonProperty("lapsComplete")]
        public int LapsComplete { get; set; }

        [JsonProperty("lapsLed")]
        public int LapsLed { get; set; }

        [JsonProperty("fastestTime")]
        public float FastestTime { get; set; }

        [JsonProperty("fastestLap")]
        public int FastestLap { get; set; }

        [JsonProperty("lastTime")]
        public float LastTime { get; set; }

        [JsonProperty("incidents")]
        public int Incidents { get; set; }

        [JsonProperty("reasonOut")]
        public string ReasonOut { get; set; }

        [JsonProperty("reasonOutId")]
        public int ReasonOutId { get; set; }

        /// <summary>Last lap number (ResultsPositions.Lap).</summary>
        [JsonProperty("lap")]
        public int Lap { get; set; }

        /// <summary>Total time from ResultsPositions.Time.</summary>
        [JsonProperty("time")]
        public float Time { get; set; }

        [JsonProperty("jokerLapsComplete")]
        public int JokerLapsComplete { get; set; }

        [JsonProperty("lapsDriven")]
        public float LapsDriven { get; set; }

        [JsonProperty("abbrevName")]
        public string AbbrevName { get; set; }

        [JsonProperty("userID")]
        public int UserID { get; set; }

        [JsonProperty("teamName")]
        public string TeamName { get; set; }

        [JsonProperty("iRating")]
        public int IRating { get; set; }

        /// <summary>CurDriverIncidentCount from DriverInfo at capture (should match Incidents at session end).</summary>
        [JsonProperty("curDriverIncidentCount")]
        public int CurDriverIncidentCount { get; set; }

        [JsonProperty("teamIncidentCount")]
        public int TeamIncidentCount { get; set; }
    }
}
