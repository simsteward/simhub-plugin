#if SIMHUB_SDK
using IRSDKSharper;
#endif

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;

namespace SimSteward.Plugin
{
    public class DriverRecord
    {
        [JsonProperty("carIdx")]     public int CarIdx { get; set; }
        [JsonProperty("userId")]     public int UserId { get; set; }
        [JsonProperty("userName")]   public string UserName { get; set; }
        [JsonProperty("carNumber")]  public string CarNumber { get; set; }
        [JsonProperty("incidents")]  public int IncidentCount { get; set; }
        [JsonProperty("isPlayer")]   public bool IsPlayer { get; set; }
        [JsonProperty("isSpectator")] public bool IsSpectator { get; set; }
    }

    public class IncidentEvent
    {
        [JsonProperty("id")]                    public string Id { get; set; }
        [JsonProperty("subSessionId")]          public int SubSessionId { get; set; }
        [JsonProperty("userId")]                public int UserId { get; set; }
        [JsonProperty("sessionTime")]           public double SessionTime { get; set; }
        [JsonProperty("sessionTimeFormatted")]  public string SessionTimeFormatted { get; set; }
        [JsonProperty("carIdx")]                public int CarIdx { get; set; }
        [JsonProperty("driverName")]            public string DriverName { get; set; }
        [JsonProperty("carNumber")]             public string CarNumber { get; set; }
        [JsonProperty("delta")]                 public int Delta { get; set; }
        [JsonProperty("totalAfter")]            public int TotalAfter { get; set; }
        [JsonProperty("type")]                  public string Type { get; set; }
        [JsonProperty("source")]                public string Source { get; set; }
        [JsonProperty("otherCarIdx")]           public int OtherCarIdx { get; set; } = -1;
        [JsonProperty("otherCarNumber")]        public string OtherCarNumber { get; set; }
        [JsonProperty("otherDriverName")]       public string OtherDriverName { get; set; }
        [JsonProperty("cause")]                 public string Cause { get; set; }
        [JsonProperty("lap")]                   public int Lap { get; set; }
        [JsonProperty("trackPct")]              public float TrackPct { get; set; }
        /// <summary>iRacing replay frame number at incident time. OBS uses this to seek the replay to the exact frame before starting a clip. 0 if unavailable.</summary>
        [JsonProperty("replayFrameNum")]        public int ReplayFrameNum { get; set; }
        /// <summary>Hierarchical session prefix: ir_{subSessionId}_s{sessionNum}. Joins to session_meta_{subSessionId}.json.</summary>
        [JsonProperty("sessionPrefix", NullValueHandling = NullValueHandling.Ignore)] public string SessionPrefix { get; set; }
        /// <summary>IncidentSnapshot.IncidentId this event was captured from (replay scan only). Links to telemetry archive.</summary>
        [JsonProperty("snapshotRef",   NullValueHandling = NullValueHandling.Ignore)] public string SnapshotRef { get; set; }
    }

    /// <summary>
    /// Incident detection from iRacing session YAML.
    /// Session YAML DriverInfo.Drivers[].CurDriverIncidentCount (all drivers):
    /// authoritative totals, batched at high replay speeds. Same admin
    /// restriction as ResultsPositions: non-admin live race = 0 for others.
    /// </summary>
    public class IncidentTracker
    {
        private const int MaxCars = 64;
        /// <summary>Only trim in extreme cases; persisted file holds full history. No drop in normal use.</summary>
        private const int MaxIncidents = 500000;
        /// <summary>No cap; every incident is pushed to dashboard in real time.</summary>
        private const int MaxPendingBroadcast = 500000;

        private const float ProximityPctClose = 0.008f;
        private const float ProximityPctNear = 0.025f;
        /// <summary>Session-time bucket (seconds) for fingerprint; absorbs YAML lag at high replay speed.</summary>
        private const double GraceWindowSeconds = 2.0;

        // YAML incident tracking state
        private readonly Dictionary<int, int> _prevYamlIncidents = new Dictionary<int, int>();
        private readonly Dictionary<int, DriverRecord> _drivers = new Dictionary<int, DriverRecord>();
        private readonly List<IncidentEvent> _incidents = new List<IncidentEvent>();
        private readonly ConcurrentQueue<IncidentEvent> _pendingBroadcast = new ConcurrentQueue<IncidentEvent>();

        private int _lastSessionInfoUpdate = -1;
        private bool _baselineEstablished;
        private double _lastSessionTime = -1;
        private int _lastReplayFrameNum = -1;
        private int _lastReplayPositionFrame = -1;
        private int _lastKnownSessionNum = -1; // -1 = not yet seen; used for session-change detection
        private bool _sessionInfoNullLogged;   // suppress repeated "SessionInfo is null" log entries
        private bool _baselineJustEstablished; // true for one Update() after baseline is established
        private bool _yamlAllZeroLogged;       // suppress repeated "CurDriverIncidentCount all zero" (admin restriction) log

        private readonly float[] _distPctBuf = new float[MaxCars];
        private float _trackLengthM = 3000f;
        private bool _isDirt;
        private bool _trackMetadataRead;

        private int _subSessionId;
        private DateTime _lastHighSpeedSiuLogAt = DateTime.MinValue;

        // Metrics state — cumulative per iRacing connection, reset on disconnect
        private readonly DetectionMetrics _metrics = new DetectionMetrics();

        /// <summary>iRacing SubSessionID from WeekendInfo YAML. 0 until YAML is loaded.</summary>
        public int SubSessionId => _subSessionId;

        public int PlayerCarIdx { get; private set; } = -1;
        /// <summary>Player incident count from YAML (DriverInfo.Drivers[].CurDriverIncidentCount).</summary>
        public int PlayerIncidentCount =>
            PlayerCarIdx >= 0 && _drivers.TryGetValue(PlayerCarIdx, out var dr) ? dr.IncidentCount : 0;
        public bool BaselineEstablished => _baselineEstablished;
        /// <summary>True for one Update() after baseline transitions to established; plugin can emit a log event then clear.</summary>
        public bool BaselineJustEstablished => _baselineJustEstablished;

        // Track metadata exposed for snapshot/diagnostics
        public float TrackLengthM => _trackLengthM;
        public bool IsDirt => _isDirt;
        public string TrackName { get; private set; } = "";
        public string TrackCategory { get; private set; } = "Road";

        /// <summary>Structured log callback. Set by the plugin. Emits incident_detected, baseline_established, session_reset, seek_backward_detected (diagnostic only; incident counts are not reset).</summary>
        public Action<LogEntry> LogStructured { get; set; }

        /// <summary>Optional plain log callback for non-structured messages. Set by the plugin if needed.</summary>
        public Action<string> LogInfo { get; set; }

        /// <summary>Optional persistence: called for every incident added (after dedupe). Plugin can append to file for full history.</summary>
        public Action<IncidentEvent> OnIncidentPersist { get; set; }

        /// <summary>Optional persistence: called when replay scan completes. Plugin writes the full scan result to a JSON file.</summary>
        public Action<ReplayScanProgress> OnScanComplete { get; set; }

        /// <summary>Optional: called once when the scan baseline is captured (CaptureBaseline → SeekNext). Use to write session_meta.</summary>
        public Action OnBaselineCaptured { get; set; }

        private void EmitStructured(string eventType, string message, Dictionary<string, object> fields = null, string level = "INFO", string domain = null, string incidentId = null)
        {
            if (string.Equals(eventType, "incident_detected", StringComparison.OrdinalIgnoreCase) && LogStructured == null)
            {
                // #region agent log
                AgentDebugLog.WriteB0C27E("H2", "IncidentTracker.EmitStructured", "LogStructured_null", new { });
                // #endregion
                return;
            }
            LogStructured?.Invoke(new LogEntry
            {
                Level = level,
                Component = "tracker",
                Event = eventType,
                Message = message,
                Fields = fields,
                Domain = domain,
                IncidentId = incidentId
            });
        }

        public IncidentTracker()
        {
        }

        public List<DriverRecord> GetDriverSnapshot()
        {
            var list = new List<DriverRecord>(_drivers.Values);
            list.Sort((a, b) => b.IncidentCount.CompareTo(a.IncidentCount));
            return list;
        }

        public List<IncidentEvent> GetIncidentFeed() => new List<IncidentEvent>(_incidents);

        /// <summary>
        /// Add a player incident from 60 Hz telemetry (PlayerCarMyIncidentCount increased). Gives exact SessionTime/ReplayFrameNum.
        /// Call when baseline is established and count just increased. YAML may later merge Type/Delta into this event.
        /// </summary>
        public void AddPlayerIncidentFromTelemetry(double sessionTime, int replayFrameNum, int totalAfter,
            int subSessionId, int sessionNum)
        {
            if (PlayerCarIdx < 0 || totalAfter <= 0) return;
            if (!_drivers.TryGetValue(PlayerCarIdx, out var rec)) return;

            int frameNum = replayFrameNum > 0 ? replayFrameNum : 0;
            string prefix = subSessionId > 0 ? ComputeSessionPrefix(subSessionId, sessionNum) : null;
            var ev = new IncidentEvent
            {
                Id = subSessionId > 0
                    ? ComputeIncidentFingerprintV2(subSessionId, sessionNum, sessionTime, rec.UserId, 1)
                    : ShortId(),
                SessionPrefix = prefix,
                SubSessionId = subSessionId,
                UserId = rec.UserId,
                SessionTime = sessionTime,
                SessionTimeFormatted = FormatTime(sessionTime),
                CarIdx = PlayerCarIdx,
                DriverName = rec.UserName ?? "Player",
                CarNumber = rec.CarNumber ?? "?",
                Delta = 1,
                TotalAfter = totalAfter,
                Type = "1x",
                Source = "telemetry",
                ReplayFrameNum = frameNum
            };
            AddIncident(ev);
        }

