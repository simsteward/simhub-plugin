#if SIMHUB_SDK
using GameReaderCommon;
using IRSDKSharper;
using SimHub.Plugins;
using System.Windows.Media;
#endif

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Reflection;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Sentry;

namespace SimSteward.Plugin
{
#if SIMHUB_SDK
    [PluginName("Sim Steward")]
    [PluginDescription("Sim Steward: HTML dashboard bridge via WebSocket")]
    [PluginAuthor("Sim Steward")]
    public partial class SimStewardPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
#else
#pragma warning disable CS0169, CS0649
    public partial class SimStewardPlugin
#endif
    {
        private const int DefaultPort = 19847;
        private const int CapturePreRollFrames = 180;
        private const double BroadcastThrottleMs = 200;
        private const int DependencyCheckIntervalTicks = 60;
        private const double DashboardPingIntervalSec = 5;
        private int _wsPort = DefaultPort;

#if SIMHUB_SDK
        public PluginManager PluginManager { get; set; }
#endif

        private PluginLogger _logger;
        private IDisposable _sentryDisposable;
        private bool _debugMode;
        private DashboardBridge _bridge;
        private DateTime _lastBroadcastAt = DateTime.MinValue;
        private string _pluginDataPath;
        private readonly object _broadcastErrorLock = new object();
        private DateTime _lastNoClientsLogAt = DateTime.MinValue;
        private volatile bool _broadcastNoClientsPending;

        private static readonly HttpClient DashboardPingClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3)
        };

        private volatile bool _steamRunning;
        private volatile bool _simHubHttpListening;
        private volatile string _dashboardPingStatus = "—";
        private DateTime _lastDashboardPingUtc = DateTime.MinValue;
        private int _dataUpdateTick;
        private SystemMetricsSampler _resourceSampler;
        private DateTime _nextResourceSampleUtc = DateTime.MaxValue;
        private int _resourceSampleIntervalSec = 60;
        private SystemMetricsSample _lastResourceSample;
        private PluginMetricsTelemetry _metricsTelemetry;

#if SIMHUB_SDK
        private IRacingSdk _irsdk;
        private double _lastSessionTime;
        private string _pluginMode = "Unknown";
        private string _lokiBaseUrl = "";
        private string _grafanaBaseUrl = "";
        private int _replayFrameNumEnd;
        /// <summary>Replay length (telemetry <c>ReplayFrameNumEnd</c>); 0 if unknown. Unreliable — session-relative on some builds.</summary>
        private int _replayFrameTotal;
        /// <summary>Running maximum of <c>ReplayFrameNum</c> seen this session — reliable proxy for end-of-replay frame.</summary>
        private int _replayFrameMax;
        private string _currentSessionId = "";
        private string _currentSessionSeq = "";
        /// <summary>Latest iRacing session context for structured logs (WebSocket thread reads; DataUpdate writes).</summary>
        private volatile string _logCtxSubsession = SessionLogging.NotInSession;
        private volatile string _logCtxParent = SessionLogging.NotInSession;
        private volatile string _logCtxSessionNum = SessionLogging.NotInSession;
        private volatile string _logCtxTrack = SessionLogging.NotInSession;
        /// <summary>CarIdxLap for focus car (CamCarIdx if valid, else PlayerCarIdx); -1 if unknown.</summary>
        private volatile int _logCtxLap = SessionLogging.LapUnknown;

        /// <summary>SHA-256 prefix of <c>SessionInfoYaml</c> when available; merged into structured logs via <see cref="MergeSessionAndRoutingFields"/>.</summary>
        private volatile string _logCtxSessionYamlFingerprint = "";

        private int _lastSessionInfoUpdateForYamlFingerprint = -1;
        private DateTime _lastSeekIssuedUtc = DateTime.MinValue;
        private int _incidentCursor = -1;
        private bool _autoWalkActive;
        private int _autoWalkPlayUntilFrame;
        private const int AutoWalkPlayFrames = 360;

        private CaptureManifest _captureManifest;
        private bool _captureManifestDirty;
        private int _captureManifestFlushTickCounter;
        private const int CaptureManifestFlushIntervalTicks = 300; // ~5s at 60Hz
        private readonly System.Collections.Generic.Queue<CaptureTask> _captureQueue = new System.Collections.Generic.Queue<CaptureTask>();
        private int _captureQueueDrainTickCounter;
        private const int CaptureQueueDrainIntervalTicks = 120; // ~2s at 60Hz

        private struct CaptureTask
        {
            public string Fingerprint;
            public int CarIdx;
            public long SessionTimeMs;
            public int Lap;
            public int IncidentPoints;
            public string DetectionSource;
            public int ReplayFrame;
        }

        // ── Test rig: replay-control state (docs/RULES-TestRig-Contract.md) ──
        /// <summary>Last applied magnitude for replay playback (unsigned). Default 1×.</summary>
        private int _lastReplaySpeedMagnitude = 1;
        /// <summary>Last applied direction (<c>"forward"</c> or <c>"reverse"</c>). Default forward.</summary>
        private string _lastReplayDirection = "forward";
        /// <summary>True when the last <c>replay_set_speed</c> was &lt;1 (slow-mo / divisor mode).</summary>
        private bool _lastReplaySlowMotion;

        // ── Test rig: live replay aggregator (separate from FF sweep's detector) ──
        private readonly ReplayIncidentIndexDetector _liveDetector = new ReplayIncidentIndexDetector();
        private readonly ReplayIncidentLiveAggregator _liveAggregator = new ReplayIncidentLiveAggregator();
        private bool _liveDetectorBaselineReady;
        private int _liveLastReplayFrame = -1;
        private DateTime _lastReplayTickAt = DateTime.MinValue;
        private DateTime _lastSweepProgressTickAt = DateTime.MinValue;

        // ── Test rig: misfire stamps (set on jump_next/prev, evaluated 750ms later) ──
        private DateTime _lastJumpRequestedAt = DateTime.MinValue;
        private string _lastJumpDirection;
        private int _lastJumpExpectedFrame;
        private int _lastJumpExpectedSessionTimeMs;
        private string _lastJumpExpectedFingerprint;
        private bool _lastJumpEvaluated = true;
        private bool _lastJumpMisfire;
        private int _lastJumpLandedFrame;
        private int _lastJumpLandedSessionTimeMs;
        private int _lastJumpDeltaMs;
        private string _lastJumpNearestFingerprint;
        private DateTime _lastJumpEvaluatedAt = DateTime.MinValue;
#endif

#if SIMHUB_SDK
        private string BuildStateJson(PluginSnapshot snapshot)
        {
            var state = new
            {
                type = "state",
                pluginVersion = snapshot.PluginVersion,
                pluginMode = snapshot.PluginMode,
                currentSessionTime = snapshot.CurrentSessionTime,
                currentSessionTimeFormatted = snapshot.CurrentSessionTimeFormatted,
                lap = snapshot.Lap,
                frame = snapshot.Frame,
                frameEnd = snapshot.FrameEnd,
                replaySessionCount = snapshot.ReplaySessionCount,
                replaySessionNum = snapshot.ReplaySessionNum,
                replaySessionName = snapshot.ReplaySessionName,
                drivers = BuildDriverList(),
                cameraGroups = GetCameraGroupNames(),
                diagnostics = snapshot.Diagnostics,
                replayIncidentIndex = snapshot.ReplayIncidentIndex
            };
            return JsonConvert.SerializeObject(state);
        }
#else
        private string BuildStateJson()
        {
            var state = new
            {
                type = "state",
                pluginVersion = PluginVersionInfo.Display,
                pluginMode = "Unknown",
                currentSessionTime = 0.0,
                currentSessionTimeFormatted = "0:00",
                diagnostics = new PluginDiagnostics()
            };
            return JsonConvert.SerializeObject(state);
        }
#endif

#if SIMHUB_SDK
        private static string FormatSessionTime(double totalSeconds)
        {
            if (double.IsNaN(totalSeconds) || totalSeconds < 0 || double.IsInfinity(totalSeconds))
                return "0:00";
            int minutes = (int)(totalSeconds / 60);
            int seconds = (int)(totalSeconds % 60);
            return $"{minutes}:{seconds:D2}";
        }

        private static string BuildSessionSeq(string trackName)
        {
            if (string.IsNullOrEmpty(trackName)) return "";
            var safe = new System.Text.StringBuilder();
            foreach (var c in trackName)
                safe.Append(char.IsLetterOrDigit(c) ? c : '_');
            return $"{safe}_{DateTime.UtcNow:yyyyMMdd}";
        }
#endif

        private string GetStateForNewClient()
        {
#if SIMHUB_SDK
            return BuildStateJson(BuildPluginSnapshot());
#else
            return BuildStateJson();
#endif
        }

        private string GetLogTailForNewClient()
        {
            if (_logger == null) return null;
            var tail = _logger.GetTail(50);
            if (tail == null || tail.Count == 0) return null;
            var msg = new { type = "logEvents", entries = tail };
            return JsonConvert.SerializeObject(msg);
        }

        private void OnLogWritten(LogEntry entry)
        {
            if (entry == null || _bridge == null) return;
            try
            {
                var msg = new { type = "logEvents", entries = new[] { entry } };
                _bridge.Broadcast(JsonConvert.SerializeObject(msg), "logEvents");
            }
            catch (Exception ex)
            {
                WriteBroadcastError("OnLogWritten", ex);
            }
        }

        private void OnLogWriteError(string eventType, Exception ex)
        {
            // Write directly to plugin.log to avoid re-entering PluginLogger
            var logPath = _logger?.LogPath;
            if (!string.IsNullOrEmpty(logPath))
            {
                var line = $"{DateTime.UtcNow:o} [ERROR] {eventType}: {ex?.Message}{Environment.NewLine}";
                try { File.AppendAllText(logPath, line, System.Text.Encoding.UTF8); } catch { }
            }

            // Surface in App Health tab
            var entry = new LogEntry
            {
                Level = "ERROR",
                Component = "simhub-plugin",
                Event = eventType,
                Message = $"Log write failed: {ex?.Message}",
                Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                Domain = "health"
            };
            try
            {
                var msg = new { type = "logEvents", entries = new[] { entry } };
                _bridge?.Broadcast(JsonConvert.SerializeObject(msg), "logEvents");
            }
            catch { }

            try { if (ex != null) SentrySdk.CaptureException(ex); } catch { }
        }

        private void WriteBroadcastError(string context, Exception ex)
        {
            if (ex != null) SentrySdk.CaptureException(ex);
            if (string.IsNullOrEmpty(_pluginDataPath)) return;
            var path = Path.Combine(_pluginDataPath, "broadcast-errors.log");
            var line = DateTime.UtcNow.ToString("o") + " " + context + (ex != null ? " " + ex.Message : "") + Environment.NewLine;
            lock (_broadcastErrorLock)
            {
                try
                {
                    var dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    File.AppendAllText(path, line, System.Text.Encoding.UTF8);
                }
                catch { }
            }
        }

