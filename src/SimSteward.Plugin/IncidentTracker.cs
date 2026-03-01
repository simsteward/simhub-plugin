#if SIMHUB_SDK
using IRSDKSharper;
#endif

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using SimSteward.Plugin.MemoryBank;

namespace SimSteward.Plugin
{
    public class DriverRecord
    {
        [JsonProperty("carIdx")]     public int CarIdx { get; set; }
        [JsonProperty("userName")]   public string UserName { get; set; }
        [JsonProperty("carNumber")]  public string CarNumber { get; set; }
        [JsonProperty("incidents")]  public int IncidentCount { get; set; }
        [JsonProperty("isPlayer")]   public bool IsPlayer { get; set; }
        [JsonProperty("isSpectator")] public bool IsSpectator { get; set; }
    }

    public class IncidentEvent
    {
        [JsonProperty("id")]                    public string Id { get; set; }
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
        [JsonProperty("peakG")]                 public float PeakG { get; set; }
        [JsonProperty("lap")]                   public int Lap { get; set; }
        [JsonProperty("trackPct")]              public float TrackPct { get; set; }
    }

    /// <summary>
    /// Multi-layer incident detection from iRacing shared memory.
    ///
    ///   Layer 1 — PlayerCarMyIncidentCount telemetry (60 Hz): exact per-incident
    ///             deltas for the player. Delta value IS the type (1x/2x/4x).
    ///             Cross-referenced with LongAccel/LatAccel/YawRate for cause.
    ///
    ///   Layer 2 — CarIdxLapDistPct velocity → G-force (all cars, ~60 Hz):
    ///             detects impacts via sudden deceleration for every car on track.
    ///
    ///   Layer 3 — CarIdxTrackSurface transitions (all cars, ~60 Hz):
    ///             detects off-track events via OnTrack→OffTrack for every car.
    ///
    ///   Layer 4 — Session YAML ResultsPositions[].Incidents (all drivers):
    ///             authoritative totals, batched at high replay speeds.
    ///
    ///   0x      — Physics-detected events (contact/off-track) without a
    ///             corresponding incident count change → light contact or brush.
    /// </summary>
    public class IncidentTracker
    {
        private const int MaxCars = 64;
        private const int MaxIncidents = 200;
        private const int MaxPendingBroadcast = 50;

        private const float GForceImpactThreshold = 3.0f;
        private const float GForcePlayerContactThreshold = 5.0f;
        private const float ProximityPctClose = 0.008f;
        private const float ProximityPctNear = 0.025f;
        private const double PhysicsEventCooldownSec = 3.0;
        private const float YawRateSpinThreshold = 1.2f;

        private class CarPhysicsState
        {
            public float PrevDistPct = -1;
            public float Velocity;
            public float PrevVelocity;
            public int PrevTrackSurface = -1;
            public double LastImpactEventTime = -999;
            public double LastOffTrackEventTime = -999;
            public bool HasPrev;

            public void Clear()
            {
                PrevDistPct = -1;
                Velocity = 0;
                PrevVelocity = 0;
                PrevTrackSurface = -1;
                LastImpactEventTime = -999;
                LastOffTrackEventTime = -999;
                HasPrev = false;
            }
        }

        // Layer 4 state
        private readonly Dictionary<int, int> _prevYamlIncidents = new Dictionary<int, int>();
        private readonly Dictionary<int, DriverRecord> _drivers = new Dictionary<int, DriverRecord>();
        private readonly List<IncidentEvent> _incidents = new List<IncidentEvent>();
        private readonly ConcurrentQueue<IncidentEvent> _pendingBroadcast = new ConcurrentQueue<IncidentEvent>();

        // Layer 1 state
        private int _prevPlayerIncidentCount = -1;
        private int _lastSessionInfoUpdate = -1;
        private bool _baselineEstablished;
        private double _lastSessionTime = -1;
        private int _lastReplayFrameNum = -1;
        private int _lastKnownSessionNum = -1; // -1 = not yet seen; used for session-change detection
        private bool _sessionInfoNullLogged;   // suppress repeated "SessionInfo is null" log entries
        private bool _baselineJustEstablished; // true for one Update() after baseline is established