        /// <summary>
        /// Returns a point-in-time copy of the detection counters.
        /// Safe to call from any thread; IncidentTracker fields are only mutated
        /// on the DataUpdate thread, so a shallow copy here is sufficient.
        /// </summary>
        public DetectionMetrics GetMetricsSnapshot() => new DetectionMetrics
        {
            YamlIncidentEvents          = _metrics.YamlIncidentEvents,
            TotalEvents           = _metrics.TotalEvents,
            YamlUpdates           = _metrics.YamlUpdates,
            LastDetectionSessionTime = _metrics.LastDetectionSessionTime,
        };

        public List<IncidentEvent> DrainNewIncidents()
        {
            var result = new List<IncidentEvent>();
            while (_pendingBroadcast.TryDequeue(out var ev))
                result.Add(ev);
            return result;
        }

        public void Reset()
        {
            _prevYamlIncidents.Clear();
            _drivers.Clear();
            _incidents.Clear();
            while (_pendingBroadcast.TryDequeue(out _)) { }
            _lastSessionInfoUpdate = -1;
            _baselineEstablished = false;
            _lastSessionTime = -1;
            _lastReplayFrameNum = -1;
            _lastReplayPositionFrame = -1;
            _lastKnownSessionNum = -1;
            _sessionInfoNullLogged = false;
            _baselineJustEstablished = false;
            _yamlAllZeroLogged = false;
            PlayerCarIdx = -1;
            _subSessionId = 0;
            _trackMetadataRead = false;
            ResetMetrics();
        }

        /// <summary>
        /// After replay scan completes, reset _prevYamlIncidents to current YAML totals so live
        /// tracking resumes from a clean baseline at the current replay position.
        /// </summary>
        public void ResetLiveBaseline(IRacingSdk irsdk)
        {
#if SIMHUB_SDK
            if (irsdk == null || !irsdk.IsConnected) return;
            var driverList = irsdk.Data?.SessionInfo?.DriverInfo?.Drivers;
            if (driverList == null) return;
            foreach (var d in driverList)
            {
                if (d.CarIdx < 0) continue;
                _prevYamlIncidents[d.CarIdx] = d.CurDriverIncidentCount;
            }
            _baselineEstablished = true;
#endif
        }

        private void ResetMetrics()
        {
            _metrics.YamlIncidentEvents = 0;
            _metrics.TotalEvents = 0;
            _metrics.YamlUpdates = 0;
            _metrics.LastDetectionSessionTime = -1;
        }

#if SIMHUB_SDK
        // ================================================================
        //  Main update — called every DataUpdate tick (~60 Hz)
        // ================================================================

        public void Update(IRacingSdk irsdk, double sessionTime)
        {
            if (irsdk == null || !irsdk.IsConnected) return;

            _baselineJustEstablished = false; // clear so plugin only sees it for one tick after establishment

            // ── Focused / selected driver (for incident count and "player" display) ──
            // In replay or spectator, the user may be watching another car; we want that car's incidents.
            // CamCarIdx = camera-focused car (who we're watching); DriverCarIdx = car we're driving (YAML).
            // Prefer CamCarIdx when valid so replay "follow car" shows the correct driver's data.
            int camCarIdx = GetInt(irsdk, "CamCarIdx");
            int driverCarIdx = irsdk.Data?.SessionInfo?.DriverInfo?.DriverCarIdx ?? -1;
            int prevPlayerCarIdx = PlayerCarIdx;
            if (camCarIdx >= 0 && camCarIdx < MaxCars)
                PlayerCarIdx = camCarIdx;
            else if (driverCarIdx >= 0)
                PlayerCarIdx = driverCarIdx;
            // #region agent log
            if (PlayerCarIdx != prevPlayerCarIdx)
                AgentDebugLog.WriteB0C27E("H4", "IncidentTracker.Update", "focused_driver_set", new { prevPlayerCarIdx, PlayerCarIdx, camCarIdx, driverCarIdx, source = (camCarIdx >= 0 && camCarIdx < MaxCars) ? "CamCarIdx" : "DriverCarIdx" });
            // #endregion

            // ── Seek-backward detection (diagnostic only; we do not reset incident counts) ──
            // Use ReplayFrameNum as current playback position (iRacing replay position).
            // ReplayFrameNumEnd is session end frame and can cause false backward detection if used here.
            // Threshold: decrease of 10+ frames (≈0.17s at 60 Hz). When ReplayPlaySpeed > 1, ignore backward blips.
            int replayFrame = GetInt(irsdk, "ReplayFrameNum");
            double prevSessionTime = _lastSessionTime;
            int prevReplayFrame = _lastReplayFrameNum;
            int replayPlaySpeed = GetInt(irsdk, "ReplayPlaySpeed");
            bool seekedBackward = false;
            bool frameGuard = false;
            bool sessionTimeGuard = false;
            bool inFastForward = replayPlaySpeed > 1;
            if (!inFastForward)
            {
                frameGuard = prevReplayFrame >= 0 && replayFrame < prevReplayFrame - 10;
                sessionTimeGuard = prevSessionTime >= 0 && sessionTime < prevSessionTime - 1.0;
                if (frameGuard)
                    seekedBackward = true;
                else if (sessionTimeGuard)
                    seekedBackward = true;
            }
            _lastSessionTime = sessionTime;
            _lastReplayFrameNum = replayFrame;

            if (seekedBackward)
            {
                // Log only; do not reset incident baselines or counts (per product requirement).
                int frameDelta = prevReplayFrame >= 0 ? prevReplayFrame - replayFrame : 0;
                double sessionTimeDelta = prevSessionTime >= 0 ? sessionTime - prevSessionTime : 0;
                AgentDebugLog.Write740824("H1", "IncidentTracker.Update", "seek_backward_detected", new
                {
                    replayPlaySpeed,
                    frameDelta,
                    prevReplayFrame,
                    replayFrame,
                    sessionTimeDelta,
                    frameGuard,
                    sessionTimeGuard
                });
                LogStructured?.Invoke(new LogEntry
                {
                    Level = "INFO",
                    Component = "tracker",
                    Event = "seek_backward_detected",
                    Message = $"Seek-backward detected (frame {prevReplayFrame}->{replayFrame}); incident counts not reset.",
                    Fields = new Dictionary<string, object> { ["from_frame"] = prevReplayFrame, ["to_frame"] = replayFrame },
                    Domain = "replay"
                });
            }

            // ── Session-change detection ────────────────────────────
            // Resets incident baselines when the user moves between practice / qualifying / race
            // without a full iRacing disconnect. Driver roster is preserved.
            int currentSessionNum = GetInt(irsdk, "SessionNum");
            if (_lastKnownSessionNum >= 0 && currentSessionNum != _lastKnownSessionNum)
            {
                // #region agent log
                AgentDebugLog.WriteB0C27E("H9", "IncidentTracker.Update", "session_reset",
                    new { oldSession = _lastKnownSessionNum, newSession = currentSessionNum, playerCarIdx = PlayerCarIdx });
                // #endregion
                LogStructured?.Invoke(new LogEntry
                {
                    Level = "INFO",
                    Component = "tracker",
                    Event = "session_reset",
                    Message = $"Session changed ({_lastKnownSessionNum}→{currentSessionNum}): clearing incident baselines.",
                    Fields = new Dictionary<string, object> { ["old_session"] = _lastKnownSessionNum, ["new_session"] = currentSessionNum },
                    Domain = "lifecycle"
                });
                _incidents.Clear();
                while (_pendingBroadcast.TryDequeue(out _)) { }
                _prevYamlIncidents.Clear();
                _baselineEstablished = false;
                _yamlAllZeroLogged = false;
                _lastSessionInfoUpdate = -1;
                ResetMetrics();
            }
            _lastKnownSessionNum = currentSessionNum;

            // ── Read telemetry arrays (for cause inference / other-car identification) ─
            TryGetFloatArray(irsdk, "CarIdxLapDistPct", _distPctBuf);
            _lastReplayPositionFrame = replayFrame;

            // ── Track metadata (once per session) ───────────────────
            UpdateTrackMetadata(irsdk);

            // ── YAML-based incident tracking (all drivers) ──────────
            // Gate on SessionInfoUpdate so we only parse YAML when iRacing has actually
            // changed the session data. CurDriverIncidentCount is a YAML field — if SIU
            // hasn't incremented, the data is byte-for-byte identical and there is nothing
            // to detect. Also refresh when we have SessionInfo but no drivers yet (e.g. first
            // time YAML is available, or SIU not yet incrementing), so the roster appears.
            int currentSIU = GetInt(irsdk, "SessionInfoUpdate");
            bool siuChanged = currentSIU != _lastSessionInfoUpdate;
            var si = irsdk.Data.SessionInfo;
            bool needRoster = _drivers.Count == 0 && si != null;
            // #region agent log
            if (siuChanged)
            {
                AgentDebugLog.Write740824("H1", "IncidentTracker.Update", "siu_changed",
                    new { currentSIU, _lastSessionInfoUpdate, replayPlaySpeed, sessionTime, sessionNum = currentSessionNum });
                AgentDebugLog.WriteB0C27E("H9", "IncidentTracker.Update", "siu_changed",
                    new { currentSIU, previousSIU = _lastSessionInfoUpdate, sessionNum = currentSessionNum, playerCarIdx = PlayerCarIdx });
            }
            else if (inFastForward && (DateTime.UtcNow - _lastHighSpeedSiuLogAt).TotalSeconds >= 2.0)
            {
                _lastHighSpeedSiuLogAt = DateTime.UtcNow;
                AgentDebugLog.Write740824("H1", "IncidentTracker.Update", "siu_unchanged_at_high_speed",
                    new { currentSIU, _lastSessionInfoUpdate, replayPlaySpeed, sessionTime });
            }
            // #endregion
            if (siuChanged)
            {
                _lastSessionInfoUpdate = currentSIU;
                _metrics.YamlUpdates++;
                RefreshFromYaml(irsdk, sessionTime, replayPlaySpeed);
                EmitStructured("yaml_update", $"SessionInfoUpdate {currentSIU} processed",
                    new Dictionary<string, object>
                    {
                        ["session_info_update"] = currentSIU,
                        ["session_num"] = currentSessionNum,
                        ["session_time"] = sessionTime
                    },
                    level: "DEBUG");
            }
            else if (needRoster)
            {
                RefreshFromYaml(irsdk, sessionTime, replayPlaySpeed);
            }
        }

