#if SIMHUB_SDK
using System;
using System.Collections.Generic;
using System.Diagnostics;
using IRSDKSharper;

namespace SimSteward.Plugin
{
    public partial class SimStewardPlugin
    {
        private readonly object _replayIndexBuildLock = new object();
        private ReplayIndexBuildPhase _replayIndexBuildPhase = ReplayIndexBuildPhase.Idle;
        private volatile bool _replayIndexStartRequested;
        private volatile bool _replayIndexCancelRequested;
        private int _replayIndexSavedReplayFrame;
        private int _replayIndexFrameZeroConsecutive;
        private int _replayIndexSeekTelemetryTicks;
        private readonly int[] _replayIndexBaselineCarIdxSessionFlags = new int[ReplayIncidentIndexBuild.CarSlotCount];
        private int _replayIndexBaselinePlayerCarMyIncidentCount;
        private int _replayIndexReplayFrameNumEndSnapshot;
        private Stopwatch _replayIndexFfWallClock;
        private long _replayIndexFfTelemetrySampleCount;
        private int _replayIndexActivePlaySpeed = ReplayIncidentIndexBuild.DefaultFastForwardPlaySpeed;
        private readonly ReplayIncidentIndexDetector _replayIndexDetector = new ReplayIncidentIndexDetector();
        private readonly List<IncidentSample> _replayIndexIncidentSamples = new List<IncidentSample>();
        private readonly int[] _replayIndexBaselineFastRepairsUsed = new int[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly int[] _replayIndexScratchCarIdxSessionFlags = new int[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly int[] _replayIndexScratchCarIdxFastRepairsUsed = new int[ReplayIncidentIndexBuild.CarSlotCount];

        private enum ReplayIndexBuildPhase
        {
            Idle,
            SeekingStart,
            FastForwarding
        }

        private void OnIrsdkTelemetryDataForReplayIndex()
        {
            if (_irsdk == null || !_irsdk.IsConnected || _logger == null)
                return;

            try
            {
                ProcessReplayIncidentIndexBuildTelemetry();
            }
            catch (Exception ex)
            {
                _logger.Warn("replay_incident_index telemetry: " + ex.Message);
            }
        }

        /// <summary>Reset fast-forward state when iRacing disconnects mid-build.</summary>
        private void ReplayIncidentIndexOnIracingDisconnected()
        {
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
                _replayIndexBuildPhase = ReplayIndexBuildPhase.Idle;
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
                        try
                        {
                            _irsdk.ReplaySetPlaySpeed(1, false);
                        }
                        catch { /* ignored */ }
                        finally
                        {
                            _replayIndexActivePlaySpeed = 1;
                        }

                        var f = new Dictionary<string, object> { ["reason"] = "cancel_requested" };
                        MergeSessionAndRoutingFields(f);
                        _logger.Structured("INFO", "simhub-plugin", ReplayIncidentIndexBuild.EventBuildCancelled,
                            "Replay incident index build cancelled.", f, "lifecycle", null);
                    }

                    _replayIndexBuildPhase = ReplayIndexBuildPhase.Idle;
                    _replayIndexStartRequested = false;
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
            _replayIndexFrameZeroConsecutive = 0;
            _replayIndexSeekTelemetryTicks = 0;
            _replayIndexActivePlaySpeed = 1;
            _replayIndexIncidentSamples.Clear();

            var started = new Dictionary<string, object>
            {
                ["saved_replay_frame_before_seek"] = _replayIndexSavedReplayFrame,
                ["target_play_speed"] = ReplayIncidentIndexBuild.DefaultFastForwardPlaySpeed
            };
            MergeSessionAndRoutingFields(started);
            _logger.Structured("INFO", "simhub-plugin", ReplayIncidentIndexBuild.EventStarted,
                "Replay incident index build: seek to start (TR-004).", started, "lifecycle", null);

            try
            {
                _irsdk.ReplaySearch(IRacingSdkEnum.RpySrchMode.ToStart);
            }
            catch (Exception ex)
            {
                error = ex.Message ?? "replay_search_failed";
                return false;
            }

            _replayIndexBuildPhase = ReplayIndexBuildPhase.SeekingStart;
            return true;
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
                _replayIndexBuildPhase = ReplayIndexBuildPhase.Idle;
                return;
            }

            int frame = SafeGetInt("ReplayFrameNum");
            _replayIndexFrameZeroConsecutive =
                ReplayIncidentIndexBuild.NextFrameZeroConsecutiveCount(frame, _replayIndexFrameZeroConsecutive);

            if (_replayIndexFrameZeroConsecutive < ReplayIncidentIndexBuild.FrameZeroStableConsecutiveSamples)
                return;

            CaptureBaselineAndStartFastForwardLocked();
        }