        // Layer 2/3 state
        private readonly CarPhysicsState[] _carPhysics;
        private readonly float[] _distPctBuf = new float[MaxCars];
        private readonly int[] _trackSurfBuf = new int[MaxCars];
        private float _trackLengthM = 3000f;
        private bool _isDirt;
        private double _prevPhysicsTime = -1;
        private bool _intArrayAvailable = true;
        private bool _trackMetadataRead;

        // Metrics state — cumulative per iRacing connection, reset on disconnect
        private readonly DetectionMetrics _metrics = new DetectionMetrics();

        public int PlayerCarIdx { get; private set; } = -1;
        public int PlayerIncidentCount { get; private set; }
        public bool BaselineEstablished => _baselineEstablished;
        /// <summary>True for one Update() after baseline transitions to established; plugin can emit a log event then clear.</summary>
        public bool BaselineJustEstablished => _baselineJustEstablished;

        // Track metadata exposed for snapshot/diagnostics
        public float TrackLengthM => _trackLengthM;
        public bool IsDirt => _isDirt;
        public string TrackName { get; private set; } = "";
        public string TrackCategory { get; private set; } = "Road";

        // Optional log callback: (message) => logger.Info(message). Set by the plugin after construction.
        public Action<string> LogInfo { get; set; }

        public IncidentTracker()
        {
            _carPhysics = new CarPhysicsState[MaxCars];
            for (int i = 0; i < MaxCars; i++)
                _carPhysics[i] = new CarPhysicsState();
        }

        public List<DriverRecord> GetDriverSnapshot()
        {
            var list = new List<DriverRecord>(_drivers.Values);
            list.Sort((a, b) => b.IncidentCount.CompareTo(a.IncidentCount));
            return list;
        }

        public List<IncidentEvent> GetIncidentFeed() => new List<IncidentEvent>(_incidents);

        /// <summary>
        /// Returns a point-in-time copy of the per-layer detection counters.
        /// Safe to call from any thread; IncidentTracker fields are only mutated
        /// on the DataUpdate thread, so a shallow copy here is sufficient.
        /// </summary>
        public DetectionMetrics GetMetricsSnapshot() => new DetectionMetrics
        {
            L1PlayerEvents        = _metrics.L1PlayerEvents,
            L2PhysicsImpacts      = _metrics.L2PhysicsImpacts,
            L3OffTrackEvents      = _metrics.L3OffTrackEvents,
            L4YamlEvents          = _metrics.L4YamlEvents,
            ZeroXEvents           = _metrics.ZeroXEvents,
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
            _prevPlayerIncidentCount = -1;
            _lastSessionInfoUpdate = -1;
            _baselineEstablished = false;
            _lastSessionTime = -1;
            _lastReplayFrameNum = -1;
            _lastKnownSessionNum = -1;
            _sessionInfoNullLogged = false;
            _baselineJustEstablished = false;
            PlayerCarIdx = -1;
            PlayerIncidentCount = 0;
            _prevPhysicsTime = -1;
            _trackMetadataRead = false;
            for (int i = 0; i < MaxCars; i++)
                _carPhysics[i].Clear();

            ResetMetrics();
        }