        // ================================================================
        //  YAML-based tracking (all drivers)
        // ================================================================

        private void RefreshFromYaml(IRacingSdk irsdk, double sessionTime, int replayPlaySpeed = 0)
        {
            var si = irsdk.Data.SessionInfo;
            // #region agent log
            int driverCount = si?.DriverInfo?.Drivers?.Count ?? 0;
            int subSessionId = si?.WeekendInfo?.SubSessionID ?? 0;
            AgentDebugLog.Write740824("H2", "IncidentTracker.RefreshFromYaml", "entry",
                new { siNull = si == null, driverCount, subSessionId, replayPlaySpeed, sessionTime });
            // #endregion
            if (si == null)
            {
                if (!_sessionInfoNullLogged)
                {
                    EmitStructured("tracker_status", "SessionInfo is null — YAML not yet available. Incident totals will be delayed until iRacing populates session data.",
                        new Dictionary<string, object> { ["reason"] = "sessioninfo_null" });
                    _sessionInfoNullLogged = true;
                }
                return;
            }
            _sessionInfoNullLogged = false;
            // YamlUpdates counts actual SIU increments, not every-tick polling calls.
            // Counting happens in Update() when currentSIU != _lastSessionInfoUpdate.

            _subSessionId = si.WeekendInfo?.SubSessionID ?? 0;
            if (_subSessionId == 0)
            {
                // #region agent log
                AgentDebugLog.Write740824("H5", "IncidentTracker.RefreshFromYaml", "early_return_subsession_zero", new { replayPlaySpeed });
                AgentDebugLog.WriteB0C27E("H1", "IncidentTracker.RefreshFromYaml", "early_return_subsession_zero", new { replayPlaySpeed });
                // #endregion
                return; // YAML not fully loaded yet; skip roster and incident processing to avoid Hash(0:...) collisions
            }

            // PlayerCarIdx is set in Update() from CamCarIdx (camera-focused car) or DriverCarIdx; do not overwrite here.

            // ── Rebuild driver roster: CurDriverIncidentCount (all drivers) ─
            // CurDriverIncidentCount is populated from first YAML update; no session-match needed.
            // Same admin restriction as ResultsPositions: live non-admin = 0 for other drivers.
            var driverList = si.DriverInfo?.Drivers;
            int driversBaselined = 0;
            int driversWithNonZeroIncidents = 0;
            bool allNonSpectatorZero = true;
            if (driverList != null)
            {
                foreach (var d in driverList)
                {
                    if (d.CarIdx < 0) continue;
                    if (!_drivers.TryGetValue(d.CarIdx, out var rec))
                    {
                        rec = new DriverRecord { CarIdx = d.CarIdx };
                        _drivers[d.CarIdx] = rec;
                    }
                    rec.UserId = d.UserID;
                    rec.UserName = string.IsNullOrEmpty(d.UserName) ? rec.UserName ?? "Unknown" : d.UserName;
                    rec.CarNumber = string.IsNullOrEmpty(d.CarNumber) ? rec.CarNumber ?? "?" : d.CarNumber;
                    rec.IsPlayer = (d.CarIdx == PlayerCarIdx);
                    rec.IsSpectator = (d.IsSpectator != 0);

                    int inc = ClampIncidentCount(d.CurDriverIncidentCount);
                    if (d.CarIdx == PlayerCarIdx)
                    {
                        _prevYamlIncidents.TryGetValue(d.CarIdx, out int prevKnown);
                        AgentDebugLog.WriteB0C27E("H7", "IncidentTracker.RefreshFromYaml", "focused_yaml_snapshot",
                            new { PlayerCarIdx, inc, prevKnown, baselineEstablished = _baselineEstablished, replayPlaySpeed, sessionTime });
                    }
                    if (inc != 0 && !rec.IsSpectator)
                    {
                        allNonSpectatorZero = false;
                        driversWithNonZeroIncidents++;
                    }

                    if (!_baselineEstablished)
                    {
                        _prevYamlIncidents[d.CarIdx] = inc;
                        if (!rec.IsSpectator)
                            driversBaselined++;
                        // #region agent log
                        if (d.CarIdx == PlayerCarIdx)
                            AgentDebugLog.WriteB0C27E("H3", "IncidentTracker.RefreshFromYaml", "baseline_set_player", new { PlayerCarIdx, inc, carNumber = rec.CarNumber });
                        // #endregion
                    }
                    else
                    {
                        _prevYamlIncidents.TryGetValue(d.CarIdx, out int prev);
                        int delta = inc - prev;
                        rec.IncidentCount = ClampIncidentCount(inc); // absolute total from YAML; shows correct cumulative count in replay and live

                        if (d.CarIdx == PlayerCarIdx && (delta != 0 || inc > 0))
                            AgentDebugLog.WriteB0C27E("H1", "IncidentTracker.RefreshFromYaml", "player_yaml_delta", new { PlayerCarIdx, prev, inc, delta, willEmit = (delta > 0 && IsValidIncidentValues(delta, inc)), sessionTime });

                        if (delta > 0 && IsValidIncidentValues(delta, inc))
                        {
                            // #region agent log
                            AgentDebugLog.Write740824("H3", "IncidentTracker.RefreshFromYaml", "delta_positive",
                                new { carIdx = d.CarIdx, delta, inc, prev, sessionTime, replayPlaySpeed, carNumber = rec.CarNumber });
                            // #endregion
                            _drivers.TryGetValue(d.CarIdx, out var driverRec);
                            string cause = InferYamlCause(d.CarIdx, sessionTime, delta);
                            float trackPct = (d.CarIdx >= 0 && d.CarIdx < MaxCars) ? _distPctBuf[d.CarIdx] : 0;
                            int frameNum = _lastReplayPositionFrame > 0 ? _lastReplayPositionFrame : 0;
                            int userId = driverRec?.UserId ?? 0;
                            string sessionPrefix = ComputeSessionPrefix(_subSessionId, _lastKnownSessionNum);
                            var ev = new IncidentEvent
                            {
                                Id = ComputeIncidentFingerprintV2(_subSessionId, _lastKnownSessionNum, sessionTime, userId, delta),
                                SessionPrefix = sessionPrefix,
                                SubSessionId = _subSessionId,
                                UserId = userId,
                                SessionTime = sessionTime,
                                SessionTimeFormatted = FormatTime(sessionTime),
                                CarIdx = d.CarIdx,
                                DriverName = driverRec?.UserName ?? $"Car {d.CarIdx}",
                                CarNumber = driverRec?.CarNumber ?? "?",
                                Delta = delta,
                                TotalAfter = inc,
                                Type = ClassifyYamlDelta(delta),
                                Source = "yaml",
                                Cause = cause,
                                TrackPct = trackPct,
                                ReplayFrameNum = frameNum
                            };
                            TryIdentifyOtherCar(ev, ProximityPctNear);
                            _metrics.YamlIncidentEvents++;
                            AddIncident(ev);
                            if (delta > 4)
                                EmitStructured("yaml_batch_detected",
                                    $"Batched delta {delta}x for #{rec.CarNumber} at {replayPlaySpeed}x replay",
                                    new Dictionary<string, object>
                                    {
                                        ["car_idx"]           = d.CarIdx,
                                        ["car_number"]        = rec.CarNumber,
                                        ["delta"]             = delta,
                                        ["total_after"]       = inc,
                                        ["replay_play_speed"] = replayPlaySpeed,
                                        ["session_time"]      = sessionTime,
                                    },
                                    level: "WARN", domain: "replay");
                        }
                        else if (delta > 0 && !IsValidIncidentValues(delta, inc))
                        {
                            AgentDebugLog.Write740824("H4", "IncidentTracker.RefreshFromYaml", "delta_rejected_invalid",
                                new { carIdx = d.CarIdx, delta, inc });
                        }
                        _prevYamlIncidents[d.CarIdx] = inc;
                    }
                }
            }

            // #region agent log
            if (driverList != null && (driversWithNonZeroIncidents > 0 || replayPlaySpeed > 2))
                AgentDebugLog.Write740824("H3", "IncidentTracker.RefreshFromYaml", "after_loop",
                    new { driversWithNonZeroIncidents, baselineEstablished = _baselineEstablished, driverCount = driverList?.Count ?? 0, replayPlaySpeed });
            // #endregion

            if (!_baselineEstablished && driversBaselined > 0)
            {
                _baselineEstablished = true;
                _baselineJustEstablished = true;
                LogStructured?.Invoke(new LogEntry
                {
                    Level = "INFO",
                    Component = "tracker",
                    Event = "baseline_established",
                    Message = $"Incident baseline established ({driversBaselined} drivers).",
                    Fields = new Dictionary<string, object> { ["driver_count"] = driversBaselined },
                    Domain = "lifecycle"
                });
            }

            int sessionState = GetInt(irsdk, "SessionState");
            bool isPostRace = sessionState >= 5; // irsdk_StateCheckered = 5, StateCoolDown = 6
            if (allNonSpectatorZero && _baselineEstablished && !isPostRace && driverList != null && driverList.Count > 0)
            {
                if (!_yamlAllZeroLogged)
                {
                    EmitStructured("tracker_status", "CurDriverIncidentCount is 0 for all non-spectator drivers. Likely non-admin in a live session — all-driver YAML incident data is restricted by iRacing until checkered or in replay.",
                        new Dictionary<string, object> { ["reason"] = "admin_restriction" });
                    _yamlAllZeroLogged = true;
                }
            }
            else if (!allNonSpectatorZero || isPostRace)
                _yamlAllZeroLogged = false;

        }

