#if SIMHUB_SDK
using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace SimSteward.Plugin
{
    public partial class SimStewardPlugin
    {
        private readonly ReplayIncidentIndexDetector _liveRaceDetector = new ReplayIncidentIndexDetector();
        /// <summary>Merges raw detections into escalation-aware incidents (max-points-wins within a quick-succession window) — see IncidentSeverityCorrelator.</summary>
        private readonly IncidentSeverityCorrelator _liveIncidentCorrelator = new IncidentSeverityCorrelator();
        /// <summary>Accumulated for the dashboard's "Incidents" tab — this WS message replaces the client's whole array on every send, so the full list must be resent each time, not just the delta.</summary>
        private readonly List<LiveIncidentBoardEntry> _liveIncidentBoardEntries = new List<LiveIncidentBoardEntry>();
        /// <summary>Fingerprint of the currently-open (within the correlator's window) board entry per carIdx, so an escalation can find and amend the right row instead of appending a duplicate.</summary>
        private readonly Dictionary<int, string> _livePendingIncidentFingerprintByCar = new Dictionary<int, string>();
        /// <summary>
        /// True when the track's racing groove is dirt (CarIdxTrackSurfaceMaterial 7/8), computed once per
        /// baseline reset. Caps heavy-contact (4x) at 2x per iRacing's official dirt-oval/dirt-road rule —
        /// shipped as an unvalidated heuristic, never confirmed against a real live dirt-oval session.
        /// </summary>
        private bool _liveRaceIsDirtSession;
        private readonly int[] _liveRaceScratchCarIdxSessionFlags    = new int[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly int[] _liveRaceScratchCarIdxTrackSurface    = new int[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly int[] _liveRaceScratchCarIdxSurfaceMaterial = new int[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly int[] _liveRaceScratchCarIdxLap             = new int[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly float[] _liveRaceScratchCarIdxLapDistPct    = new float[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly int[] _liveRaceScratchCarIdxPosition        = new int[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly int[] _liveRaceScratchCarIdxFastRepairs     = new int[ReplayIncidentIndexBuild.CarSlotCount];

        // Incident-snapshot enrichment — read every tick alongside the detection arrays above so
        // the values logged reflect the same instant as the detection, not a stale later read.
        private readonly float[] _liveRaceScratchCarIdxRPM           = new float[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly int[] _liveRaceScratchCarIdxGear            = new int[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly float[] _liveRaceScratchCarIdxSteer         = new float[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly bool[] _liveRaceScratchCarIdxOnPitRoad      = new bool[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly int[] _liveRaceScratchCarIdxTireCompound    = new int[ReplayIncidentIndexBuild.CarSlotCount];

        private bool _liveRaceDetectorBaselineReady;
        private int _liveRaceLastSubSessionId = -1;
        private int _liveRaceLastSessionNum = int.MinValue;

        // Empirical verification probe (see docs/IRACING-DATA-AVAILABILITY.md ResultsPositions[].Incidents
        // classification) — polls SessionInfoYaml's authoritative per-car incident counts during a genuinely
        // live session to determine whether they update progressively or only post-checkered. Pure
        // observation: never feeds into samples/_liveIncidentBoardEntries on its own.
        private Dictionary<int, int> _liveYamlIncidentsProbePrev;
        private DateTime _liveYamlProbeLastPollUtc = DateTime.MinValue;
        private static readonly TimeSpan LiveYamlProbePollInterval = TimeSpan.FromSeconds(5);

        /// <summary>Last per-driver point totals broadcast to the Live tab via <see cref="BroadcastLiveIncidentTotals"/>; null until results go final. See LiveIncidentTotalsBackfill.</summary>
        private Dictionary<int, int> _liveIncidentTotalsLastBroadcast;

        private const string EventLiveDetection = "live_incident_detection";
        private const string EventLiveBaselineReady = "live_incident_detection_baseline_ready";
        private const string EventLiveYamlProbe = "live_yaml_incident_probe";
        private const string EventLiveEscalated = "live_incident_escalated";
        private const string EventLiveTotalsResolved = "live_incident_totals_resolved";

        /// <summary>
        /// Field-wide, no-admin-required incident detection during a genuine LIVE session (not replay,
        /// not an FF sweep). Called from the native SDK telemetry thread. Deliberately re-derives
        /// live/replay status from _irsdk.Data rather than reading _pluginMode, which is a
        /// DataUpdate-thread field with no cross-thread synchronization.
        /// </summary>
        private void ProcessLiveIncidentDetectionTick()
        {
            if (_irsdk == null || !_irsdk.IsConnected || _logger == null)
                return;

            string simMode = "";
            try { simMode = _irsdk.Data?.SessionInfo?.WeekendInfo?.SimMode ?? ""; } catch { }

            if (!LiveIncidentDetectionPrerequisites.IsGenuinelyLive(_irsdk.IsConnected, simMode))
            {
                _liveRaceDetectorBaselineReady = false; // force fresh baseline next time live resumes
                return;
            }

            lock (_replayIndexBuildLock)
            {
                if (_replayIndexBuildPhase != ReplayIndexBuildPhase.Idle)
                    return; // never run alongside an active FF sweep
            }

            int subSessionId = 0;
            try { subSessionId = _irsdk.Data?.SessionInfo?.WeekendInfo?.SubSessionID ?? 0; } catch { }
            int sessionNum = SafeGetInt("SessionNum");

            bool needReset = !_liveRaceDetectorBaselineReady
                || LiveIncidentDetectionPrerequisites.IsSessionBoundary(
                       _liveRaceLastSubSessionId, _liveRaceLastSessionNum, subSessionId, sessionNum);

            if (needReset)
            {
                SafeGetIntPerCar("CarIdxSessionFlags", _liveRaceScratchCarIdxSessionFlags);
                SafeGetIntPerCar("CarIdxTrackSurface", _liveRaceScratchCarIdxTrackSurface);
                SafeGetIntPerCar("CarIdxFastRepairsUsed", _liveRaceScratchCarIdxFastRepairs);
                SafeGetIntPerCar("CarIdxTrackSurfaceMaterial", _liveRaceScratchCarIdxSurfaceMaterial);

                int playerIncidents0 = 0;
                try { playerIncidents0 = _irsdk.Data.GetInt("PlayerCarMyIncidentCount"); } catch { }
                int playerCarIdx0 = SafeGetInt("PlayerCarIdx");

                _liveRaceDetector.Reset(
                    _liveRaceScratchCarIdxSessionFlags,
                    playerIncidents0,
                    playerCarIdx0,
                    baselineTrackSurface: _liveRaceScratchCarIdxTrackSurface,
                    baselineFastRepairs: _liveRaceScratchCarIdxFastRepairs);
                _liveIncidentCorrelator.Reset();
                _livePendingIncidentFingerprintByCar.Clear();

                // Dirt-cap signal (unvalidated heuristic — see IncidentSeverityCorrelator): sample the
                // racing-surface material under the player's own car, if it's currently on track.
                _liveRaceIsDirtSession = playerCarIdx0 >= 0 && playerCarIdx0 < _liveRaceScratchCarIdxSurfaceMaterial.Length
                    && ReplayIncidentIndexDetection.IsDirtRacingSurface(_liveRaceScratchCarIdxSurfaceMaterial[playerCarIdx0]);

                string reason = !_liveRaceDetectorBaselineReady ? "first_run_live" : "session_boundary";
                _liveRaceDetectorBaselineReady = true;
                _liveRaceLastSubSessionId = subSessionId;
                _liveRaceLastSessionNum = sessionNum;
                _liveIncidentBoardEntries.Clear(); // new session — old entries no longer apply
                _liveYamlIncidentsProbePrev = null; // new session — YAML Incidents counts reset too
                _liveIncidentTotalsLastBroadcast = null; // new session — prior totals no longer apply
                BroadcastLiveIncidentBoard();

                var f = new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["subsession_id"] = subSessionId,
                    ["session_num"] = sessionNum,
                    ["is_dirt_session"] = _liveRaceIsDirtSession
                };
                MergeSessionAndRoutingFields(f);
                _logger.Structured("INFO", "simhub-plugin", EventLiveBaselineReady,
                    "Live incident detection: baseline captured.", f, "lifecycle", null);
                return; // no detections on the baseline tick itself, matches the FF-sweep/live-scrub pattern
            }

            _liveRaceLastSubSessionId = subSessionId;
            _liveRaceLastSessionNum = sessionNum;

            double sessionTimeSec = 0;
            try { sessionTimeSec = _irsdk.Data.GetDouble("SessionTime"); } catch { }

            SafeGetIntPerCar("CarIdxSessionFlags", _liveRaceScratchCarIdxSessionFlags);
            SafeGetIntPerCar("CarIdxTrackSurface", _liveRaceScratchCarIdxTrackSurface);
            SafeGetIntPerCar("CarIdxTrackSurfaceMaterial", _liveRaceScratchCarIdxSurfaceMaterial);
            SafeGetIntPerCar("CarIdxLap", _liveRaceScratchCarIdxLap);
            SafeGetFloatPerCar("CarIdxLapDistPct", _liveRaceScratchCarIdxLapDistPct);
            SafeGetIntPerCar("CarIdxPosition", _liveRaceScratchCarIdxPosition);
            SafeGetIntPerCar("CarIdxFastRepairsUsed", _liveRaceScratchCarIdxFastRepairs);
            SafeGetFloatPerCar("CarIdxRPM", _liveRaceScratchCarIdxRPM);
            SafeGetIntPerCar("CarIdxGear", _liveRaceScratchCarIdxGear);
            SafeGetFloatPerCar("CarIdxSteer", _liveRaceScratchCarIdxSteer);
            SafeGetBoolPerCar("CarIdxOnPitRoad", _liveRaceScratchCarIdxOnPitRoad);
            SafeGetIntPerCar("CarIdxTireCompound", _liveRaceScratchCarIdxTireCompound);

            int playerIncidents = 0;
            try { playerIncidents = _irsdk.Data.GetInt("PlayerCarMyIncidentCount"); } catch { }
            int playerCarIdx = SafeGetInt("PlayerCarIdx");
            int replayFrame = SafeGetInt("ReplayFrameNum"); // harmless during live; kept for IncidentSample schema parity

            var samples = _liveRaceDetector.Process(
                sessionTimeSec,
                _liveRaceScratchCarIdxSessionFlags,
                playerIncidents,
                playerCarIdx,
                replayFrame,
                trackSurface: _liveRaceScratchCarIdxTrackSurface,
                carIdxLap: _liveRaceScratchCarIdxLap,
                sessionNum: sessionNum,
                carIdxLapDistPct: _liveRaceScratchCarIdxLapDistPct,
                carIdxPosition: _liveRaceScratchCarIdxPosition,
                carIdxFastRepairsUsed: _liveRaceScratchCarIdxFastRepairs,
                carIdxTrackSurfaceMaterial: _liveRaceScratchCarIdxSurfaceMaterial);

            if (samples.Count > 0)
                LogLiveIncidentDetectionsLocked(samples, sessionTimeSec, subSessionId, replayFrame, playerCarIdx);

            PollLiveYamlIncidentsForVerificationLocked(sessionTimeSec, subSessionId, sessionNum);
        }

        /// <summary>
        /// Empirically confirmed (docs/IRACING-DATA-AVAILABILITY.md Group 5): SessionInfoYaml's
        /// ResultsPositions[].Incidents block does NOT update progressively during a genuinely LIVE
        /// (non-replay) session — it stays flat until results go official, then jumps straight to the
        /// final per-driver total in one step. Reuses the same parse+diff helpers already proven
        /// correct on the replay fast-forward path (ProcessYamlIncidentDiffLocked in
        /// SimStewardPlugin.ReplayIncidentIndexBuild.cs) verbatim — no new parsing logic. Logs every
        /// throttled poll (not just when deltas exist) so the Loki timeseries shows the full
        /// trajectory of cars_in_snapshot across a session.
        ///
        /// Deliberately does NOT feed _liveIncidentBoardEntries (per-incident rows) — a single
        /// finalization jump can't be attributed to one specific earlier row without the full
        /// chronological re-sweep the Replay Index Build already does. It DOES feed an aggregate
        /// per-driver TOTAL to the Live tab once finalized, via <see cref="TryBroadcastLiveIncidentTotalsLocked"/>.
        /// </summary>
        private void PollLiveYamlIncidentsForVerificationLocked(double sessionTimeSec, int subSessionId, int sessionNum)
        {
            var nowUtc = DateTime.UtcNow;
            if (nowUtc - _liveYamlProbeLastPollUtc < LiveYamlProbePollInterval)
                return;
            _liveYamlProbeLastPollUtc = nowUtc;

            string yaml = "";
            try { yaml = _irsdk.Data?.SessionInfoYaml ?? ""; } catch { }

            bool parseOk = ReplayIncidentIndexResultsYaml.TryParseOfficialIncidentsByCarIdx(
                yaml, sessionNum, out Dictionary<int, int> currByCar, out int sessUsed, out string parseErr);

            int deltaCount = 0;
            var sampleDeltas = new List<string>();
            if (parseOk)
            {
                var deltas = ReplayIncidentYamlDiff.Diff(
                    _liveYamlIncidentsProbePrev, currByCar, sessionTimeSec, 0, sessionNum,
                    _liveRaceScratchCarIdxLap);
                deltaCount = deltas.Count;
                foreach (var d in deltas)
                {
                    int prevVal = 0;
                    _liveYamlIncidentsProbePrev?.TryGetValue(d.CarIdx, out prevVal);
                    int currVal = 0;
                    currByCar.TryGetValue(d.CarIdx, out currVal);
                    if (sampleDeltas.Count < 5)
                        sampleDeltas.Add(d.CarIdx + ":" + prevVal + "->" + currVal);
                }
            }

            var f = new Dictionary<string, object>
            {
                ["cars_in_snapshot"] = parseOk ? currByCar.Count : 0,
                ["parse_ok"] = parseOk,
                ["parse_error"] = parseErr ?? "",
                ["yaml_session_num_used"] = sessUsed,
                ["deltas_since_last_poll"] = deltaCount,
                ["sample_deltas"] = string.Join(", ", sampleDeltas),
                ["session_time"] = Math.Round(sessionTimeSec, 2),
                ["subsession_id"] = subSessionId,
                ["is_live"] = true,
                ["baseline_established"] = (_liveYamlIncidentsProbePrev != null)
            };
            MergeSessionAndRoutingFields(f);
            string probeMessage = "Live YAML incident probe: " +
                (parseOk ? currByCar.Count + " cars, " + deltaCount + " deltas" : "parse failed (" + parseErr + ")");
            if (LiveYamlProbeLogLevel.IsInfoWorthy(parseOk, deltaCount))
                _logger?.Structured("INFO", "simhub-plugin", EventLiveYamlProbe, probeMessage, f, "lifecycle", null);
            else
                _logger?.Debug(probeMessage, "simhub-plugin", EventLiveYamlProbe, f);

            if (parseOk)
            {
                _liveYamlIncidentsProbePrev = currByCar;
                TryBroadcastLiveIncidentTotalsLocked(currByCar, subSessionId, sessionTimeSec);
            }
        }

        /// <summary>
        /// Aggregate-only live backfill (TR-continuation of the Live tab's "pending" points badge):
        /// once results go official while still connected live, push each driver's real point TOTAL
        /// to the dashboard. Never per-incident — see class remarks on
        /// <see cref="PollLiveYamlIncidentsForVerificationLocked"/> for why that's not attributable.
        /// No-op (and does not re-broadcast) until results are final or once totals stop changing.
        /// </summary>
        private void TryBroadcastLiveIncidentTotalsLocked(Dictionary<int, int> yamlByCarIdx, int subSessionId, double sessionTimeSec)
        {
            if (!LiveIncidentTotalsBackfill.IsFinal(yamlByCarIdx))
                return;
            if (!LiveIncidentTotalsBackfill.HasChanged(_liveIncidentTotalsLastBroadcast, yamlByCarIdx))
                return;

            _liveIncidentTotalsLastBroadcast = yamlByCarIdx;

            var totals = new List<LiveIncidentTotalEntry>(yamlByCarIdx.Count);
            foreach (var kv in yamlByCarIdx)
            {
                totals.Add(new LiveIncidentTotalEntry
                {
                    CarIdx = kv.Key,
                    Driver = ResolveDriverNameForCarIdxOrEmpty(kv.Key),
                    Points = kv.Value
                });
            }

            if (_bridge != null)
            {
                try
                {
                    var msg = new { type = "liveIncidentTotals", totals };
                    _bridge.Broadcast(JsonConvert.SerializeObject(msg), "liveIncidentTotals");
                }
                catch (Exception ex)
                {
                    _logger?.Warn("live_incident_totals broadcast: " + ex.Message);
                }
            }

            var f = new Dictionary<string, object>
            {
                ["subsession_id"] = subSessionId,
                ["session_time"] = Math.Round(sessionTimeSec, 2),
                ["drivers_with_totals"] = yamlByCarIdx.Count,
                ["total_points"] = SumPoints(yamlByCarIdx)
            };
            MergeSessionAndRoutingFields(f);
            _logger?.Structured("INFO", "simhub-plugin", EventLiveTotalsResolved,
                "Live incident totals: results went official while still live — backfilling per-driver totals to the dashboard.",
                f, "lifecycle", null);
        }

        private static int SumPoints(Dictionary<int, int> yamlByCarIdx)
        {
            int total = 0;
            foreach (var kv in yamlByCarIdx) total += kv.Value;
            return total;
        }

        private void LogLiveIncidentDetectionsLocked(IReadOnlyList<IncidentSample> samples, double sessionTimeSec, int subSessionId, int replayFrame, int playerCarIdx)
        {
            string trackName = "";
            float trackLengthMeters = 0f;
            try
            {
                trackName = _irsdk.Data?.SessionInfo?.WeekendInfo?.TrackName ?? "";
                trackLengthMeters = TrackLandmarks.ParseTrackLengthMeters(_irsdk.Data?.SessionInfo?.WeekendInfo?.TrackLength as string);
            }
            catch { }

            bool boardChanged = false;

            foreach (var raw in samples)
            {
                try
                {
                    var result = _liveIncidentCorrelator.Correlate(raw, sessionTimeSec, _liveRaceIsDirtSession);

                    if (!result.IsNewIncident && !result.IsEscalation)
                        continue; // merged, no numeric change — no-op, avoid log/broadcast spam

                    var s = result.Merged;
                    string driverName = ResolveDriverNameForCarIdxOrEmpty(s.CarIdx);
                    string driverDisplay = string.IsNullOrEmpty(driverName) ? ("car " + s.CarIdx) : (driverName + " (car " + s.CarIdx + ")");
                    var location = s.LapDistPct.HasValue
                        ? TrackLandmarks.Resolve(trackName, s.LapDistPct.Value, trackLengthMeters)
                        : new TrackLandmarks.Result(null, "unknown");
                    string locationDisplay = location.Label ?? "?";

                    if (result.IsNewIncident)
                    {
                        string fp = ReplayIncidentIndexFingerprint.ComputeHexV1(
                            subSessionId, s.CarIdx, s.SessionTimeMs, s.DetectionSource, s.IncidentPoints);
                        _livePendingIncidentFingerprintByCar[s.CarIdx] = fp;
                        string pointsDisplay = s.IncidentPoints.HasValue ? "+" + s.IncidentPoints.Value + "x" : "pts:?";

                        var fields = BuildLiveIncidentLogFields(s, sessionTimeSec, replayFrame, fp, driverName, location, result.Cause, playerCarIdx);

                        string message = string.Format(
                            System.Globalization.CultureInfo.InvariantCulture,
                            "Live incident: {0}  {1}  {2}  {3}  @{4}  t:{5:0.00}s  frame:{6}  fp:{7}",
                            driverDisplay, s.DetectionSource, pointsDisplay, result.Cause, locationDisplay, sessionTimeSec, replayFrame, fp.Substring(0, 16));

                        _logger.Structured("INFO", "simhub-plugin", EventLiveDetection,
                            message, fields, "lifecycle", null);

                        _liveIncidentBoardEntries.Add(new LiveIncidentBoardEntry
                        {
                            Id = fp.Substring(0, 16),
                            Time = FormatIncidentTime(sessionTimeSec),
                            Car = s.CarIdx,
                            Driver = string.IsNullOrEmpty(driverName) ? ("Car " + s.CarIdx) : driverName,
                            Points = s.IncidentPoints ?? 0,
                            PointsResolved = s.IncidentPoints.HasValue,
                            EstimatedPoints = s.IncidentPoints.HasValue
                                ? (int?)null
                                : IncidentPointsEstimate.Estimate(s.DetectionSource, _liveRaceIsDirtSession),
                            Cause = result.Cause,
                            Frame = replayFrame,
                            Lap = s.Lap,
                            Player = s.CarIdx == playerCarIdx
                        });
                        boardChanged = true;
                    }
                    else // IsEscalation
                    {
                        if (!_livePendingIncidentFingerprintByCar.TryGetValue(s.CarIdx, out string fp) || string.IsNullOrEmpty(fp))
                            continue; // correlator thinks this is an escalation but we lost track of the original row — shouldn't happen, skip rather than fabricate an entry

                        string entryId = fp.Substring(0, 16);
                        var entry = _liveIncidentBoardEntries.Find(e => e.Id == entryId);
                        if (entry == null)
                            continue; // board was cleared/rebuilt out from under us — nothing to amend

                        int fromPoints = entry.Points;
                        string fromCause = entry.Cause;
                        entry.Points = s.IncidentPoints ?? 0;
                        entry.PointsResolved = s.IncidentPoints.HasValue;
                        // Real resolution supersedes any earlier guess; otherwise re-derive the
                        // estimate since the driving detection source/cause may have changed.
                        entry.EstimatedPoints = s.IncidentPoints.HasValue
                            ? (int?)null
                            : IncidentPointsEstimate.Estimate(s.DetectionSource, _liveRaceIsDirtSession);
                        entry.Cause = result.Cause;

                        var escFields = new Dictionary<string, object>
                        {
                            ["fingerprint"] = fp,
                            ["car_idx"] = s.CarIdx,
                            ["driver_name"] = driverName,
                            ["from_points"] = fromPoints,
                            ["to_points"] = entry.Points,
                            ["from_cause"] = fromCause,
                            ["to_cause"] = entry.Cause,
                            ["detection_source"] = s.DetectionSource,
                            ["session_time"] = Math.Round(sessionTimeSec, 6),
                            ["car_lap"] = s.Lap
                        };
                        MergeSessionAndRoutingFields(escFields);

                        _logger.Structured("INFO", "simhub-plugin", EventLiveEscalated,
                            string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                "Live incident escalated: {0}  {1}x {2} -> {3}x {4}  fp:{5}",
                                driverDisplay, fromPoints, fromCause, entry.Points, entry.Cause, fp.Substring(0, 16)),
                            escFields, "lifecycle", null);
                        boardChanged = true;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn("live_incident_detection logging: " + ex.Message);
                }
            }

            if (boardChanged)
                BroadcastLiveIncidentBoard();
        }

        private Dictionary<string, object> BuildLiveIncidentLogFields(
            IncidentSample s, double sessionTimeSec, int replayFrame, string fp, string driverName,
            TrackLandmarks.Result location, string cause, int playerCarIdx)
        {
            var fields = new Dictionary<string, object>
            {
                ["track_location"] = location.Label,
                ["track_location_source"] = location.Source,
                ["fingerprint"] = fp,
                ["car_idx"] = s.CarIdx,
                ["driver_name"] = driverName,
                ["session_time_ms"] = s.SessionTimeMs,
                ["detection_source"] = s.DetectionSource,
                ["cause"] = cause,
                ["session_time"] = Math.Round(sessionTimeSec, 6),
                ["incident_points"] = s.IncidentPoints.HasValue ? (object)s.IncidentPoints.Value : null,
                ["is_aggregate_delta"] = s.IsAggregateDelta,
                ["car_lap"] = s.Lap,
                ["replay_frame"] = replayFrame,
                ["lap_dist_pct"] = s.LapDistPct.HasValue ? (object)s.LapDistPct.Value : null,
                ["car_position"] = s.CarPosition.HasValue ? (object)s.CarPosition.Value : null,
                ["surface_material"] = s.CarIdx >= 0 && s.CarIdx < _liveRaceScratchCarIdxSurfaceMaterial.Length
                    ? _liveRaceScratchCarIdxSurfaceMaterial[s.CarIdx] : (int?)null,
                // Field-wide (Group 2, no admin needed) — newly read this pass, any car.
                ["car_rpm"] = _liveRaceScratchCarIdxRPM[s.CarIdx],
                ["car_gear"] = _liveRaceScratchCarIdxGear[s.CarIdx],
                ["car_steer_rad"] = _liveRaceScratchCarIdxSteer[s.CarIdx],
                ["car_on_pit_road"] = _liveRaceScratchCarIdxOnPitRoad[s.CarIdx],
                ["car_tire_compound"] = _liveRaceScratchCarIdxTireCompound[s.CarIdx]
            };

            // Player-only rich block (Group 3 — structurally unavailable for any other car,
            // confirmed docs/IRACING-DATA-AVAILABILITY.md; only ever attempted for the player's own car).
            if (s.CarIdx == playerCarIdx)
                AddPlayerOnlyIncidentContext(fields);

            MergeSessionAndRoutingFields(fields);
            return fields;
        }

        /// <summary>Formats session time as "m:ss.s" — matches index.html's existing mock-data time format, no client-side reformatting needed.</summary>
        private static string FormatIncidentTime(double sessionTimeSec)
        {
            if (double.IsNaN(sessionTimeSec) || double.IsInfinity(sessionTimeSec) || sessionTimeSec < 0)
                sessionTimeSec = 0;
            int totalTenths = (int)Math.Round(sessionTimeSec * 10);
            int minutes = totalTenths / 600;
            int seconds = (totalTenths / 10) % 60;
            int tenths = totalTenths % 10;
            return $"{minutes}:{seconds:D2}.{tenths}";
        }

        private void BroadcastLiveIncidentBoard()
        {
            if (_bridge == null || _bridge.ClientCount <= 0) return;
            try
            {
                var trimmed = LiveIncidentBoardTrim.Trim(_liveIncidentBoardEntries, LiveIncidentBoardTrim.DefaultMaxBroadcastEntries);
                var msg = new { type = "incidents", entries = trimmed };
                _bridge.Broadcast(JsonConvert.SerializeObject(msg), "incidents");
            }
            catch (Exception ex)
            {
                _logger?.Warn("live_incident_board broadcast: " + ex.Message);
            }
        }

        /// <summary>
        /// Richer context available ONLY for the player's own car (Group 3 SDK fields — never
        /// available for other cars, admin or not). Uses the plain scalar SDK vars, not the CarIdx
        /// array equivalents, since docs/IRACING-DATA-AVAILABILITY.md notes these are higher-fidelity
        /// for the player's own car specifically.
        /// </summary>
        private void AddPlayerOnlyIncidentContext(Dictionary<string, object> fields)
        {
            try { fields["player_throttle"] = _irsdk.Data.GetFloat("Throttle"); } catch { }
            try { fields["player_brake"] = _irsdk.Data.GetFloat("Brake"); } catch { }
            try { fields["player_clutch"] = _irsdk.Data.GetFloat("Clutch"); } catch { }
            try { fields["player_speed_mps"] = _irsdk.Data.GetFloat("Speed"); } catch { }
            try { fields["player_rpm"] = _irsdk.Data.GetFloat("RPM"); } catch { }
            try { fields["player_gear"] = _irsdk.Data.GetInt("Gear"); } catch { }
            try { fields["player_steering_wheel_angle_rad"] = _irsdk.Data.GetFloat("SteeringWheelAngle"); } catch { }
            try { fields["player_lat_accel"] = _irsdk.Data.GetFloat("LatAccel"); } catch { }
            try { fields["player_long_accel"] = _irsdk.Data.GetFloat("LongAccel"); } catch { }
            try { fields["player_vert_accel"] = _irsdk.Data.GetFloat("VertAccel"); } catch { }
        }
    }
}
#endif