        private void ResetMetrics()
        {
            _metrics.L1PlayerEvents = 0;
            _metrics.L2PhysicsImpacts = 0;
            _metrics.L3OffTrackEvents = 0;
            _metrics.L4YamlEvents = 0;
            _metrics.ZeroXEvents = 0;
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

            // ── Seek-backward detection ─────────────────────────────
            int replayFrame = GetInt(irsdk, "ReplayFrameNum");
            double prevSessionTime = _lastSessionTime;
            int prevReplayFrame = _lastReplayFrameNum;
            bool seekedBackward = false;
            if (prevSessionTime >= 0 && sessionTime < prevSessionTime - 5.0)
                seekedBackward = true;
            else if (prevReplayFrame >= 0 && replayFrame < prevReplayFrame - 60)
                seekedBackward = true;
            _lastSessionTime = sessionTime;
            _lastReplayFrameNum = replayFrame;

            if (seekedBackward)
            {
                LogInfo?.Invoke($"Seek-backward detected (sessionTime {prevSessionTime:F1}->{sessionTime:F1}): clearing incident baselines and metrics.");
                _prevYamlIncidents.Clear();
                _baselineEstablished = false;
                _incidents.Clear();
                while (_pendingBroadcast.TryDequeue(out _)) { }
                _prevPlayerIncidentCount = GetInt(irsdk, "PlayerCarMyIncidentCount"); // raw; baseline re-set
                _prevPhysicsTime = -1;
                for (int i = 0; i < MaxCars; i++)
                    _carPhysics[i].Clear();
                ResetMetrics();
            }

            // ── Session-change detection ────────────────────────────
            // Resets incident baselines when the user moves between practice / qualifying / race
            // without a full iRacing disconnect. Driver roster is preserved.
            int currentSessionNum = GetInt(irsdk, "SessionNum");
            if (_lastKnownSessionNum >= 0 && currentSessionNum != _lastKnownSessionNum)
            {
                LogInfo?.Invoke($"Session changed ({_lastKnownSessionNum}→{currentSessionNum}): clearing incident baselines.");
                _incidents.Clear();
                while (_pendingBroadcast.TryDequeue(out _)) { }
                _prevYamlIncidents.Clear();
                _baselineEstablished = false;
                _prevPlayerIncidentCount = -1;
                _prevPhysicsTime = -1;
                _lastSessionInfoUpdate = -1;
                for (int i = 0; i < MaxCars; i++)
                    _carPhysics[i].Clear();
                ResetMetrics();
            }
            _lastKnownSessionNum = currentSessionNum;

            // ── Read telemetry arrays ───────────────────────────────
            bool haveDistPct = TryGetFloatArray(irsdk, "CarIdxLapDistPct", _distPctBuf);
            bool haveTrackSurf = _intArrayAvailable && TryGetIntArray(irsdk, "CarIdxTrackSurface", _trackSurfBuf);

            // ── Track metadata (once per session) ───────────────────
            UpdateTrackMetadata(irsdk);

            // Snapshot player's previous track surface BEFORE physics updates it
            int playerPrevTrackSurf = -1;
            if (haveTrackSurf && PlayerCarIdx >= 0 && PlayerCarIdx < MaxCars)
                playerPrevTrackSurf = _carPhysics[PlayerCarIdx].PrevTrackSurface;

            // ── Layer 2 & 3: Physics-based detection (all cars) ─────
            double dt = (_prevPhysicsTime > 0) ? sessionTime - _prevPhysicsTime : 0;
            _prevPhysicsTime = sessionTime;
            if (haveDistPct && dt > 0.001 && dt < 2.0)
                ProcessPhysicsFrame(irsdk, sessionTime, (float)dt, haveTrackSurf);

            // ── Layer 4: YAML-based tracking (all drivers) ──────────
            int currentSIU = GetInt(irsdk, "SessionInfoUpdate");
            if (currentSIU != _lastSessionInfoUpdate)
            {
                _lastSessionInfoUpdate = currentSIU;
                RefreshFromYaml(irsdk, sessionTime);
            }

            // ── Layer 1: Player telemetry (60 Hz, precise) ──────────
            int playerCount = GetInt(irsdk, "PlayerCarMyIncidentCount");
            PlayerIncidentCount = playerCount;

            if (_prevPlayerIncidentCount < 0)
            {
                _prevPlayerIncidentCount = playerCount;
            }
            else if (playerCount != _prevPlayerIncidentCount)
            {
                int delta = playerCount - _prevPlayerIncidentCount;
                if (delta > 0 && IsValidIncidentValues(delta, playerCount))
                {
                    EmitPlayerIncident(irsdk, sessionTime, delta, playerCount);
                    if (_drivers.TryGetValue(PlayerCarIdx, out var dr))
                        dr.IncidentCount = ClampIncidentCount(playerCount);
                }
                _prevPlayerIncidentCount = playerCount; // store raw; only clamp for display
            }
            else if (haveDistPct)
            {
                // No incident count change — check for 0x events
                Check0xContact(irsdk, sessionTime, playerCount);
                if (haveTrackSurf && playerPrevTrackSurf == 3 &&
                    PlayerCarIdx >= 0 && PlayerCarIdx < MaxCars &&
                    _trackSurfBuf[PlayerCarIdx] == 0)
                {
                    Check0xOffTrack(sessionTime, playerCount);
                }
            }
        }