        /// <summary>
        /// Infer likely cause for a YAML-reported incident delta.
        /// Uses car proximity at the moment YAML fires.
        /// </summary>
        private string InferYamlCause(int carIdx, double sessionTime, int delta)
        {
            if (carIdx < 0 || carIdx >= MaxCars) return null;
            float distPct = carIdx < _distPctBuf.Length ? _distPctBuf[carIdx] : -1f;

            if (delta == 1)
                return "off-track";

            // 4x on paved = heavy contact; on dirt check proximity to distinguish car-contact
            if (delta >= 4)
            {
                if (!_isDirt) return "heavy-contact";
                int nearbyForHeavy = distPct >= 0 ? FindNearestCar(carIdx, distPct, ProximityPctNear) : -1;
                return nearbyForHeavy >= 0 ? "car-contact" : "heavy-contact";
            }

            // 2x: prefer car-contact if another car is close at YAML-fire time, else wall-or-spin
            if (delta == 2)
            {
                int nearbyIdx = distPct >= 0 ? FindNearestCar(carIdx, distPct, ProximityPctClose) : -1;
                if (nearbyIdx >= 0) return "car-contact";
                int nearbyWide = distPct >= 0 ? FindNearestCar(carIdx, distPct, ProximityPctNear) : -1;
                return nearbyWide >= 0 ? "car-contact" : "wall-or-spin";
            }

            // Batched or unusual delta — check proximity
            if (distPct >= 0)
            {
                int nearbyIdx = FindNearestCar(carIdx, distPct, ProximityPctNear);
                if (nearbyIdx >= 0) return "car-contact";
            }
            return null;
        }

        // ================================================================
        //  Telemetry helpers
        // ================================================================

        private bool TryGetFloatArray(IRacingSdk irsdk, string name, float[] buf)
        {
            try
            {
                var props = irsdk.Data.TelemetryDataProperties;
                if (props == null || !props.ContainsKey(name)) return false;
                irsdk.Data.GetFloatArray(props[name], buf, 0, buf.Length);
                return true;
            }
            catch { return false; }
        }