#if SIMHUB_SDK
        private PluginSnapshot BuildPluginSnapshot()
        {
            var irConnected = _irsdk?.IsConnected ?? false;
            var clientCount = _bridge?.ClientCount ?? 0;
            int replaySessionCount = 0;
            int replaySessionNum = 0;
            var replaySessionName = "—";
            if (irConnected && _irsdk != null)
            {
                try
                {
                    replaySessionNum = SafeGetInt("SessionNum");
                    var si = _irsdk.Data?.SessionInfo;
                    replaySessionCount = SafeSessionListCount(si);
                    replaySessionName = ResolveSessionNameFromYaml(si, replaySessionNum);
                }
                catch
                {
                    replaySessionCount = 0;
                    replaySessionNum = 0;
                    replaySessionName = "—";
                }
            }

            return new PluginSnapshot
            {
                PluginVersion = PluginVersionInfo.Display,
                PluginMode = _pluginMode,
                CurrentSessionTime = _lastSessionTime,
                CurrentSessionTimeFormatted = FormatSessionTime(_lastSessionTime),
                Lap = _logCtxLap,
                Frame = _replayFrameNumEnd,
                FrameEnd = _replayFrameTotal,
                ReplaySessionCount = replaySessionCount,
                ReplaySessionNum = replaySessionNum,
                ReplaySessionName = replaySessionName,
                Diagnostics = BuildDiagnostics(clientCount),
                ReplayIncidentIndex = BuildReplayIncidentIndexDashboardSnapshot()
            };
        }

        private PluginDiagnostics BuildDiagnostics(int clientCount)
        {
            var d = new PluginDiagnostics
            {
                IrsdkStarted = _irsdk != null,
                IrsdkConnected = _irsdk?.IsConnected ?? false,
                WsRunning = _bridge != null,
                WsPort = _wsPort,
                WsClients = clientCount,
                SteamRunning = _steamRunning,
                SimHubHttpListening = _simHubHttpListening,
                DashboardPing = _dashboardPingStatus,
                GrafanaConfigured = !string.IsNullOrEmpty(_lokiBaseUrl),
                ReplaySessionCompleted = IsReplaySessionCompleted()
            };
            if (_lastResourceSample != null)
            {
                var s = _lastResourceSample;
                d.ResourceSampleAgeSec = Math.Max(0, (DateTime.UtcNow - s.TimestampUtc).TotalSeconds);
                d.ProcessCpuPct = s.ProcessCpuPct;
                d.ProcessWorkingSetMb = s.ProcessWorkingSetMb;
                d.ProcessPrivateMb = s.ProcessPrivateMb;
                d.GcHeapMb = s.GcHeapMb;
                d.DiskRoot = s.DiskRoot ?? "";
                d.DiskUsedPct = s.DiskUsedPct;
                d.DiskFreeGb = s.DiskFreeGb;
            }
            else
                d.ResourceSampleAgeSec = -1;
            return d;
        }

        private static int SafeSessionListCount(IRacingSdkSessionInfo sessionInfo)
        {
            try
            {
                var sessions = sessionInfo?.SessionInfo?.Sessions;
                return sessions is IList list ? list.Count : 0;
            }
            catch
            {
                return 0;
            }
        }

        private static string ResolveSessionNameFromYaml(IRacingSdkSessionInfo sessionInfo, int sessionNum)
        {
            try
            {
                if (!(sessionInfo?.SessionInfo?.Sessions is IList list))
                    return "—";
                foreach (var o in list)
                {
                    if (o == null) continue;
                    var t = o.GetType();
                    var snProp = t.GetProperty("SessionNum");
                    var nameProp = t.GetProperty("SessionName");
                    if (snProp == null || nameProp == null) continue;
                    var snVal = snProp.GetValue(o);
                    if (snVal is int num && num == sessionNum)
                        return nameProp.GetValue(o)?.ToString() ?? "—";
                }
            }
            catch
            {
                // ignored
            }

            return "—";
        }

        /// <summary>
        /// Checks YAML <c>Sessions[].SessionState</c> for any Race session that reached Checkered/CoolDown.
        /// Uses reflection (same pattern as <see cref="ResolveSessionNameFromYaml"/>).
        /// </summary>
        private bool IsReplaySessionCompleted()
        {
            try
            {
                var sessionInfo = _irsdk?.Data?.SessionInfo;
                if (!(sessionInfo?.SessionInfo?.Sessions is IList list)) return false;
                foreach (var o in list)
                {
                    if (o == null) continue;
                    var t = o.GetType();
                    var typeProp = t.GetProperty("SessionType");
                    var stateProp = t.GetProperty("ResultsOfficial");
                    // Check if it's a Race session
                    var sessionType = typeProp?.GetValue(o)?.ToString() ?? "";
                    if (!string.Equals(sessionType, "Race", StringComparison.OrdinalIgnoreCase)) continue;
                    // ResultsOfficial = 1 means session completed with official results
                    var official = stateProp?.GetValue(o);
                    if (official is int ival && ival >= 1) return true;
                    if (int.TryParse(official?.ToString(), out int parsed) && parsed >= 1) return true;
                }
            }
            catch { }
            return false;
        }

        private string GetSessionTypeFromYaml()
        {
            try
            {
                var sessionInfo = _irsdk?.Data?.SessionInfo;
                if (!(sessionInfo?.SessionInfo?.Sessions is IList list)) return "";
                foreach (var o in list)
                {
                    if (o == null) continue;
                    var typeProp = o.GetType().GetProperty("SessionType");
                    var sessionType = typeProp?.GetValue(o)?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(sessionType)) return sessionType;
                }
            }
            catch { }
            return "";
        }

        private object[] BuildDriverList()
        {
            try
            {
                var driverInfo = _irsdk?.Data?.SessionInfo?.DriverInfo;
                var drivers = driverInfo?.Drivers as IList;
                if (drivers == null)
                    return Array.Empty<object>();
                // DriverInfo.DriverCarIdx (session YAML) is the authoritative player-car signal.
                var playerIdx = driverInfo != null ? driverInfo.DriverCarIdx : SafeGetInt("PlayerCarIdx");
                var list = new System.Collections.Generic.List<object>();
                foreach (var d in drivers)
                {
                    if (d == null) continue;
                    var t = d.GetType();
                    var carIdxObj = t.GetProperty("CarIdx")?.GetValue(d);
                    var carIdx = carIdxObj is int ci ? ci : Convert.ToInt32(carIdxObj ?? -1);
                    var carNum = t.GetProperty("CarNumber")?.GetValue(d)?.ToString() ?? "";
                    var name = t.GetProperty("UserName")?.GetValue(d)?.ToString() ?? "";
                    var isPlayer = carIdx == playerIdx;
                    list.Add(new { carIdx, carNum, name, isPlayer });
                }
                return list.ToArray();
            }
            catch
            {
                return Array.Empty<object>();
            }
        }

        private string[] GetCameraGroupNames()
        {
            try
            {
                var groups = _irsdk?.Data?.SessionInfo?.CameraInfo?.Groups as IList;
                if (groups == null) return Array.Empty<string>();
                var names = new System.Collections.Generic.List<string>();
                foreach (var g in groups)
                {
                    var n = g?.GetType().GetProperty("GroupName")?.GetValue(g)?.ToString();
                    if (!string.IsNullOrEmpty(n)) names.Add(n);
                }
                return names.ToArray();
            }
            catch
            {
                return Array.Empty<string>();
            }
        }

        private int ResolveCameraGroupNum(string groupName)
        {
            try
            {
                var groups = _irsdk?.Data?.SessionInfo?.CameraInfo?.Groups as IList;
                if (groups == null) return -1;
                int idx = 0;
                foreach (var g in groups)
                {
                    var t = g?.GetType();
                    var n = t?.GetProperty("GroupName")?.GetValue(g)?.ToString();
                    if (string.Equals(n, groupName, StringComparison.OrdinalIgnoreCase))
                    {
                        var numProp = t.GetProperty("GroupNum");
                        return numProp != null ? Convert.ToInt32(numProp.GetValue(g)) : idx;
                    }
                    idx++;
                }
            }
            catch
            {
                // ignored
            }
            return -1;
        }

        private void SwitchCameraToGroup(int groupNum)
        {
            if (groupNum < 0 || _irsdk == null || !_irsdk.IsConnected) return;
            int carPos = ResolveFocusCarIdx();
            if (carPos < 0) carPos = SafeGetInt("PlayerCarIdx");
            _irsdk.CamSwitchPos(IRacingSdkEnum.CamSwitchMode.FocusAtDriver, carPos, groupNum, 0);
        }

        private bool IsSeekThrottled() =>
            (DateTime.UtcNow - _lastSeekIssuedUtc).TotalMilliseconds < 750;
        private void MarkSeekIssued() =>
            _lastSeekIssuedUtc = DateTime.UtcNow;

        private void LogActionResult(string action, string arg, string correlationId, bool success, string error, System.Collections.Generic.Dictionary<string, object> supplementalFields = null)
        {
            var resultFields = new System.Collections.Generic.Dictionary<string, object>
            {
                ["action"] = action,
                ["arg"] = arg,
                ["correlation_id"] = correlationId,
                ["success"] = success,
                ["error"] = error ?? ""
            };
            MergeSessionAndRoutingFields(resultFields);
            if (supplementalFields != null)
            {
                foreach (var kv in supplementalFields)
                    resultFields[kv.Key] = kv.Value;
            }
            var summary = success
                ? $"{action} -> ok"
                : $"{action} -> {error ?? "failed"}";
            _logger?.Structured("INFO", "simhub-plugin", "action_result", summary, resultFields, "action", null);
        }

        private class CaptureIncidentArg
        {
            [JsonProperty("frame")] public int frame { get; set; } = -1;
            [JsonProperty("camera")] public string camera { get; set; }
            /// <summary>Incident car number from dashboard (for Loki fingerprint / CustID lookup).</summary>
            [JsonProperty("car")] public int? car { get; set; }
            // Extended fields — dashboard passes these from the index row (all optional for backward compat)
            [JsonProperty("fingerprint")] public string fingerprint { get; set; }
            [JsonProperty("sessionTimeMs")] public long? sessionTimeMs { get; set; }
            [JsonProperty("carIdx")] public int? carIdx { get; set; }
            [JsonProperty("lap")] public int? lap { get; set; }
            [JsonProperty("incidentPoints")] public int? incidentPoints { get; set; }
            [JsonProperty("detectionSource")] public string detectionSource { get; set; }
        }

        /// <summary>
        /// Fills <c>unique_user_id</c>, <c>driver_name</c>, <c>car_number</c> for capture_incident logs.
        /// Resolves by car number when provided; else uses replay camera / focus car index.
        /// </summary>
        private void TryFillDriverIdentityForCapture(string carNumberStr, System.Collections.Generic.Dictionary<string, object> target)
        {
            target["car_number"] = carNumberStr ?? "";
            target["unique_user_id"] = "unknown";
            target["driver_name"] = "";
            if (_irsdk?.Data?.SessionInfo?.DriverInfo?.Drivers is not IList list)
                return;

            object match = null;
            if (!string.IsNullOrWhiteSpace(carNumberStr))
            {
                var want = carNumberStr.Trim();
                foreach (var o in list)
                {
                    if (o == null) continue;
                    var t = o.GetType();
                    var num = t.GetProperty("CarNumber")?.GetValue(o)?.ToString()?.Trim() ?? "";
                    if (string.Equals(num, want, StringComparison.OrdinalIgnoreCase))
                    {
                        match = o;
                        break;
                    }
                }
            }

            if (match == null)
            {
                int cam = ResolveFocusCarIdx();
                if (cam >= 0)
                {
                    foreach (var o in list)
                    {
                        if (o == null) continue;
                        var t = o.GetType();
                        var carIdxObj = t.GetProperty("CarIdx")?.GetValue(o);
                        var idx = carIdxObj is int ci ? ci : System.Convert.ToInt32(carIdxObj ?? -1);
                        if (idx == cam)
                        {
                            match = o;
                            break;
                        }
                    }
                }
            }

            if (match == null) return;
            var mt = match.GetType();
            target["driver_name"] = mt.GetProperty("UserName")?.GetValue(match)?.ToString() ?? "";
            var uidObj = mt.GetProperty("UserID")?.GetValue(match) ?? mt.GetProperty("CustID")?.GetValue(match);
            if (uidObj != null)
                target["unique_user_id"] = uidObj.ToString();
            if (string.IsNullOrWhiteSpace(carNumberStr))
                target["car_number"] = mt.GetProperty("CarNumber")?.GetValue(match)?.ToString() ?? "";
        }

        private System.Collections.Generic.Dictionary<string, object> BuildCaptureIncidentSupplement(CaptureIncidentArg parsed, long durationMs)
        {
            var d = new System.Collections.Generic.Dictionary<string, object>
            {
                ["replay_frame"] = parsed.frame,
                ["capture_camera"] = parsed.camera ?? "",
                ["duration_ms"] = durationMs
            };
            var carNumStr = parsed.car != null ? parsed.car.Value.ToString() : null;
            TryFillDriverIdentityForCapture(carNumStr, d);
            return d;
        }

        private (bool success, string result, string error) DispatchAction(string action, string arg, string correlationId)
        {
            if (string.IsNullOrEmpty(action))
                return (false, null, "missing_action");

            // Sentry: create a child span under the current transaction (if any)
            ISpan _actionSpan = null;
            SentrySdk.ConfigureScope(scope =>
            {
                var parentTx = scope.Transaction;
                if (parentTx != null)
                    _actionSpan = parentTx.StartChild("action", action ?? "unknown");
            });

            var dispatchFields = new System.Collections.Generic.Dictionary<string, object>
            {
                ["action"] = action,
                ["arg"] = arg ?? "",
                ["correlation_id"] = correlationId ?? ""
            };
            MergeSessionAndRoutingFields(dispatchFields);
            _logger?.Structured("INFO", "simhub-plugin", "action_dispatched", action, dispatchFields, "action", null);

            try
            {

            if (string.Equals(action, "replay_session", StringComparison.OrdinalIgnoreCase))
            {
                var dir = (arg ?? "").Trim().ToLowerInvariant();
                if (dir != "prev" && dir != "next")
                {
                    const string err = "bad_arg";
                    LogActionResult(action, arg, correlationId, false, err);
                    return (false, null, err);
                }

                if (_irsdk == null || !_irsdk.IsConnected)
                {
                    const string err = "not_connected";
                    LogActionResult(action, arg, correlationId, false, err);
                    return (false, null, err);
                }

                try
                {
                    var mode = dir == "prev"
                        ? IRacingSdkEnum.RpySrchMode.PrevSession
                        : IRacingSdkEnum.RpySrchMode.NextSession;
                    _irsdk.ReplaySearch(mode);
                    LogActionResult(action, arg, correlationId, true, "");
                    return (true, "ok", null);
                }
                catch (Exception ex)
                {
                    SentrySdk.CaptureException(ex);
                    var err = ex.Message ?? "replay_session_failed";
                    LogActionResult(action, arg, correlationId, false, err);
                    return (false, null, err);
                }
            }

            // ── Test rig (docs/RULES-TestRig-Contract.md): replay control surface ──
            if (string.Equals(action, "replay_play", StringComparison.OrdinalIgnoreCase))
            {
                if (_irsdk == null || !_irsdk.IsConnected)
                {
                    LogActionResult(action, arg, correlationId, false, "not_connected");
                    return (false, null, "not_connected");
                }
                try
                {
                    int magnitude = _lastReplaySpeedMagnitude > 0 ? _lastReplaySpeedMagnitude : 1;
                    int signedSpeed = string.Equals(_lastReplayDirection, "reverse", StringComparison.OrdinalIgnoreCase)
                        ? -magnitude : magnitude;
                    _irsdk.ReplaySetPlaySpeed(signedSpeed, _lastReplaySlowMotion);
                    var sup = new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["speed"] = magnitude,
                        ["direction"] = _lastReplayDirection,
                        ["slow_motion"] = _lastReplaySlowMotion
                    };
                    LogActionResult(action, arg, correlationId, true, "", sup);
                    return (true, "ok", null);
                }
                catch (Exception ex)
                {
                    SentrySdk.CaptureException(ex);
                    var err = ex.Message ?? "replay_play_failed";
                    LogActionResult(action, arg, correlationId, false, err);
                    return (false, null, err);
                }
            }

            if (string.Equals(action, "replay_pause", StringComparison.OrdinalIgnoreCase))
            {
                if (_irsdk == null || !_irsdk.IsConnected)
                {
                    LogActionResult(action, arg, correlationId, false, "not_connected");
                    return (false, null, "not_connected");
                }
                try
                {
                    _irsdk.ReplaySetPlaySpeed(0, false);
                    LogActionResult(action, arg, correlationId, true, "");
                    return (true, "ok", null);
                }
                catch (Exception ex)
                {
                    SentrySdk.CaptureException(ex);
                    var err = ex.Message ?? "replay_pause_failed";
                    LogActionResult(action, arg, correlationId, false, err);
                    return (false, null, err);
                }
            }

            if (string.Equals(action, "replay_set_speed", StringComparison.OrdinalIgnoreCase))
            {
                var parsed = ReplayControlActions.ParseSetSpeed(arg);
                if (!parsed.Ok)
                {
                    LogActionResult(action, arg, correlationId, false, parsed.Error);
                    return (false, null, parsed.Error);
                }
                if (_irsdk == null || !_irsdk.IsConnected)
                {
                    LogActionResult(action, arg, correlationId, false, "not_connected");
                    return (false, null, "not_connected");
                }
                try
                {
                    _lastReplaySpeedMagnitude = parsed.Magnitude;
                    _lastReplaySlowMotion = parsed.SlowMotion;
                    int signed = string.Equals(_lastReplayDirection, "reverse", StringComparison.OrdinalIgnoreCase)
                        ? -parsed.Magnitude : parsed.Magnitude;
                    _irsdk.ReplaySetPlaySpeed(signed, parsed.SlowMotion);
                    var sup = new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["speed"] = parsed.Magnitude,
                        ["slow_motion"] = parsed.SlowMotion,
                        ["direction"] = _lastReplayDirection
                    };
                    LogActionResult(action, arg, correlationId, true, "", sup);
                    return (true, "ok", null);
                }
                catch (Exception ex)
                {
                    SentrySdk.CaptureException(ex);
                    var err = ex.Message ?? "replay_set_speed_failed";
                    LogActionResult(action, arg, correlationId, false, err);
                    return (false, null, err);
                }
            }

            if (string.Equals(action, "replay_set_direction", StringComparison.OrdinalIgnoreCase))
            {
                var parsed = ReplayControlActions.ParseSetDirection(arg);
                if (!parsed.Ok)
                {
                    LogActionResult(action, arg, correlationId, false, parsed.Error);
                    return (false, null, parsed.Error);
                }
                if (_irsdk == null || !_irsdk.IsConnected)
                {
                    LogActionResult(action, arg, correlationId, false, "not_connected");
                    return (false, null, "not_connected");
                }
                if (_lastReplaySpeedMagnitude <= 0)
                {
                    LogActionResult(action, arg, correlationId, false, "no_prior_speed");
                    return (false, null, "no_prior_speed");
                }
                try
                {
                    _lastReplayDirection = parsed.Direction;
                    int signed = string.Equals(parsed.Direction, "reverse", StringComparison.OrdinalIgnoreCase)
                        ? -_lastReplaySpeedMagnitude : _lastReplaySpeedMagnitude;
                    _irsdk.ReplaySetPlaySpeed(signed, _lastReplaySlowMotion);
                    var sup = new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["speed"] = _lastReplaySpeedMagnitude,
                        ["direction"] = _lastReplayDirection,
                        ["slow_motion"] = _lastReplaySlowMotion
                    };
                    LogActionResult(action, arg, correlationId, true, "", sup);
                    return (true, "ok", null);
                }
                catch (Exception ex)
                {
                    SentrySdk.CaptureException(ex);
                    var err = ex.Message ?? "replay_set_direction_failed";
                    LogActionResult(action, arg, correlationId, false, err);
                    return (false, null, err);
                }
            }

            if (string.Equals(action, "replay_seek_frame", StringComparison.OrdinalIgnoreCase))
            {
                var parsed = ReplayControlActions.ParseSeekFrame(arg);
                if (!parsed.Ok)
                {
                    LogActionResult(action, arg, correlationId, false, parsed.Error);
                    return (false, null, parsed.Error);
                }
                if (IsSeekThrottled())
                {
                    LogActionResult(action, arg, correlationId, false, "seek_throttled");
                    return (false, null, "seek_throttled");
                }
                if (_irsdk == null || !_irsdk.IsConnected)
                {
                    LogActionResult(action, arg, correlationId, false, "not_connected");
                    return (false, null, "not_connected");
                }
                try
                {
                    MarkSeekIssued();
                    _irsdk.ReplaySetPlayPosition(IRacingSdkEnum.RpyPosMode.Begin, parsed.Frame);
                    var sup = new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["frame"] = parsed.Frame
                    };
                    LogActionResult(action, arg, correlationId, true, "", sup);
                    return (true, "ok", null);
                }
                catch (Exception ex)
                {
                    SentrySdk.CaptureException(ex);
                    var err = ex.Message ?? "replay_seek_frame_failed";
                    LogActionResult(action, arg, correlationId, false, err);
                    return (false, null, err);
                }
            }

            if (string.Equals(action, "replay_jump_next_incident", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(action, "replay_jump_prev_incident", StringComparison.OrdinalIgnoreCase))
            {
                bool isNext = string.Equals(action, "replay_jump_next_incident", StringComparison.OrdinalIgnoreCase);
                var rows = _replayIndexDashboardCachedRoot?.Incidents;
                if (rows == null || rows.Count == 0)
                {
                    LogActionResult(action, arg, correlationId, false, "index_unavailable");
                    return (false, null, "index_unavailable");
                }
                if (_irsdk == null || !_irsdk.IsConnected)
                {
                    LogActionResult(action, arg, correlationId, false, "not_connected");
                    return (false, null, "not_connected");
                }
                if (IsSeekThrottled())
                {
                    LogActionResult(action, arg, correlationId, false, "seek_throttled");
                    return (false, null, "seek_throttled");
                }
                int currentFrame = SafeGetInt("ReplayFrameNum");
                var picked = isNext
                    ? ReplayControlActions.ResolveNextIncident(rows, currentFrame)
                    : ReplayControlActions.ResolvePrevIncident(rows, currentFrame);
                if (!picked.Ok)
                {
                    LogActionResult(action, arg, correlationId, false, picked.Error);
                    return (false, null, picked.Error);
                }
                try
                {
                    MarkSeekIssued();
                    int sessionNum = SafeGetInt("SessionNum");
                    _irsdk.ReplaySearchSessionTime(sessionNum, picked.Row.SessionTimeMs);
                    // Stamp expected jump for misfire evaluator (DataUpdate evaluates ~750 ms later).
                    _lastJumpRequestedAt = DateTime.UtcNow;
                    _lastJumpDirection = isNext ? "next" : "prev";
                    _lastJumpExpectedFrame = picked.Row.ReplayFrame;
                    _lastJumpExpectedSessionTimeMs = picked.Row.SessionTimeMs;
                    _lastJumpExpectedFingerprint = picked.Row.Fingerprint;
                    _lastJumpEvaluated = false;
                    var sup = new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["expected_frame"] = picked.Row.ReplayFrame,
                        ["expected_session_time_ms"] = picked.Row.SessionTimeMs,
                        ["expected_fingerprint"] = picked.Row.Fingerprint ?? ""
                    };
                    LogActionResult(action, arg, correlationId, true, "", sup);
                    return (true, "ok", null);
                }
                catch (Exception ex)
                {
                    SentrySdk.CaptureException(ex);
                    var err = ex.Message ?? "replay_jump_failed";
                    LogActionResult(action, arg, correlationId, false, err);
                    return (false, null, err);
                }
            }

            if (string.Equals(action, "replay_seek", StringComparison.OrdinalIgnoreCase))
            {
                var dir = (arg ?? "").Trim().ToLowerInvariant();
                if (dir != "prev" && dir != "next")
                {
                    const string err = "bad_arg";
                    LogActionResult(action, arg, correlationId, false, err);
                    return (false, null, err);
                }

                if (IsSeekThrottled())
                {
                    LogActionResult(action, arg, correlationId, false, "seek_throttled");
                    return (false, null, "seek_throttled");
                }
                MarkSeekIssued();

                if (_irsdk == null || !_irsdk.IsConnected)
                {
                    const string err = "not_connected";
                    LogActionResult(action, arg, correlationId, false, err);
                    return (false, null, err);
                }

                try
                {
                    var mode = dir == "prev"
                        ? IRacingSdkEnum.RpySrchMode.PrevIncident
                        : IRacingSdkEnum.RpySrchMode.NextIncident;
                    _irsdk.ReplaySearch(mode);
                    LogActionResult(action, arg, correlationId, true, "");
                    return (true, "ok", null);
                }
                catch (Exception ex)
                {
                    SentrySdk.CaptureException(ex);
                    var err = ex.Message ?? "replay_seek_failed";
                    LogActionResult(action, arg, correlationId, false, err);
                    return (false, null, err);
                }
            }

            if (string.Equals(action, "replay_jump", StringComparison.OrdinalIgnoreCase))
            {
                var target = (arg ?? "").Trim().ToLowerInvariant();
                if (target != "start" && target != "end")
                {
                    const string err = "bad_arg";
                    LogActionResult(action, arg, correlationId, false, err);
                    return (false, null, err);
                }

                if (_irsdk == null || !_irsdk.IsConnected)
                {
                    const string err = "not_connected";
                    LogActionResult(action, arg, correlationId, false, err);
                    return (false, null, err);
                }

                try
                {
                    var mode = target == "start"
                        ? IRacingSdkEnum.RpySrchMode.ToStart
                        : IRacingSdkEnum.RpySrchMode.ToEnd;
                    _irsdk.ReplaySearch(mode);
                    LogActionResult(action, arg, correlationId, true, "");
                    return (true, "ok", null);
                }
                catch (Exception ex)
                {
                    SentrySdk.CaptureException(ex);
                    var err = ex.Message ?? "replay_jump_failed";
                    LogActionResult(action, arg, correlationId, false, err);
                    return (false, null, err);
                }
            }

            if (string.Equals(action, "seek_to_incident", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse((arg ?? "").Trim(), out var frame) || frame < 0)
                {
                    const string err = "bad_arg";
                    LogActionResult(action, arg, correlationId, false, err);
                    return (false, null, err);
                }

                if (IsSeekThrottled())
                {
                    LogActionResult(action, arg, correlationId, false, "seek_throttled");
                    return (false, null, "seek_throttled");
                }
                MarkSeekIssued();

                if (_irsdk == null || !_irsdk.IsConnected)
                {
                    const string err = "not_connected";
                    LogActionResult(action, arg, correlationId, false, err);
                    return (false, null, err);
                }

                try
                {
                    _irsdk.ReplaySetPlayPosition(IRacingSdkEnum.RpyPosMode.Begin, frame);
                    LogActionResult(action, arg, correlationId, true, "");
                    return (true, "ok", null);
                }
                catch (Exception ex)
                {
                    SentrySdk.CaptureException(ex);
                    var err = ex.Message ?? "seek_to_incident_failed";
                    LogActionResult(action, arg, correlationId, false, err);
                    return (false, null, err);
                }
            }

            if (string.Equals(action, "set_camera", StringComparison.OrdinalIgnoreCase))
            {
                var groupName = (arg ?? "").Trim();
                if (string.IsNullOrEmpty(groupName))
                {
                    LogActionResult(action, arg, correlationId, false, "bad_arg");
                    return (false, null, "bad_arg");
                }
                if (_irsdk == null || !_irsdk.IsConnected)
                {
                    LogActionResult(action, arg, correlationId, false, "not_connected");
                    return (false, null, "not_connected");
                }
                try
                {
                    int g = ResolveCameraGroupNum(groupName);
                    if (g < 0)
                    {
                        LogActionResult(action, arg, correlationId, false, "group_not_found");
                        return (false, null, "group_not_found");
                    }
                    SwitchCameraToGroup(g);
                    LogActionResult(action, arg, correlationId, true, "");
                    return (true, "ok", null);
                }
                catch (Exception ex)
                {
                    SentrySdk.CaptureException(ex);
                    LogActionResult(action, arg, correlationId, false, ex.Message);
                    return (false, null, ex.Message);
                }
            }

            if (string.Equals(action, "capture_incident", StringComparison.OrdinalIgnoreCase))
            {
                CaptureIncidentArg parsed;
                try
                {
                    parsed = JsonConvert.DeserializeObject<CaptureIncidentArg>(arg ?? "");
                }
                catch
                {
                    LogActionResult(action, arg, correlationId, false, "bad_arg");
                    return (false, null, "bad_arg");
                }
                if (parsed == null || parsed.frame < 0)
                {
                    LogActionResult(action, arg, correlationId, false, "bad_arg");
                    return (false, null, "bad_arg");
                }
                if (IsSeekThrottled())
                {
                    LogActionResult(action, arg, correlationId, false, "seek_throttled");
                    return (false, null, "seek_throttled");
                }
                MarkSeekIssued();
                if (_irsdk == null || !_irsdk.IsConnected)
                {
                    var supOff = BuildCaptureIncidentSupplement(parsed, 0);
                    LogActionResult(action, arg, correlationId, false, "not_connected", supOff);
                    return (false, null, "not_connected");
                }
                var sw = Stopwatch.StartNew();
                try
                {
                    int seekFrame = Math.Max(0, parsed.frame - CapturePreRollFrames);
                    _irsdk.ReplaySetPlayPosition(IRacingSdkEnum.RpyPosMode.Begin, seekFrame);
                    if (!string.IsNullOrEmpty(parsed.camera))
                    {
                        int g = ResolveCameraGroupNum(parsed.camera);
                        if (g >= 0) SwitchCameraToGroup(g);
                    }
                    _irsdk.ReplaySetPlaySpeed(1, false);
                    sw.Stop();

                    // Write to CaptureManifest
                    long sessionTimeMsResolved = parsed.sessionTimeMs ?? ResolveSessionTimeMsFromFrame(parsed.frame);
                    string fp = !string.IsNullOrEmpty(parsed.fingerprint)
                        ? parsed.fingerprint
                        : ReplayIncidentIndexFingerprint.ComputeHexV1(
                            (int)Math.Min(_captureManifest?.SubSessionId ?? GetCurrentSubSessionIdForCapture(), int.MaxValue),
                            parsed.carIdx ?? parsed.car ?? 0,
                            (int)Math.Min(Math.Max(sessionTimeMsResolved, 0), int.MaxValue),
                            parsed.detectionSource ?? "manual",
                            parsed.incidentPoints);

                    long subSessId = GetCurrentSubSessionIdForCapture();
                    if (_captureManifest == null || _captureManifest.SubSessionId != subSessId)
                    {
                        _captureManifest = new CaptureManifest
                        {
                            SubSessionId = subSessId,
                            GeneratedAt = DateTimeOffset.UtcNow.ToString("o")
                        };
                    }

                    var cameraView = parsed.camera ?? "";
                    var nowStr = DateTimeOffset.UtcNow.ToString("o");
                    var existingEntry = _captureManifest.Entries.Find(e => e.Fingerprint == fp);
                    if (existingEntry != null)
                    {
                        if (!existingEntry.Clips.Exists(c => c.CameraView == cameraView))
                            existingEntry.Clips.Add(new CaptureClipEntry { CameraView = cameraView, CapturedAt = nowStr });
                    }
                    else
                    {
                        _captureManifest.Entries.Add(new CaptureManifestEntry
                        {
                            Fingerprint = fp,
                            CarIdx = parsed.carIdx ?? parsed.car ?? 0,
                            SessionNum = SafeGetInt("SessionNum"),
                            SessionTimeMs = sessionTimeMsResolved,
                            Lap = parsed.lap ?? SessionLogging.LapUnknown,
                            IncidentPoints = parsed.incidentPoints ?? 0,
                            DetectionSource = parsed.detectionSource ?? "manual",
                            PushedToQueue = false,
                            CapturedAt = nowStr,
                            Clips = new System.Collections.Generic.List<CaptureClipEntry>
                            {
                                new CaptureClipEntry { CameraView = cameraView, CapturedAt = nowStr }
                            }
                        });
                    }
                    _captureManifestDirty = true;

                    var captureFields = new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["fingerprint"] = fp,
                        ["car_idx"] = parsed.carIdx ?? parsed.car ?? 0,
                        ["session_time_ms"] = sessionTimeMsResolved,
                        ["lap"] = parsed.lap ?? SessionLogging.LapUnknown,
                        ["incident_points"] = parsed.incidentPoints ?? 0,
                        ["detection_source"] = parsed.detectionSource ?? "manual",
                        ["camera_view"] = cameraView
                    };
                    MergeSessionAndRoutingFields(captureFields);
                    _logger?.Structured("INFO", "CaptureManifest", "incident_committed",
                        $"Incident committed fingerprint:{fp}", captureFields, domain: SessionLogging.DomainCapture);

                    LogActionResult(action, arg, correlationId, true, "", BuildCaptureIncidentSupplement(parsed, sw.ElapsedMilliseconds));
                    return (true, "ok", null);
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    SentrySdk.CaptureException(ex);
                    LogActionResult(action, arg, correlationId, false, ex.Message, BuildCaptureIncidentSupplement(parsed, sw.ElapsedMilliseconds));
                    return (false, null, ex.Message);
                }
            }

            if (string.Equals(action, "replay_incident_index_seek", StringComparison.OrdinalIgnoreCase))
            {
                return DispatchReplayIncidentIndexSeek(arg, correlationId);
            }

            if (string.Equals(action, "replay_incident_index_record", StringComparison.OrdinalIgnoreCase))
            {
                return DispatchReplayIncidentIndexRecord(arg, correlationId);
            }

            if (string.Equals(action, "replay_incident_index_build", StringComparison.OrdinalIgnoreCase))
            {
                return DispatchReplayIncidentIndexBuild(arg, correlationId);
            }

            if (string.Equals(action, "index_next_incident", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(action, "index_prev_incident", StringComparison.OrdinalIgnoreCase))
            {
                bool isNext = string.Equals(action, "index_next_incident", StringComparison.OrdinalIgnoreCase);
                var incidents = _replayIndexDashboardCachedRoot?.Incidents;
                if (incidents == null || incidents.Count == 0)
                {
                    LogActionResult(action, arg, correlationId, false, "no_index");
                    return (false, null, "no_index");
                }
                if (_irsdk == null || !_irsdk.IsConnected)
                {
                    LogActionResult(action, arg, correlationId, false, "not_connected");
                    return (false, null, "not_connected");
                }
                string simMode2 = _irsdk.Data?.SessionInfo?.WeekendInfo?.SimMode ?? "";
                if (!string.Equals(simMode2, "replay", StringComparison.OrdinalIgnoreCase))
                {
                    LogActionResult(action, arg, correlationId, false, "not_replay_mode");
                    return (false, null, "not_replay_mode");
                }
                if (IsSeekThrottled())
                {
                    LogActionResult(action, arg, correlationId, false, "seek_throttled");
                    return (false, null, "seek_throttled");
                }
                if (_incidentCursor < 0) _incidentCursor = 0;
                else if (isNext) _incidentCursor = (_incidentCursor + 1) % incidents.Count;
                else _incidentCursor = (_incidentCursor - 1 + incidents.Count) % incidents.Count;
                var row = incidents[_incidentCursor];
                int sessionNumCursor = SafeGetInt("SessionNum");
                try
                {
                    MarkSeekIssued();
                    _irsdk.ReplaySearchSessionTime(sessionNumCursor, row.SessionTimeMs);
                    _irsdk.CamSwitchPos(IRacingSdkEnum.CamSwitchMode.FocusAtDriver, row.CarIdx, 0, 0);
                    var supFields = new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["cursor_idx"] = _incidentCursor,
                        ["incident_count"] = incidents.Count,
                        ["car_idx"] = row.CarIdx,
                        ["session_time_ms"] = row.SessionTimeMs
                    };
                    LogActionResult(action, arg, correlationId, true, "", supFields);
                    return (true, "ok", null);
                }
                catch (Exception ex)
                {
                    SentrySdk.CaptureException(ex);
                    LogActionResult(action, arg, correlationId, false, ex.Message ?? "seek_failed");
                    return (false, null, ex.Message ?? "seek_failed");
                }
            }

            if (string.Equals(action, "index_walk_start", StringComparison.OrdinalIgnoreCase))
            {
                var incidents = _replayIndexDashboardCachedRoot?.Incidents;
                if (incidents == null || incidents.Count == 0)
                {
                    LogActionResult(action, arg, correlationId, false, "no_index");
                    return (false, null, "no_index");
                }
                if (_irsdk == null || !_irsdk.IsConnected)
                {
                    LogActionResult(action, arg, correlationId, false, "not_connected");
                    return (false, null, "not_connected");
                }
                _autoWalkActive = true;
                if (_incidentCursor < 0) _incidentCursor = 0;
                _autoWalkPlayUntilFrame = SafeGetInt("ReplayFrameNum") + AutoWalkPlayFrames;
                var walkRow = incidents[_incidentCursor];
                int snWalk = SafeGetInt("SessionNum");
                try
                {
                    MarkSeekIssued();
                    _irsdk.ReplaySearchSessionTime(snWalk, walkRow.SessionTimeMs);
                    _irsdk.CamSwitchPos(IRacingSdkEnum.CamSwitchMode.FocusAtDriver, walkRow.CarIdx, 0, 0);
                }
                catch (Exception ex) { SentrySdk.CaptureException(ex); }
                LogActionResult(action, arg, correlationId, true, "");
                return (true, "ok", null);
            }

            if (string.Equals(action, "index_walk_stop", StringComparison.OrdinalIgnoreCase))
            {
                _autoWalkActive = false;
                LogActionResult(action, arg, correlationId, true, "");
                return (true, "ok", null);
            }

            if (string.Equals(action, "capture_all_incidents", StringComparison.OrdinalIgnoreCase))
            {
                var corrId = Guid.NewGuid().ToString("N");
                var fields = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["action"] = action, ["arg"] = arg ?? "", ["correlation_id"] = corrId
                };
                MergeSessionAndRoutingFields(fields);
                _logger?.Structured("INFO", "DispatchAction", "action_dispatched",
                    "Action:capture_all_incidents", fields, domain: SessionLogging.DomainAction);

                var incidents = _replayIndexDashboardCachedRoot?.Incidents;
                if (incidents == null || incidents.Count == 0)
                {
                    fields["success"] = false;
                    fields["error"] = "no_index";
                    _logger?.Structured("INFO", "DispatchAction", "action_result",
                        "Action:capture_all_incidents result:no_index", fields, domain: SessionLogging.DomainAction);
                    return (false, null, "no_index");
                }

                int playerCarIdx = SafeGetInt("PlayerCarIdx");
                var toEnqueue = playerCarIdx >= 0
                    ? incidents.Where(r => r.CarIdx == playerCarIdx).ToList()
                    : incidents.ToList();

                int queued = 0;
                foreach (var row in toEnqueue)
                {
                    _captureQueue.Enqueue(new CaptureTask
                    {
                        Fingerprint = row.Fingerprint,
                        CarIdx = row.CarIdx,
                        SessionTimeMs = row.SessionTimeMs,
                        Lap = row.Lap,
                        IncidentPoints = row.IncidentPoints ?? 0,
                        DetectionSource = row.DetectionSource ?? "",
                        ReplayFrame = row.ReplayFrame
                    });
                    queued++;
                }

                fields["success"] = true;
                fields["queued"] = queued;
                fields["player_car_idx"] = playerCarIdx;
                _logger?.Structured("INFO", "DispatchAction", "action_result",
                    $"Action:capture_all_incidents result:ok queued:{queued}", fields, domain: SessionLogging.DomainAction);
                return (true, $"queued:{queued}", null);
            }

            if (string.Equals(action, "capture_cancel", StringComparison.OrdinalIgnoreCase))
            {
                var corrId = correlationId ?? Guid.NewGuid().ToString("N");
                var fields = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["action"] = action, ["arg"] = arg ?? "", ["correlation_id"] = corrId
                };
                MergeSessionAndRoutingFields(fields);
                _logger?.Structured("INFO", "simhub-plugin", "action_dispatched",
                    "Action:capture_cancel", fields, domain: SessionLogging.DomainAction);
                int cleared = _captureQueue.Count;
                _captureQueue.Clear();
                fields["success"] = true;
                fields["cleared"] = cleared;
                _logger?.Structured("INFO", "simhub-plugin", "action_result",
                    "Action:capture_cancel result:ok", fields, domain: SessionLogging.DomainAction);
                return (true, "ok", null);
            }

            LogActionResult(action, arg, correlationId, false, "not_supported");
            return (false, null, "not_supported");

            }
            catch
            {
                _actionSpan?.Finish(SpanStatus.InternalError);
                _actionSpan = null;
                throw;
            }
            finally
            {
                _actionSpan?.Finish(SpanStatus.Ok);
            }
        }

        private void OnLog(string level, string message, string source)
        {
            var prefix = "[OVERLAY]";
            if (!string.IsNullOrEmpty(source)) prefix += " [" + source + "]";
            var line = prefix + " " + message;
            if (string.Equals(level, "warn", StringComparison.OrdinalIgnoreCase))
                _logger?.Warn(line);
            else if (string.Equals(level, "error", StringComparison.OrdinalIgnoreCase))
                _logger?.Error(line);
            else
                _logger?.Info(line);
        }

        private void OnDashboardStructuredLog(string eventType, string message, System.Collections.Generic.Dictionary<string, object> fields)
        {
            if (string.IsNullOrEmpty(eventType) || fields == null) return;
            MergeSessionAndRoutingFields(fields);
            _logger?.Structured("INFO", "bridge", eventType, message, fields, "ui", null);
        }

        private void MergeSessionAndRoutingFields(System.Collections.Generic.Dictionary<string, object> fields)
        {
            if (fields == null) return;
            fields["subsession_id"] = _logCtxSubsession;
            fields["parent_session_id"] = _logCtxParent;
            fields["session_num"] = _logCtxSessionNum;
            fields["track_display_name"] = _logCtxTrack;
            fields["lap"] = _logCtxLap;
            var fp = _logCtxSessionYamlFingerprint;
            if (!string.IsNullOrEmpty(fp))
                fields["session_yaml_fingerprint_sha256_16"] = fp;
            SessionLogging.AppendRoutingAndDestination(fields);
        }

        private void RefreshDependencyChecks()
        {
            try
            {
                _steamRunning = Process.GetProcessesByName("steam").Length > 0;
            }
            catch
            {
                _steamRunning = false;
            }

            IPEndPoint[] listeners = Array.Empty<IPEndPoint>();
            try
            {
                listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
                _simHubHttpListening = listeners.Any(e => e.Port == 8888);
            }
            catch
            {
                _simHubHttpListening = false;
            }

            var now = DateTime.UtcNow;
            if ((now - _lastDashboardPingUtc).TotalSeconds < DashboardPingIntervalSec)
                return;
            _lastDashboardPingUtc = now;

            Task.Run(() =>
            {
                const string pingUrl = "http://127.0.0.1:8888/Web/sim-steward-dash/index.html";
                try
                {
                    var response = DashboardPingClient
                        .GetAsync(pingUrl)
                        .ConfigureAwait(false)
                        .GetAwaiter()
                        .GetResult();
                    _dashboardPingStatus = response.IsSuccessStatusCode
                        ? $"OK ({(int)response.StatusCode})"
                        : $"HTTP {(int)response.StatusCode}";
                }
                catch (Exception ex)
                {
                    SentrySdk.CaptureException(ex);
                    _dashboardPingStatus = "Error: " + ex.Message;
                }
            });
        }