        // ================================================================
        //  Layer 1 — Player incident with physics-based cause
        // ================================================================

        private void EmitPlayerIncident(IRacingSdk irsdk, double sessionTime, int delta, int totalAfter)
        {
            _drivers.TryGetValue(PlayerCarIdx, out var dr);
            string cause = ClassifyPlayerCause(irsdk, delta);
            float peakG = GetPlayerCombinedG(irsdk);
            int lap = GetInt(irsdk, "Lap");
            float trackPct = (PlayerCarIdx >= 0 && PlayerCarIdx < MaxCars)
                ? _distPctBuf[PlayerCarIdx] : 0;

            var ev = new IncidentEvent
            {
                Id = ShortId(),
                SessionTime = sessionTime,
                SessionTimeFormatted = FormatTime(sessionTime),
                CarIdx = PlayerCarIdx,
                DriverName = dr?.UserName ?? "Player",
                CarNumber = dr?.CarNumber ?? "?",
                Delta = ClampIncidentCount(delta),
                TotalAfter = ClampIncidentCount(totalAfter),
                Type = ClassifyPlayerDelta(delta),
                Source = "player",
                Cause = cause,
                PeakG = peakG,
                Lap = lap,
                TrackPct = trackPct
            };
            TryIdentifyOtherCar(ev, ProximityPctNear);
            _metrics.L1PlayerEvents++;
            AddIncident(ev);
        }

        private string ClassifyPlayerCause(IRacingSdk irsdk, int delta)
        {
            float yawRate = SafeGetFloat(irsdk, "YawRate");
            float combinedG = GetPlayerCombinedG(irsdk);

            int trackSurf = -1;
            if (PlayerCarIdx >= 0 && PlayerCarIdx < MaxCars && _intArrayAvailable)
                trackSurf = _trackSurfBuf[PlayerCarIdx];

            switch (delta)
            {
                case 1:
                    return "off-track";
                case 2:
                    if (Math.Abs(yawRate) > YawRateSpinThreshold)
                        return "spin";
                    if (combinedG > 3.0f)
                        return "wall";
                    return "wall-or-spin";
                case 4:
                    return _isDirt ? "car-contact" : "heavy-contact";
                default:
                    if (delta >= 4) return "heavy-contact";
                    return "impact";
            }
        }

        private float GetPlayerCombinedG(IRacingSdk irsdk)
        {
            float longA = SafeGetFloat(irsdk, "LongAccel");
            float latA = SafeGetFloat(irsdk, "LatAccel");
            return (float)Math.Sqrt(longA * longA + latA * latA) / 9.80665f;
        }

        // ================================================================
        //  Layer 2 & 3 — Per-frame physics for all cars
        // ================================================================

        private void ProcessPhysicsFrame(IRacingSdk irsdk, double sessionTime, float dt, bool haveTrackSurf)
        {
            for (int i = 0; i < MaxCars; i++)
            {
                var state = _carPhysics[i];
                float distPct = _distPctBuf[i];

                // Car not in world
                if (distPct < 0)
                {
                    state.HasPrev = false;
                    if (haveTrackSurf) state.PrevTrackSurface = _trackSurfBuf[i];
                    continue;
                }

                if (!state.HasPrev)
                {
                    state.PrevDistPct = distPct;
                    state.Velocity = 0;
                    state.PrevVelocity = 0;
                    if (haveTrackSurf) state.PrevTrackSurface = _trackSurfBuf[i];
                    state.HasPrev = true;
                    continue;
                }

                // ── Layer 2: Velocity / G-force from position deltas ──
                float deltaPct = distPct - state.PrevDistPct;
                if (deltaPct > 0.5f) deltaPct -= 1f;
                if (deltaPct < -0.5f) deltaPct += 1f;

                float velocity = (deltaPct * _trackLengthM) / dt;
                state.Velocity = velocity;

                if (Math.Abs(state.PrevVelocity) > 0.1f)
                {
                    float accel = (velocity - state.PrevVelocity) / dt;
                    float gForce = Math.Abs(accel / 9.80665f);

                    if (gForce > GForceImpactThreshold && i != PlayerCarIdx)
                    {
                        if (sessionTime - state.LastImpactEventTime > PhysicsEventCooldownSec)
                        {
                            state.LastImpactEventTime = sessionTime;
                            EmitPhysicsImpact(i, sessionTime, gForce, distPct);
                        }
                    }
                }

                state.PrevVelocity = velocity;
                state.PrevDistPct = distPct;

                // ── Layer 3: Track surface transitions ──────────────
                if (haveTrackSurf)
                {
                    int curSurf = _trackSurfBuf[i];
                    int prevSurf = state.PrevTrackSurface;

                    if (prevSurf == 3 && curSurf == 0 && i != PlayerCarIdx)
                    {
                        if (sessionTime - state.LastOffTrackEventTime > PhysicsEventCooldownSec)
                        {
                            state.LastOffTrackEventTime = sessionTime;
                            EmitPhysicsOffTrack(i, sessionTime, distPct);
                        }
                    }
                    state.PrevTrackSurface = curSurf;
                }
            }
        }