        private void CaptureBaselineAndStartFastForwardLocked()
        {
            _replayIndexReplayFrameNumEndSnapshot = SafeGetInt("ReplayFrameNumEnd");
            for (int i = 0; i < _replayIndexBaselineCarIdxSessionFlags.Length; i++)
            {
                try
                {
                    _replayIndexBaselineCarIdxSessionFlags[i] = _irsdk.Data.GetInt("CarIdxSessionFlags", i);
                }
                catch
                {
                    _replayIndexBaselineCarIdxSessionFlags[i] = 0;
                }
            }

            try
            {
                _replayIndexBaselinePlayerCarMyIncidentCount = _irsdk.Data.GetInt("PlayerCarMyIncidentCount");
            }
            catch
            {
                _replayIndexBaselinePlayerCarMyIncidentCount = 0;
            }

            for (int i = 0; i < _replayIndexBaselineFastRepairsUsed.Length; i++)
            {
                try
                {
                    _replayIndexBaselineFastRepairsUsed[i] = _irsdk.Data.GetInt("CarIdxFastRepairsUsed", i);
                }
                catch
                {
                    _replayIndexBaselineFastRepairsUsed[i] = 0;
                }
            }

            int playerCarIdxBaseline = SafeGetInt("PlayerCarIdx");
            _replayIndexDetector.Reset(
                _replayIndexBaselineCarIdxSessionFlags,
                _replayIndexBaselinePlayerCarMyIncidentCount,
                playerCarIdxBaseline,
                _replayIndexBaselineFastRepairsUsed);

            var baselineFields = new Dictionary<string, object>
            {
                ["replay_frame_num_end"] = _replayIndexReplayFrameNumEndSnapshot,
                ["car_idx_session_flags"] = _replayIndexBaselineCarIdxSessionFlags,
                ["player_car_my_incident_count_baseline"] = _replayIndexBaselinePlayerCarMyIncidentCount
            };
            MergeSessionAndRoutingFields(baselineFields);
            _logger.Structured("INFO", "simhub-plugin", ReplayIncidentIndexBuild.EventBaselineReady,
                "Replay incident index: baseline captured at frame 0 (TR-005–TR-007).", baselineFields, "lifecycle", null);

            _replayIndexActivePlaySpeed = ReplayIncidentIndexBuild.DefaultFastForwardPlaySpeed;
            try
            {
                _irsdk.ReplaySetPlaySpeed(_replayIndexActivePlaySpeed, false);
            }
            catch (Exception ex)
            {
                var ef = new Dictionary<string, object> { ["error"] = ex.Message ?? "replay_set_play_speed_failed" };
                MergeSessionAndRoutingFields(ef);
                _logger.Structured("WARN", "simhub-plugin", ReplayIncidentIndexBuild.EventBuildError,
                    "Replay incident index: failed to set fast-forward speed (TR-008).", ef, "lifecycle", null);
                _replayIndexBuildPhase = ReplayIndexBuildPhase.Idle;
                return;
            }

            double effectiveHz = ReplayIncidentIndexBuild.ComputeEffectiveSessionTimeSampleHz(_replayIndexActivePlaySpeed);
            int reportedSpeed = SafeGetInt("ReplayPlaySpeed");
            var ffStart = new Dictionary<string, object>
            {
                ["replay_play_speed_requested"] = _replayIndexActivePlaySpeed,
                ["replay_play_speed_telemetry"] = reportedSpeed,
                ["effective_sample_hz_vs_session_time"] = Math.Round(effectiveHz, 4),
                ["sdk_update_interval_ms"] = _irsdk.UpdateInterval
            };
            MergeSessionAndRoutingFields(ffStart);
            _logger.Structured("INFO", "simhub-plugin", ReplayIncidentIndexBuild.EventFastForwardStarted,
                "Replay incident index: fast-forward started (TR-008/009/011, NFR-008).", ffStart, "lifecycle", null);

            _replayIndexFfWallClock = Stopwatch.StartNew();
            _replayIndexFfTelemetrySampleCount = 0;
            _replayIndexBuildPhase = ReplayIndexBuildPhase.FastForwarding;
        }

