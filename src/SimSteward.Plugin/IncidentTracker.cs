#if SIMHUB_SDK
using IRSDKSharper;
#endif

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
    }

    /// <summary>
    /// Incident detection from iRacing session YAML (Layer 4 only).
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

        // Layer 4 state
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
            var ev = new IncidentEvent
            {
                Id = subSessionId > 0
                    ? ComputeIncidentFingerprint(subSessionId, sessionNum, PlayerCarIdx, sessionTime, 1)
                    : ShortId(),
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
        /// Returns a point-in-time copy of the per-layer detection counters.
        /// Safe to call from any thread; IncidentTracker fields are only mutated
        /// on the DataUpdate thread, so a shallow copy here is sufficient.
        /// </summary>
        public DetectionMetrics GetMetricsSnapshot() => new DetectionMetrics
        {
            L4YamlEvents          = _metrics.L4YamlEvents,
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

        private void ResetMetrics()
        {
            _metrics.L4YamlEvents = 0;
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
            // Use ReplayFrameNum as current playback position (per END-OF-SESSION-DATAPOINTS: "Replay position").
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
                // Log only; do not reset incident baselines or counts (per user requirement and STATE_AND_ROADMAP).
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

            // ── Read telemetry arrays (for Layer 4 cause inference / other-car identification) ─
            TryGetFloatArray(irsdk, "CarIdxLapDistPct", _distPctBuf);
            _lastReplayPositionFrame = replayFrame;

            // ── Track metadata (once per session) ───────────────────
            UpdateTrackMetadata(irsdk);

            // ── Layer 4: YAML-based tracking (all drivers) ──────────
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
        //  Layer 4 — YAML-based tracking (all drivers)
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

            // ── Rebuild driver roster and Layer 4: CurDriverIncidentCount (all drivers) ─
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
                            var ev = new IncidentEvent
                            {
                                Id = ComputeIncidentFingerprint(_subSessionId, _lastKnownSessionNum, d.CarIdx, sessionTime, delta),
                                SubSessionId = _subSessionId,
                                UserId = driverRec?.UserId ?? 0,
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
                            _metrics.L4YamlEvents++;
                            AddIncident(ev);
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
                ["incident_id"] = ev.Id,
                ["sub_session_id"] = ev.SubSessionId,
                ["user_id"] = ev.UserId,
                ["incident_type"] = ev.Type,
                ["car_number"] = ev.CarNumber,
                ["driver_name"] = ev.DriverName,
                ["delta"] = ev.Delta,
                ["session_time"] = ev.SessionTime,
                ["session_num"] = _lastKnownSessionNum,
                ["replay_frame"] = ev.ReplayFrameNum,
                ["lap"] = ev.Lap
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
        /// Deterministic incident fingerprint from intrinsic session/incident properties.
        /// Same incident always produces the same ID regardless of camera view or when it's detected.
        /// Uses quantized session time (grace window) so YAML lag at high replay speed still dedupes.
        /// </summary>
        private static string ComputeIncidentFingerprint(
            int subSessionId, int sessionNum, int carIdx, double sessionTime, int delta)
        {
            int timeKey = (int)(sessionTime / GraceWindowSeconds);
            var input = $"{subSessionId}:{sessionNum}:{carIdx}:{timeKey}:{delta}";
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
                return BitConverter.ToString(hash, 0, 8).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