        private void EmitPhysicsImpact(int carIdx, double sessionTime, float gForce, float trackPct)
        {
            _drivers.TryGetValue(carIdx, out var dr);
            int nearbyIdx = FindNearestCar(carIdx, trackPct, ProximityPctNear);
            string cause = nearbyIdx >= 0 ? "car-contact" : "impact";

            var ev = new IncidentEvent
            {
                Id = ShortId(),
                SessionTime = sessionTime,
                SessionTimeFormatted = FormatTime(sessionTime),
                CarIdx = carIdx,
                DriverName = dr?.UserName ?? $"Car {carIdx}",
                CarNumber = dr?.CarNumber ?? "?",
                Delta = 0,
                TotalAfter = dr?.IncidentCount ?? 0,
                Type = "detected",
                Source = "physics",
                Cause = cause,
                PeakG = gForce,
                TrackPct = trackPct
            };
            if (nearbyIdx >= 0 && _drivers.TryGetValue(nearbyIdx, out var other))
            {
                ev.OtherCarIdx = nearbyIdx;
                ev.OtherCarNumber = other.CarNumber ?? "?";
                ev.OtherDriverName = other.UserName ?? "Unknown";
            }
            _metrics.L2PhysicsImpacts++;
            AddIncident(ev);
        }

        private void EmitPhysicsOffTrack(int carIdx, double sessionTime, float trackPct)
        {
            _drivers.TryGetValue(carIdx, out var dr);
            var ev = new IncidentEvent
            {
                Id = ShortId(),
                SessionTime = sessionTime,
                SessionTimeFormatted = FormatTime(sessionTime),
                CarIdx = carIdx,
                DriverName = dr?.UserName ?? $"Car {carIdx}",
                CarNumber = dr?.CarNumber ?? "?",
                Delta = 0,
                TotalAfter = dr?.IncidentCount ?? 0,
                Type = "detected",
                Source = "physics",
                Cause = "off-track",
                TrackPct = trackPct
            };
            _metrics.L3OffTrackEvents++;
            AddIncident(ev);
        }

        // ================================================================
        //  0x detection — player contact/off-track without count change
        // ================================================================

        private void Check0xContact(IRacingSdk irsdk, double sessionTime, int playerCount)
        {
            if (PlayerCarIdx < 0 || PlayerCarIdx >= MaxCars) return;
            float playerDist = _distPctBuf[PlayerCarIdx];
            if (playerDist < 0) return;

            float combinedG = GetPlayerCombinedG(irsdk);
            if (combinedG < GForcePlayerContactThreshold) return;

            int nearbyIdx = FindNearestCar(PlayerCarIdx, playerDist, ProximityPctClose);
            if (nearbyIdx < 0) return;

            var state = _carPhysics[PlayerCarIdx];
            if (sessionTime - state.LastImpactEventTime < PhysicsEventCooldownSec) return;
            state.LastImpactEventTime = sessionTime;

            _drivers.TryGetValue(PlayerCarIdx, out var playerRec);
            _drivers.TryGetValue(nearbyIdx, out var otherRec);

            var ev = new IncidentEvent
            {
                Id = ShortId(),
                SessionTime = sessionTime,
                SessionTimeFormatted = FormatTime(sessionTime),
                CarIdx = PlayerCarIdx,
                DriverName = playerRec?.UserName ?? "Player",
                CarNumber = playerRec?.CarNumber ?? "?",
                Delta = 0,
                TotalAfter = playerCount,
                Type = "0x",
                Source = "physics",
                Cause = "car-contact",
                PeakG = combinedG,
                TrackPct = playerDist,
                OtherCarIdx = nearbyIdx,
                OtherCarNumber = otherRec?.CarNumber ?? "?",
                OtherDriverName = otherRec?.UserName ?? "Unknown",
                Lap = GetInt(irsdk, "Lap")
            };
            _metrics.ZeroXEvents++;
            AddIncident(ev);
        }