#endif

#if SIMHUB_SDK
        public void Init(PluginManager pluginManager)
        {
            PluginManager = pluginManager;
            _pluginDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SimHubWpf", "PluginsData", "SimSteward");

            _debugMode = Environment.GetEnvironmentVariable("SIMSTEWARD_LOG_DEBUG") == "1";
            _lokiBaseUrl = Environment.GetEnvironmentVariable("SIMSTEWARD_LOKI_URL")?.Trim() ?? "";
            _grafanaBaseUrl = Environment.GetEnvironmentVariable("SIMSTEWARD_GRAFANA_URL")?.Trim() ?? "http://localhost:3000";

            var sentryDsn = Environment.GetEnvironmentVariable("SIMSTEWARD_SENTRY_DSN")?.Trim();
            if (string.IsNullOrWhiteSpace(sentryDsn))
                sentryDsn = "https://ab2d0a6f7cd97033a46f4fa7d90dabab@o4511097126780928.ingest.us.sentry.io/4511102961319936";

            _logger = new PluginLogger(_pluginDataPath, isDebugMode: _debugMode);
            _logger.SetSpineProvider(() => (_currentSessionId ?? "", _currentSessionSeq ?? "", _replayFrameNumEnd));
            _logger.WriteError += OnLogWriteError;
            _logger.Structured("INFO", "simhub-plugin", "logging_ready", "Logging pipeline ready; init continuing.", null, "lifecycle", null);

            try
            {
                _sentryDisposable = SentrySdk.Init(o =>
                {
                    o.Dsn = sentryDsn;
                    o.Environment = Environment.GetEnvironmentVariable("SIMSTEWARD_LOG_ENV") ?? "local";
                    o.Release = PluginVersionInfo.Display;
                    o.TracesSampleRate = 1.0;
                    o.IsGlobalModeEnabled = true;
                    o.AutoSessionTracking = false;
                    o.MaxBreadcrumbs = 100;
                    o.SetBeforeSend((sentryEvent, hint) =>
                    {
                        sentryEvent.SetTag("plugin_mode", _pluginMode ?? "Unknown");
                        sentryEvent.SetTag("iracing_connected", (_irsdk?.IsConnected ?? false).ToString().ToLowerInvariant());
                        return sentryEvent;
                    });
                });
            }
            catch (Exception ex)
            {
                _logger.Structured("WARN", "simhub-plugin", "sentry_init_failed",
                    $"Sentry SDK init failed: {ex.Message}",
                    new System.Collections.Generic.Dictionary<string, object> { ["error"] = ex.Message }, "lifecycle", null);
            }

            var structuredPath = _logger.StructuredLogPath;
            _logger.Structured("INFO", "simhub-plugin", "file_tail_ready", "Structured log file ready for Loki ingestion.",
                new System.Collections.Generic.Dictionary<string, object> { ["path"] = structuredPath ?? "(none)" }, "lifecycle", null);

            _logger.Structured("INFO", "simhub-plugin", "plugin_started", "SimSteward plugin starting.", null, "lifecycle", null);
            SentrySdk.AddBreadcrumb("Plugin started", "lifecycle");

            pluginManager.AddProperty("SimSteward.PluginMode", GetType(), "Unknown");
            pluginManager.AddProperty("SimSteward.ClientCount", GetType(), 0);

            var wsPort = DefaultPort;
            var wsPortEnv = Environment.GetEnvironmentVariable("SIMSTEWARD_WS_PORT");
            if (!string.IsNullOrWhiteSpace(wsPortEnv) &&
                int.TryParse(wsPortEnv, out var parsedPort) &&
                parsedPort > 0 && parsedPort < 65536)
            {
                wsPort = parsedPort;
            }
            _wsPort = wsPort;
            var wsBind = Environment.GetEnvironmentVariable("SIMSTEWARD_WS_BIND");
            if (string.IsNullOrWhiteSpace(wsBind))
                wsBind = "0.0.0.0";
            var wsToken = Environment.GetEnvironmentVariable("SIMSTEWARD_WS_TOKEN");
            if (string.IsNullOrWhiteSpace(wsToken))
                wsToken = null;

            _logger.Structured("INFO", "simhub-plugin", "bridge_starting", "WebSocket bridge starting.",
                new System.Collections.Generic.Dictionary<string, object> { ["bind"] = wsBind, ["port"] = _wsPort }, "lifecycle", null);

            _bridge = new DashboardBridge(
                GetStateForNewClient,
                GetLogTailForNewClient,
                DispatchAction,
                OnLog,
                _logger,
                OnDashboardStructuredLog,
                onSendError: (ex, payloadType) => WriteBroadcastError("Send:" + payloadType, ex),
                onNoClients: () =>
                {
                    var n = DateTime.UtcNow;
                    lock (_broadcastErrorLock)
                    {
                        if ((n - _lastNoClientsLogAt).TotalSeconds < 10) return;
                        _lastNoClientsLogAt = n;
                    }
                    WriteBroadcastError("Broadcast skipped: 0 clients", null);
                    _broadcastNoClientsPending = true;
                });

            try
            {
                _bridge.Start(wsBind, wsPort, wsToken);
                _logger.Structured("INFO", "simhub-plugin", "plugin_ready", "SimSteward ready.",
                    new System.Collections.Generic.Dictionary<string, object> { ["ws_port"] = _wsPort }, "lifecycle", null);
                SentrySdk.AddBreadcrumb("WebSocket bridge started", "lifecycle",
                    data: new Dictionary<string, string> { ["port"] = _wsPort.ToString() });
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
                _logger.Structured("WARN", "simhub-plugin", "bridge_start_failed", $"WebSocket server could not start: {ex.Message}",
                    new System.Collections.Generic.Dictionary<string, object> { ["bind"] = wsBind, ["port"] = _wsPort, ["error"] = ex.Message }, "lifecycle", null);
                _logger.Error($"WebSocket server could not start on {wsBind}:{wsPort}. Is it already in use?", ex);
                _bridge = null;
            }

            _logger.LogWritten += OnLogWritten;
            _logger.Structured("INFO", "simhub-plugin", "log_streaming_subscribed", "Dashboard log streaming attached.", null, "lifecycle", null);

            try
            {
                _irsdk = new IRacingSdk();
                _irsdk.UpdateInterval = 1;
                _irsdk.OnConnected += () =>
                {
                    SentrySdk.AddBreadcrumb("iRacing connected", "lifecycle");
                    _logger?.Structured("INFO", "simhub-plugin", "iracing_connected", "iRacing connected.", null, "lifecycle", null);
                    if (_irsdk != null && _logger != null)
                    {
                        var sdkReady = ReplayIncidentIndexPrerequisites.BuildSdkReadyFields(_irsdk.IsConnected, _irsdk.UpdateInterval);
                        _logger.Structured("INFO", "simhub-plugin", ReplayIncidentIndexPrerequisites.EventSdkReady,
                            "Replay incident index: IRSDK memory map ready (TR-001).", sdkReady, "lifecycle", null);
                    }
                };
                _irsdk.OnDisconnected += () =>
                {
                    SentrySdk.AddBreadcrumb("iRacing disconnected", "lifecycle");
                    ReplayIncidentIndexOnIracingDisconnected();
                    _replayIncidentIndexPrereqLogKey = "";
                    _logger?.Structured("INFO", "simhub-plugin", "iracing_disconnected", "iRacing disconnected.", null, "lifecycle", null);
                };
                _irsdk.OnTelemetryData += OnIrsdkTelemetryDataForReplayIndex;
                _irsdk.Start();
                _logger.Structured("INFO", "simhub-plugin", "irsdk_started", "iRacing SDK started.", null, "lifecycle", null);
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
                _logger.Error("iRacing SDK (IRSDKSharper) failed to start. Plugin will run without iRacing data.", ex);
                _irsdk = null;
            }

            _logger?.Info($"SimSteward ready. WebSocket on :{_wsPort}.");

            _resourceSampleIntervalSec = 60;
            var resIntv = Environment.GetEnvironmentVariable("SIMSTEWARD_RESOURCE_SAMPLE_SEC");
            if (!string.IsNullOrWhiteSpace(resIntv) &&
                int.TryParse(resIntv, out var ri) &&
                ri >= 15 && ri <= 3600)
            {
                _resourceSampleIntervalSec = ri;
            }

            _resourceSampler = new SystemMetricsSampler();
            _nextResourceSampleUtc = DateTime.UtcNow.AddSeconds(_resourceSampleIntervalSec);

            try
            {
                _metricsTelemetry = PluginMetricsTelemetry.TryCreate(_logger, () => _lastResourceSample);
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
                _logger?.Structured("WARN", "simhub-plugin", "otel_metrics_load_failed",
                    $"OTLP metrics disabled — assembly load failure: {ex.Message}",
                    new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["exception_type"] = ex.GetType().Name,
                    }, "lifecycle", null);
            }

            RefreshDependencyChecks();
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            try
            {
            _dataUpdateTick++;
            if (_dataUpdateTick % DependencyCheckIntervalTicks == 0)
                RefreshDependencyChecks();

            int clientCount = _bridge != null ? _bridge.ClientCount : 0;

            if (_logger != null && _resourceSampler != null && DateTime.UtcNow >= _nextResourceSampleUtc)
            {
                _nextResourceSampleUtc = DateTime.UtcNow.AddSeconds(_resourceSampleIntervalSec);
                var sample = _resourceSampler.TrySample(_pluginDataPath, clientCount, _resourceSampleIntervalSec);
                if (sample != null)
                {
                    _lastResourceSample = sample;
                    _logger.Structured("INFO", "simhub-plugin", "host_resource_sample", "Periodic host/process resource sample",
                        SystemMetricsSampler.ToLogFields(sample), "lifecycle", null);
                }
            }

            if (_irsdk != null && _irsdk.IsConnected)
            {
                try
                {
                    _lastSessionTime = _irsdk.Data.GetDouble("SessionTime");
                }
                catch
                {
                    _lastSessionTime = 0;
                }

                try
                {
                    string simMode = _irsdk.Data.SessionInfo?.WeekendInfo?.SimMode;
                    if (string.Equals(simMode, "replay", StringComparison.OrdinalIgnoreCase))
                        _pluginMode = "Replay";
                    else
                        _pluginMode = "Unknown";
                }
                catch
                {
                    _pluginMode = "Unknown";
                }

                _replayFrameNumEnd = SafeGetInt("ReplayFrameNum");
                _replayFrameTotal = SafeGetInt("ReplayFrameNumEnd");
                if (_replayFrameNumEnd > _replayFrameMax) _replayFrameMax = _replayFrameNumEnd;
                int subId = _irsdk.Data?.SessionInfo?.WeekendInfo?.SubSessionID ?? 0;
                int parentId = 0;
                try
                {
                    parentId = _irsdk.Data.SessionInfo?.WeekendInfo?.SessionID ?? 0;
                }
                catch { }
                string trackName = "";
                try
                {
                    trackName = _irsdk.Data.SessionInfo?.WeekendInfo?.TrackDisplayName ?? "";
                }
                catch { }
                int sessionNum = SafeGetInt("SessionNum");
                _currentSessionSeq = BuildSessionSeq(trackName);
                _currentSessionId = subId > 0 ? subId.ToString() : _currentSessionSeq;

                _logCtxSubsession = subId > 0 ? subId.ToString() : SessionLogging.NotInSession;
                _logCtxParent = parentId > 0 ? parentId.ToString() : SessionLogging.NotInSession;
                _logCtxSessionNum = sessionNum.ToString();
                _logCtxTrack = string.IsNullOrEmpty(trackName) ? SessionLogging.NotInSession : trackName;
                int focusCar = ResolveFocusCarIdx();
                _logCtxLap = focusCar >= 0 ? SafeGetCarIdxLap(focusCar) : SessionLogging.LapUnknown;

                try
                {
                    int siu = _irsdk.Data?.SessionInfoUpdate ?? 0;
                    if (siu != _lastSessionInfoUpdateForYamlFingerprint)
                    {
                        _lastSessionInfoUpdateForYamlFingerprint = siu;
                        string yaml = _irsdk.Data?.SessionInfoYaml;
                        _logCtxSessionYamlFingerprint = ReplayIncidentIndexPrerequisites.ComputeSessionYamlFingerprint(yaml ?? "");
                    }
                }
                catch
                {
                    _logCtxSessionYamlFingerprint = "";
                }

                if (_autoWalkActive) ProcessAutoWalkTick();
                ProcessCaptureQueueTick();
                ProcessLiveReplayAggregatorTick();
                ProcessJumpMisfireEvaluatorTick();

                if (++_captureManifestFlushTickCounter >= CaptureManifestFlushIntervalTicks)
                {
                    _captureManifestFlushTickCounter = 0;
                    FlushCaptureManifestIfDirty();
                }
            }
            else
            {
                _lastSessionTime = 0;
                _pluginMode = "Unknown";
                _replayFrameNumEnd = 0;
                _replayFrameTotal = 0;
                _replayFrameMax = 0;
                _currentSessionId = "";
                _currentSessionSeq = "";
                _logCtxSubsession = SessionLogging.NotInSession;
                _logCtxParent = SessionLogging.NotInSession;
                _logCtxSessionNum = SessionLogging.NotInSession;
                _logCtxTrack = SessionLogging.NotInSession;
                _logCtxLap = SessionLogging.LapUnknown;
                _logCtxSessionYamlFingerprint = "";
                _lastSessionInfoUpdateForYamlFingerprint = -1;
                // Test rig: tear down live aggregator state when iRacing disconnects.
                _liveDetectorBaselineReady = false;
                _liveLastReplayFrame = -1;
                _liveAggregator.Reset();
                _lastJumpEvaluated = true;
            }

            pluginManager.SetPropertyValue("SimSteward.PluginMode", GetType(), _pluginMode);
            pluginManager.SetPropertyValue("SimSteward.ClientCount", GetType(), clientCount);

            if (_bridge == null) return;

            if (_broadcastNoClientsPending)
            {
                _broadcastNoClientsPending = false;
                _logger?.Structured("WARN", "simhub-plugin", "broadcast_no_clients", "Broadcast skipped: 0 connected clients.",
                    new System.Collections.Generic.Dictionary<string, object> { ["hint"] = "Open dashboard at http://localhost:8888/Web/sim-steward-dash/index.html" }, "lifecycle", null);
            }

            var now = DateTime.UtcNow;

            if ((now - _lastBroadcastAt).TotalMilliseconds < BroadcastThrottleMs)
                return;
            _lastBroadcastAt = now;

            var snapshot = BuildPluginSnapshot();
            _bridge.BroadcastState(BuildStateJson(snapshot));
            }
            catch (Exception ex)
            {
                try { SentrySdk.CaptureException(ex); } catch { }
                try { _logger?.Error("DataUpdate top-level exception", ex); } catch { }
            }
        }

        private int SafeGetInt(string name)
        {
            try
            {
                return _irsdk.Data.GetInt(name);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>Replay camera car if valid; otherwise player car index.</summary>
        private int ResolveFocusCarIdx()
        {
            const int maxCars = 64;
            int cam = SafeGetInt("CamCarIdx");
            if (cam >= 0 && cam < maxCars)
                return cam;
            int player = SafeGetInt("PlayerCarIdx");
            if (player >= 0 && player < maxCars)
                return player;
            return -1;
        }

        private int SafeGetCarIdxLap(int carIdx)
        {
            try
            {
                return _irsdk.Data.GetInt("CarIdxLap", carIdx);
            }
            catch
            {
                return SessionLogging.LapUnknown;
            }
        }

        private long ResolveSessionTimeMsFromFrame(int frame)
        {
            var incidents = _replayIndexDashboardCachedRoot?.Incidents;
            if (incidents == null) return -1;
            foreach (var inc in incidents)
            {
                if (inc.ReplayFrame == frame)
                    return inc.SessionTimeMs;
            }
            return -1;
        }

        private string GetCaptureManifestPath(long subSessionId)
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SimHub", "SimSteward");
            System.IO.Directory.CreateDirectory(dir);
            return System.IO.Path.Combine(dir, $"{subSessionId}-capture-manifest.json");
        }

        private void FlushCaptureManifestIfDirty()
        {
            if (!_captureManifestDirty || _captureManifest == null) return;
            _captureManifestDirty = false;
            var manifest = _captureManifest;
            var path = GetCaptureManifestPath(manifest.SubSessionId);
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var json = Newtonsoft.Json.JsonConvert.SerializeObject(manifest,
                        Newtonsoft.Json.Formatting.Indented);
                    System.IO.File.WriteAllText(path, json);
                }
                catch (Exception ex)
                {
                    _logger?.Structured("WARN", "CaptureManifest", "capture_manifest_flush_error",
                        $"Failed to flush manifest: {ex.Message}",
                        new System.Collections.Generic.Dictionary<string, object> { ["error"] = ex.Message },
                        domain: SessionLogging.DomainCapture);
                }
            });
        }

        private long GetCurrentSubSessionIdForCapture()
        {
            try
            {
                var info = _irsdk?.Data?.SessionInfo?.WeekendInfo;
                if (info == null) return 0;
                var prop = info.GetType().GetProperty("SubSessionID");
                if (prop != null)
                {
                    var v = prop.GetValue(info);
                    return v != null ? System.Convert.ToInt64(v) : 0;
                }
            }
            catch { }
            return 0;
        }

