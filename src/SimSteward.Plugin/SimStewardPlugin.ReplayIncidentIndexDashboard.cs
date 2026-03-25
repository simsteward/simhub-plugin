#if SIMHUB_SDK
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SimSteward.Plugin
{
    public partial class SimStewardPlugin
    {
        private ReplayIncidentIndexFileRoot _replayIndexDashboardCachedRoot;
        private int _replayIndexDashboardCachedForSubSessionId = -1;
        private int _replayIndexDiskLoadAttemptedForSub = -1;

        private volatile bool _replayIndexRecordModeEnabled;
        private readonly object _replayIndexRecordWriterLock = new object();
        private StreamWriter _replayIndexRecordWriter;
        private string _replayIndexRecordActivePath;
        private string _replayIndexRecordLastPath;
        private int _replayIndexRecordTicksForStructuredWindow;

        private static string ReplayIndexBuildPhaseToString(ReplayIndexBuildPhase phase)
        {
            switch (phase)
            {
                case ReplayIndexBuildPhase.SeekingStart: return "seeking_start";
                case ReplayIndexBuildPhase.FastForwarding: return "fast_forward";
                case ReplayIndexBuildPhase.CameraValidating: return "camera_validating";
                default: return "idle";
            }
        }

        private ReplayIncidentIndexDashboardSnapshot BuildReplayIncidentIndexDashboardSnapshot()
        {
            var snap = new ReplayIncidentIndexDashboardSnapshot
            {
                RecordMode = _replayIndexRecordModeEnabled,
                RecordSamplePath = !string.IsNullOrEmpty(_replayIndexRecordActivePath)
                    ? _replayIndexRecordActivePath
                    : (_replayIndexRecordLastPath ?? "")
            };

            bool ir = _irsdk?.IsConnected ?? false;
            snap.IrsdkConnected = ir;
            if (!ir || _irsdk == null)
            {
                snap.Phase = "idle";
                snap.IsReplayMode = false;
                return snap;
            }

            int sub = _irsdk.Data?.SessionInfo?.WeekendInfo?.SubSessionID ?? 0;
            snap.SubSessionId = sub;
            string simMode = _irsdk.Data?.SessionInfo?.WeekendInfo?.SimMode ?? "";
            snap.IsReplayMode = string.Equals(simMode, "replay", StringComparison.OrdinalIgnoreCase);
            snap.SessionNum = SafeGetInt("SessionNum");
            snap.ReplayFrameNum = SafeGetInt("ReplayFrameNum");
            snap.ReplayFrameEnd = SafeGetInt("ReplayFrameNumEnd");
            try
            {
                snap.ReplaySessionTime = _irsdk.Data.GetDouble("ReplaySessionTime");
            }
            catch
            {
                snap.ReplaySessionTime = 0;
            }

            ReplayIndexBuildPhase phase;
            lock (_replayIndexBuildLock)
            {
                phase = _replayIndexBuildPhase;
                snap.Phase = ReplayIndexBuildPhaseToString(phase);
                snap.BuildElapsedMs = _replayIndexBuildTotalWallClock?.ElapsedMilliseconds ?? 0;
            }

            if (sub <= 0)
            {
                _replayIndexDashboardCachedRoot = null;
                _replayIndexDashboardCachedForSubSessionId = 0;
            }
            else if (sub != _replayIndexDashboardCachedForSubSessionId)
            {
                _replayIndexDashboardCachedRoot = null;
                _replayIndexDashboardCachedForSubSessionId = sub;
                _replayIndexDiskLoadAttemptedForSub = -1;
            }

            if (phase == ReplayIndexBuildPhase.Idle && sub > 0 && _replayIndexDashboardCachedRoot == null &&
                _replayIndexDiskLoadAttemptedForSub != sub)
            {
                _replayIndexDiskLoadAttemptedForSub = sub;
                if (ReplayIncidentIndexOutputPaths.TryReadIndexFile(sub, out var diskRoot))
                    _replayIndexDashboardCachedRoot = diskRoot;
            }

            if (phase == ReplayIndexBuildPhase.Idle)
                snap.Index = _replayIndexDashboardCachedRoot;
            else
                snap.Index = null;

            return snap;
        }

        private void ReplayIncidentIndexDashboardNotifyIndexWritten(int subSessionId, ReplayIncidentIndexFileRoot root)
        {
            if (root == null || subSessionId <= 0) return;
            _replayIndexDashboardCachedRoot = root;
            _replayIndexDashboardCachedForSubSessionId = subSessionId;
            _replayIndexDiskLoadAttemptedForSub = subSessionId;
        }

        private void AppendReplayIncidentIndexRecordSampleIfEnabled()
        {
            if (!_replayIndexRecordModeEnabled || _irsdk == null || !_irsdk.IsConnected || _logger == null)
                return;

            string simMode = _irsdk.Data?.SessionInfo?.WeekendInfo?.SimMode ?? "";
            if (!string.Equals(simMode, "replay", StringComparison.OrdinalIgnoreCase))
                return;

            int sub = _irsdk.Data?.SessionInfo?.WeekendInfo?.SubSessionID ?? 0;
            if (sub <= 0) return;

            double rst;
            try
            {
                rst = _irsdk.Data.GetDouble("ReplaySessionTime");
            }
            catch
            {
                return;
            }

            int frame = SafeGetInt("ReplayFrameNum");
            int sessionNum = SafeGetInt("SessionNum");
            int playerCarIdx = SafeGetInt("PlayerCarIdx");
            int playerInc = SafeGetInt("PlayerCarMyIncidentCount");

            var flags = new int[ReplayIncidentIndexBuild.CarSlotCount];
            for (int i = 0; i < flags.Length; i++)
            {
                try
                {
                    flags[i] = _irsdk.Data.GetInt("CarIdxSessionFlags", i);
                }
                catch
                {
                    flags[i] = 0;
                }
            }

            var lineObj = new JObject
            {
                ["utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["subsession_id"] = sub,
                ["replay_session_time"] = rst,
                ["replay_frame"] = frame,
                ["session_num"] = sessionNum,
                ["player_car_idx"] = playerCarIdx,
                ["player_car_my_incident_count"] = playerInc,
                ["car_idx_session_flags"] = JArray.FromObject(flags)
            };

            lock (_replayIndexRecordWriterLock)
            {
                if (_replayIndexRecordWriter == null)
                    return;
                try
                {
                    _replayIndexRecordWriter.WriteLine(lineObj.ToString(Formatting.None));
                }
                catch
                {
                    /* ignored — do not break telemetry */
                }
            }

            _replayIndexRecordTicksForStructuredWindow++;
            if (_replayIndexRecordTicksForStructuredWindow < 60)
                return;
            _replayIndexRecordTicksForStructuredWindow = 0;

            try
            {
                var f = new Dictionary<string, object>
                {
                    ["telemetry_ticks"] = 60,
                    ["record_file"] = _replayIndexRecordActivePath ?? "",
                    ["subsession_id"] = sub.ToString(CultureInfo.InvariantCulture)
                };
                MergeSessionAndRoutingFields(f);
                _logger.Structured("INFO", "simhub-plugin", ReplayIncidentIndexBuild.EventRecordWindow,
                    "Replay incident index record mode: 60 telemetry ticks written to sample file.", f, "lifecycle", null);
            }
            catch
            {
                /* ignored */
            }
        }

        /// <summary>
        /// Starts 60 Hz record mode for the given reason tag.
        /// No-op if already recording, not connected, or not in replay mode.
        /// </summary>
        private void StartReplayIncidentIndexRecordModeLocked(string reason)
        {
            if (_irsdk == null || !_irsdk.IsConnected) return;
            string simMode = _irsdk.Data?.SessionInfo?.WeekendInfo?.SimMode ?? "";
            if (!string.Equals(simMode, "replay", StringComparison.OrdinalIgnoreCase)) return;
            int sub = _irsdk.Data?.SessionInfo?.WeekendInfo?.SubSessionID ?? 0;
            if (sub <= 0) return;

            lock (_replayIndexRecordWriterLock)
            {
                if (_replayIndexRecordWriter != null) return; // already recording

                string dir  = ReplayIncidentIndexOutputPaths.GetRecordSamplesDirectory();
                Directory.CreateDirectory(dir);
                string name = sub + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture) + ".ndjson";
                string path = Path.Combine(dir, name);
                try
                {
                    _replayIndexRecordWriter = new StreamWriter(
                        new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                        new UTF8Encoding(false)) { AutoFlush = true };
                    _replayIndexRecordActivePath = path;
                    _replayIndexRecordLastPath   = path;
                }
                catch
                {
                    return;
                }
            }

            _replayIndexRecordTicksForStructuredWindow = 0;
            _replayIndexRecordModeEnabled = true;

            if (_logger != null)
            {
                var fields = new Dictionary<string, object>
                {
                    ["reason"] = reason,
                    ["record_file"] = _replayIndexRecordActivePath ?? ""
                };
                MergeSessionAndRoutingFields(fields);
                _logger.Structured("INFO", "simhub-plugin", "replay_incident_index_record_started",
                    "Replay incident index record mode started.", fields, "lifecycle", null);
            }
        }

        private void StopReplayIncidentIndexRecordModeLocked(string reason)
        {
            bool hadWriter;
            lock (_replayIndexRecordWriterLock)
            {
                hadWriter = _replayIndexRecordWriter != null;
                if (_replayIndexRecordWriter != null)
                {
                    try
                    {
                        _replayIndexRecordWriter.Flush();
                        _replayIndexRecordWriter.Dispose();
                    }
                    catch { /* ignored */ }
                    _replayIndexRecordWriter = null;
                }

                _replayIndexRecordActivePath = null;
            }

            _replayIndexRecordModeEnabled = false;
            _replayIndexRecordTicksForStructuredWindow = 0;
            if (hadWriter && _logger != null && !string.IsNullOrEmpty(reason))
            {
                var fields = new Dictionary<string, object> { ["reason"] = reason };
                MergeSessionAndRoutingFields(fields);
                _logger.Structured("INFO", "simhub-plugin", "replay_incident_index_record_stopped",
                    "Replay incident index record mode stopped.", fields, "lifecycle", null);
            }
        }

        private (bool success, string result, string error) DispatchReplayIncidentIndexSeek(string arg, string correlationId)
        {
            const string action = "replay_incident_index_seek";
            JObject jo;
            try
            {
                jo = string.IsNullOrWhiteSpace(arg) ? null : JObject.Parse(arg);
            }
            catch
            {
                LogActionResult(action, arg, correlationId, false, "bad_arg");
                return (false, null, "bad_arg");
            }

            if (jo == null || jo["sessionTimeMs"] == null)
            {
                LogActionResult(action, arg, correlationId, false, "bad_arg");
                return (false, null, "bad_arg");
            }

            int sessionTimeMs;
            try
            {
                sessionTimeMs = jo["sessionTimeMs"].Value<int>();
            }
            catch
            {
                LogActionResult(action, arg, correlationId, false, "bad_arg");
                return (false, null, "bad_arg");
            }

            int sessionNum;
            if (jo["sessionNum"] != null && jo["sessionNum"].Type != JTokenType.Null)
            {
                try
                {
                    sessionNum = jo["sessionNum"].Value<int>();
                }
                catch
                {
                    LogActionResult(action, arg, correlationId, false, "bad_arg");
                    return (false, null, "bad_arg");
                }
            }
            else if (_irsdk != null && _irsdk.IsConnected)
            {
                sessionNum = SafeGetInt("SessionNum");
            }
            else
            {
                sessionNum = 0;
            }

            if (_irsdk == null || !_irsdk.IsConnected)
            {
                LogActionResult(action, arg, correlationId, false, "not_connected");
                return (false, null, "not_connected");
            }

            string simMode = _irsdk.Data?.SessionInfo?.WeekendInfo?.SimMode ?? "";
            if (!string.Equals(simMode, "replay", StringComparison.OrdinalIgnoreCase))
            {
                LogActionResult(action, arg, correlationId, false, "not_replay_mode");
                return (false, null, "not_replay_mode");
            }

            lock (_replayIndexBuildLock)
            {
                if (_replayIndexBuildPhase != ReplayIndexBuildPhase.Idle)
                {
                    LogActionResult(action, arg, correlationId, false, "build_in_progress");
                    return (false, null, "build_in_progress");
                }
            }

            try
            {
                _irsdk.ReplaySearchSessionTime(sessionNum, sessionTimeMs);
                LogActionResult(action, arg, correlationId, true, "");
                return (true, "ok", null);
            }
            catch (Exception ex)
            {
                var err = ex.Message ?? "seek_failed";
                LogActionResult(action, arg, correlationId, false, err);
                return (false, null, err);
            }
        }

        private (bool success, string result, string error) DispatchReplayIncidentIndexRecord(string arg, string correlationId)
        {
            const string action = "replay_incident_index_record";
            var verb = (arg ?? "").Trim().ToLowerInvariant();
            if (verb != "on" && verb != "off")
            {
                LogActionResult(action, arg, correlationId, false, "bad_arg");
                return (false, null, "bad_arg");
            }

            if (verb == "off")
            {
                StopReplayIncidentIndexRecordModeLocked("user_off");
                LogActionResult(action, arg, correlationId, true, "");
                return (true, "ok", null);
            }

            if (_irsdk == null || !_irsdk.IsConnected)
            {
                LogActionResult(action, arg, correlationId, false, "not_connected");
                return (false, null, "not_connected");
            }

            string simMode = _irsdk.Data?.SessionInfo?.WeekendInfo?.SimMode ?? "";
            if (!string.Equals(simMode, "replay", StringComparison.OrdinalIgnoreCase))
            {
                LogActionResult(action, arg, correlationId, false, "not_replay_mode");
                return (false, null, "not_replay_mode");
            }

            int sub = _irsdk.Data?.SessionInfo?.WeekendInfo?.SubSessionID ?? 0;
            if (sub <= 0)
            {
                LogActionResult(action, arg, correlationId, false, "no_subsession");
                return (false, null, "no_subsession");
            }

            lock (_replayIndexRecordWriterLock)
            {
                if (_replayIndexRecordWriter != null)
                {
                    try
                    {
                        _replayIndexRecordWriter.Flush();
                        _replayIndexRecordWriter.Dispose();
                    }
                    catch { /* ignored */ }
                    _replayIndexRecordWriter = null;
                    _replayIndexRecordActivePath = null;
                }

                string dir = ReplayIncidentIndexOutputPaths.GetRecordSamplesDirectory();
                Directory.CreateDirectory(dir);
                string name = sub + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture) + ".ndjson";
                string path = Path.Combine(dir, name);
                try
                {
                    _replayIndexRecordWriter = new StreamWriter(
                        new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                        new UTF8Encoding(false))
                    { AutoFlush = true };
                    _replayIndexRecordActivePath = path;
                    _replayIndexRecordLastPath = path;
                }
                catch (Exception ex)
                {
                    LogActionResult(action, arg, correlationId, false, ex.Message ?? "open_failed");
                    return (false, null, ex.Message ?? "open_failed");
                }
            }

            _replayIndexRecordTicksForStructuredWindow = 0;
            _replayIndexRecordModeEnabled = true;

            if (_logger != null)
            {
                var fields = new Dictionary<string, object>
                {
                    ["record_file"] = _replayIndexRecordActivePath ?? "",
                    ["subsession_id"] = sub.ToString(CultureInfo.InvariantCulture)
                };
                MergeSessionAndRoutingFields(fields);
                _logger.Structured("INFO", "simhub-plugin", "replay_incident_index_record_started",
                    "Replay incident index record mode started (TR-038).", fields, "lifecycle", null);
            }

            LogActionResult(action, arg, correlationId, true, "");
            return (true, "ok", null);
        }
    }
}
#endif