        private void Check0xOffTrack(double sessionTime, int playerCount)
        {
            var state = _carPhysics[PlayerCarIdx];
            if (sessionTime - state.LastOffTrackEventTime < PhysicsEventCooldownSec) return;
            state.LastOffTrackEventTime = sessionTime;

            _drivers.TryGetValue(PlayerCarIdx, out var dr);
            float trackPct = _distPctBuf[PlayerCarIdx];

            var ev = new IncidentEvent
            {
                Id = ShortId(),
                SessionTime = sessionTime,
                SessionTimeFormatted = FormatTime(sessionTime),
                CarIdx = PlayerCarIdx,
                DriverName = dr?.UserName ?? "Player",
                CarNumber = dr?.CarNumber ?? "?",
                Delta = 0,
                TotalAfter = playerCount,
                Type = "0x",
                Source = "physics",
                Cause = "off-track",
                TrackPct = trackPct
            };
            _metrics.ZeroXEvents++;
            AddIncident(ev);
        }

        // ================================================================
        //  Layer 4 — YAML-based tracking (all drivers)
        // ================================================================

        private void RefreshFromYaml(IRacingSdk irsdk, double sessionTime)
        {
            var si = irsdk.Data.SessionInfo;
            if (si == null)
            {
                if (!_sessionInfoNullLogged)
                {
                    LogInfo?.Invoke("SessionInfo is null — YAML not yet available. Incident totals will be delayed until iRacing populates session data.");
                    _sessionInfoNullLogged = true;
                }
                return;
            }
            _sessionInfoNullLogged = false; // reset so we log again if null returns after being valid
            _metrics.YamlUpdates++;

            PlayerCarIdx = si.DriverInfo?.DriverCarIdx ?? PlayerCarIdx;

            // ── Rebuild driver roster ─────────────────────────────
            var driverList = si.DriverInfo?.Drivers;
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
                    rec.UserName = string.IsNullOrEmpty(d.UserName) ? rec.UserName ?? "Unknown" : d.UserName;
                    rec.CarNumber = string.IsNullOrEmpty(d.CarNumber) ? rec.CarNumber ?? "?" : d.CarNumber;
                    rec.IsPlayer = (d.CarIdx == PlayerCarIdx);
                    rec.IsSpectator = (d.IsSpectator != 0);
                    rec.IncidentCount = ClampIncidentCount(d.CurDriverIncidentCount);
                }
            }