#if SIMHUB_SDK
        private void ProcessCaptureQueueTick()
        {
            if (_replayIndexBuildPhase != ReplayIndexBuildPhase.Idle) return;
            if (_captureQueue.Count == 0) return;
            if (++_captureQueueDrainTickCounter < CaptureQueueDrainIntervalTicks) return;
            _captureQueueDrainTickCounter = 0;
            if (IsSeekThrottled()) return;

            var task = _captureQueue.Dequeue();

            long subSessId = GetCurrentSubSessionIdForCapture();
            if (_captureManifest == null || _captureManifest.SubSessionId != subSessId)
            {
                _captureManifest = new CaptureManifest
                {
                    SubSessionId = subSessId,
                    GeneratedAt = DateTimeOffset.UtcNow.ToString("o")
                };
            }

            var existing = _captureManifest.Entries.Find(e => e.Fingerprint == task.Fingerprint);
            if (existing == null)
            {
                var now = DateTimeOffset.UtcNow.ToString("o");
                _captureManifest.Entries.Add(new CaptureManifestEntry
                {
                    Fingerprint = task.Fingerprint,
                    CarIdx = task.CarIdx,
                    SessionNum = SafeGetInt("SessionNum"),
                    SessionTimeMs = task.SessionTimeMs,
                    Lap = task.Lap,
                    IncidentPoints = task.IncidentPoints,
                    DetectionSource = task.DetectionSource,
                    PushedToQueue = false,
                    CapturedAt = now,
                    Clips = new System.Collections.Generic.List<CaptureClipEntry>()
                });
                _captureManifestDirty = true;

                var captureFields = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["fingerprint"] = task.Fingerprint,
                    ["car_idx"] = task.CarIdx,
                    ["session_time_ms"] = task.SessionTimeMs,
                    ["lap"] = task.Lap,
                    ["incident_points"] = task.IncidentPoints,
                    ["detection_source"] = task.DetectionSource,
                    ["queue_remaining"] = _captureQueue.Count
                };
                MergeSessionAndRoutingFields(captureFields);
                _logger?.Structured("INFO", "CaptureManifest", "incident_committed",
                    $"Auto-captured fingerprint:{task.Fingerprint}", captureFields,
                    domain: SessionLogging.DomainCapture);
            }
        }

        private void ProcessAutoWalkTick()
        {
            if (!_autoWalkActive || _irsdk == null || !_irsdk.IsConnected) return;
            var incidents = _replayIndexDashboardCachedRoot?.Incidents;
            if (incidents == null || incidents.Count == 0) { _autoWalkActive = false; return; }
            int frame = SafeGetInt("ReplayFrameNum");
            if (frame < _autoWalkPlayUntilFrame) return;
            int next = _incidentCursor + 1;
            if (next >= incidents.Count) { _autoWalkActive = false; return; }
            _incidentCursor = next;
            if (IsSeekThrottled()) return;
            _autoWalkPlayUntilFrame = frame + AutoWalkPlayFrames;
            var row = incidents[_incidentCursor];
            int sn = SafeGetInt("SessionNum");
            try
            {
                MarkSeekIssued();
                _irsdk.ReplaySearchSessionTime(sn, row.SessionTimeMs);
                _irsdk.CamSwitchPos(IRacingSdkEnum.CamSwitchMode.FocusAtDriver, row.CarIdx, 0, 0);
            }
            catch (Exception ex) { SentrySdk.CaptureException(ex); }
        }

        /// <summary>
        /// Test rig (docs/RULES-TestRig-Contract.md): per-tick live replay aggregator + 250ms broadcast.
        /// Skipped during FF index build to avoid colliding with that detector's baseline.
        /// </summary>
        private void ProcessLiveReplayAggregatorTick()
        {
            try
            {
                if (_irsdk == null || !_irsdk.IsConnected) return;
                if (_replayIndexBuildPhase != ReplayIndexBuildPhase.Idle) return;
                if (!string.Equals(_pluginMode, "Replay", StringComparison.OrdinalIgnoreCase)) return;

                int replayFrame = SafeGetInt("ReplayFrameNum");

                // Reset on backward seek (user scrubbed back) or first run after reconnect.
                bool needReset = !_liveDetectorBaselineReady || (_liveLastReplayFrame >= 0 && replayFrame < _liveLastReplayFrame);
                if (needReset)
                {
                    SafeGetIntPerCar("CarIdxSessionFlags", _replayIndexScratchCarIdxSessionFlags);
                    SafeGetIntPerCar("CarIdxTrackSurface", _replayIndexScratchCarIdxTrackSurface);
                    int playerInc0 = 0;
                    try { playerInc0 = _irsdk.Data.GetInt("PlayerCarMyIncidentCount"); } catch { }
                    int playerCarIdx0 = SafeGetInt("PlayerCarIdx");
                    _liveDetector.Reset(
                        _replayIndexScratchCarIdxSessionFlags,
                        playerInc0,
                        playerCarIdx0,
                        _replayIndexScratchCarIdxTrackSurface);
                    _liveAggregator.Reset();
                    _liveDetectorBaselineReady = true;
                    _liveLastReplayFrame = replayFrame;
                    return;
                }

                _liveLastReplayFrame = replayFrame;

                double rst = 0;
                try { rst = _irsdk.Data.GetDouble("ReplaySessionTime"); }
                catch { try { rst = _irsdk.Data.GetDouble("SessionTime"); } catch { } }

                SafeGetIntPerCar("CarIdxSessionFlags", _replayIndexScratchCarIdxSessionFlags);
                SafeGetIntPerCar("CarIdxTrackSurface", _replayIndexScratchCarIdxTrackSurface);
                SafeGetIntPerCar("CarIdxLap", _replayIndexScratchCarIdxLap);
                int playerIncidents = 0;
                try { playerIncidents = _irsdk.Data.GetInt("PlayerCarMyIncidentCount"); } catch { }
                int playerCarIdx = SafeGetInt("PlayerCarIdx");

                var samples = _liveDetector.Process(
                    rst,
                    _replayIndexScratchCarIdxSessionFlags,
                    playerIncidents,
                    playerCarIdx,
                    replayFrame,
                    _replayIndexScratchCarIdxTrackSurface,
                    _replayIndexScratchCarIdxLap);
                for (int i = 0; i < samples.Count; i++)
                    _liveAggregator.Apply(samples[i]);

                // Throttle the broadcast to ReplayControlActions.LiveTickCadenceMs.
                var nowUtc = DateTime.UtcNow;
                if ((nowUtc - _lastReplayTickAt).TotalMilliseconds < ReplayControlActions.LiveTickCadenceMs)
                    return;
                _lastReplayTickAt = nowUtc;

                if (_bridge == null || _bridge.ClientCount <= 0) return;
                BroadcastReplayStateTick(replayFrame, rst, nowUtc);
            }
            catch (Exception ex)
            {
                try { SentrySdk.CaptureException(ex); } catch { }
            }
        }

        private void BroadcastReplayStateTick(int replayFrame, double sessionTime, DateTime nowUtc)
        {
            // YAML side: use cached parse if available; pass null when in-progress so the dashboard renders "—".
            System.Collections.Generic.IReadOnlyDictionary<int, int> yamlByCarIdx = null;
            try
            {
                string yaml = _irsdk.Data?.SessionInfoYaml;
                int sn = SafeGetInt("SessionNum");
                if (!string.IsNullOrEmpty(yaml) &&
                    ReplayIncidentIndexResultsYaml.TryParseOfficialIncidentsByCarIdx(yaml, sn,
                        out System.Collections.Generic.Dictionary<int, int> map, out _, out _))
                {
                    yamlByCarIdx = map;
                }
            }
            catch { }

            var driverList = BuildDriverIdentityList();
            var aggregates = _liveAggregator.BuildAggregates(yamlByCarIdx);
            var driverRows = _liveAggregator.BuildDriverRows(driverList, yamlByCarIdx);

            int playSpeed = SafeGetInt("ReplayPlaySpeed");
            bool paused = playSpeed == 0;
            int magnitude = Math.Abs(playSpeed);
            string direction = playSpeed < 0 ? "reverse" : "forward";

            var payload = new ReplayStateTickPayload
            {
                Ts = nowUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                Frame = replayFrame,
                FrameEnd = SafeGetInt("ReplayFrameNumEnd"),
                SessionTime = Math.Round(sessionTime, 3),
                Paused = paused,
                Speed = magnitude > 0 ? magnitude : _lastReplaySpeedMagnitude,
                Direction = direction,
                SlowMotion = _lastReplaySlowMotion,
                Aggregates = aggregates,
                Drivers = driverRows,
                Misfire = BuildMisfirePayload(nowUtc)
            };
            try
            {
                string json = JsonConvert.SerializeObject(payload);
                _bridge.Broadcast(json, "replayStateTick");
            }
            catch (Exception ex)
            {
                WriteBroadcastError("BroadcastReplayStateTick", ex);
            }
        }

        private System.Collections.Generic.List<DriverIdentity> BuildDriverIdentityList()
        {
            var list = new System.Collections.Generic.List<DriverIdentity>();
            try
            {
                if (!(_irsdk?.Data?.SessionInfo?.DriverInfo?.Drivers is System.Collections.IList drivers))
                    return list;
                foreach (var d in drivers)
                {
                    if (d == null) continue;
                    var t = d.GetType();
                    var carIdxObj = t.GetProperty("CarIdx")?.GetValue(d);
                    int carIdx = carIdxObj is int ci ? ci : Convert.ToInt32(carIdxObj ?? -1);
                    if (carIdx < 0) continue;
                    string name = t.GetProperty("UserName")?.GetValue(d)?.ToString() ?? "";
                    string custId = t.GetProperty("UserID")?.GetValue(d)?.ToString()
                                    ?? t.GetProperty("CustID")?.GetValue(d)?.ToString()
                                    ?? "";
                    list.Add(new DriverIdentity(carIdx, name, custId));
                }
            }
            catch { }
            return list;
        }

        private ReplayStateLastJump BuildMisfirePayload(DateTime nowUtc)
        {
            // Show the misfire payload for MisfireVisibilityMs after evaluation, then revert to the all-null skeleton.
            if (!_lastJumpEvaluated || _lastJumpEvaluatedAt == DateTime.MinValue)
                return new ReplayStateLastJump();
            if ((nowUtc - _lastJumpEvaluatedAt).TotalMilliseconds > ReplayControlActions.MisfireVisibilityMs)
                return new ReplayStateLastJump();
            return new ReplayStateLastJump
            {
                Active = _lastJumpMisfire,
                Direction = _lastJumpDirection,
                ExpectedFrame = _lastJumpExpectedFrame,
                LandedFrame = _lastJumpLandedFrame,
                DeltaFrames = _lastJumpLandedFrame - _lastJumpExpectedFrame,
                DeltaMs = _lastJumpDeltaMs,
                ExpectedFingerprint = _lastJumpExpectedFingerprint,
                NearestFingerprint = _lastJumpNearestFingerprint
            };
        }

        /// <summary>
        /// Test rig misfire evaluator. Runs once per <c>replay_jump_*_incident</c>, ~750 ms after the SDK
        /// seek call (gives iRacing time to settle), provided the seek throttle is also clear.
        /// </summary>
        private void ProcessJumpMisfireEvaluatorTick()
        {
            try
            {
                if (_lastJumpEvaluated) return;
                if (_lastJumpRequestedAt == DateTime.MinValue) return;
                if ((DateTime.UtcNow - _lastJumpRequestedAt).TotalMilliseconds < ReplayControlActions.MisfireSettleMs)
                    return;
                if (IsSeekThrottled()) return;
                if (_irsdk == null || !_irsdk.IsConnected) return;

                int landedFrame = SafeGetInt("ReplayFrameNum");
                double rst = 0;
                try { rst = _irsdk.Data.GetDouble("ReplaySessionTime"); }
                catch { try { rst = _irsdk.Data.GetDouble("SessionTime"); } catch { } }
                int landedSessionTimeMs = ReplayIncidentIndexDetection.ToSessionTimeMs(rst);

                var rows = _replayIndexDashboardCachedRoot?.Incidents;
                var result = ReplayControlActions.EvaluateMisfire(
                    rows, landedFrame, landedSessionTimeMs, _lastJumpExpectedFingerprint);

                _lastJumpEvaluated = true;
                _lastJumpEvaluatedAt = DateTime.UtcNow;
                _lastJumpMisfire = result.Misfire;
                _lastJumpLandedFrame = landedFrame;
                _lastJumpLandedSessionTimeMs = landedSessionTimeMs;
                _lastJumpDeltaMs = result.DeltaMs == int.MaxValue ? -1 : result.DeltaMs;
                _lastJumpNearestFingerprint = result.NearestFingerprint;

                int subSessionId = _irsdk.Data?.SessionInfo?.WeekendInfo?.SubSessionID ?? 0;
                string indexPath = subSessionId > 0
                    ? ReplayIncidentIndexOutputPaths.GetFilePathForSubSession(subSessionId)
                    : "";

                var fields = ReplayControlActions.BuildMisfireLogFields(
                    _lastJumpDirection,
                    _lastJumpExpectedFrame,
                    _lastJumpExpectedSessionTimeMs,
                    _lastJumpExpectedFingerprint,
                    result,
                    indexPath);
                MergeSessionAndRoutingFields(fields);
                string level = result.Misfire ? "WARN" : "DEBUG";
                _logger?.Structured(level, "simhub-plugin", "replay_jump_misfire",
                    result.Misfire ? "Replay jump misfire detected." : "Replay jump landed on expected incident.",
                    fields, SessionLogging.DomainAction, null);

                try
                {
                    SentrySdk.AddBreadcrumb(
                        message: "replay_jump_misfire",
                        category: "action",
                        level: result.Misfire ? BreadcrumbLevel.Warning : BreadcrumbLevel.Info,
                        data: new System.Collections.Generic.Dictionary<string, string>
                        {
                            ["direction"] = _lastJumpDirection ?? "",
                            ["delta_ms"] = _lastJumpDeltaMs.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        });
                }
                catch { }
            }
            catch (Exception ex)
            {
                try { SentrySdk.CaptureException(ex); } catch { }
            }
        }
