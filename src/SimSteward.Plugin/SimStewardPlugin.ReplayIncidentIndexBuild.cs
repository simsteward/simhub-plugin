#if SIMHUB_SDK
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using IRSDKSharper;

namespace SimSteward.Plugin
{
    public partial class SimStewardPlugin
    {
        private readonly object _replayIndexBuildLock = new object();
        private ReplayIndexBuildPhase _replayIndexBuildPhase = ReplayIndexBuildPhase.Idle;
        private DateTime _lastTelemetryTickUtc = DateTime.MinValue;
        private volatile bool _replayIndexStartRequested;
        private volatile bool _replayIndexCancelRequested;
        private volatile bool _replayIndexFinalizeRequested;

        private int _replayIndexSavedReplayFrame;
        private int _replayIndexSeekTelemetryTicks;
        private readonly int[] _replayIndexBaselineCarIdxSessionFlags = new int[ReplayIncidentIndexBuild.CarSlotCount];
        private int _replayIndexBaselinePlayerCarMyIncidentCount;
        private int _replayIndexReplayFrameNumEndSnapshot;
        private Stopwatch _replayIndexFfWallClock;
        private long _replayIndexFfTelemetrySampleCount;
        private bool _replayIndexFfSpeedConfirmed;
        private readonly ReplayIncidentIndexDetector _replayIndexDetector = new ReplayIncidentIndexDetector();
        private readonly List<IncidentSample> _replayIndexIncidentSamples = new List<IncidentSample>();
        private readonly int[] _replayIndexBaselineCarIdxTrackSurface = new int[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly int[] _replayIndexScratchCarIdxSessionFlags  = new int[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly int[] _replayIndexScratchCarIdxTrackSurface  = new int[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly int[] _replayIndexScratchCarIdxLap           = new int[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly float[] _replayIndexScratchCarIdxLapDistPct      = new float[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly int[] _replayIndexScratchCarIdxPosition          = new int[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly int[] _replayIndexScratchCarIdxFastRepairs       = new int[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly int[] _replayIndexScratchCarIdxSurfaceMaterial   = new int[ReplayIncidentIndexBuild.CarSlotCount];
        // Incident-snapshot enrichment — plan §5.
        private readonly float[] _replayIndexScratchCarIdxRPM             = new float[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly int[] _replayIndexScratchCarIdxGear              = new int[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly float[] _replayIndexScratchCarIdxSteer           = new float[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly bool[] _replayIndexScratchCarIdxOnPitRoad        = new bool[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly int[] _replayIndexScratchCarIdxTireCompound      = new int[ReplayIncidentIndexBuild.CarSlotCount];

        private Stopwatch _replayIndexBuildTotalWallClock;
        private int _replayIndexSessionNum;
        private ReplayIncidentIndexValidationBlock _replayIndexLastValidationBlock;

        // YAML-diff incident detector state (per FF sweep).
        private int _replayIndexFfLastSessionInfoUpdate;
        private int _replayIndexFfLastYamlSessionNum;
        private Dictionary<int, int> _replayIndexFfLastYamlIncidentsByCar;
        private long _replayIndexFfLastYamlPollSampleCount;

        // End-first pre-pass state.
        private int _replayIndexSeekEndTelemetryTicks;
        private int _replayIndexSeekEndLastSessionInfoUpdate;
        private int _replayIndexSeekEndStableSampleCount;
        private Dictionary<int, int> _replayIndexExpectedLedger;
        private int _replayIndexExpectedLedgerSessionNum;
        private List<DriverIdentity> _replayIndexExpectedIdentities;
        private DateTime _replayIndexPhaseEnteredUtc;

        // Cars to focus the camera on during the build. Today: always [playerCarIdx].
        // Future: race director may select a subset; only entry [0] is used for camera focus.
        private List<int> _replayIndexFocusCarIndices;

        private enum ReplayIndexBuildPhase
        {
            Idle,
            SeekingEnd,
            SeekingStart,
            FastForwarding
        }

        private void OnIrsdkTelemetryDataForReplayIndex()
        {
            if (_irsdk == null || !_irsdk.IsConnected || _logger == null)
                return;
            _lastTelemetryTickUtc = DateTime.UtcNow;

            try
            {
                ProcessReplayIncidentIndexBuildTelemetry();
            }
            catch (Exception ex)
            {
                _logger.Warn("replay_incident_index telemetry: " + ex.Message);
            }

            try
            {
                AppendReplayIncidentIndexRecordSampleIfEnabled();
            }
            catch (Exception ex)
            {
                _logger.Warn("replay_incident_index record sample: " + ex.Message);
            }

            try
            {
                ProcessLiveIncidentDetectionTick();
            }
            catch (Exception ex)
            {
                _logger.Warn("live_incident_detection telemetry: " + ex.Message);
            }
        }

        /// <summary>Reset fast-forward state when iRacing disconnects mid-build.</summary>
        private void ReplayIncidentIndexOnIracingDisconnected()
        {
            StopReplayIncidentIndexRecordModeLocked("iracing_disconnected");
            lock (_replayIndexBuildLock)
            {
                _replayIndexStartRequested = false;
                _replayIndexCancelRequested = false;
                if (_replayIndexBuildPhase == ReplayIndexBuildPhase.Idle)
                    return;
                try
                {
                    if (_irsdk != null && _irsdk.IsConnected)
                        _irsdk.ReplaySetPlaySpeed(1, false);
                }
                catch { /* ignored */ }
                TryRestoreReplayIndexSavedFrameLocked();
                ClearReplayIndexBuildTransientLocked();
                ChangePhaseLocked(ReplayIndexBuildPhase.Idle, "iracing_disconnected");
            }
        }

        private void ProcessReplayIncidentIndexBuildTelemetry()
        {
            lock (_replayIndexBuildLock)
            {
                if (_replayIndexBuildPhase == ReplayIndexBuildPhase.Idle &&
                    !_replayIndexCancelRequested &&
                    !_replayIndexStartRequested)
                {
                    return;
                }

                if (_replayIndexCancelRequested)
                {
                    _replayIndexCancelRequested = false;
                    if (_replayIndexBuildPhase != ReplayIndexBuildPhase.Idle)
                    {
                        try { _irsdk.ReplaySetPlaySpeed(1, false); } catch { /* ignored */ }
                        TryRestoreReplayIndexSavedFrameLocked();
                        var f = new Dictionary<string, object> { ["reason"] = "cancel_requested" };
                        MergeSessionAndRoutingFields(f);
                        _logger.Structured("INFO", "simhub-plugin", ReplayIncidentIndexBuild.EventBuildCancelled,
                            "Replay incident index build cancelled.", f, "lifecycle", null);
                    }

                    ClearReplayIndexBuildTransientLocked();
                    ChangePhaseLocked(ReplayIndexBuildPhase.Idle, "cancel_requested");
                    _replayIndexStartRequested = false;
                    return;
                }

                if (_replayIndexFinalizeRequested)
                {
                    _replayIndexFinalizeRequested = false;
                    if (_replayIndexBuildPhase == ReplayIndexBuildPhase.FastForwarding)
                    {
                        try { _irsdk.ReplaySetPlaySpeed(1, false); } catch { /* ignored */ }
                        var f = new Dictionary<string, object> { ["reason"] = "finalize_requested" };
                        MergeSessionAndRoutingFields(f);
                        _logger.Structured("INFO", "simhub-plugin", ReplayIncidentIndexBuild.EventFastForwardComplete,
                            "Replay incident index build finalized early by user.", f, "lifecycle", null);
                        _replayIndexFfWallClock = null;
                        _replayIndexLastValidationBlock = BuildReplayIndexValidationBlockLocked(_replayIndexIncidentSamples);
                        FinalizeReplayIndexBuildLocked();
                    }
                    return;
                }

                if (_replayIndexBuildPhase == ReplayIndexBuildPhase.Idle && _replayIndexStartRequested)
                {
                    _replayIndexStartRequested = false;
                    if (!TryBeginReplayIncidentIndexBuildLocked(out string err))
                    {
                        var ef = new Dictionary<string, object> { ["error"] = err ?? "start_failed" };
                        MergeSessionAndRoutingFields(ef);
                        _logger.Structured("WARN", "simhub-plugin", ReplayIncidentIndexBuild.EventBuildError,
                            "Replay incident index build could not start.", ef, "lifecycle", null);
                    }
                    return;
                }

                if (_replayIndexBuildPhase == ReplayIndexBuildPhase.SeekingEnd)
                {
                    ProcessSeekingEndLocked();
                    return;
                }

                if (_replayIndexBuildPhase == ReplayIndexBuildPhase.SeekingStart)
                {
                    ProcessSeekingStartLocked();
                    return;
                }

                if (_replayIndexBuildPhase == ReplayIndexBuildPhase.FastForwarding)
                {
                    ProcessFastForwardingLocked();
                }
            }
        }

        private bool TryBeginReplayIncidentIndexBuildLocked(out string error)
        {
            error = null;
            string simMode = _irsdk.Data?.SessionInfo?.WeekendInfo?.SimMode ?? "";
            int subId = _irsdk.Data?.SessionInfo?.WeekendInfo?.SubSessionID ?? 0;
            var eval = ReplayIncidentIndexPrerequisites.Evaluate(true, simMode, subId);
            if (!eval.IsFullyReady)
            {
                error = "not_replay_or_no_subsession";
                return false;
            }

            _replayIndexSavedReplayFrame = SafeGetInt("ReplayFrameNum");
            _replayIndexSeekTelemetryTicks = 0;
            _replayIndexSeekEndTelemetryTicks = 0;
            _replayIndexSeekEndLastSessionInfoUpdate = _irsdk.Data?.SessionInfoUpdate ?? -1;
            _replayIndexSeekEndStableSampleCount = 0;
            _replayIndexExpectedLedger = null;
            _replayIndexExpectedLedgerSessionNum = IncidentSample.SessionNumUnknown;
            _replayIndexExpectedIdentities = null;
            _replayIndexIncidentSamples.Clear();
            _replayIndexBuildTotalWallClock = Stopwatch.StartNew();

            // Resolve focus list. Today this is always [playerCarIdx]; future race-director
            // mode may pass a multi-car selection — only entry [0] drives the camera.
            int playerCarIdx = ResolvePlayerCarIdxForFocusLocked();
            _replayIndexFocusCarIndices = new List<int> { playerCarIdx };

            var started = new Dictionary<string, object>
            {
                ["saved_replay_frame_before_seek"] = _replayIndexSavedReplayFrame,
                ["target_play_speed"] = ReplayIncidentIndexBuild.DefaultFastForwardPlaySpeed,
                ["strategy"] = "end_first_then_start",
                ["focus_car_indices"] = _replayIndexFocusCarIndices.ToArray()
            };
            MergeSessionAndRoutingFields(started);
            _logger.Structured("INFO", "simhub-plugin", ReplayIncidentIndexBuild.EventStarted,
                "Replay incident index build: end-first pre-pass then sweep (TR-004).", started, "lifecycle", null);

            // Snap camera to the primary focus car before seeking. The player-incident
            // detector keys off whichever car the camera is locked to (CamCarIdx), so
            // every build must guarantee that lock instead of relying on the user.
            SnapCameraToPrimaryFocusLocked();

            try
            {
                try { _irsdk.ReplaySetPlaySpeed(1, false); } catch { /* ensure replay is in a responsive state before seeking */ }
                _irsdk.ReplaySearch(IRacingSdkEnum.RpySrchMode.ToEnd);
            }
            catch (Exception ex)
            {
                error = ex.Message ?? "replay_search_failed";
                TryRestoreReplayIndexSavedFrameLocked();
                ClearReplayIndexBuildTransientLocked();
                return false;
            }

            ChangePhaseLocked(ReplayIndexBuildPhase.SeekingEnd, "build_started");
            return true;
        }

        /// <summary>
        /// Transition to a new phase. Logs the change with elapsed time in the previous phase.
        /// Must be called under <see cref="_replayIndexBuildLock"/>.
        /// </summary>
        private void ChangePhaseLocked(ReplayIndexBuildPhase next, string reason)
        {
            var prev = _replayIndexBuildPhase;
            var nowUtc = DateTime.UtcNow;
            long elapsedMs = _replayIndexPhaseEnteredUtc == DateTime.MinValue
                ? 0
                : (long)(nowUtc - _replayIndexPhaseEnteredUtc).TotalMilliseconds;

            _replayIndexBuildPhase = next;
            _replayIndexPhaseEnteredUtc = nowUtc;

            var f = new Dictionary<string, object>
            {
                ["previous_state"] = prev.ToString().ToLowerInvariant(),
                ["new_state"] = next.ToString().ToLowerInvariant(),
                ["reason"] = reason ?? "",
                ["elapsed_ms_in_previous"] = elapsedMs
            };
            MergeSessionAndRoutingFields(f);
            _logger?.Structured("INFO", "simhub-plugin", ReplayIncidentIndexBuild.EventPhaseChanged,
                "Replay incident index build: phase changed.", f, "lifecycle", null);
        }

        /// <summary>
        /// Phase 1 (end-first pre-pass): wait for iRacing to land near the replay end with a stable YAML,
        /// then snapshot final per-driver Incidents tallies + DriverInfo identities, then advance to SeekingStart.
        /// On timeout: log skipped and advance anyway — sweep proceeds without an expected ledger (degraded
        /// validation but a successful build).
        /// </summary>
        private void ProcessSeekingEndLocked()
        {
            _replayIndexSeekEndTelemetryTicks++;

            if (_replayIndexSeekEndTelemetryTicks > ReplayIncidentIndexBuild.SeekEndTimeoutTelemetryTicks)
            {
                var sf = new Dictionary<string, object>
                {
                    ["reason"] = "seek_end_timeout",
                    ["seek_end_telemetry_ticks"] = _replayIndexSeekEndTelemetryTicks
                };
                MergeSessionAndRoutingFields(sf);
                _logger?.Structured("WARN", "simhub-plugin", ReplayIncidentIndexBuild.EventExpectedLedgerSkipped,
                    "Replay index end-first pre-pass: timeout waiting for end-of-replay YAML stability.",
                    sf, "lifecycle", null);
                AdvanceFromSeekingEndToSeekingStartLocked("ledger_skipped_timeout");
                return;
            }

            int curFrame = SafeGetInt("ReplayFrameNum");
            int liveEnd = SafeGetInt("ReplayFrameNumEnd");
            int curSiu = _irsdk.Data?.SessionInfoUpdate ?? -1;

            // Track YAML stability: count consecutive ticks where SessionInfoUpdate didn't change.
            if (curSiu == _replayIndexSeekEndLastSessionInfoUpdate)
            {
                _replayIndexSeekEndStableSampleCount++;
            }
            else
            {
                _replayIndexSeekEndLastSessionInfoUpdate = curSiu;
                _replayIndexSeekEndStableSampleCount = 0;
            }

            bool nearEnd = liveEnd > 0 && curFrame >= liveEnd - 4;
            bool yamlStable = _replayIndexSeekEndStableSampleCount
                              >= ReplayIncidentIndexBuild.SeekEndStableYamlSamples;
            if (!nearEnd || !yamlStable)
                return;

            CaptureExpectedLedgerLocked();
            AdvanceFromSeekingEndToSeekingStartLocked("ledger_captured");
        }

        /// <summary>Snapshot YAML ResultsPositions[*].Incidents and DriverInfo identities at the replay end.</summary>
        private void CaptureExpectedLedgerLocked()
        {
            string yaml = _irsdk.Data?.SessionInfoYaml ?? "";
            int sessionNumAtEnd = SafeGetInt("SessionNum");

            // Identities come from DriverInfo and are stable across sessions; snapshot once here so finalize
            // doesn't depend on YAML still being healthy at the time the build completes.
            _replayIndexExpectedIdentities = BuildDriverIdentityList();

            if (string.IsNullOrEmpty(yaml))
            {
                var sf = new Dictionary<string, object>
                {
                    ["reason"] = "empty_yaml_at_end",
                    ["seek_end_telemetry_ticks"] = _replayIndexSeekEndTelemetryTicks
                };
                MergeSessionAndRoutingFields(sf);
                _logger?.Structured("WARN", "simhub-plugin", ReplayIncidentIndexBuild.EventExpectedLedgerSkipped,
                    "Replay index end-first pre-pass: SessionInfoYaml empty at replay end.",
                    sf, "lifecycle", null);
                _replayIndexExpectedLedger = null;
                return;
            }

            if (!ReplayIncidentIndexResultsYaml.TryParseOfficialIncidentsByCarIdx(
                    yaml, sessionNumAtEnd,
                    out Dictionary<int, int> ledger, out int sessUsed, out string parseErr))
            {
                var sf = new Dictionary<string, object>
                {
                    ["reason"] = parseErr ?? "yaml_parse_failed",
                    ["seek_end_telemetry_ticks"] = _replayIndexSeekEndTelemetryTicks,
                    ["preferred_session_num"] = sessionNumAtEnd
                };
                MergeSessionAndRoutingFields(sf);
                _logger?.Structured("WARN", "simhub-plugin", ReplayIncidentIndexBuild.EventExpectedLedgerSkipped,
                    "Replay index end-first pre-pass: YAML ResultsPositions parse failed.",
                    sf, "lifecycle", null);
                _replayIndexExpectedLedger = null;
                return;
            }

            _replayIndexExpectedLedger = ledger;
            _replayIndexExpectedLedgerSessionNum = sessUsed;

            int totalExpected = 0;
            int carsWithIncidents = 0;
            foreach (var kv in ledger)
            {
                if (kv.Value > 0) { totalExpected += kv.Value; carsWithIncidents++; }
            }

            var f = new Dictionary<string, object>
            {
                ["cars_with_incidents"] = carsWithIncidents,
                ["total_expected_incidents"] = totalExpected,
                ["yaml_session_num_used"] = sessUsed,
                ["seek_end_telemetry_ticks"] = _replayIndexSeekEndTelemetryTicks,
                ["identities_captured"] = _replayIndexExpectedIdentities?.Count ?? 0
            };
            MergeSessionAndRoutingFields(f);
            _logger?.Structured("INFO", "simhub-plugin", ReplayIncidentIndexBuild.EventExpectedLedgerCaptured,
                "Replay index end-first pre-pass: expected ledger captured from YAML.",
                f, "lifecycle", null);
        }

        /// <summary>Issue the ToStart seek and transition phase. Resets the SeekingStart tick counter.</summary>
        private void AdvanceFromSeekingEndToSeekingStartLocked(string reason)
        {
            _replayIndexSeekTelemetryTicks = 0;
            try
            {
                try { _irsdk.ReplaySetPlaySpeed(1, false); } catch { /* ignored */ }
                _irsdk.ReplaySearch(IRacingSdkEnum.RpySrchMode.ToStart);
            }
            catch (Exception ex)
            {
                var ef = new Dictionary<string, object>
                {
                    ["error"] = ex.Message ?? "replay_search_to_start_failed"
                };
                MergeSessionAndRoutingFields(ef);
                _logger?.Structured("WARN", "simhub-plugin", ReplayIncidentIndexBuild.EventBuildError,
                    "Replay incident index: failed to issue ToStart after end-first pre-pass.",
                    ef, "lifecycle", null);
                TryRestoreReplayIndexSavedFrameLocked();
                ClearReplayIndexBuildTransientLocked();
                ChangePhaseLocked(ReplayIndexBuildPhase.Idle, "to_start_failed");
                return;
            }
            ChangePhaseLocked(ReplayIndexBuildPhase.SeekingStart, reason);
        }

        private void ProcessSeekingStartLocked()
        {
            _replayIndexSeekTelemetryTicks++;
            if (_replayIndexSeekTelemetryTicks > ReplayIncidentIndexBuild.SeekStartTimeoutTelemetryTicks)
            {
                try { _irsdk.ReplaySetPlaySpeed(1, false); } catch { /* ignored */ }
                var ef = new Dictionary<string, object>
                {
                    ["error"] = "seek_start_timeout",
                    ["seek_telemetry_ticks"] = _replayIndexSeekTelemetryTicks
                };
                MergeSessionAndRoutingFields(ef);
                _logger.Structured("WARN", "simhub-plugin", ReplayIncidentIndexBuild.EventBuildError,
                    "Replay incident index: timeout waiting for ReplayFrameNum==0 (TR-004).", ef, "lifecycle", null);
                TryRestoreReplayIndexSavedFrameLocked();
                ClearReplayIndexBuildTransientLocked();
                ChangePhaseLocked(ReplayIndexBuildPhase.Idle, "seek_start_timeout");
                return;
            }

            int frame = SafeGetInt("ReplayFrameNum");
            if (frame != 0)
                return;

            CaptureBaselineAndStartFastForwardLocked();
        }

        private void CaptureBaselineAndStartFastForwardLocked()
        {
            _replayIndexSessionNum = SafeGetInt("SessionNum");
            _replayIndexReplayFrameNumEndSnapshot = SafeGetInt("ReplayFrameNumEnd");

            SafeGetIntPerCar("CarIdxSessionFlags", _replayIndexBaselineCarIdxSessionFlags);
            SafeGetIntPerCar("CarIdxTrackSurface", _replayIndexBaselineCarIdxTrackSurface);

            int playerCarMyIncidentCount = 0;
            try { playerCarMyIncidentCount = _irsdk.Data.GetInt("PlayerCarMyIncidentCount"); } catch { }
            _replayIndexBaselinePlayerCarMyIncidentCount = playerCarMyIncidentCount;

            int playerCarIdxBaseline = SafeGetInt("PlayerCarIdx");
            SafeGetIntPerCar("CarIdxFastRepairsUsed", _replayIndexScratchCarIdxFastRepairs);
            _replayIndexDetector.Reset(
                _replayIndexBaselineCarIdxSessionFlags,
                _replayIndexBaselinePlayerCarMyIncidentCount,
                playerCarIdxBaseline,
                _replayIndexBaselineCarIdxTrackSurface,
                baselineFastRepairs: _replayIndexScratchCarIdxFastRepairs);

            var baselineFields = new Dictionary<string, object>
            {
                ["replay_frame_num_end"] = _replayIndexReplayFrameNumEndSnapshot,
                ["car_idx_session_flags"] = _replayIndexBaselineCarIdxSessionFlags,
                ["player_car_my_incident_count_baseline"] = _replayIndexBaselinePlayerCarMyIncidentCount
            };
            MergeSessionAndRoutingFields(baselineFields);
            _logger.Structured("INFO", "simhub-plugin", ReplayIncidentIndexBuild.EventBaselineReady,
                "Replay incident index: baseline captured at frame 0 (TR-005–TR-007).", baselineFields, "lifecycle", null);

            try
            {
                _irsdk.ReplaySetPlaySpeed(ReplayIncidentIndexBuild.DefaultFastForwardPlaySpeed, false);
            }
            catch (Exception ex)
            {
                var ef = new Dictionary<string, object> { ["error"] = ex.Message ?? "replay_set_play_speed_failed" };
                MergeSessionAndRoutingFields(ef);
                _logger.Structured("WARN", "simhub-plugin", ReplayIncidentIndexBuild.EventBuildError,
                    "Replay incident index: failed to set fast-forward speed (TR-008).", ef, "lifecycle", null);
                TryRestoreReplayIndexSavedFrameLocked();
                ClearReplayIndexBuildTransientLocked();
                ChangePhaseLocked(ReplayIndexBuildPhase.Idle, "set_play_speed_failed");
                return;
            }

            double effectiveHz = ReplayIncidentIndexBuild.ComputeEffectiveSessionTimeSampleHz(ReplayIncidentIndexBuild.DefaultFastForwardPlaySpeed);
            int reportedSpeed = SafeGetInt("ReplayPlaySpeed");
            var ffStart = new Dictionary<string, object>
            {
                ["replay_play_speed_requested"] = ReplayIncidentIndexBuild.DefaultFastForwardPlaySpeed,
                ["replay_play_speed_telemetry"] = reportedSpeed,
                ["effective_sample_hz_vs_session_time"] = Math.Round(effectiveHz, 4),
                ["sdk_update_interval_ms"] = _irsdk.UpdateInterval
            };
            MergeSessionAndRoutingFields(ffStart);
            _logger.Structured("INFO", "simhub-plugin", ReplayIncidentIndexBuild.EventFastForwardStarted,
                "Replay incident index: fast-forward started (TR-008/009/011, NFR-008).", ffStart, "lifecycle", null);

            _replayIndexFfWallClock = Stopwatch.StartNew();
            _replayIndexFfTelemetrySampleCount = 0;
            _replayIndexFfSpeedConfirmed = false;
            _replayIndexFfLastSessionInfoUpdate = _irsdk.Data?.SessionInfoUpdate ?? -1;
            _replayIndexFfLastYamlSessionNum = -1;
            _replayIndexFfLastYamlIncidentsByCar = null;
            _replayIndexFfLastYamlPollSampleCount = 0;
            _logCtxReplayTotalFrames = _replayIndexReplayFrameNumEndSnapshot;
            _logCtxReplaySessionCount = CountReplaySessionsLocked();
            ChangePhaseLocked(ReplayIndexBuildPhase.FastForwarding, "baseline_captured");
        }

        /// <summary>TR-028: one structured log per primary detection; fingerprint matches TR-020 / <see cref="ReplayIncidentIndexDocumentBuilder"/>.</summary>
        private void LogReplayIncidentIndexDetectionsLocked(IReadOnlyList<IncidentSample> samples, double replaySessionTimeSec)
        {
            if (_logger == null || samples == null || samples.Count == 0)
                return;

            int subSessionId = _irsdk.Data?.SessionInfo?.WeekendInfo?.SubSessionID ?? 0;
            int playerCarIdx = SafeGetInt("PlayerCarIdx");
            string trackName = "";
            float trackLengthMeters = 0f;
            try
            {
                trackName = _irsdk.Data?.SessionInfo?.WeekendInfo?.TrackName ?? "";
                trackLengthMeters = TrackLandmarks.ParseTrackLengthMeters(_irsdk.Data?.SessionInfo?.WeekendInfo?.TrackLength as string);
            }
            catch { }

            foreach (IncidentSample s in samples)
            {
                try
                {
                    string fp = ReplayIncidentIndexFingerprint.ComputeHexV1(
                        subSessionId,
                        s.CarIdx,
                        s.SessionTimeMs,
                        s.DetectionSource,
                        s.IncidentPoints);
                    var location = s.LapDistPct.HasValue
                        ? TrackLandmarks.Resolve(trackName, s.LapDistPct.Value, trackLengthMeters)
                        : new TrackLandmarks.Result(null, "unknown");

                    var fields = new Dictionary<string, object>
                    {
                        ["fingerprint"] = fp,
                        ["car_idx"] = s.CarIdx,
                        ["driver_name"] = ResolveDriverNameForCarIdxOrEmpty(s.CarIdx),
                        ["track_location"] = location.Label,
                        ["track_location_source"] = location.Source,
                        ["session_time_ms"] = s.SessionTimeMs,
                        ["detection_source"] = s.DetectionSource,
                        ["replay_frame"] = s.ReplayFrame,
                        ["replay_session_time"] = Math.Round(replaySessionTimeSec, 6),
                        ["incident_points"] = s.IncidentPoints.HasValue ? (object)s.IncidentPoints.Value : null,
                        // Renamed from "lap" — MergeSessionAndRoutingFields sets its own "lap" key
                        // (the focus car's lap), which silently clobbered this per-detection value
                        // before this fix (same bug already found and fixed on the live-detector path).
                        ["car_lap"] = s.Lap,
                        // Already captured on IncidentSample but previously dropped on the floor — plan §5.
                        ["lap_dist_pct"] = s.LapDistPct.HasValue ? (object)s.LapDistPct.Value : null,
                        ["car_position"] = s.CarPosition.HasValue ? (object)s.CarPosition.Value : null,
                        ["surface_material"] = s.CarIdx >= 0 && s.CarIdx < _replayIndexScratchCarIdxSurfaceMaterial.Length
                            ? _replayIndexScratchCarIdxSurfaceMaterial[s.CarIdx] : (int?)null,
                        // Field-wide (Group 2, no admin needed) — plan §5.
                        ["car_rpm"] = s.CarIdx >= 0 && s.CarIdx < _replayIndexScratchCarIdxRPM.Length ? _replayIndexScratchCarIdxRPM[s.CarIdx] : 0f,
                        ["car_gear"] = s.CarIdx >= 0 && s.CarIdx < _replayIndexScratchCarIdxGear.Length ? _replayIndexScratchCarIdxGear[s.CarIdx] : 0,
                        ["car_steer_rad"] = s.CarIdx >= 0 && s.CarIdx < _replayIndexScratchCarIdxSteer.Length ? _replayIndexScratchCarIdxSteer[s.CarIdx] : 0f,
                        ["car_on_pit_road"] = s.CarIdx >= 0 && s.CarIdx < _replayIndexScratchCarIdxOnPitRoad.Length && _replayIndexScratchCarIdxOnPitRoad[s.CarIdx],
                        ["car_tire_compound"] = s.CarIdx >= 0 && s.CarIdx < _replayIndexScratchCarIdxTireCompound.Length ? _replayIndexScratchCarIdxTireCompound[s.CarIdx] : 0
                    };

                    // Player-only rich block (Group 3 — structurally unavailable for any other car).
                    if (s.CarIdx == playerCarIdx)
                        AddPlayerOnlyIncidentContext(fields);

                    MergeSessionAndRoutingFields(fields);
                    _logger.Structured(
                        "INFO",
                        "simhub-plugin",
                        ReplayIncidentIndexBuild.EventDetection,
                        "Replay incident index: detection during fast-forward (TR-028).",
                        fields,
                        "lifecycle",
                        null);
                }
                catch
                {
                    // TR-030: logging must not abort index build
                }
            }
        }

        /// <summary>
        /// Watch SessionInfoUpdate ticks during FF and diff per-driver YAML Incidents between snapshots.
        /// Three triggers: SessionInfoUpdate changed, SessionNum changed (counts reset between sessions),
        /// or a periodic fallback poll (in case SessionInfoUpdate doesn't tick during replay scrub).
        /// First snapshot per session is baseline-only; no rows are emitted from it.
        /// </summary>
        private void ProcessYamlIncidentDiffLocked(double replaySessionTimeSec, int replayFrame, int sessionNum)
        {
            if (_irsdk == null || !_irsdk.IsConnected) return;

            int siu = _irsdk.Data?.SessionInfoUpdate ?? -1;
            long sampleCount = _replayIndexFfTelemetrySampleCount;
            bool ticked = siu != _replayIndexFfLastSessionInfoUpdate;
            bool sessionChanged = sessionNum != _replayIndexFfLastYamlSessionNum;
            bool fallbackPoll = (sampleCount - _replayIndexFfLastYamlPollSampleCount)
                                >= ReplayIncidentIndexBuild.FfYamlFallbackPollIntervalTicks;
            if (!ticked && !sessionChanged && !fallbackPoll) return;

            string yaml = _irsdk.Data?.SessionInfoYaml ?? "";
            if (string.IsNullOrEmpty(yaml))
            {
                _replayIndexFfLastSessionInfoUpdate = siu;
                _replayIndexFfLastYamlPollSampleCount = sampleCount;
                return;
            }

            if (!ReplayIncidentIndexResultsYaml.TryParseOfficialIncidentsByCarIdx(
                    yaml, sessionNum,
                    out Dictionary<int, int> currByCar, out int sessUsed, out string parseErr))
            {
                // No ResultsPositions yet (race not started, or pre-race session) — bookkeep and bail.
                _replayIndexFfLastSessionInfoUpdate = siu;
                _replayIndexFfLastYamlPollSampleCount = sampleCount;
                return;
            }

            // Session change resets the baseline because Incidents counts reset between sessions.
            if (sessionChanged)
                _replayIndexFfLastYamlIncidentsByCar = null;

            var prev = _replayIndexFfLastYamlIncidentsByCar;
            List<IncidentSample> deltas = ReplayIncidentYamlDiff.Diff(
                prev, currByCar, replaySessionTimeSec, replayFrame, sessionNum,
                _replayIndexScratchCarIdxLap);

            if (deltas.Count > 0)
            {
                _replayIndexIncidentSamples.AddRange(deltas);
                LogReplayIncidentIndexDetectionsLocked(deltas, replaySessionTimeSec);
            }

            // Per-snapshot lifecycle log — low frequency (once per YAML publish, ~Hz, not 60Hz).
            var snapF = new Dictionary<string, object>
            {
                ["session_info_update"] = siu,
                ["snapshot_session_num"] = sessionNum,
                ["yaml_session_num_used"] = sessUsed,
                ["cars_in_snapshot"] = currByCar.Count,
                ["deltas_detected"] = deltas.Count,
                ["replay_frame"] = replayFrame,
                ["replay_session_time"] = Math.Round(replaySessionTimeSec, 2),
                ["trigger"] = ticked ? "session_info_update_tick"
                                     : (sessionChanged ? "session_change" : "fallback_poll"),
                ["baseline_established"] = (prev == null)
            };
            MergeSessionAndRoutingFields(snapF);
            _logger?.Structured("INFO", "simhub-plugin", ReplayIncidentIndexBuild.EventYamlSnapshot,
                "Replay index FF: YAML ResultsPositions snapshot parsed.", snapF, "lifecycle", null);

            _replayIndexFfLastSessionInfoUpdate = siu;
            _replayIndexFfLastYamlSessionNum = sessionNum;
            _replayIndexFfLastYamlIncidentsByCar = currByCar;
            _replayIndexFfLastYamlPollSampleCount = sampleCount;
        }

        /// <summary>
        /// Test rig sweep-progress broadcast. ~1 Hz cadence; bridges the silence while the live
        /// aggregator is paused for the duration of the FF index build.
        /// </summary>
        private void BroadcastReplaySweepProgressIfDueLocked(int replayFrame, double replaySessionTimeSec)
        {
            if (_bridge == null || _bridge.ClientCount <= 0) return;
            var nowUtc = DateTime.UtcNow;
            if ((nowUtc - _lastSweepProgressTickAt).TotalMilliseconds < 1000)
                return;
            _lastSweepProgressTickAt = nowUtc;

            int frameEnd = _replayIndexReplayFrameNumEndSnapshot > 0
                ? _replayIndexReplayFrameNumEndSnapshot
                : SafeGetInt("ReplayFrameNumEnd");
            double pct = frameEnd > 0 ? (100.0 * replayFrame / frameEnd) : 0.0;
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;

            long elapsedMs = _replayIndexFfWallClock?.ElapsedMilliseconds ?? 0;
            long estRemainingMs = 0;
            if (pct > 0.5 && elapsedMs > 0)
                estRemainingMs = (long)(elapsedMs * (100.0 - pct) / pct);

            int telemetrySpeed = SafeGetInt("ReplayPlaySpeed");
            var payload = new ReplaySweepProgressPayload
            {
                Ts = nowUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                Frame = replayFrame,
                FrameEnd = frameEnd,
                SamplesSoFar = _replayIndexIncidentSamples.Count,
                EstCompletionPct = Math.Round(pct, 2),
                EstRemainingMs = estRemainingMs,
                TelemetryPlaySpeed = telemetrySpeed,
                PlaySpeedRequested = ReplayIncidentIndexBuild.DefaultFastForwardPlaySpeed
            };
            try
            {
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(payload);
                _bridge.Broadcast(json, "replaySweepProgressTick");
            }
            catch (Exception ex)
            {
                WriteBroadcastError("BroadcastReplaySweepProgressIfDueLocked", ex);
            }
        }

        private void ProcessFastForwardingLocked()
        {
            _replayIndexFfTelemetrySampleCount++;

            // Every N ticks verify iRacing is actually running at the requested speed; re-issue if not.
            if (_replayIndexFfTelemetrySampleCount % ReplayIncidentIndexBuild.FfSpeedCheckIntervalTicks == 0)
            {
                int actualSpeed = SafeGetInt("ReplayPlaySpeed");
                bool reissued = actualSpeed != ReplayIncidentIndexBuild.DefaultFastForwardPlaySpeed;
                if (reissued)
                    try { _irsdk.ReplaySetPlaySpeed(ReplayIncidentIndexBuild.DefaultFastForwardPlaySpeed, false); } catch { }

                // Speed-lost: confirmed once but now wrong.
                if (_replayIndexFfSpeedConfirmed && reissued)
                {
                    var lostF = new Dictionary<string, object>
                    {
                        ["actual_speed"] = actualSpeed,
                        ["expected_speed"] = ReplayIncidentIndexBuild.DefaultFastForwardPlaySpeed,
                        ["telemetry_sample_count"] = _replayIndexFfTelemetrySampleCount
                    };
                    MergeSessionAndRoutingFields(lostF);
                    _logger?.Structured("WARN", "simhub-plugin", ReplayIncidentIndexBuild.EventFfSpeedLost,
                        "Replay index FF: speed dropped from expected — re-issuing.", lostF, "lifecycle", null);
                    _replayIndexFfSpeedConfirmed = false;
                }

                // Speed-check DEBUG log (guarded so it's off by default).
                if (_logger?.IsDebugMode == true)
                {
                    var checkF = new Dictionary<string, object>
                    {
                        ["actual_speed"] = actualSpeed,
                        ["expected_speed"] = ReplayIncidentIndexBuild.DefaultFastForwardPlaySpeed,
                        ["reissued"] = reissued,
                        ["telemetry_sample_count"] = _replayIndexFfTelemetrySampleCount
                    };
                    MergeSessionAndRoutingFields(checkF);
                    _logger.Structured("DEBUG", "simhub-plugin", ReplayIncidentIndexBuild.EventFfSpeedCheck,
                        "Replay index FF: periodic speed check.", checkF, "lifecycle", null);
                }
            }

            // Speed-confirmed: first tick where speed reads back correctly.
            if (!_replayIndexFfSpeedConfirmed)
            {
                int speedNow = SafeGetInt("ReplayPlaySpeed");
                if (speedNow == ReplayIncidentIndexBuild.DefaultFastForwardPlaySpeed)
                {
                    _replayIndexFfSpeedConfirmed = true;
                    var confF = new Dictionary<string, object>
                    {
                        ["actual_speed"] = speedNow,
                        ["telemetry_sample_count"] = _replayIndexFfTelemetrySampleCount
                    };
                    MergeSessionAndRoutingFields(confF);
                    _logger?.Structured("INFO", "simhub-plugin", ReplayIncidentIndexBuild.EventFfSpeedConfirmed,
                        "Replay index FF: play speed confirmed at expected value.", confF, "lifecycle", null);
                }
            }

            bool playing;
            try { playing = _irsdk.Data.GetBool("IsReplayPlaying"); }
            catch { playing = SafeGetInt("IsReplayPlaying") != 0; }

            bool checkered = false;
            if (playing)
            {
                checkered = (SafeGetInt("SessionFlags") & ReplayIncidentIndexDetection.CheckeredSessionFlag) != 0;
                if (checkered)
                    playing = false;
            }

            if (playing)
            {
                double replaySessionTimeSec = 0;
                try { replaySessionTimeSec = _irsdk.Data.GetDouble("ReplaySessionTime"); }
                catch
                {
                    try { replaySessionTimeSec = _irsdk.Data.GetDouble("SessionTime"); } catch { }
                }

                SafeGetIntPerCar("CarIdxSessionFlags", _replayIndexScratchCarIdxSessionFlags);
                SafeGetIntPerCar("CarIdxTrackSurface", _replayIndexScratchCarIdxTrackSurface);
                SafeGetIntPerCar("CarIdxLap", _replayIndexScratchCarIdxLap);
                SafeGetFloatPerCar("CarIdxLapDistPct", _replayIndexScratchCarIdxLapDistPct);
                SafeGetIntPerCar("CarIdxPosition", _replayIndexScratchCarIdxPosition);
                SafeGetIntPerCar("CarIdxFastRepairsUsed", _replayIndexScratchCarIdxFastRepairs);
                SafeGetIntPerCar("CarIdxTrackSurfaceMaterial", _replayIndexScratchCarIdxSurfaceMaterial);
                SafeGetFloatPerCar("CarIdxRPM", _replayIndexScratchCarIdxRPM);
                SafeGetIntPerCar("CarIdxGear", _replayIndexScratchCarIdxGear);
                SafeGetFloatPerCar("CarIdxSteer", _replayIndexScratchCarIdxSteer);
                SafeGetBoolPerCar("CarIdxOnPitRoad", _replayIndexScratchCarIdxOnPitRoad);
                SafeGetIntPerCar("CarIdxTireCompound", _replayIndexScratchCarIdxTireCompound);

                int playerIncidents = 0;
                try { playerIncidents = _irsdk.Data.GetInt("PlayerCarMyIncidentCount"); } catch { }

                int playerCarIdx = SafeGetInt("PlayerCarIdx");
                int replayFrame = SafeGetInt("ReplayFrameNum");
                int sessionNum = SafeGetInt("SessionNum");

                var tick = _replayIndexDetector.Process(
                    replaySessionTimeSec,
                    _replayIndexScratchCarIdxSessionFlags,
                    playerIncidents,
                    playerCarIdx,
                    replayFrame,
                    trackSurface: _replayIndexScratchCarIdxTrackSurface,
                    carIdxLap: _replayIndexScratchCarIdxLap,
                    sessionNum: sessionNum,
                    carIdxLapDistPct: _replayIndexScratchCarIdxLapDistPct,
                    carIdxPosition: _replayIndexScratchCarIdxPosition,
                    carIdxFastRepairsUsed: _replayIndexScratchCarIdxFastRepairs,
                    carIdxTrackSurfaceMaterial: _replayIndexScratchCarIdxSurfaceMaterial);
                if (tick.Count > 0)
                {
                    _replayIndexIncidentSamples.AddRange(tick);
                    LogReplayIncidentIndexDetectionsLocked(tick, replaySessionTimeSec);
                }

                // Authoritative per-driver detector: diff SessionInfoYaml ResultsPositions[].Incidents.
                ProcessYamlIncidentDiffLocked(replaySessionTimeSec, replayFrame, sessionNum);

                // Test rig (docs/RULES-TestRig-Contract.md): ~1 Hz sweep progress broadcast.
                BroadcastReplaySweepProgressIfDueLocked(replayFrame, replaySessionTimeSec);

                // Gap 1: periodic Loki progress heartbeat every ~1000 ticks (~16.7s wall time).
                if (_replayIndexFfTelemetrySampleCount % ReplayIncidentIndexBuild.FfProgressLogIntervalTicks == 0)
                {
                    int frameEnd2 = _replayIndexReplayFrameNumEndSnapshot > 0
                        ? _replayIndexReplayFrameNumEndSnapshot
                        : SafeGetInt("ReplayFrameNumEnd");
                    double pct2 = frameEnd2 > 0 ? Math.Round(100.0 * replayFrame / frameEnd2, 1) : 0.0;
                    var progF = new Dictionary<string, object>
                    {
                        ["replay_frame"] = replayFrame,
                        ["replay_frame_end"] = frameEnd2,
                        ["est_completion_pct"] = pct2,
                        ["samples_so_far"] = _replayIndexIncidentSamples.Count,
                        ["telemetry_sample_count"] = _replayIndexFfTelemetrySampleCount,
                        ["actual_play_speed"] = SafeGetInt("ReplayPlaySpeed"),
                        ["elapsed_wall_ms"] = _replayIndexFfWallClock?.ElapsedMilliseconds ?? 0,
                        ["replay_session_time"] = Math.Round(replaySessionTimeSec, 2)
                    };
                    MergeSessionAndRoutingFields(progF);
                    _logger?.Structured("INFO", "simhub-plugin", ReplayIncidentIndexBuild.EventFfProgress,
                        "Replay index FF: sweep progress checkpoint.", progF, "lifecycle", null);
                }

                // Cap at 90,000 samples (~24h of simulated race time at 32x) as failsafe.
                // Also exit when current frame reaches total — handles IsReplayPlaying never going false.
                // Use the snapshot taken at frame 0: ReplayFrameNumEnd shifts per-session during playback
                // and would falsely trigger frameAtEnd mid-replay at session boundaries.
                int totalFrames = _replayIndexReplayFrameNumEndSnapshot > 0
                    ? _replayIndexReplayFrameNumEndSnapshot
                    : SafeGetInt("ReplayFrameNumEnd");
                bool frameAtEnd = totalFrames > 0 && replayFrame >= totalFrames - 2;
                if (!frameAtEnd && _replayIndexFfTelemetrySampleCount <= 90000)
                    return;

                // Gap 4: WARN when the 90k sample cap is hit.
                if (_replayIndexFfTelemetrySampleCount > 90000)
                {
                    var capF = new Dictionary<string, object>
                    {
                        ["sample_count"] = _replayIndexFfTelemetrySampleCount,
                        ["replay_frame"] = replayFrame,
                        ["samples_so_far"] = _replayIndexIncidentSamples.Count
                    };
                    MergeSessionAndRoutingFields(capF);
                    _logger?.Structured("WARN", "simhub-plugin", ReplayIncidentIndexBuild.EventFfSampleCapHit,
                        "Replay index FF: 90k sample cap reached — forcing build completion.", capF, "lifecycle", null);
                }
            }

            int rfn = SafeGetInt("ReplayFrameNum");
            // Use snapshot for end-frame: live ReplayFrameNumEnd shifts per-session.
            int rfe = _replayIndexReplayFrameNumEndSnapshot > 0
                ? _replayIndexReplayFrameNumEndSnapshot
                : SafeGetInt("ReplayFrameNumEnd");
            double rst = 0;
            try { rst = _irsdk.Data.GetDouble("ReplaySessionTime"); }
            catch { try { rst = _irsdk.Data.GetDouble("SessionTime"); } catch { } }

            string reason = checkered ? "checkered_flag"
                : ReplayIncidentIndexBuild.InferCompletionReason(false, rfn, rfe, rst);

            // Gap 3: INFO on first checkered-flag detection.
            if (checkered)
            {
                var cf = new Dictionary<string, object>
                {
                    ["replay_frame"] = rfn,
                    ["replay_session_time"] = Math.Round(rst, 3),
                    ["samples_so_far"] = _replayIndexIncidentSamples.Count,
                    ["telemetry_sample_count"] = _replayIndexFfTelemetrySampleCount
                };
                MergeSessionAndRoutingFields(cf);
                _logger?.Structured("INFO", "simhub-plugin", ReplayIncidentIndexBuild.EventFfCheckeredDetected,
                    "Replay index FF: checkered flag detected — completing sweep.", cf, "lifecycle", null);
            }
            // Gap 2: WARN when IsReplayPlaying went false before natural end.
            else if (reason == "paused_or_stopped")
            {
                var uf = new Dictionary<string, object>
                {
                    ["replay_frame"] = rfn,
                    ["replay_frame_num_end"] = rfe,
                    ["replay_session_time"] = Math.Round(rst, 3),
                    ["telemetry_sample_count"] = _replayIndexFfTelemetrySampleCount,
                    ["samples_so_far"] = _replayIndexIncidentSamples.Count
                };
                MergeSessionAndRoutingFields(uf);
                _logger?.Structured("WARN", "simhub-plugin", ReplayIncidentIndexBuild.EventFfUnexpectedStop,
                    "Replay index FF: IsReplayPlaying false before expected end — forcing completion.", uf, "lifecycle", null);
            }

            long wallMs = _replayIndexFfWallClock?.ElapsedMilliseconds ?? 0;
            double effectiveHz = ReplayIncidentIndexBuild.ComputeEffectiveSessionTimeSampleHz(ReplayIncidentIndexBuild.DefaultFastForwardPlaySpeed);

            try { _irsdk.ReplaySetPlaySpeed(1, false); } catch { /* ignored */ }

            var done = new Dictionary<string, object>
            {
                ["index_build_time_ms"] = wallMs,
                ["fast_forward_telemetry_samples"] = _replayIndexFfTelemetrySampleCount,
                ["completion_reason"] = reason,
                ["replay_play_speed"] = ReplayIncidentIndexBuild.DefaultFastForwardPlaySpeed,
                ["effective_sample_hz_vs_session_time"] = Math.Round(effectiveHz, 4),
                ["replay_frame_num_at_end"] = rfn,
                ["replay_frame_num_end"] = rfe,
                ["replay_session_time"] = Math.Round(rst, 3),
                ["detected_incident_samples"] = _replayIndexIncidentSamples.Count
            };
            MergeSessionAndRoutingFields(done);
            _logger.Structured("INFO", "simhub-plugin", ReplayIncidentIndexBuild.EventFastForwardComplete,
                "Replay incident index: fast-forward complete (TR-010/011).", done, "lifecycle", null);

            _replayIndexFfWallClock = null;
            _replayIndexLastValidationBlock = BuildReplayIndexValidationBlockLocked(_replayIndexIncidentSamples);
            FinalizeReplayIndexBuildLocked();
        }

        private void TryRestoreReplayIndexSavedFrameLocked()
        {
            if (_irsdk == null || !_irsdk.IsConnected)
                return;
            try
            {
                int f = Math.Max(0, _replayIndexSavedReplayFrame);
                _irsdk.ReplaySetPlayPosition(IRacingSdkEnum.RpyPosMode.Begin, f);
                _irsdk.ReplaySetPlaySpeed(1, false);
            }
            catch { /* ignored */ }
        }

        private void ClearReplayIndexBuildTransientLocked()
        {
            _replayIndexFfWallClock = null;
            _replayIndexBuildTotalWallClock = null;
            _replayIndexIncidentSamples.Clear();
            _replayIndexLastValidationBlock = null;
            _replayIndexFfLastYamlIncidentsByCar = null;
            _replayIndexFfLastSessionInfoUpdate = 0;
            _replayIndexFfLastYamlSessionNum = -1;
            _replayIndexFfLastYamlPollSampleCount = 0;
            _replayIndexExpectedLedger = null;
            _replayIndexExpectedLedgerSessionNum = IncidentSample.SessionNumUnknown;
            _replayIndexExpectedIdentities = null;
            _replayIndexSeekEndTelemetryTicks = 0;
            _replayIndexSeekEndLastSessionInfoUpdate = -1;
            _replayIndexSeekEndStableSampleCount = 0;
            _replayIndexFocusCarIndices = null;
            _logCtxReplayTotalFrames = 0;
            _logCtxReplaySessionCount = 0;
        }

        /// <summary>
        /// DriverInfo.DriverCarIdx (session YAML) is the authoritative logged-in-player
        /// signal. Telemetry "PlayerCarIdx" is the fallback if YAML hasn't published yet.
        /// </summary>
        private int ResolvePlayerCarIdxForFocusLocked()
        {
            try
            {
                var di = _irsdk?.Data?.SessionInfo?.DriverInfo;
                if (di != null) return di.DriverCarIdx;
            }
            catch { /* fall through */ }
            return SafeGetInt("PlayerCarIdx");
        }

        /// <summary>
        /// Snap the iRacing camera to <c>_replayIndexFocusCarIndices[0]</c>. Logged
        /// always (INFO on success, WARN on failure) — the player-incident detector
        /// keys off CamCarIdx and silently failing here corrupts the whole build.
        /// </summary>
        private void SnapCameraToPrimaryFocusLocked()
        {
            int carIdx = _replayIndexFocusCarIndices != null && _replayIndexFocusCarIndices.Count > 0
                ? _replayIndexFocusCarIndices[0]
                : -1;
            string driverName = ResolveDriverNameForCarIdxOrEmpty(carIdx);

            var f = new Dictionary<string, object>
            {
                ["car_idx"] = carIdx,
                ["driver_name"] = driverName,
                ["focus_car_indices"] = _replayIndexFocusCarIndices?.ToArray() ?? new int[0]
            };
            MergeSessionAndRoutingFields(f);

            if (carIdx < 0)
            {
                f["error"] = "no_player_car_idx";
                _logger?.Structured("WARN", "simhub-plugin", ReplayIncidentIndexBuild.EventCameraLockedToPlayer,
                    "Replay index camera-lock skipped: no player car idx resolvable.", f, "lifecycle", null);
                return;
            }

            try
            {
                _irsdk.CamSwitchPos(IRacingSdkEnum.CamSwitchMode.FocusAtDriver, carIdx, 0, 0);
                _logger?.Structured("INFO", "simhub-plugin", ReplayIncidentIndexBuild.EventCameraLockedToPlayer,
                    "Replay index camera locked to primary focus car.", f, "lifecycle", null);
            }
            catch (Exception ex)
            {
                f["error"] = ex.Message ?? "cam_switch_failed";
                _logger?.Structured("WARN", "simhub-plugin", ReplayIncidentIndexBuild.EventCameraLockedToPlayer,
                    "Replay index camera-lock failed: " + (ex.Message ?? "cam_switch_failed"),
                    f, "lifecycle", null);
            }
        }

        private string ResolveDriverNameForCarIdxOrEmpty(int carIdx)
        {
            if (carIdx < 0) return "";
            try
            {
                var drivers = _irsdk?.Data?.SessionInfo?.DriverInfo?.Drivers as System.Collections.IList;
                if (drivers == null) return "";
                foreach (var d in drivers)
                {
                    if (d == null) continue;
                    var t = d.GetType();
                    var ciObj = t.GetProperty("CarIdx")?.GetValue(d);
                    int ci = ciObj is int v ? v : Convert.ToInt32(ciObj ?? -1);
                    if (ci != carIdx) continue;
                    var name = t.GetProperty("UserName")?.GetValue(d)?.ToString();
                    return name ?? "";
                }
            }
            catch { /* ignored */ }
            return "";
        }

        private int CountReplaySessionsLocked()
        {
            try
            {
                if (_irsdk?.Data?.SessionInfo?.SessionInfo?.Sessions is System.Collections.IList list)
                    return list.Count;
            }
            catch { }
            return 0;
        }

        private ReplayIncidentIndexValidationBlock BuildReplayIndexValidationBlockLocked(
            IReadOnlyList<IncidentSample> samples)
        {
            var detectedByCar = new Dictionary<int, int>();
            var detectedSourcesByCar = new Dictionary<int, Dictionary<string, int>>();
            if (samples != null)
            {
                foreach (var s in samples)
                {
                    if (!detectedByCar.TryGetValue(s.CarIdx, out int c)) c = 0;
                    detectedByCar[s.CarIdx] = c + 1;

                    if (!detectedSourcesByCar.TryGetValue(s.CarIdx, out var perSrc))
                    {
                        perSrc = new Dictionary<string, int>();
                        detectedSourcesByCar[s.CarIdx] = perSrc;
                    }
                    string src = string.IsNullOrEmpty(s.DetectionSource) ? "unknown" : s.DetectionSource;
                    if (!perSrc.TryGetValue(src, out int sc)) sc = 0;
                    perSrc[src] = sc + 1;
                }
            }

            // Identity snapshot: prefer the one captured at end-of-replay (more authoritative);
            // fall back to live identities at finalize time.
            IReadOnlyList<DriverIdentity> identities =
                (_replayIndexExpectedIdentities != null && _replayIndexExpectedIdentities.Count > 0)
                    ? (IReadOnlyList<DriverIdentity>)_replayIndexExpectedIdentities
                    : BuildDriverIdentityList();

            var vb = new ReplayIncidentIndexValidationBlock();

            // Prefer the end-first expected ledger; fall back to live YAML at finalize.
            if (_replayIndexExpectedLedger != null && _replayIndexExpectedLedger.Count > 0)
            {
                vb.YamlResultsAvailable = true;
                vb.YamlSessionNumUsed = _replayIndexExpectedLedgerSessionNum;
                vb.Discrepancies = ReplayIncidentIndexValidationComparer.BuildDiscrepancies(
                    detectedByCar, _replayIndexExpectedLedger, identities, detectedSourcesByCar);
                return vb;
            }

            string yaml = _irsdk.Data?.SessionInfoYaml ?? "";
            if (ReplayIncidentIndexResultsYaml.TryParseOfficialIncidentsByCarIdx(
                    yaml,
                    _replayIndexSessionNum,
                    out Dictionary<int, int> official,
                    out int sessUsed,
                    out string err))
            {
                vb.YamlResultsAvailable = true;
                vb.YamlSessionNumUsed = sessUsed;
                vb.Discrepancies = ReplayIncidentIndexValidationComparer.BuildDiscrepancies(
                    detectedByCar, official, identities, detectedSourcesByCar);
            }
            else
            {
                vb.YamlResultsAvailable = false;
                vb.YamlParseError = err;
                vb.Discrepancies = new List<ReplayIncidentIndexDiscrepancyRow>();
            }

            return vb;
        }

        private void FinalizeReplayIndexBuildLocked()
        {
            int subSessionId = _irsdk.Data?.SessionInfo?.WeekendInfo?.SubSessionID ?? 0;
            long totalMs = _replayIndexBuildTotalWallClock?.ElapsedMilliseconds ?? 0;

            string path = ReplayIncidentIndexOutputPaths.GetFilePathForSubSession(subSessionId);
            try
            {
                var root = ReplayIncidentIndexDocumentBuilder.Build(
                    subSessionId,
                    totalMs,
                    _replayIndexIncidentSamples,
                    _replayIndexLastValidationBlock,
                    path);
                root.Sessions = BuildReplayIndexSessionsLocked();
                string json = ReplayIncidentIndexDocumentBuilder.Serialize(root);
                ReplayIncidentIndexOutputPaths.WriteJsonAtomic(path, json);
                EnqueueReplayIndexForCloudSync(root, json);
                ReplayIncidentIndexDashboardNotifyIndexWritten(subSessionId, root);
            }
            catch (Exception ex)
            {
                var wf = new Dictionary<string, object>
                {
                    ["error"] = ex.Message ?? "json_write_failed",
                    ["path"] = path
                };
                MergeSessionAndRoutingFields(wf);
                _logger.Structured("WARN", "simhub-plugin", ReplayIncidentIndexBuild.EventBuildError,
                    "Replay incident index: failed to write JSON index (TR-019).", wf, "lifecycle", null);
            }

            var summary = new Dictionary<string, object>
            {
                ["output_path"] = path,
                ["index_build_time_ms_total"] = totalMs,
                ["detected_incident_rows"] = _replayIndexIncidentSamples.Count,
                ["yaml_results_available"] = _replayIndexLastValidationBlock?.YamlResultsAvailable == true,
                ["yaml_session_num_used"] = _replayIndexLastValidationBlock?.YamlSessionNumUsed,
                ["discrepancy_count"] = _replayIndexLastValidationBlock?.Discrepancies?.Count ?? 0,
                ["expected_ledger_source"] = (_replayIndexExpectedLedger != null && _replayIndexExpectedLedger.Count > 0)
                    ? "end_first_pre_pass" : "finalize_yaml_live"
            };
            if (_replayIndexLastValidationBlock?.YamlParseError != null)
                summary["yaml_parse_error"] = _replayIndexLastValidationBlock.YamlParseError;

            MergeSessionAndRoutingFields(summary);
            _logger.Structured("INFO", "simhub-plugin", ReplayIncidentIndexBuild.EventValidationSummary,
                "Replay incident index: validation summary (TR-023–TR-025).", summary, "lifecycle", null);

            // Per-driver gap logging + completion audit.
            EmitDriverGapAndAuditLogsLocked();

            TryRestoreReplayIndexSavedFrameLocked();
            ClearReplayIndexBuildTransientLocked();
            ChangePhaseLocked(ReplayIndexBuildPhase.Idle, "build_finalized");
        }

        /// <summary>
        /// Emit a structured log per driver where detected count differs from expected (WARN if under-detected,
        /// INFO if over-detected), followed by a single completion-audit INFO with totals.
        /// </summary>
        private void EmitDriverGapAndAuditLogsLocked()
        {
            var vb = _replayIndexLastValidationBlock;
            if (vb == null) return;

            int driversUnder = 0;
            int driversOver = 0;
            int totalDetected = 0;
            int totalExpected = 0;

            if (vb.Discrepancies != null)
            {
                foreach (var row in vb.Discrepancies)
                {
                    totalDetected += row.DetectedEventCount;
                    totalExpected += row.YamlIncidents;
                    if (row.Gap > 0) driversUnder++;
                    else if (row.Gap < 0) driversOver++;

                    var gapF = new Dictionary<string, object>
                    {
                        ["car_idx"] = row.CarIdx,
                        ["display_name"] = row.DisplayName ?? "",
                        ["unique_user_id"] = row.UniqueUserId ?? "",
                        ["detected_count"] = row.DetectedEventCount,
                        ["expected_count"] = row.YamlIncidents,
                        ["gap"] = row.Gap,
                        ["sources_hit"] = row.SourcesHit ?? new Dictionary<string, int>()
                    };
                    MergeSessionAndRoutingFields(gapF);
                    string level = row.Gap > 0 ? "WARN" : "INFO";
                    string msg = row.Gap > 0
                        ? "Replay index build: driver under-detected (expected > detected)."
                        : "Replay index build: driver over-detected (detected > expected).";
                    _logger?.Structured(level, "simhub-plugin", ReplayIncidentIndexBuild.EventDriverGap,
                        msg, gapF, "lifecycle", null);
                }
            }

            int totalGap = totalExpected - totalDetected;
            double? coverage = totalExpected > 0
                ? (double?)Math.Round(100.0 * totalDetected / totalExpected, 2)
                : null;

            var auditF = new Dictionary<string, object>
            {
                ["drivers_under_detected"] = driversUnder,
                ["drivers_over_detected"] = driversOver,
                ["drivers_with_gaps"] = driversUnder + driversOver,
                ["total_detected"] = totalDetected,
                ["total_expected"] = totalExpected,
                ["total_gap"] = totalGap,
                ["coverage_pct"] = coverage,
                ["expected_ledger_source"] = (_replayIndexExpectedLedger != null && _replayIndexExpectedLedger.Count > 0)
                    ? "end_first_pre_pass" : "finalize_yaml_live"
            };
            MergeSessionAndRoutingFields(auditF);
            _logger?.Structured("INFO", "simhub-plugin", ReplayIncidentIndexBuild.EventBuildCompletionAudit,
                "Replay incident index build: completion audit.", auditF, "lifecycle", null);
        }

        private (bool success, string result, string error) DispatchReplayIncidentIndexBuild(string arg, string correlationId)
        {
            var verb = (arg ?? "").Trim().ToLowerInvariant();
            if (verb != "start" && verb != "cancel" && verb != "finalize")
            {
                LogActionResult("replay_incident_index_build", arg, correlationId, false, "bad_arg");
                return (false, null, "bad_arg");
            }

            if (_irsdk == null || !_irsdk.IsConnected)
            {
                LogActionResult("replay_incident_index_build", arg, correlationId, false, "not_connected");
                return (false, null, "not_connected");
            }

            lock (_replayIndexBuildLock)
            {
                if (verb == "cancel")
                {
                    _replayIndexCancelRequested = true;
                    LogActionResult("replay_incident_index_build", arg, correlationId, true, "");
                    return (true, "ok", null);
                }

                if (verb == "finalize")
                {
                    if (_replayIndexBuildPhase != ReplayIndexBuildPhase.FastForwarding)
                    {
                        LogActionResult("replay_incident_index_build", arg, correlationId, false, "not_fast_forwarding");
                        return (false, null, "not_fast_forwarding");
                    }
                    _replayIndexFinalizeRequested = true;
                    LogActionResult("replay_incident_index_build", arg, correlationId, true, "");
                    return (true, "ok", null);
                }

                if (_replayIndexBuildPhase != ReplayIndexBuildPhase.Idle || _replayIndexStartRequested)
                {
                    LogActionResult("replay_incident_index_build", arg, correlationId, false, "build_in_progress");
                    return (false, null, "build_in_progress");
                }

                string simMode = _irsdk.Data?.SessionInfo?.WeekendInfo?.SimMode ?? "";
                int subId = _irsdk.Data?.SessionInfo?.WeekendInfo?.SubSessionID ?? 0;
                var eval = ReplayIncidentIndexPrerequisites.Evaluate(true, simMode, subId);
                if (!eval.IsFullyReady)
                {
                    LogActionResult("replay_incident_index_build", arg, correlationId, false, "not_replay_mode");
                    return (false, null, "not_replay_mode");
                }

                _replayIndexStartRequested = true;
            }

            LogActionResult("replay_incident_index_build", arg, correlationId, true, "");
            return (true, "ok", null);
        }

        /// <summary>
        /// Mirror <c>SessionInfo.Sessions[]</c> into the on-disk lookup block, classifying
        /// each session via <see cref="SessionTypeImpactClass"/>. Unknown <c>SessionType</c>
        /// strings emit a structured WARN (fail-safe = "free") and the entry is still written.
        /// </summary>
        private IReadOnlyList<ReplayIncidentIndexSessionEntry> BuildReplayIndexSessionsLocked()
        {
            var entries = new List<ReplayIncidentIndexSessionEntry>();
            try
            {
                var sessionInfo = _irsdk?.Data?.SessionInfo;
                if (!(sessionInfo?.SessionInfo?.Sessions is IList list)) return entries;

                int subSessionId = sessionInfo?.WeekendInfo?.SubSessionID ?? 0;

                foreach (var o in list)
                {
                    if (o == null) continue;
                    var t = o.GetType();
                    var snProp = t.GetProperty("SessionNum");
                    var nameProp = t.GetProperty("SessionName");
                    var typeProp = t.GetProperty("SessionType");

                    int sn = -1;
                    var snVal = snProp?.GetValue(o);
                    if (snVal is int i) sn = i;
                    else if (snVal != null) int.TryParse(snVal.ToString(), out sn);

                    string name = nameProp?.GetValue(o)?.ToString() ?? "";
                    string type = typeProp?.GetValue(o)?.ToString() ?? "";

                    if (!SessionTypeImpactClass.IsKnown(type))
                    {
                        try
                        {
                            var wf = new Dictionary<string, object>
                            {
                                ["session_type"] = type,
                                ["session_num"] = sn,
                                ["sub_session_id"] = subSessionId
                            };
                            MergeSessionAndRoutingFields(wf);
                            _logger.Structured(
                                "WARN",
                                "simhub-plugin",
                                "session_type_unrecognized",
                                "Unrecognized SessionType '" + type + "' for session " + sn + " — classifying as 'free' (fail-safe).",
                                wf,
                                "iracing",
                                null);
                        }
                        catch
                        {
                            // logging must not abort sessions[] build
                        }
                    }

                    entries.Add(new ReplayIncidentIndexSessionEntry
                    {
                        SessionNum = sn,
                        Name = name,
                        Type = type,
                        ImpactClass = SessionTypeImpactClass.Classify(type)
                    });
                }
            }
            catch
            {
                // never abort index finalize on yaml reflection error
            }
            return entries;
        }

        /// <summary>Read one int per car slot into <paramref name="buffer"/>, defaulting to 0 on any error.</summary>
        private void SafeGetIntPerCar(string field, int[] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                try { buffer[i] = _irsdk.Data.GetInt(field, i); }
                catch { buffer[i] = 0; }
            }
        }

        private void SafeGetFloatPerCar(string field, float[] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                try { buffer[i] = _irsdk.Data.GetFloat(field, i); }
                catch { buffer[i] = 0f; }
            }
        }

        private void SafeGetBoolPerCar(string field, bool[] buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                try { buffer[i] = _irsdk.Data.GetBool(field, i); }
                catch { buffer[i] = false; }
            }
        }
    }
}
#endif
