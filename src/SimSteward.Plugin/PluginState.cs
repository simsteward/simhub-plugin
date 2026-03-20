using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Incident detection counters for the current plugin session.
    /// Resets when iRacing disconnects.
    /// </summary>
    public class DetectionMetrics
    {
        [JsonProperty("yamlIncidentEvents")]
        public int YamlIncidentEvents { get; set; }

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

        /// <summary>Replay scan progress (null when no scan has been run).</summary>
        [JsonProperty("replayScan", NullValueHandling = NullValueHandling.Ignore)]
        public ReplayScanProgress ReplayScan { get; set; }
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

    // ================================================================
    //  Replay Scan — incident enumeration via NextIncident
    // ================================================================

    /// <summary>
    /// Full telemetry snapshot captured at the exact frame of an incident during replay scan.
    /// Contains all CarIdx arrays and scalar context for every car on track.
    /// </summary>
    public class IncidentSnapshot
    {
        [JsonProperty("incidentId")]           public string IncidentId { get; set; }
        [JsonProperty("replayFrameNum")]       public int ReplayFrameNum { get; set; }
        [JsonProperty("sessionTime")]          public double SessionTime { get; set; }
        [JsonProperty("sessionTimeFormatted")] public string SessionTimeFormatted { get; set; }
        [JsonProperty("sessionNum")]           public int SessionNum { get; set; }
        [JsonProperty("sessionTick")]          public int SessionTick { get; set; }

        /// <summary>Car index iRacing focused the camera on after NextIncident seek — the primary incident car.</summary>
        [JsonProperty("camCarIdx")]            public int CamCarIdx { get; set; } = -1;

        /// <summary>Drivers whose CurDriverIncidentCount increased at this incident frame.</summary>
        [JsonProperty("driversInvolved")]      public List<IncidentDriverDelta> DriversInvolved { get; set; } = new List<IncidentDriverDelta>();

        // ── All-car telemetry arrays (64 slots each) ──

        [JsonProperty("carIdxLapDistPct")]             public float[] CarIdxLapDistPct { get; set; }
        [JsonProperty("carIdxTrackSurface")]           public int[] CarIdxTrackSurface { get; set; }
        [JsonProperty("carIdxTrackSurfaceMaterial")]   public int[] CarIdxTrackSurfaceMaterial { get; set; }
        [JsonProperty("carIdxLap")]                    public int[] CarIdxLap { get; set; }
        [JsonProperty("carIdxLapCompleted")]           public int[] CarIdxLapCompleted { get; set; }
        [JsonProperty("carIdxGear")]                   public int[] CarIdxGear { get; set; }
        [JsonProperty("carIdxRPM")]                    public float[] CarIdxRPM { get; set; }
        [JsonProperty("carIdxSteer")]                  public float[] CarIdxSteer { get; set; }
        [JsonProperty("carIdxPosition")]               public int[] CarIdxPosition { get; set; }
        [JsonProperty("carIdxClassPosition")]          public int[] CarIdxClassPosition { get; set; }
        [JsonProperty("carIdxOnPitRoad")]               public bool[] CarIdxOnPitRoad { get; set; }
        [JsonProperty("carIdxBestLapTime")]            public float[] CarIdxBestLapTime { get; set; }
        [JsonProperty("carIdxLastLapTime")]            public float[] CarIdxLastLapTime { get; set; }
        [JsonProperty("carIdxFastRepairsUsed")]        public int[] CarIdxFastRepairsUsed { get; set; }
        [JsonProperty("carIdxTireCompound")]           public int[] CarIdxTireCompound { get; set; }
        [JsonProperty("carIdxEstTime")]                public float[] CarIdxEstTime { get; set; }
        [JsonProperty("carIdxF2Time")]                 public float[] CarIdxF2Time { get; set; }
        [JsonProperty("carIdxPaceFlags")]              public int[] CarIdxPaceFlags { get; set; }
        [JsonProperty("carIdxSessionFlags")]           public int[] CarIdxSessionFlags { get; set; }

        // ── Scalar context ──

        [JsonProperty("sessionFlags")]  public int SessionFlags { get; set; }
        [JsonProperty("sessionState")]  public int SessionState { get; set; }

        // ── Cam-focused car physics (only available for the car the camera is on) ──

        [JsonProperty("camSpeed")]              public float CamSpeed { get; set; }
        [JsonProperty("camThrottle")]           public float CamThrottle { get; set; }
        [JsonProperty("camBrake")]              public float CamBrake { get; set; }
        [JsonProperty("camLatAccel")]           public float CamLatAccel { get; set; }
        [JsonProperty("camLongAccel")]          public float CamLongAccel { get; set; }
        [JsonProperty("camYawRate")]            public float CamYawRate { get; set; }
        [JsonProperty("camSteeringWheelAngle")] public float CamSteeringWheelAngle { get; set; }

        // Motion — world-frame velocity and orientation
        [JsonProperty("camVelocityX")]          public float CamVelocityX { get; set; }
        [JsonProperty("camVelocityY")]          public float CamVelocityY { get; set; }
        [JsonProperty("camVelocityZ")]          public float CamVelocityZ { get; set; }
        [JsonProperty("camPitch")]              public float CamPitch { get; set; }
        [JsonProperty("camRoll")]               public float CamRoll { get; set; }
        [JsonProperty("camYaw")]                public float CamYaw { get; set; }
        [JsonProperty("camPitchRate")]          public float CamPitchRate { get; set; }
        [JsonProperty("camRollRate")]           public float CamRollRate { get; set; }
        [JsonProperty("camVertAccel")]          public float CamVertAccel { get; set; }

        // Controls
        [JsonProperty("camClutch")]             public float CamClutch { get; set; }
        [JsonProperty("camSteeringWheelTorque")]public float CamSteeringWheelTorque { get; set; }
        [JsonProperty("camShiftIndicatorPct")]  public float CamShiftIndicatorPct { get; set; }

        // Powertrain
        [JsonProperty("camRPM")]                public float CamRPM { get; set; }
        [JsonProperty("camGear")]               public int   CamGear { get; set; }
        [JsonProperty("camFuelLevel")]          public float CamFuelLevel { get; set; }
        [JsonProperty("camFuelLevelPct")]       public float CamFuelLevelPct { get; set; }
        [JsonProperty("camFuelUsePerHour")]     public float CamFuelUsePerHour { get; set; }
        [JsonProperty("camManifoldPress")]      public float CamManifoldPress { get; set; }

        // Engine health
        [JsonProperty("camWaterTemp")]          public float CamWaterTemp { get; set; }
        [JsonProperty("camOilTemp")]            public float CamOilTemp { get; set; }
        [JsonProperty("camOilPress")]           public float CamOilPress { get; set; }

        // Tire temperatures (inner/middle/outer per corner, °C)
        [JsonProperty("camLFtempCL")]           public float CamLFtempCL { get; set; }
        [JsonProperty("camLFtempCM")]           public float CamLFtempCM { get; set; }
        [JsonProperty("camLFtempCR")]           public float CamLFtempCR { get; set; }
        [JsonProperty("camRFtempCL")]           public float CamRFtempCL { get; set; }
        [JsonProperty("camRFtempCM")]           public float CamRFtempCM { get; set; }
        [JsonProperty("camRFtempCR")]           public float CamRFtempCR { get; set; }
        [JsonProperty("camLRtempCL")]           public float CamLRtempCL { get; set; }
        [JsonProperty("camLRtempCM")]           public float CamLRtempCM { get; set; }
        [JsonProperty("camLRtempCR")]           public float CamLRtempCR { get; set; }
        [JsonProperty("camRRtempCL")]           public float CamRRtempCL { get; set; }
        [JsonProperty("camRRtempCM")]           public float CamRRtempCM { get; set; }
        [JsonProperty("camRRtempCR")]           public float CamRRtempCR { get; set; }

        // Tire wear (left/middle/right tread per corner, 0–1)
        [JsonProperty("camLFwearL")]            public float CamLFwearL { get; set; }
        [JsonProperty("camLFwearM")]            public float CamLFwearM { get; set; }
        [JsonProperty("camLFwearR")]            public float CamLFwearR { get; set; }
        [JsonProperty("camRFwearL")]            public float CamRFwearL { get; set; }
        [JsonProperty("camRFwearM")]            public float CamRFwearM { get; set; }
        [JsonProperty("camRFwearR")]            public float CamRFwearR { get; set; }
        [JsonProperty("camLRwearL")]            public float CamLRwearL { get; set; }
        [JsonProperty("camLRwearM")]            public float CamLRwearM { get; set; }
        [JsonProperty("camLRwearR")]            public float CamLRwearR { get; set; }
        [JsonProperty("camRRwearL")]            public float CamRRwearL { get; set; }
        [JsonProperty("camRRwearM")]            public float CamRRwearM { get; set; }
        [JsonProperty("camRRwearR")]            public float CamRRwearR { get; set; }

        // ── YAML context at capture ──

        [JsonProperty("trackName")]     public string TrackName { get; set; }
        [JsonProperty("trackCategory")] public string TrackCategory { get; set; }
    }

    /// <summary>One driver's incident delta at a specific incident frame.</summary>
    public class IncidentDriverDelta
    {
        [JsonProperty("carIdx")]                   public int CarIdx { get; set; }
        [JsonProperty("userId")]                   public int UserId { get; set; }
        [JsonProperty("driverName")]               public string DriverName { get; set; }
        [JsonProperty("carNumber")]                public string CarNumber { get; set; }
        [JsonProperty("incidentDelta")]            public int IncidentDelta { get; set; }
        [JsonProperty("incidentTotalAfter")]       public int IncidentTotalAfter { get; set; }
        [JsonProperty("teamIncidentCount")]        public int TeamIncidentCount { get; set; }
        [JsonProperty("incidentEventId", NullValueHandling = NullValueHandling.Ignore)] public string IncidentEventId { get; set; }
        [JsonProperty("sessionPrefix",   NullValueHandling = NullValueHandling.Ignore)] public string SessionPrefix { get; set; }
    }

    /// <summary>Scan progress and results for the replay incident scan.</summary>
    public class ReplayScanProgress
    {
        [JsonProperty("state")]              public string State { get; set; } = "idle";
        [JsonProperty("incidentsFound")]     public int IncidentsFound { get; set; }
        [JsonProperty("currentFrameNum")]    public int CurrentFrameNum { get; set; }
        [JsonProperty("totalFrames")]        public int TotalFrames { get; set; }
        [JsonProperty("startedAtUtc")]       public string StartedAtUtc { get; set; }
        [JsonProperty("completedAtUtc")]     public string CompletedAtUtc { get; set; }
        [JsonProperty("error")]              public string Error { get; set; }
        [JsonProperty("snapshots")]          public List<IncidentSnapshot> Snapshots { get; set; } = new List<IncidentSnapshot>();

        /// <summary>Validation: sum of all captured deltas per driver vs YAML final totals.</summary>
        [JsonProperty("validation")]         public ReplayScanValidation Validation { get; set; }
    }

    /// <summary>Post-scan validation comparing captured incidents against YAML ground truth.</summary>
    public class ReplayScanValidation
    {
        [JsonProperty("valid")]              public bool Valid { get; set; }
        [JsonProperty("totalCaptured")]      public int TotalCaptured { get; set; }
        [JsonProperty("totalExpected")]      public int TotalExpected { get; set; }
        [JsonProperty("driverMismatches")]   public List<DriverIncidentMismatch> DriverMismatches { get; set; } = new List<DriverIncidentMismatch>();
    }

    public class DriverIncidentMismatch
    {
        [JsonProperty("carIdx")]       public int CarIdx { get; set; }
        [JsonProperty("driverName")]   public string DriverName { get; set; }
        [JsonProperty("captured")]     public int Captured { get; set; }
        [JsonProperty("expected")]     public int Expected { get; set; }
    }
}