        private void ProcessFastForwardingLocked()
        {
            _replayIndexFfTelemetrySampleCount++;

            bool playing;
            try
            {
                playing = _irsdk.Data.GetBool("IsReplayPlaying");
            }
            catch
            {
                playing = SafeGetInt("IsReplayPlaying") != 0;
            }

            if (playing)
            {
                double replaySessionTimeSec = 0;
                try
                {
                    replaySessionTimeSec = _irsdk.Data.GetDouble("ReplaySessionTime");
                }
                catch
                {
                    try
                    {
                        replaySessionTimeSec = _irsdk.Data.GetDouble("SessionTime");
                    }
                    catch
                    {
                        replaySessionTimeSec = 0;
                    }
                }

                for (int i = 0; i < ReplayIncidentIndexBuild.CarSlotCount; i++)
                {
                    try
                    {
                        _replayIndexScratchCarIdxSessionFlags[i] = _irsdk.Data.GetInt("CarIdxSessionFlags", i);
                    }
                    catch
                    {
                        _replayIndexScratchCarIdxSessionFlags[i] = 0;
                    }

                    try
                    {
                        _replayIndexScratchCarIdxFastRepairsUsed[i] = _irsdk.Data.GetInt("CarIdxFastRepairsUsed", i);
                    }
                    catch
                    {
                        _replayIndexScratchCarIdxFastRepairsUsed[i] = 0;
                    }
                }

                int playerIncidents = 0;
                try
                {
                    playerIncidents = _irsdk.Data.GetInt("PlayerCarMyIncidentCount");
                }
                catch
                {
                    playerIncidents = 0;
                }

                int playerCarIdx = SafeGetInt("PlayerCarIdx");
                int replayFrame = SafeGetInt("ReplayFrameNum");
                var tick = _replayIndexDetector.Process(
                    replaySessionTimeSec,
                    _replayIndexScratchCarIdxSessionFlags,
                    playerIncidents,
                    playerCarIdx,
                    _replayIndexScratchCarIdxFastRepairsUsed,
                    replayFrame);
                if (tick.Count > 0)
                    _replayIndexIncidentSamples.AddRange(tick);

                return;
            }

            int rfn = SafeGetInt("ReplayFrameNum");
            int rfe = SafeGetInt("ReplayFrameNumEnd");
            double rst = 0;
            try
            {
                rst = _irsdk.Data.GetDouble("ReplaySessionTime");
            }
            catch
            {
                try { rst = _irsdk.Data.GetDouble("SessionTime"); } catch { rst = 0; }
            }

            string reason = ReplayIncidentIndexBuild.InferCompletionReason(false, rfn, rfe, rst);

            long wallMs = _replayIndexFfWallClock?.ElapsedMilliseconds ?? 0;
            int speedUsedForLog = _replayIndexActivePlaySpeed;
            double effectiveHz = ReplayIncidentIndexBuild.ComputeEffectiveSessionTimeSampleHz(speedUsedForLog);

            try
            {
                _irsdk.ReplaySetPlaySpeed(1, false);
            }
            catch { /* ignored */ }
            finally
            {
                _replayIndexActivePlaySpeed = 1;
            }

            var done = new Dictionary<string, object>
            {
                ["index_build_time_ms"] = wallMs,
                ["fast_forward_telemetry_samples"] = _replayIndexFfTelemetrySampleCount,
                ["completion_reason"] = reason,
                ["replay_play_speed"] = speedUsedForLog,
                ["effective_sample_hz_vs_session_time"] = Math.Round(effectiveHz, 4),
                ["replay_frame_num_at_end"] = rfn,
                ["replay_frame_num_end"] = rfe,
                ["replay_session_time"] = Math.Round(rst, 3),
                ["detected_incident_samples"] = _replayIndexIncidentSamples.Count,
                ["fast_repair_delta_events"] = _replayIndexDetector.FastRepairDeltas.Count
            };
            MergeSessionAndRoutingFields(done);
            _logger.Structured("INFO", "simhub-plugin", ReplayIncidentIndexBuild.EventFastForwardComplete,
                "Replay incident index: fast-forward complete (TR-010/011).", done, "lifecycle", null);

            _replayIndexBuildPhase = ReplayIndexBuildPhase.Idle;
            _replayIndexFfWallClock = null;
        }

        private (bool success, string result, string error) DispatchReplayIncidentIndexBuild(string arg, string correlationId)
        {
            var verb = (arg ?? "").Trim().ToLowerInvariant();
            if (verb != "start" && verb != "cancel")
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

                // start
                if (_replayIndexBuildPhase != ReplayIndexBuildPhase.Idle)
                {
                    LogActionResult("replay_incident_index_build", arg, correlationId, false, "build_in_progress");
                    return (false, null, "build_in_progress");
                }

                if (_replayIndexStartRequested)
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
    }
}
#endif