        private void UpdateTrackMetadata(IRacingSdk irsdk)
        {
            if (_trackMetadataRead) return;
            try
            {
                var si = irsdk.Data.SessionInfo;
                if (si?.WeekendInfo == null) return;

                try
                {
                    var trackLen = si.WeekendInfo.TrackLength;
                    if (!string.IsNullOrEmpty(trackLen))
                    {
                        var numChars = trackLen.Where(c => char.IsDigit(c) || c == '.').ToArray();
                        if (float.TryParse(new string(numChars), NumberStyles.Float, CultureInfo.InvariantCulture, out float val) && val > 0)
                        {
                            _trackLengthM = trackLen.IndexOf("mi", StringComparison.OrdinalIgnoreCase) >= 0
                                ? val * 1609.34f
                                : val * 1000f;
                        }
                    }
                }
                catch { /* TrackLength property may not exist */ }

                try
                {
                    var cat = si.WeekendInfo.Category;
                    if (!string.IsNullOrEmpty(cat))
                    {
                        TrackCategory = cat;
                        _isDirt = cat.IndexOf("dirt", StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                }
                catch { /* Category property may not exist */ }

                try
                {
                    var name = si.WeekendInfo.TrackName;
                    if (!string.IsNullOrEmpty(name))
                        TrackName = name;
                }
                catch { /* TrackName property may not exist */ }

                _trackMetadataRead = true;
            }
            catch { }
        }

        private static int GetInt(IRacingSdk irsdk, string name)
        {
            try { return irsdk.Data.GetInt(name); }
            catch { return 0; }
        }

        private static float GetFloat(IRacingSdk irsdk, string name)
        {
            try { return irsdk.Data.GetFloat(name); }
            catch { return 0f; }
        }

        private static bool GetBool(IRacingSdk irsdk, string name)
        {
            try { return irsdk.Data.GetBool(name); }
            catch { return false; }
        }

        // ================================================================
        //  Replay Scan — enumerate ALL incidents via NextIncident
        // ================================================================
        //
        //  State machine driven by DataUpdate ticks. Each tick advances
        //  one step of the scan. The replay is paused throughout; we seek
        //  via NextIncident and read telemetry at each incident frame.
        //
        //  States:
        //    Idle             — not scanning
        //    SeekingStart     — seeking to start of session
        //    WaitForSettle    — waiting for ReplayFrameNum to stabilize after seek
        //    WaitForYaml      — waiting for SessionInfoUpdate to change so YAML reflects new frame
        //    CaptureBaseline  — capture YAML baselines at session start before first NextIncident
        //    ReadIncident     — read telemetry snapshot at current incident frame
        //    SeekNext         — issue NextIncident broadcast
        //    Validating       — compare captured totals vs YAML ground truth
        //    Complete         — scan finished
        //    Error            — scan failed

        public enum ScanState { Idle, SeekingStart, WaitForSettle, WaitForYaml, CaptureBaseline, ReadIncident, SeekNext, Validating, Complete, Error }

        private ScanState _scanState = ScanState.Idle;
        private ReplayScanProgress _scanProgress = new ReplayScanProgress();
        private int _scanSettleTicksRemaining;
        private int _scanLastFrameNum = -1;
        private int _scanStableFrameCount;
        private int _scanSessionNum = -1;
        private int _scanSiuAtSettle;       // SIU value when frame settled; wait for it to change
        private int _scanYamlWaitTicks;     // ticks spent waiting for SIU to change
        private const int ScanYamlWaitMax = 90; // ~1.5s max wait for YAML refresh
        private int _scanLastReadFrameNum = -1; // Gap 1: track last frame read to detect same-frame multi-car incidents
        private int _scanLastReadSiu = -1;      // Gap 1: paired with _scanLastReadFrameNum for termination check
        private const int ScanSettleMaxTicks = 120; // 2 seconds at 60Hz
        private const int ScanStableThreshold = 6;  // ~100ms of stable frame = seek complete
        private const int ScanMaxIncidents = 10000; // safety cap

        // YAML incident baselines captured at scan start for delta detection
        private readonly Dictionary<int, int> _scanYamlBaseline = new Dictionary<int, int>();
        // Running totals per driver during scan
        private readonly Dictionary<int, int> _scanYamlPrev = new Dictionary<int, int>();

        /// <summary>IRacingSdk reference stored during scan for broadcast calls.</summary>
        private IRacingSdk _scanIrsdk;

        public ScanState CurrentScanState => _scanState;
        public ReplayScanProgress ScanProgress => _scanProgress;

        /// <summary>
        /// Start a replay incident scan. Call from DataUpdate thread.
        /// The scan will pause the replay, seek to start, and enumerate all incidents.
        /// </summary>
        public bool StartReplayScan(IRacingSdk irsdk, int sessionNum)
        {
            if (irsdk == null || !irsdk.IsConnected) return false;
            if (_scanState != ScanState.Idle && _scanState != ScanState.Complete && _scanState != ScanState.Error)
                return false; // scan already in progress

            _scanIrsdk = irsdk;
            _scanSessionNum = sessionNum;
            _scanState = ScanState.SeekingStart;
            _scanProgress = new ReplayScanProgress
            {
                State = "seeking_start",
                StartedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                TotalFrames = GetInt(irsdk, "ReplayFrameNumEnd")
            };
            _scanYamlBaseline.Clear();
            _scanYamlPrev.Clear();
            _scanLastFrameNum = -1;
            _scanStableFrameCount = 0;
            _scanLastReadFrameNum = -1;
            _scanLastReadSiu = -1;

            // Pause replay and seek to session start
            // Baseline is captured in CaptureBaseline state after seek settles (so YAML reflects session start, not mid-replay position)
            irsdk.ReplaySetPlaySpeed(0, false);
            irsdk.ReplaySearchSessionTime(sessionNum, 0);

            EmitStructured("replay_scan_started", $"Replay scan started for session {sessionNum}",
                new Dictionary<string, object> { ["session_num"] = sessionNum },
                domain: "replay_scan");

            return true;
        }

        /// <summary>Stop/cancel an in-progress scan.</summary>
        public void StopReplayScan()
        {
            if (_scanState == ScanState.Idle) return;
            _scanState = ScanState.Idle;
            _scanProgress.State = "cancelled";
            _scanIrsdk = null;
            _scanLastReadFrameNum = -1;
            _scanLastReadSiu = -1;
            EmitStructured("replay_scan_cancelled", "Replay scan cancelled by user.", domain: "replay_scan");
        }

        /// <summary>
        /// Advance the scan state machine by one tick. Call from DataUpdate every frame.
        /// Returns true if scan is still active (not Idle/Complete/Error).
        /// </summary>
        public bool TickReplayScan(IRacingSdk irsdk)
        {
            if (_scanState == ScanState.Idle || _scanState == ScanState.Complete || _scanState == ScanState.Error)
                return false;
            if (irsdk == null || !irsdk.IsConnected)
            {
                _scanState = ScanState.Error;
                _scanProgress.State = _scanState.ToString();
                _scanProgress.Error = "irsdk_disconnected";
                return false;
            }
            _scanIrsdk = irsdk;

            switch (_scanState)
            {
                case ScanState.SeekingStart:
                    // Transition to WaitForSettle to let the seek complete
                    _scanState = ScanState.WaitForSettle;
                    _scanSettleTicksRemaining = ScanSettleMaxTicks;
                    _scanLastFrameNum = -1;
                    _scanStableFrameCount = 0;
                    break;

                case ScanState.WaitForSettle:
                    TickWaitForSettle(irsdk);
                    break;

                case ScanState.WaitForYaml:
                    TickWaitForYaml(irsdk);
                    break;

                case ScanState.CaptureBaseline:
                    TickCaptureBaseline(irsdk);
                    break;

                case ScanState.ReadIncident:
                    TickReadIncident(irsdk);
                    break;

                case ScanState.SeekNext:
                    TickSeekNext(irsdk);
                    break;

                case ScanState.Validating:
                    TickValidate(irsdk);
                    break;
            }

            // Keep progress state string in sync with the enum so the dashboard can use it directly.
            // Terminal states (Complete/Error) are set by their tick methods before we reach here;
            // this sync ensures in-progress transitions (SeekingStart → WaitForSettle etc.) are also reflected.
            _scanProgress.State = _scanState.ToString();

            return _scanState != ScanState.Idle && _scanState != ScanState.Complete && _scanState != ScanState.Error;
        }

        private void TickWaitForSettle(IRacingSdk irsdk)
        {
            int currentFrame = GetInt(irsdk, "ReplayFrameNum"); // current playback position
            _scanProgress.CurrentFrameNum = currentFrame;

            if (currentFrame == _scanLastFrameNum && currentFrame >= 0)
            {
                _scanStableFrameCount++;
                if (_scanStableFrameCount >= ScanStableThreshold)
                {
                    // Frame has stabilized — seek is complete; always wait for YAML to refresh first
                    _scanSiuAtSettle = GetInt(irsdk, "SessionInfoUpdate");
                    _scanYamlWaitTicks = 0;
                    _scanState = ScanState.WaitForYaml;
                    return;
                }
            }
            else
            {
                _scanStableFrameCount = 0;
                _scanLastFrameNum = currentFrame;
            }

            _scanSettleTicksRemaining--;
            if (_scanSettleTicksRemaining <= 0)
            {
                // Timeout — frame hasn't settled. Treat current frame as stable.
                _scanSiuAtSettle = GetInt(irsdk, "SessionInfoUpdate");
                _scanYamlWaitTicks = 0;
                _scanState = ScanState.WaitForYaml;
            }
        }

        private void TickWaitForYaml(IRacingSdk irsdk)
        {
            int currentSiu = GetInt(irsdk, "SessionInfoUpdate");
            bool yamlRefreshed = currentSiu != _scanSiuAtSettle;

            if (yamlRefreshed || _scanYamlWaitTicks >= ScanYamlWaitMax)
            {
                if (!yamlRefreshed)
                {
                    // Timed out — YAML did not refresh; proceeding with stale data
                    EmitStructured("scan_yaml_timeout",
                        $"YAML did not refresh within timeout at frame {GetInt(irsdk, "ReplayFrameNum")}",
                        new Dictionary<string, object>
                        {
                            ["frame"]            = GetInt(irsdk, "ReplayFrameNum"),
                            ["siu_at_settle"]    = _scanSiuAtSettle,
                            ["wait_ticks"]       = _scanYamlWaitTicks,
                            ["incidents_so_far"] = _scanProgress.IncidentsFound,
                        },
                        level: "WARN", domain: "replay_scan");
                }
                // YAML refreshed (or timed out) — decide next state
                bool atSessionStart = _scanProgress.Snapshots.Count == 0 && _scanProgress.IncidentsFound == 0;
                _scanState = atSessionStart ? ScanState.CaptureBaseline : ScanState.ReadIncident;
                return;
            }
            _scanYamlWaitTicks++;
        }

        private void TickCaptureBaseline(IRacingSdk irsdk)
        {
            // Capture YAML incident totals at session start as the delta baseline.
            // Running at session start frame means totals should be 0 for all drivers.
            var driverList = irsdk.Data?.SessionInfo?.DriverInfo?.Drivers;
            if (driverList != null)
            {
                foreach (var d in driverList)
                {
                    if (d.CarIdx < 0) continue;
                    _scanYamlBaseline[d.CarIdx] = d.CurDriverIncidentCount;
                    _scanYamlPrev[d.CarIdx] = d.CurDriverIncidentCount;
                }
            }
            _scanState = ScanState.SeekNext;
            OnBaselineCaptured?.Invoke();
        }

        private void TickReadIncident(IRacingSdk irsdk)
        {
            int frameNum = GetInt(irsdk, "ReplayFrameNum");
            double sessionTime = 0;
            try { sessionTime = irsdk.Data.GetDouble("SessionTime"); } catch { }
            int camCarIdx = GetInt(irsdk, "CamCarIdx");
            int sessionNum = GetInt(irsdk, "SessionNum");
            int sessionTick = GetInt(irsdk, "SessionTick");
            int sessionFlags = GetInt(irsdk, "SessionFlags");
            int sessionState = GetInt(irsdk, "SessionState");

            // Check for end-of-incidents: terminate only when BOTH frame AND SIU are unchanged.
            // Using frame alone causes false termination when two cars have incidents at the same
            // frame — NextIncident returns that frame twice (once per car) but SIU changes between reads.
            int currentSiu = GetInt(irsdk, "SessionInfoUpdate");
            if (frameNum == _scanLastReadFrameNum && currentSiu == _scanLastReadSiu)
            {
                // NextIncident didn't advance frame or refresh YAML — no more incidents
                _scanState = ScanState.Validating;
                return;
            }
            _scanLastReadFrameNum = frameNum;
            _scanLastReadSiu = currentSiu;

            // Capture full telemetry snapshot
            var snapshot = new IncidentSnapshot
            {
                IncidentId = $"scan_{_scanProgress.IncidentsFound:D4}_{frameNum}",
                ReplayFrameNum = frameNum,
                SessionTime = sessionTime,
                SessionTimeFormatted = FormatTime(sessionTime),
                SessionNum = sessionNum,
                SessionTick = sessionTick,
                CamCarIdx = camCarIdx,
                SessionFlags = sessionFlags,
                SessionState = sessionState,
                TrackName = TrackName,
                TrackCategory = TrackCategory
            };

            // Read all CarIdx arrays
            snapshot.CarIdxLapDistPct = ReadFloatArray(irsdk, "CarIdxLapDistPct");
            snapshot.CarIdxRPM = ReadFloatArray(irsdk, "CarIdxRPM");
            snapshot.CarIdxSteer = ReadFloatArray(irsdk, "CarIdxSteer");
            snapshot.CarIdxBestLapTime = ReadFloatArray(irsdk, "CarIdxBestLapTime");
            snapshot.CarIdxLastLapTime = ReadFloatArray(irsdk, "CarIdxLastLapTime");
            snapshot.CarIdxEstTime = ReadFloatArray(irsdk, "CarIdxEstTime");
            snapshot.CarIdxF2Time = ReadFloatArray(irsdk, "CarIdxF2Time");

            snapshot.CarIdxTrackSurface = ReadIntArray(irsdk, "CarIdxTrackSurface");
            snapshot.CarIdxTrackSurfaceMaterial = ReadIntArray(irsdk, "CarIdxTrackSurfaceMaterial");
            snapshot.CarIdxLap = ReadIntArray(irsdk, "CarIdxLap");
            snapshot.CarIdxLapCompleted = ReadIntArray(irsdk, "CarIdxLapCompleted");
            snapshot.CarIdxGear = ReadIntArray(irsdk, "CarIdxGear");
            snapshot.CarIdxPosition = ReadIntArray(irsdk, "CarIdxPosition");
            snapshot.CarIdxClassPosition = ReadIntArray(irsdk, "CarIdxClassPosition");
            snapshot.CarIdxFastRepairsUsed = ReadIntArray(irsdk, "CarIdxFastRepairsUsed");
            snapshot.CarIdxTireCompound = ReadIntArray(irsdk, "CarIdxTireCompound");

            snapshot.CarIdxOnPitRoad = ReadBoolArray(irsdk, "CarIdxOnPitRoad");
            snapshot.CarIdxPaceFlags = ReadIntArray(irsdk, "CarIdxPaceFlags");
            snapshot.CarIdxSessionFlags = ReadIntArray(irsdk, "CarIdxSessionFlags");

            // Cam-focused car physics (scalar telemetry only available for camera car)
            snapshot.CamSpeed              = GetFloat(irsdk, "Speed");
            snapshot.CamThrottle           = GetFloat(irsdk, "Throttle");
            snapshot.CamBrake              = GetFloat(irsdk, "Brake");
            snapshot.CamLatAccel           = GetFloat(irsdk, "LatAccel");
            snapshot.CamLongAccel          = GetFloat(irsdk, "LongAccel");
            snapshot.CamYawRate            = GetFloat(irsdk, "YawRate");
            snapshot.CamSteeringWheelAngle = GetFloat(irsdk, "SteeringWheelAngle");

            // Extended cam-car telemetry — all available scalars for the incident car
            snapshot.CamVelocityX           = GetFloat(irsdk, "VelocityX");
            snapshot.CamVelocityY           = GetFloat(irsdk, "VelocityY");
            snapshot.CamVelocityZ           = GetFloat(irsdk, "VelocityZ");
            snapshot.CamPitch               = GetFloat(irsdk, "Pitch");
            snapshot.CamRoll                = GetFloat(irsdk, "Roll");
            snapshot.CamYaw                 = GetFloat(irsdk, "Yaw");
            snapshot.CamPitchRate           = GetFloat(irsdk, "PitchRate");
            snapshot.CamRollRate            = GetFloat(irsdk, "RollRate");
            snapshot.CamVertAccel           = GetFloat(irsdk, "VertAccel");
            snapshot.CamClutch              = GetFloat(irsdk, "Clutch");
            snapshot.CamSteeringWheelTorque = GetFloat(irsdk, "SteeringWheelTorque");
            snapshot.CamShiftIndicatorPct   = GetFloat(irsdk, "ShiftIndicatorPct");
            snapshot.CamRPM                 = GetFloat(irsdk, "RPM");
            snapshot.CamGear                = GetInt(irsdk,   "Gear");
            snapshot.CamFuelLevel           = GetFloat(irsdk, "FuelLevel");
            snapshot.CamFuelLevelPct        = GetFloat(irsdk, "FuelLevelPct");
            snapshot.CamFuelUsePerHour      = GetFloat(irsdk, "FuelUsePerHour");
            snapshot.CamManifoldPress       = GetFloat(irsdk, "ManifoldPress");
            snapshot.CamWaterTemp           = GetFloat(irsdk, "WaterTemp");
            snapshot.CamOilTemp             = GetFloat(irsdk, "OilTemp");
            snapshot.CamOilPress            = GetFloat(irsdk, "OilPress");
            // Tire temperatures (inner/middle/outer per corner)
            snapshot.CamLFtempCL = GetFloat(irsdk, "LFtempCL"); snapshot.CamLFtempCM = GetFloat(irsdk, "LFtempCM"); snapshot.CamLFtempCR = GetFloat(irsdk, "LFtempCR");
            snapshot.CamRFtempCL = GetFloat(irsdk, "RFtempCL"); snapshot.CamRFtempCM = GetFloat(irsdk, "RFtempCM"); snapshot.CamRFtempCR = GetFloat(irsdk, "RFtempCR");
            snapshot.CamLRtempCL = GetFloat(irsdk, "LRtempCL"); snapshot.CamLRtempCM = GetFloat(irsdk, "LRtempCM"); snapshot.CamLRtempCR = GetFloat(irsdk, "LRtempCR");
            snapshot.CamRRtempCL = GetFloat(irsdk, "RRtempCL"); snapshot.CamRRtempCM = GetFloat(irsdk, "RRtempCM"); snapshot.CamRRtempCR = GetFloat(irsdk, "RRtempCR");
            // Tire wear (left/middle/right tread per corner)
            snapshot.CamLFwearL = GetFloat(irsdk, "LFwearL"); snapshot.CamLFwearM = GetFloat(irsdk, "LFwearM"); snapshot.CamLFwearR = GetFloat(irsdk, "LFwearR");
            snapshot.CamRFwearL = GetFloat(irsdk, "RFwearL"); snapshot.CamRFwearM = GetFloat(irsdk, "RFwearM"); snapshot.CamRFwearR = GetFloat(irsdk, "RFwearR");
            snapshot.CamLRwearL = GetFloat(irsdk, "LRwearL"); snapshot.CamLRwearM = GetFloat(irsdk, "LRwearM"); snapshot.CamLRwearR = GetFloat(irsdk, "LRwearR");
            snapshot.CamRRwearL = GetFloat(irsdk, "RRwearL"); snapshot.CamRRwearM = GetFloat(irsdk, "RRwearM"); snapshot.CamRRwearR = GetFloat(irsdk, "RRwearR");

            // Detect YAML incident deltas — which drivers got incident points at this frame
            var driverList = irsdk.Data?.SessionInfo?.DriverInfo?.Drivers;
            if (driverList != null)
            {
                foreach (var d in driverList)
                {
                    if (d.CarIdx < 0 || d.IsSpectator != 0) continue;
                    int currentInc = d.CurDriverIncidentCount;
                    _scanYamlPrev.TryGetValue(d.CarIdx, out int prevInc);
                    int delta = currentInc - prevInc;
                    if (delta > 0)
                    {
                        snapshot.DriversInvolved.Add(new IncidentDriverDelta
                        {
                            CarIdx = d.CarIdx,
                            UserId = d.UserID,
                            DriverName = d.UserName ?? $"Car {d.CarIdx}",
                            CarNumber = d.CarNumber ?? "?",
                            IncidentDelta = delta,
                            IncidentTotalAfter = currentInc,
                            TeamIncidentCount = d.TeamIncidentCount
                        });
                    }
                    _scanYamlPrev[d.CarIdx] = currentInc;
                }
            }

            // If no YAML deltas detected but iRacing pointed camera at a car, record that car as involved
            if (snapshot.DriversInvolved.Count == 0 && camCarIdx >= 0 && _drivers.TryGetValue(camCarIdx, out var camDriver))
            {
                snapshot.DriversInvolved.Add(new IncidentDriverDelta
                {
                    CarIdx = camCarIdx,
                    UserId = camDriver.UserId,
                    DriverName = camDriver.UserName ?? $"Car {camCarIdx}",
                    CarNumber = camDriver.CarNumber ?? "?",
                    IncidentDelta = 0, // unknown — YAML may not have updated yet
                    IncidentTotalAfter = camDriver.IncidentCount
                });
            }

            // Route each driver delta through AddIncident for dedup, persist, and broadcast
            string scanSessionPrefix = ComputeSessionPrefix(_subSessionId, sessionNum);
            foreach (var dd in snapshot.DriversInvolved)
            {
                if (dd.IncidentDelta <= 0) continue;
                _drivers.TryGetValue(dd.CarIdx, out var dRec);
                var ev = new IncidentEvent
                {
                    Id = ComputeScanIncidentFingerprint(_subSessionId, sessionNum, frameNum, dd.UserId, dd.IncidentDelta),
                    SessionPrefix = scanSessionPrefix,
                    SnapshotRef = snapshot.IncidentId,
                    SubSessionId = _subSessionId,
                    UserId = dd.UserId,
                    SessionTime = sessionTime,
                    SessionTimeFormatted = FormatTime(sessionTime),
                    CarIdx = dd.CarIdx,
                    DriverName = dd.DriverName,
                    CarNumber = dd.CarNumber,
                    Delta = dd.IncidentDelta,
                    TotalAfter = dd.IncidentTotalAfter,
                    Type = ClassifyYamlDelta(dd.IncidentDelta),
                    Source = "replay_scan",
                    ReplayFrameNum = frameNum,
                    Lap = (dd.CarIdx >= 0 && dd.CarIdx < MaxCars) ? snapshot.CarIdxLap[dd.CarIdx] : 0,
                    TrackPct = (dd.CarIdx >= 0 && dd.CarIdx < MaxCars) ? snapshot.CarIdxLapDistPct[dd.CarIdx] : 0f
                };
                dd.IncidentEventId = ev.Id;
                dd.SessionPrefix   = ev.SessionPrefix;
                TryIdentifyOtherCar(ev, ProximityPctNear);
                _metrics.YamlIncidentEvents++;
                AddIncident(ev);
            }

            _scanProgress.Snapshots.Add(snapshot);
            _scanProgress.IncidentsFound = _scanProgress.Snapshots.Count;

            var scanFields = new Dictionary<string, object>
            {
                ["incident_num"]      = _scanProgress.IncidentsFound,
                ["session_prefix"]    = scanSessionPrefix,
                ["snapshot_id"]       = snapshot.IncidentId,
                ["frame"]             = frameNum,
                ["session_time"]      = sessionTime,
                ["session_num"]       = sessionNum,
                ["cam_car_idx"]       = camCarIdx,
                ["drivers_involved"]  = snapshot.DriversInvolved.Count,
                // Cam-focused car physics at incident frame
                ["cam_speed_ms"]      = snapshot.CamSpeed,
                ["cam_throttle"]      = snapshot.CamThrottle,
                ["cam_brake"]         = snapshot.CamBrake,
                ["cam_lat_accel"]     = snapshot.CamLatAccel,
                ["cam_long_accel"]    = snapshot.CamLongAccel,
                ["cam_yaw_rate"]      = snapshot.CamYawRate,
                ["cam_steer_rad"]     = snapshot.CamSteeringWheelAngle,
                ["session_flags"]     = snapshot.SessionFlags,
                ["session_state"]     = snapshot.SessionState,
            };
            // Per-driver deltas (inline as indexed fields for Grafana)
            for (int di = 0; di < snapshot.DriversInvolved.Count; di++)
            {
                var dd = snapshot.DriversInvolved[di];
                scanFields[$"driver{di}_car"]          = dd.CarNumber;
                scanFields[$"driver{di}_name"]         = dd.DriverName;
                scanFields[$"driver{di}_delta"]        = dd.IncidentDelta;
                scanFields[$"driver{di}_total"]        = dd.IncidentTotalAfter;
                scanFields[$"driver{di}_team_inc"]     = dd.TeamIncidentCount;
                scanFields[$"driver{di}_user_id"]      = dd.UserId;
                scanFields[$"driver{di}_incident_id"]  = dd.IncidentEventId;
                scanFields[$"driver{di}_session_prefix"] = dd.SessionPrefix;
                // Cam-car pace flags at incident frame for this driver's slot
                if (snapshot.CarIdxPaceFlags != null && dd.CarIdx >= 0 && dd.CarIdx < snapshot.CarIdxPaceFlags.Length)
                    scanFields[$"driver{di}_pace_flags"] = snapshot.CarIdxPaceFlags[dd.CarIdx];
            }
            EmitStructured("replay_scan_incident", $"Scan: incident #{_scanProgress.IncidentsFound} at frame {frameNum} (cam={camCarIdx})",
                scanFields, domain: "replay_scan");

            // Safety cap
            if (_scanProgress.IncidentsFound >= ScanMaxIncidents)
            {
                _scanState = ScanState.Validating;
                return;
            }

            // Seek to next incident
            _scanState = ScanState.SeekNext;
        }

        private void TickSeekNext(IRacingSdk irsdk)
        {
            // Issue NextIncident search and wait for settle
            irsdk.ReplaySearch(IRacingSdkEnum.RpySrchMode.NextIncident);
            _scanState = ScanState.WaitForSettle;
            _scanSettleTicksRemaining = ScanSettleMaxTicks;
            _scanLastFrameNum = GetInt(irsdk, "ReplayFrameNum");
            _scanStableFrameCount = 0;
        }

        private void TickValidate(IRacingSdk irsdk)
        {
            // Compare captured incident deltas vs YAML final totals
            var validation = new ReplayScanValidation { Valid = true };

            // Sum captured deltas per driver across all snapshots
            var capturedPerDriver = new Dictionary<int, int>();
            foreach (var snap in _scanProgress.Snapshots)
            {
                foreach (var d in snap.DriversInvolved)
                {
                    if (!capturedPerDriver.ContainsKey(d.CarIdx))
                        capturedPerDriver[d.CarIdx] = 0;
                    capturedPerDriver[d.CarIdx] += d.IncidentDelta;
                }
            }

            // Compare against YAML final totals (current minus baseline)
            var driverList = irsdk.Data?.SessionInfo?.DriverInfo?.Drivers;
            if (driverList != null)
            {
                foreach (var d in driverList)
                {
                    if (d.CarIdx < 0 || d.IsSpectator != 0) continue;
                    _scanYamlBaseline.TryGetValue(d.CarIdx, out int baseline);
                    int expected = d.CurDriverIncidentCount - baseline;
                    capturedPerDriver.TryGetValue(d.CarIdx, out int captured);
                    validation.TotalExpected += expected;
                    validation.TotalCaptured += captured;

                    if (captured != expected && expected > 0)
                    {
                        validation.Valid = false;
                        validation.DriverMismatches.Add(new DriverIncidentMismatch
                        {
                            CarIdx = d.CarIdx,
                            DriverName = d.UserName ?? $"Car {d.CarIdx}",
                            Captured = captured,
                            Expected = expected
                        });
                    }
                }
            }

            _scanProgress.Validation = validation;
            _scanProgress.CompletedAtUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            _scanState = ScanState.Complete;
            _scanIrsdk = null;

            var completeFields = new Dictionary<string, object>
            {
                ["incidents_found"]  = _scanProgress.IncidentsFound,
                ["valid"]            = validation.Valid,
                ["total_captured"]   = validation.TotalCaptured,
                ["total_expected"]   = validation.TotalExpected,
                ["mismatches"]       = validation.DriverMismatches.Count,
                ["session_num"]      = _scanSessionNum,
                ["sub_session_id"]   = _subSessionId,
            };
            for (int mi = 0; mi < validation.DriverMismatches.Count; mi++)
            {
                var mm = validation.DriverMismatches[mi];
                completeFields[$"mismatch{mi}_car"]      = mm.CarIdx;
                completeFields[$"mismatch{mi}_driver"]   = mm.DriverName;
                completeFields[$"mismatch{mi}_captured"] = mm.Captured;
                completeFields[$"mismatch{mi}_expected"] = mm.Expected;
            }
            EmitStructured("replay_scan_complete",
                $"Replay scan complete: {_scanProgress.IncidentsFound} incidents found. Valid={validation.Valid} (captured={validation.TotalCaptured}, expected={validation.TotalExpected})",
                completeFields,
                level: validation.Valid ? "INFO" : "WARN",
                domain: "replay_scan");

            OnScanComplete?.Invoke(_scanProgress);
        }

        // ── Array read helpers for scan snapshots ──

        private float[] ReadFloatArray(IRacingSdk irsdk, string name)
        {
            var buf = new float[MaxCars];
            TryGetFloatArray(irsdk, name, buf);
            return buf;
        }

        private int[] ReadIntArray(IRacingSdk irsdk, string name)
        {
            try
            {
                var props = irsdk.Data.TelemetryDataProperties;
                if (props == null || !props.ContainsKey(name)) return new int[MaxCars];
                var buf = new int[MaxCars];
                irsdk.Data.GetIntArray(props[name], buf, 0, buf.Length);
                return buf;
            }
            catch { return new int[MaxCars]; }
        }

        private bool[] ReadBoolArray(IRacingSdk irsdk, string name)
        {
            try
            {
                var props = irsdk.Data.TelemetryDataProperties;
                if (props == null || !props.ContainsKey(name)) return new bool[MaxCars];
                var buf = new bool[MaxCars];
                irsdk.Data.GetBoolArray(props[name], buf, 0, buf.Length);
                return buf;
            }
            catch { return new bool[MaxCars]; }
        }
#endif

        // ================================================================
        //  Shared helpers (available with or without SIMHUB_SDK)
        // ================================================================

        private int FindNearestCar(int excludeIdx, float distPct, float maxDelta)
        {
            if (distPct < 0) return -1;
            int bestIdx = -1;
            float bestDist = maxDelta;
            for (int i = 0; i < MaxCars; i++)
            {
                if (i == excludeIdx) continue;
                if (_distPctBuf[i] < 0) continue;
                float d = Math.Abs(DistDelta(distPct, _distPctBuf[i]));
                if (d < bestDist)
                {
                    bestDist = d;
                    bestIdx = i;
                }
            }
            return bestIdx;
        }

        private void TryIdentifyOtherCar(IncidentEvent ev, float maxDelta)
        {
            if (ev.CarIdx < 0 || ev.CarIdx >= MaxCars) return;
            float myDist = _distPctBuf[ev.CarIdx];
            if (myDist < 0) return;

            int nearbyIdx = FindNearestCar(ev.CarIdx, myDist, maxDelta);
            if (nearbyIdx >= 0 && _drivers.TryGetValue(nearbyIdx, out var other))
            {
                ev.OtherCarIdx = nearbyIdx;
                ev.OtherCarNumber = other.CarNumber ?? "?";
                ev.OtherDriverName = other.UserName ?? "Unknown";
            }
        }

        private void AddIncident(IncidentEvent ev)
        {
            if (ev == null) return;
            // #region agent log
            AgentDebugLog.WriteB0C27E("H1", "IncidentTracker.AddIncident", "AddIncident_called", new { evId = ev.Id, carIdx = ev.CarIdx, carNumber = ev.CarNumber });
            // #endregion

            // When YAML adds a player incident, merge into a recent telemetry-added player event if present (exact time from 60 Hz, type from YAML).
            if (string.Equals(ev.Source, "yaml", StringComparison.OrdinalIgnoreCase) && ev.CarIdx == PlayerCarIdx)
            {
                for (int i = 0; i < _incidents.Count; i++)
                {
                    var existing = _incidents[i];
                    if (existing.CarIdx != PlayerCarIdx || !string.Equals(existing.Source, "telemetry", StringComparison.OrdinalIgnoreCase))
                        continue;
                    double dt = ev.SessionTime - existing.SessionTime;
                    if (dt >= 0 && dt <= 10.0)
                    {
                        // #region agent log
                        AgentDebugLog.Write740824("H4", "IncidentTracker.AddIncident", "merged_into_telemetry", new { evId = ev.Id, existingSessionTime = existing.SessionTime });
                        AgentDebugLog.WriteB0C27E("H1", "IncidentTracker.AddIncident", "AddIncident_merged", new { evId = ev.Id, existingSessionTime = existing.SessionTime });
                        // #endregion
                        existing.Type = ev.Type;
                        existing.Delta = ev.Delta;
                        existing.TotalAfter = ev.TotalAfter;
                        if (!string.IsNullOrEmpty(ev.Cause)) existing.Cause = ev.Cause;
                        existing.Source = "yaml";
                        return;
                    }
                }
            }

            // Scan events carry exact frame and snapshotRef. If a YAML event already exists for the same
            // incident (same carIdx + userId + delta within the grace window), upgrade it with the exact
            // replay frame and snapshotRef rather than adding a duplicate entry in the feed.
            if (string.Equals(ev.Source, "replay_scan", StringComparison.OrdinalIgnoreCase) && ev.ReplayFrameNum > 0)
            {
                int timeKey = (int)(ev.SessionTime / GraceWindowSeconds);
                for (int i = 0; i < _incidents.Count; i++)
                {
                    var ex = _incidents[i];
                    if (ex.CarIdx != ev.CarIdx || ex.UserId != ev.UserId || ex.Delta != ev.Delta) continue;
                    int exTimeKey = (int)(ex.SessionTime / GraceWindowSeconds);
                    if (Math.Abs(exTimeKey - timeKey) > 1) continue;
                    // Upgrade existing event with scan-precise data
                    ex.ReplayFrameNum = ev.ReplayFrameNum;
                    if (!string.IsNullOrEmpty(ev.SnapshotRef)) ex.SnapshotRef = ev.SnapshotRef;
                    // Proximity at exact incident frame is more accurate than at YAML-fire time
                    if (ev.OtherCarIdx >= 0)
                    {
                        ex.OtherCarIdx    = ev.OtherCarIdx;
                        ex.OtherCarNumber = ev.OtherCarNumber;
                        ex.OtherDriverName = ev.OtherDriverName;
                    }
                    if (!string.IsNullOrEmpty(ev.Cause)) ex.Cause = ev.Cause;
                    if (string.Equals(ex.Source, "yaml", StringComparison.OrdinalIgnoreCase))
                        ex.Source = "replay_scan";
                    return;
                }
                // No matching YAML event — fall through and add as a new scan-only event
            }

            // Deduplicate by fingerprint (session + driver + quantized time + delta).
            for (int i = 0; i < _incidents.Count; i++)
            {
                if (_incidents[i].Id == ev.Id)
                {
                    // #region agent log
                    AgentDebugLog.Write740824("H4", "IncidentTracker.AddIncident", "deduped", new { evId = ev.Id, carNumber = ev.CarNumber, sessionTime = ev.SessionTime });
                    AgentDebugLog.WriteB0C27E("H1", "IncidentTracker.AddIncident", "AddIncident_deduped", new { evId = ev.Id, carNumber = ev.CarNumber, sessionTime = ev.SessionTime });
                    // #endregion
                    return;
                }
            }

            // #region agent log
            AgentDebugLog.Write740824("H4", "IncidentTracker.AddIncident", "emitting", new { evId = ev.Id, carNumber = ev.CarNumber, delta = ev.Delta, sessionTime = ev.SessionTime });
            if (ev.CarIdx == PlayerCarIdx)
                AgentDebugLog.WriteB0C27E("H2", "IncidentTracker.AddIncident", "emitting_player", new { evId = ev.Id, carNumber = ev.CarNumber, delta = ev.Delta, sessionTime = ev.SessionTime, source = ev.Source });
            AgentDebugLog.WriteB0C27E("H1", "IncidentTracker.AddIncident", "incident_about_to_emit", new { evId = ev.Id, carNumber = ev.CarNumber, carIdx = ev.CarIdx, delta = ev.Delta, eventType = ev.Type });
            // #endregion
            _metrics.TotalEvents++;
            _metrics.LastDetectionSessionTime = ev.SessionTime;
            var incidentFields = new Dictionary<string, object>
            {
                ["incident_id"]    = ev.Id,
                ["session_prefix"] = ev.SessionPrefix,
                ["sub_session_id"] = ev.SubSessionId,
                ["user_id"]        = ev.UserId,
                ["car_idx"]        = ev.CarIdx,
                ["incident_type"]  = ev.Type,
                ["source"]         = ev.Source,
                ["car_number"]     = ev.CarNumber,
                ["driver_name"]    = ev.DriverName,
                ["delta"]          = ev.Delta,
                ["total_after"]    = ev.TotalAfter,
                ["track_pct"]      = ev.TrackPct,
                ["session_time"]   = ev.SessionTime,
                ["session_num"]    = _lastKnownSessionNum,
                ["replay_frame"]   = ev.ReplayFrameNum,
                ["lap"]            = ev.Lap
            };
            if (!string.IsNullOrEmpty(ev.Cause))
                incidentFields["cause"] = ev.Cause;
            if (!string.IsNullOrEmpty(ev.OtherDriverName))
            {
                incidentFields["other_car_number"] = ev.OtherCarNumber;
                incidentFields["other_driver_name"] = ev.OtherDriverName;
            }
            // #region agent log — confirm what we capture for incident_detected (file/Grafana)
            var fieldKeys = new List<string>(incidentFields.Keys);
            AgentDebugLog.WriteB0C27E("ID", "IncidentTracker.AddIncident", "incident_captured_fields", new { incident_id = ev.Id, fieldCount = incidentFields.Count, fieldKeys });
            // #endregion
            EmitStructured("incident_detected", $"Incident detected: {ev.Type} #{ev.CarNumber} {ev.DriverName}", incidentFields, "INFO", "incident", ev.Id);
            _incidents.Insert(0, ev);
            if (_incidents.Count > MaxIncidents)
                _incidents.RemoveAt(_incidents.Count - 1);
            _pendingBroadcast.Enqueue(ev);
            OnIncidentPersist?.Invoke(ev);
        }

        private static int ClampIncidentCount(int value)
        {
            if (value < 0) return 0;
            if (value > 999) return 999;
            return value;
        }

        private static bool IsValidIncidentValues(int delta, int totalAfter)
        {
            if (delta <= 0 || delta > 50) return false;
            if (totalAfter < 0 || totalAfter > 999) return false;
            return true;
        }

        private static float DistDelta(float a, float b)
        {
            float d = a - b;
            if (d > 0.5f) d -= 1f;
            else if (d < -0.5f) d += 1f;
            return d;
        }

        private static string ClassifyYamlDelta(int delta)
        {
            // Standard iRacing incident point values are 1, 2, or 4.
            // Any other value is a batched YAML flush — report as "batched" not "6x".
            if (delta == 1) return "1x";
            if (delta == 2) return "2x";
            if (delta == 4) return "4x";
            return "batched";
        }

        private static string FormatTime(double totalSeconds)
        {
            if (double.IsNaN(totalSeconds) || totalSeconds < 0 || double.IsInfinity(totalSeconds))
                return "0:00";
            int minutes = (int)(totalSeconds / 60);
            int seconds = (int)(totalSeconds % 60);
            return $"{minutes}:{seconds:D2}";
        }

        private static string ShortId()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        /// <summary>
        /// Hierarchical incident fingerprint (VIN-style).
        /// Format: ir_{subSessionId}_s{sessionNum}_t{timeKey}_u{userId}_d{delta}
        /// Example: ir_4521098_s2_t71_u98765_d2
        /// subSessionId is globally unique per iRacing event — no two races share one.
        /// Uses 2s time bucket so YAML lag at high replay speed still dedupes across multiple users.
        /// </summary>
        private static string ComputeIncidentFingerprintV2(
            int subSessionId, int sessionNum, double sessionTime, int userId, int delta)
        {
            int timeKey = (int)(sessionTime / GraceWindowSeconds);
            return $"ir_{subSessionId}_s{sessionNum}_t{timeKey}_u{userId}_d{delta}";
        }

        /// <summary>
        /// Fingerprint for replay-scan incidents. Uses exact replay frame instead of session-time bucket
        /// so back-to-back incidents for the same driver (within the 2-s grace window) are not falsely deduped.
        /// Prefixed "scan_" so IDs never collide with YAML event IDs.
        /// </summary>
        private static string ComputeScanIncidentFingerprint(
            int subSessionId, int sessionNum, int frameNum, int userId, int delta)
            => $"scan_{subSessionId}_s{sessionNum}_f{frameNum}_u{userId}_d{delta}";

        /// <summary>Session prefix (first 3 segments of fingerprint): ir_{subSessionId}_s{sessionNum}.</summary>
        private static string ComputeSessionPrefix(int subSessionId, int sessionNum)
            => $"ir_{subSessionId}_s{sessionNum}";

        /// <summary>Public helper: session prefix for the current session. Used by plugin for session_meta persistence.</summary>
        public string GetSessionPrefix() =>
            ComputeSessionPrefix(_subSessionId, _lastKnownSessionNum);
    }
}