#endif

        public void End(PluginManager pluginManager)
        {
            _logger?.Structured("INFO", "simhub-plugin", "plugin_stopped", "SimSteward plugin End.", null, "lifecycle", null);

            try { SentrySdk.FlushAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(); } catch { }
            try { _sentryDisposable?.Dispose(); } catch { }
            _sentryDisposable = null;

            try
            {
                _metricsTelemetry?.Dispose();
            }
            catch
            {
                // ignore
            }
            _metricsTelemetry = null;

            StopReplayIncidentIndexRecordModeLocked("plugin_end");

            if (_logger != null)
            {
                _logger.LogWritten -= OnLogWritten;
                _logger.WriteError -= OnLogWriteError;
                _logger.FlushAndStop();
            }

            if (_irsdk != null)
            {
                try
                {
                    _irsdk.OnTelemetryData -= OnIrsdkTelemetryDataForReplayIndex;
                    _irsdk.Stop();
                }
                catch (Exception ex)
                {
                    SentrySdk.CaptureException(ex);
                    _logger?.Warn($"iRacing SDK Stop failed: {ex.Message}");
                }
                _irsdk = null;
            }

            if (_bridge != null)
            {
                _bridge.Stop();
                _bridge = null;
            }
        }

        public int ClientCountForSettings => _bridge?.ClientCount ?? 0;
        public bool WsRunningForSettings => _bridge != null;
        public int WsPortForSettings => _wsPort;
        public bool IrsdkStartedForSettings => _irsdk != null;
        public string IracingConnectionStatus =>
            _irsdk == null ? "SDK not started" : (_irsdk.IsConnected ? "Connected" : "Not connected");
        public string StructuredLogPathForSettings => _logger?.StructuredLogPath ?? "";

        public bool SteamRunningForSettings => _steamRunning;
        public bool SimHubWebServerForSettings => _simHubHttpListening;
        public string DashboardPingForSettings => _dashboardPingStatus;

        public string LeftMenuTitle => "Sim Steward";

        private static ImageSource _cachedMenuIcon;
        public ImageSource PictureIcon => _cachedMenuIcon ?? (_cachedMenuIcon = CreateMenuIcon());

        public System.Windows.Controls.Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            return new PluginControl(this);
        }

        private static ImageSource CreateMenuIcon()
        {
            const int size = 24;
            var drawing = new DrawingGroup();
            drawing.Children.Add(new GeometryDrawing(
                Brushes.Black,
                new Pen(Brushes.Black, 1),
                new RectangleGeometry(new System.Windows.Rect(2, 2, size - 4, size - 4))));
            var image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }
#endif
    }
}
