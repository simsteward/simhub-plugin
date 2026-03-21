#if SIMHUB_SDK
using GameReaderCommon;
using IRSDKSharper;
using SimHub.Plugins;
using System.Windows.Media;
#endif

using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace SimSteward.Plugin
{
#if SIMHUB_SDK
    [PluginName("Sim Steward")]
    [PluginDescription("Sim Steward: HTML dashboard bridge via WebSocket")]
    [PluginAuthor("Sim Steward")]
    public class SimStewardPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
#else
#pragma warning disable CS0169, CS0649
    public class SimStewardPlugin
#endif
    {
        private const int DefaultPort = 19847;
        private const double BroadcastThrottleMs = 200;
        private const int DependencyCheckIntervalTicks = 60;
        private const double DashboardPingIntervalSec = 5;
        private int _wsPort = DefaultPort;

#if SIMHUB_SDK
        public PluginManager PluginManager { get; set; }
#endif

        private PluginLogger _logger;
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

#if SIMHUB_SDK
        private IRacingSdk _irsdk;
        private double _lastSessionTime;
        private string _pluginMode = "Unknown";
        private int _replayFrameNumEnd;
        /// <summary>Replay length (telemetry <c>ReplayFrameNumEnd</c>); 0 if unknown.</summary>
        private int _replayFrameTotal;
        private string _currentSessionId = "";
        private string _currentSessionSeq = "";
        /// <summary>Latest iRacing session context for structured logs (WebSocket thread reads; DataUpdate writes).</summary>
        private volatile string _logCtxSubsession = SessionLogging.NotInSession;
        private volatile string _logCtxParent = SessionLogging.NotInSession;
        private volatile string _logCtxSessionNum = SessionLogging.NotInSession;
        private volatile string _logCtxTrack = SessionLogging.NotInSession;
        /// <summary>CarIdxLap for focus car (CamCarIdx if valid, else PlayerCarIdx); -1 if unknown.</summary>
        private volatile int _logCtxLap = SessionLogging.LapUnknown;
#endif

#if SIMHUB_SDK
        private string BuildStateJson(PluginSnapshot snapshot)
        {
            var state = new
            {
                type = "state",
                pluginMode = snapshot.PluginMode,
                currentSessionTime = snapshot.CurrentSessionTime,
                currentSessionTimeFormatted = snapshot.CurrentSessionTimeFormatted,
                lap = snapshot.Lap,
                frame = snapshot.Frame,
                frameEnd = snapshot.FrameEnd,
                replaySessionCount = snapshot.ReplaySessionCount,
                replaySessionNum = snapshot.ReplaySessionNum,
                replaySessionName = snapshot.ReplaySessionName,
                diagnostics = snapshot.Diagnostics
            };
            return JsonConvert.SerializeObject(state);
        }
#else
        private string BuildStateJson()
        {
            var state = new
            {
                type = "state",
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

        private void WriteBroadcastError(string context, Exception ex)
        {
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
                PluginMode = _pluginMode,
                CurrentSessionTime = _lastSessionTime,
                CurrentSessionTimeFormatted = FormatSessionTime(_lastSessionTime),
                Lap = _logCtxLap,
                Frame = _replayFrameNumEnd,
                FrameEnd = _replayFrameTotal,
                ReplaySessionCount = replaySessionCount,
                ReplaySessionNum = replaySessionNum,
                ReplaySessionName = replaySessionName,
                Diagnostics = new PluginDiagnostics
                {
                    IrsdkStarted = _irsdk != null,
                    IrsdkConnected = irConnected,
                    WsRunning = _bridge != null,
                    WsPort = _wsPort,
                    WsClients = clientCount,
                    SteamRunning = _steamRunning,
                    SimHubHttpListening = _simHubHttpListening,
                    DashboardPing = _dashboardPingStatus
                }
            };
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

        private void LogActionResult(string action, string arg, string correlationId, bool success, string error)
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
            var summary = success
                ? $"{action} -> ok"
                : $"{action} -> {error ?? "failed"}";
            _logger?.Structured("INFO", "simhub-plugin", "action_result", summary, resultFields, "action", null);
        }

        private (bool success, string result, string error) DispatchAction(string action, string arg, string correlationId)
        {
            if (string.IsNullOrEmpty(action))
                return (false, null, "missing_action");
            var dispatchFields = new System.Collections.Generic.Dictionary<string, object>
            {
                ["action"] = action,
                ["arg"] = arg ?? "",
                ["correlation_id"] = correlationId ?? ""
            };
            MergeSessionAndRoutingFields(dispatchFields);
            _logger?.Structured("INFO", "simhub-plugin", "action_dispatched", action, dispatchFields, "action", null);

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
                    var err = ex.Message ?? "replay_session_failed";
                    LogActionResult(action, arg, correlationId, false, err);
                    return (false, null, err);
                }
            }

            if (string.Equals(action, "replay_speed", StringComparison.OrdinalIgnoreCase))
            {
                if (!double.TryParse((arg ?? "").Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var speed))
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
                    int multiplier;
                    bool slowMotion;
                    if (speed == 0.0)
                    {
                        multiplier = 0; slowMotion = false;
                    }
                    else if (speed > 0.0 && speed < 1.0)
                    {
                        multiplier = (int)Math.Round(1.0 / speed);
                        slowMotion = true;
                    }
                    else if (speed >= 1.0)
                    {
                        multiplier = (int)speed; slowMotion = false;
                    }
                    else
                    {
                        // negative: rewind at magnitude (e.g. -4 → 4x reverse)
                        multiplier = (int)Math.Abs(speed); slowMotion = false;
                    }
                    _irsdk.ReplaySetPlaySpeed(multiplier, slowMotion);
                    LogActionResult(action, arg, correlationId, true, "");
                    return (true, "ok", null);
                }
                catch (Exception ex)
                {
                    var err = ex.Message ?? "replay_speed_failed";
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
                    var err = ex.Message ?? "seek_to_incident_failed";
                    LogActionResult(action, arg, correlationId, false, err);
                    return (false, null, err);
                }
            }

            LogActionResult(action, arg, correlationId, false, "not_supported");
            return (false, null, "not_supported");
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

            try
            {
                var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
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
                try
                {
                    var response = DashboardPingClient
                        .GetAsync("http://127.0.0.1:8888/Web/sim-steward-dash/index.html")
                        .ConfigureAwait(false)
                        .GetAwaiter()
                        .GetResult();
                    _dashboardPingStatus = response.IsSuccessStatusCode
                        ? $"OK ({(int)response.StatusCode})"
                        : $"HTTP {(int)response.StatusCode}";
                }
                catch (Exception ex)
                {
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
            _logger = new PluginLogger(_pluginDataPath, isDebugMode: _debugMode);
            _logger.SetSpineProvider(() => (_currentSessionId ?? "", _currentSessionSeq ?? "", _replayFrameNumEnd));
            _logger.Structured("INFO", "simhub-plugin", "logging_ready", "Logging pipeline ready; init continuing.", null, "lifecycle", null);

            var structuredPath = _logger.StructuredLogPath;
            _logger.Structured("INFO", "simhub-plugin", "file_tail_ready", "Structured log file ready for Loki ingestion.",
                new System.Collections.Generic.Dictionary<string, object> { ["path"] = structuredPath ?? "(none)" }, "lifecycle", null);

            _logger.Structured("INFO", "simhub-plugin", "plugin_started", "SimSteward plugin starting.", null, "lifecycle", null);

            pluginManager.AddProperty("SimSteward.PluginMode", GetType(), "Unknown");
            pluginManager.AddProperty("SimSteward.IncidentCount", GetType(), 0);
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
            }
            catch (Exception ex)
            {
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
                    _logger?.Structured("INFO", "simhub-plugin", "iracing_connected", "iRacing connected.", null, "lifecycle", null);
                };
                _irsdk.OnDisconnected += () =>
                {
                    _logger?.Structured("INFO", "simhub-plugin", "iracing_disconnected", "iRacing disconnected.", null, "lifecycle", null);
                };
                _irsdk.Start();
                _logger.Structured("INFO", "simhub-plugin", "irsdk_started", "iRacing SDK started.", null, "lifecycle", null);
            }
            catch (Exception ex)
            {
                _logger.Error("iRacing SDK (IRSDKSharper) failed to start. Plugin will run without iRacing data.", ex);
                _irsdk = null;
            }

            _logger?.Info($"SimSteward ready. WebSocket on :{_wsPort}.");
            RefreshDependencyChecks();
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            _dataUpdateTick++;
            if (_dataUpdateTick % DependencyCheckIntervalTicks == 0)
                RefreshDependencyChecks();

            int clientCount = _bridge != null ? _bridge.ClientCount : 0;

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
            }
            else
            {
                _lastSessionTime = 0;
                _pluginMode = "Unknown";
                _replayFrameNumEnd = 0;
                _replayFrameTotal = 0;
                _currentSessionId = "";
                _currentSessionSeq = "";
                _logCtxSubsession = SessionLogging.NotInSession;
                _logCtxParent = SessionLogging.NotInSession;
                _logCtxSessionNum = SessionLogging.NotInSession;
                _logCtxTrack = SessionLogging.NotInSession;
                _logCtxLap = SessionLogging.LapUnknown;
            }

            pluginManager.SetPropertyValue("SimSteward.PluginMode", GetType(), _pluginMode);
            pluginManager.SetPropertyValue("SimSteward.IncidentCount", GetType(), 0);
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

            if (_logger.IsDebugMode)
            {
                _logger.Debug("state broadcast", "simhub-plugin", "state_broadcast_summary",
                    new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["mode"] = snapshot.PluginMode,
                        ["client_count"] = clientCount
                    });
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

        public void End(PluginManager pluginManager)
        {
            _logger?.Structured("INFO", "simhub-plugin", "plugin_stopped", "SimSteward plugin End.", null, "lifecycle", null);

            if (_logger != null)
                _logger.LogWritten -= OnLogWritten;

            if (_irsdk != null)
            {
                try { _irsdk.Stop(); }
                catch (Exception ex)
                {
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