            // ── Per-driver deltas from ResultsPositions ──────────
            var sessions = si.SessionInfo?.Sessions;
            if (sessions != null)
            {
                int sessionNum = GetInt(irsdk, "ReplaySessionNum");
                if (sessionNum < 0) sessionNum = GetInt(irsdk, "SessionNum");
                var session = sessions.FirstOrDefault(s => s.SessionNum == sessionNum);
                if (session == null)
                    session = sessions.LastOrDefault(s => s.ResultsPositions != null && s.ResultsPositions.Count > 0);
                var positions = session?.ResultsPositions;
                if (positions != null && positions.Count > 0)
                {
                    foreach (var pos in positions)
                    {
                        int idx = pos.CarIdx;
                        if (idx < 0) continue;

                        int inc = ClampIncidentCount(pos.Incidents);
                        if (pos.Incidents != inc) continue;

                        if (_drivers.TryGetValue(idx, out var dr))
                            dr.IncidentCount = inc;

                        if (!_baselineEstablished)
                        {
                            _prevYamlIncidents[idx] = inc;
                        }
                        else
                        {
                            _prevYamlIncidents.TryGetValue(idx, out int prev);
                            int delta = inc - prev;

                            if (delta > 0 && idx != PlayerCarIdx && IsValidIncidentValues(delta, inc))
                            {
                                _drivers.TryGetValue(idx, out var driverRec);

                                string cause = InferYamlCause(idx, sessionTime, delta);
                                float trackPct = (idx >= 0 && idx < MaxCars) ? _distPctBuf[idx] : 0;

                                var ev = new IncidentEvent
                                {
                                    Id = ShortId(),
                                    SessionTime = sessionTime,
                                    SessionTimeFormatted = FormatTime(sessionTime),
                                    CarIdx = idx,
                                    DriverName = driverRec?.UserName ?? $"Car {idx}",
                                    CarNumber = driverRec?.CarNumber ?? "?",
                                    Delta = delta,
                                    TotalAfter = inc,
                                    Type = ClassifyYamlDelta(delta),
                                    Source = "yaml",
                                    Cause = cause,
                                    TrackPct = trackPct
                                };
                                TryIdentifyOtherCar(ev, ProximityPctNear);
                                _metrics.L4YamlEvents++;
                                AddIncident(ev);
                            }
                            _prevYamlIncidents[idx] = inc;
                        }
                    }
                }
            }

            if (!_baselineEstablished)
            {
                _baselineEstablished = true;
                _baselineJustEstablished = true;
                LogInfo?.Invoke($"Incident baseline established. Tracking deltas from this point. YAML updates: {_metrics.YamlUpdates}.");
            }

            if (PlayerCarIdx >= 0 && _drivers.TryGetValue(PlayerCarIdx, out var playerRec))
                playerRec.IncidentCount = ClampIncidentCount(PlayerIncidentCount);
        }

        /// <summary>
        /// Infer likely cause for a YAML-reported incident using recent physics data.
        /// </summary>
        private string InferYamlCause(int carIdx, double sessionTime, int delta)
        {
            if (carIdx < 0 || carIdx >= MaxCars) return null;
            var state = _carPhysics[carIdx];

            bool recentImpact = sessionTime - state.LastImpactEventTime < 5.0;
            bool recentOffTrack = sessionTime - state.LastOffTrackEventTime < 5.0;
            int curSurf = _intArrayAvailable && carIdx < _trackSurfBuf.Length ? _trackSurfBuf[carIdx] : -1;

            if (delta == 1 || curSurf == 0 || recentOffTrack)
                return "off-track";
            if (delta == 4 || (delta >= 4 && !_isDirt))
                return "heavy-contact";
            if (recentImpact)
            {
                int nearbyIdx = FindNearestCar(carIdx, _distPctBuf[carIdx], ProximityPctNear);
                return nearbyIdx >= 0 ? "car-contact" : "wall";
            }
            if (delta == 2)
                return "wall-or-spin";
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

        private bool TryGetIntArray(IRacingSdk irsdk, string name, int[] buf)
        {
            if (!_intArrayAvailable) return false;
            try
            {
                var props = irsdk.Data.TelemetryDataProperties;
                if (props == null || !props.ContainsKey(name)) return false;
                irsdk.Data.GetIntArray(props[name], buf, 0, buf.Length);
                return true;
            }
            catch
            {
                _intArrayAvailable = false;
                return false;
            }
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

        private static float SafeGetFloat(IRacingSdk irsdk, string name)
        {
            try { return irsdk.Data.GetFloat(name); }
            catch { return 0f; }
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
            _metrics.TotalEvents++;
            _metrics.LastDetectionSessionTime = ev.SessionTime;
            _incidents.Insert(0, ev);
            if (_incidents.Count > MaxIncidents)
                _incidents.RemoveAt(_incidents.Count - 1);
            if (_pendingBroadcast.Count < MaxPendingBroadcast)
                _pendingBroadcast.Enqueue(ev);
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

        private static string ClassifyPlayerDelta(int delta)
        {
            switch (delta)
            {
                case 1: return "1x";
                case 2: return "2x";
                case 4: return "4x";
                default: return delta > 0 ? $"{delta}x" : "?";
            }
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
    }
}
