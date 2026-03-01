#if SIMHUB_SDK
using GameReaderCommon;
using SimHub.Plugins;
using System.Windows.Media;
using IRSDKSharper;
#endif

using System;
using Newtonsoft.Json;
using SimSteward.Plugin.MemoryBank;

namespace SimSteward.Plugin
{
#if SIMHUB_SDK
    [PluginName("Sim Steward")]
    [PluginDescription("Sim Steward: HTML dashboard bridge via WebSocket")]
    [PluginAuthor("Sim Steward")]
    public class SimStewardPlugin : IPlugin, IDataPlugin, IWPFSettingsV2
#else
#pragma warning disable CS0169, CS0649 // Fields only assigned when SIMHUB_SDK; unused when SDK absent
    public class SimStewardPlugin
#endif
    {
        private const int DefaultPort = 19847;
        private const double BroadcastThrottleMs = 200;
        private const double WaitingLogThrottleMs = 10000;
        private int _wsPort = DefaultPort;

#if SIMHUB_SDK
        public PluginManager PluginManager { get; set; }
#endif

        private PluginLogger _logger;
        private DashboardBridge _bridge;
        private DateTime _lastBroadcastAt = DateTime.MinValue;
        private DateTime _lastWaitingLogAt = DateTime.MinValue;
        private string _pluginDataPath;

#if SIMHUB_SDK
        private IRacingSdk _irsdk;
        private double _lastSessionTime;
        private string _pluginMode = "Unknown";
        private int _replayFrameNum;
        private int _replayFrameNumEnd;
        private int _replayPlaySpeed;
        private bool _replayPlaySlowMotion;
        private bool _replayIsPlaying;
        private int _replaySessionNum;
        private readonly IncidentTracker _tracker = new IncidentTracker();
        private MemoryBankClient _memoryBankClient;
        private readonly object _memoryBankMarkerLock = new object();
        private string _memoryBankTaskId;
        private string _memoryBankTaskDescription;
        private string _memoryBankComplexityLevel;
        private string _memoryBankNotes;
        private string _memoryBankLastAction;
        private DateTime? _memoryBankLastActionAt;
#endif

        /// <summary>Build the full state JSON for WebSocket push.</summary>
#if SIMHUB_SDK
        private string BuildStateJson(MemoryBankSnapshot snapshot)
        {
            var state = new
            {
                type = "state",
                pluginMode = snapshot.PluginMode,
                currentSessionTime = snapshot.CurrentSessionTime,
                currentSessionTimeFormatted = snapshot.CurrentSessionTimeFormatted,
                replayIsPlaying = snapshot.ReplayIsPlaying,
                replayFrameNum = snapshot.ReplayFrameNum,
                replayFrameNumEnd = snapshot.ReplayFrameNumEnd,
                replayPlaySpeed = snapshot.ReplayPlaySpeed,
                replayPlaySlowMotion = snapshot.ReplayPlaySlowMotion,
                replaySessionNum = snapshot.ReplaySessionNum,
                playerCarIdx = snapshot.PlayerCarIdx,
                playerIncidentCount = snapshot.PlayerIncidentCount,
                hasLiveIncidentData = snapshot.HasLiveIncidentData,
                trackName = snapshot.TrackName,
                trackCategory = snapshot.TrackCategory,
                trackLengthM = snapshot.TrackLengthM,
                drivers = snapshot.Drivers,
                incidents = snapshot.Incidents,
                metrics = snapshot.Metrics,
                diagnostics = snapshot.Diagnostics
            };
            return JsonConvert.SerializeObject(state);
        }
#else
        private string BuildStateJson()
        {
            var drivers = new System.Collections.Generic.List<DriverRecord>();
            var incidents = new System.Collections.Generic.List<IncidentEvent>();
            var state = new
            {
                type = "state",
                pluginMode = "Unknown",
                currentSessionTime = 0.0,
                currentSessionTimeFormatted = "0:00",
                replayIsPlaying = false,
                replayFrameNum = 0,
                replayFrameNumEnd = 0,
                replayPlaySpeed = 0,
                replayPlaySlowMotion = false,
                playerCarIdx = -1,
                playerIncidentCount = 0,
                drivers,
                incidents
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
#endif

        private string GetStateForNewClient()
        {
#if SIMHUB_SDK
            var snapshot = BuildMemoryBankSnapshot();
            return BuildStateJson(snapshot);
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
            if (_bridge == null) return;
            try
            {
                var msg = new { type = "logEvents", entries = new[] { entry } };
                _bridge.Broadcast(JsonConvert.SerializeObject(msg));
            }
            catch { }
        }

#if SIMHUB_SDK
        private ProjectMarkers BuildProjectMarkers()
        {
            lock (_memoryBankMarkerLock)
            {
                return new ProjectMarkers
                {
                    CurrentTaskId = _memoryBankTaskId,
                    CurrentTaskDescription = _memoryBankTaskDescription,
                    ComplexityLevel = _memoryBankComplexityLevel,
                    LastAction = _memoryBankLastAction,
                    LastActionTimestamp = _memoryBankLastActionAt,
                    Notes = _memoryBankNotes
                };
            }
        }

        private MemoryBankSnapshot BuildMemoryBankSnapshot()
        {
            var irConnected = _irsdk?.IsConnected ?? false;
            var clientCount = _bridge?.ClientCount ?? 0;
            return new MemoryBankSnapshot
            {
                PluginMode = _pluginMode,
                CurrentSessionTime = _lastSessionTime,
                CurrentSessionTimeFormatted = FormatSessionTime(_lastSessionTime),
                ReplayIsPlaying = _replayIsPlaying,
                ReplayFrameNum = _replayFrameNum,
                ReplayFrameNumEnd = _replayFrameNumEnd,
                ReplayPlaySpeed = _replayPlaySpeed,
                ReplayPlaySlowMotion = _replayPlaySlowMotion,
                ReplaySessionNum = _replaySessionNum,
                PlayerCarIdx = _tracker.PlayerCarIdx,
                PlayerIncidentCount = _tracker.PlayerIncidentCount,
                HasLiveIncidentData = irConnected && _tracker.BaselineEstablished,
                TrackName = _tracker.TrackName,
                TrackCategory = _tracker.TrackCategory,
                TrackLengthM = _tracker.TrackLengthM,
                Drivers = _tracker.GetDriverSnapshot(),
                Incidents = _tracker.GetIncidentFeed(),
                Metrics = _tracker.GetMetricsSnapshot(),
                Diagnostics = new PluginDiagnostics
                {
                    IrsdkStarted   = _irsdk != null,
                    IrsdkConnected = irConnected,
                    WsRunning      = _bridge != null,
                    WsPort         = _wsPort,
                    WsClients      = clientCount,
                    MemoryBankAvailable = _memoryBankClient?.IsAvailable ?? false,
                    MemoryBankPath = _memoryBankClient != null
                        ? System.IO.Path.Combine(_pluginDataPath, "memory-bank")
                        : null,
                    PlayerCarIdx   = _tracker.PlayerCarIdx,
                },
                ProjectMarkers = BuildProjectMarkers()
            };
        }
#endif

        /// <summary>Dispatch an action from the dashboard. Returns (success, result, error).</summary>
        private (bool success, string result, string error) DispatchAction(string action, string arg)
        {
            if (string.IsNullOrEmpty(action))
                return (false, null, "missing_action");

#if SIMHUB_SDK
            if (TryHandleReplayAction(action, arg, out var replayResult))
            {
                RecordDashboardAction(action);
                return replayResult;
            }
#endif

            (bool success, string result, string error) response;
            switch (action)
            {
                case "ToggleIntentionalCapture":
                case "SetReplayCaptureSpeed":
                case "SetSecondsBefore":
                case "SetSecondsAfter":
                case "SetCaptureDriver1":
                case "SetCaptureDriver2":
                case "SetCaptureCamera1":
                case "SetCaptureCamera2":
                case "SetAutoRotateAndCapture":
                case "ToggleAutoRotateAndCapture":
                case "SetAutoRotateDwellSeconds":
                    response = (true, "stub", null);
                    break;
                default:
                    response = (false, null, "unknown_action");
                    break;
            }
            RecordDashboardAction(action);
            return response;
        }

        private void InitializeMemoryBankMarkers()
        {
            _memoryBankTaskId = Environment.GetEnvironmentVariable("MEMORY_BANK_TASK_ID") ?? "sim-steward";
            _memoryBankTaskDescription = Environment.GetEnvironmentVariable("MEMORY_BANK_TASK_DESCRIPTION") ?? "Sim Steward incident tracking";
            _memoryBankComplexityLevel = Environment.GetEnvironmentVariable("MEMORY_BANK_COMPLEXITY_LEVEL") ?? "auto";
            _memoryBankNotes = Environment.GetEnvironmentVariable("MEMORY_BANK_NOTES") ?? "Auto-synced from SimSteward plugin";
            _memoryBankLastAction = "init";
            _memoryBankLastActionAt = DateTime.UtcNow;
        }

        private void RecordDashboardAction(string action)
        {
            if (string.IsNullOrEmpty(action)) return;
            lock (_memoryBankMarkerLock)
            {
                _memoryBankLastAction = action;
                _memoryBankLastActionAt = DateTime.UtcNow;
            }
        }

#if SIMHUB_SDK
        private bool TryHandleReplayAction(string action, string arg, out (bool success, string result, string error) result)
        {
            switch (action)
            {
                case "ReplayPlayPause":
                    result = HandleReplayPlayPause();
                    return true;
                case "ReplaySetSpeed":
                    result = HandleReplaySetSpeed(arg);
                    return true;
                case "NextIncident":
                    result = HandleReplaySearch(IRacingSdkEnum.RpySrchMode.NextIncident);
                    return true;
                case "PrevIncident":
                    result = HandleReplaySearch(IRacingSdkEnum.RpySrchMode.PrevIncident);
                    return true;
                case "ReplayStepFrame":
                    result = HandleReplayStepFrame(arg);
                    return true;
                case "ReplaySeekFrame":
                    result = HandleReplaySeekFrame(arg);
                    return true;
                case "SelectIncidentAndSeek":
                    result = HandleSelectIncidentAndSeek(arg);
                    return true;
                case "ReplaySeekSessionStart":
                    result = HandleReplaySeekSessionStart();
                    return true;
                default:
                    result = default;
                    return false;
            }
        }

        private (bool success, string result, string error) HandleReplayPlayPause()
        {
            if (!EnsureIrsdkConnected(out var error))
                return (false, null, error);
            var nextSpeed = _replayPlaySpeed == 0 ? 1 : 0;
            _irsdk.ReplaySetPlaySpeed(nextSpeed, _replayPlaySlowMotion);
            _replayPlaySpeed = nextSpeed;
            _replayIsPlaying = nextSpeed != 0;
            _logger?.Info(nextSpeed == 0 ? "Replay: paused" : "Replay: playing at 1x");
            return (true, "ok", null);
        }

        private (bool success, string result, string error) HandleReplaySetSpeed(string arg)
        {
            if (!EnsureIrsdkConnected(out var error))
                return (false, null, error);
            if (!int.TryParse(arg ?? string.Empty, out var speed))
                return (false, null, "invalid_speed");
            _irsdk.ReplaySetPlaySpeed(speed, _replayPlaySlowMotion);
            _replayPlaySpeed = speed;
            _replayIsPlaying = speed != 0;
            _logger?.Info($"Replay speed -> {speed}x");
            return (true, "ok", null);
        }

        private (bool success, string result, string error) HandleReplaySearch(IRacingSdkEnum.RpySrchMode mode)
        {
            if (!EnsureIrsdkConnected(out var error))
                return (false, null, error);
            _irsdk.ReplaySearch(mode);
            _logger?.Info($"Replay search: {mode}");
            return (true, "ok", null);
        }

        private (bool success, string result, string error) HandleReplayStepFrame(string arg)
        {
            if (!EnsureIrsdkConnected(out var error))
                return (false, null, error);
            if (!int.TryParse(arg ?? string.Empty, out var step) || (step != -1 && step != 1))
                return (false, null, "invalid_step");
            _irsdk.ReplaySetPlayPosition(IRacingSdkEnum.RpyPosMode.Current, step);
            _logger?.Info($"Replay step frame: {step:+0;-0}");
            return (true, "ok", null);
        }

        private (bool success, string result, string error) HandleReplaySeekFrame(string arg)
        {
            if (!EnsureIrsdkConnected(out var error))
                return (false, null, error);
            if (!int.TryParse(arg ?? string.Empty, out var frame))
                return (false, null, "invalid_frame");
            if (frame < 0 || (_replayFrameNumEnd > 0 && frame > _replayFrameNumEnd))
                return (false, null, "frame_out_of_range");
            _irsdk.ReplaySetPlayPosition(IRacingSdkEnum.RpyPosMode.Begin, frame);
            _logger?.Info($"Replay seek to frame {frame}");
            return (true, "ok", null);
        }

        private (bool success, string result, string error) HandleSelectIncidentAndSeek(string arg)
        {
            if (!EnsureIrsdkConnected(out var error))
                return (false, null, error);
            if (string.IsNullOrEmpty(arg))
                return (false, null, "missing_incident_id");
            var incidents = _tracker.GetIncidentFeed();
            IncidentEvent target = null;
            foreach (var ev in incidents)
            {
                if (string.Equals(ev.Id, arg, StringComparison.Ordinal))
                {
                    target = ev;
                    break;
                }
            }
            if (target == null)
                return (false, null, "incident_not_found");
            int safeSession = _replaySessionNum >= 0 ? _replaySessionNum : 0;
            int safeTimeMs  = (int)Math.Min(target.SessionTime * 1000.0, int.MaxValue);
            _logger?.Info($"Seeking to incident {arg} at {target.SessionTimeFormatted} (session {safeSession}, ms {safeTimeMs})");
            _irsdk.ReplaySearchSessionTime(safeSession, safeTimeMs);
            return (true, "ok", null);
        }

        private (bool success, string result, string error) HandleReplaySeekSessionStart()
        {
            if (!EnsureIrsdkConnected(out var error))
                return (false, null, error);
            int safeSession = _replaySessionNum >= 0 ? _replaySessionNum : 0;
            _logger?.Info("Replay seek to session start");
            _irsdk.ReplaySearchSessionTime(safeSession, 0);
            return (true, "ok", null);
        }

        private bool EnsureIrsdkConnected(out string error)
        {
            if (_irsdk == null)
            {
                error = "irsdk_not_started";
                return false;
            }
            if (!_irsdk.IsConnected)
            {
                error = "irsdk_not_connected";
                return false;
            }
            error = null;
            return true;
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

        private bool SafeGetBool(string name)
        {
            try
            {
                return _irsdk.Data.GetBool(name);
            }
            catch
            {
                return false;
            }
        }
#endif

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

#if SIMHUB_SDK
        public void Init(PluginManager pluginManager)
        {
            PluginManager = pluginManager;
            _pluginDataPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SimHubWpf", "PluginsData", "SimSteward");
            _logger = new PluginLogger(_pluginDataPath);
            _tracker.LogInfo = msg => _logger.Info(msg);
            _memoryBankClient = new MemoryBankClient(_pluginDataPath, _logger);
            InitializeMemoryBankMarkers();
            _logger.Info("SimSteward plugin Init (skeleton)");

            pluginManager.AddProperty("SimSteward.PluginMode", GetType(), "Unknown");
            pluginManager.AddProperty("SimSteward.IncidentCount", GetType(), 0);
            pluginManager.AddProperty("SimSteward.HasLiveIncidentData", GetType(), false);
            pluginManager.AddProperty("SimSteward.ClientCount", GetType(), 0);

            pluginManager.AddAction("SimSteward.ToggleIntentionalCapture", GetType(), (pm, arg) => DispatchAction("ToggleIntentionalCapture", arg?.ToString()));
            pluginManager.AddAction("SimSteward.SelectIncidentAndSeek", GetType(), (pm, arg) => DispatchAction("SelectIncidentAndSeek", arg?.ToString()));
            pluginManager.AddAction("SimSteward.SetReplayCaptureSpeed", GetType(), (pm, arg) => DispatchAction("SetReplayCaptureSpeed", arg?.ToString()));
            pluginManager.AddAction("SimSteward.SetSecondsBefore", GetType(), (pm, arg) => DispatchAction("SetSecondsBefore", arg?.ToString()));
            pluginManager.AddAction("SimSteward.SetSecondsAfter", GetType(), (pm, arg) => DispatchAction("SetSecondsAfter", arg?.ToString()));
            pluginManager.AddAction("SimSteward.SetCaptureDriver1", GetType(), (pm, arg) => DispatchAction("SetCaptureDriver1", arg?.ToString()));
            pluginManager.AddAction("SimSteward.SetCaptureDriver2", GetType(), (pm, arg) => DispatchAction("SetCaptureDriver2", arg?.ToString()));
            pluginManager.AddAction("SimSteward.SetCaptureCamera1", GetType(), (pm, arg) => DispatchAction("SetCaptureCamera1", arg?.ToString()));
            pluginManager.AddAction("SimSteward.SetCaptureCamera2", GetType(), (pm, arg) => DispatchAction("SetCaptureCamera2", arg?.ToString()));
            pluginManager.AddAction("SimSteward.SetAutoRotateAndCapture", GetType(), (pm, arg) => DispatchAction("SetAutoRotateAndCapture", arg?.ToString()));
            pluginManager.AddAction("SimSteward.ToggleAutoRotateAndCapture", GetType(), (pm, arg) => DispatchAction("ToggleAutoRotateAndCapture", arg?.ToString()));
            pluginManager.AddAction("SimSteward.SetAutoRotateDwellSeconds", GetType(), (pm, arg) => DispatchAction("SetAutoRotateDwellSeconds", arg?.ToString()));
            pluginManager.AddAction("SimSteward.NextIncident", GetType(), (pm, arg) => DispatchAction("NextIncident", arg?.ToString()));
            pluginManager.AddAction("SimSteward.PrevIncident", GetType(), (pm, arg) => DispatchAction("PrevIncident", arg?.ToString()));
            pluginManager.AddAction("SimSteward.ReplayPlayPause", GetType(), (pm, arg) => DispatchAction("ReplayPlayPause", arg?.ToString()));
            pluginManager.AddAction("SimSteward.ReplaySetSpeed", GetType(), (pm, arg) => DispatchAction("ReplaySetSpeed", arg?.ToString()));
            pluginManager.AddAction("SimSteward.ReplayStepFrame", GetType(), (pm, arg) => DispatchAction("ReplayStepFrame", arg?.ToString()));
            pluginManager.AddAction("SimSteward.ReplaySeekFrame", GetType(), (pm, arg) => DispatchAction("ReplaySeekFrame", arg?.ToString()));
            pluginManager.AddAction("SimSteward.ReplaySeekSessionStart", GetType(), (pm, arg) => DispatchAction("ReplaySeekSessionStart", arg?.ToString()));

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
                wsBind = "127.0.0.1";
            var wsToken = Environment.GetEnvironmentVariable("SIMSTEWARD_WS_TOKEN");
            if (string.IsNullOrWhiteSpace(wsToken))
                wsToken = null;

            _logger.Info("Dashboard served via SimHub DashTemplates only; plugin supplies WebSocket state.");
            _bridge = new DashboardBridge(GetStateForNewClient, GetLogTailForNewClient, DispatchAction, OnLog, _logger);
            try
            {
                _bridge.Start(wsBind, wsPort, wsToken);
            }
            catch (Exception ex)
            {
                _logger.Error($"WebSocket server could not start on {wsBind}:{wsPort}. Is it already in use?", ex);
                _bridge = null;
            }

            // Subscribe log streaming after bridge is up so we only forward lines once a client can receive them
            _logger.LogWritten += OnLogWritten;

            try
            {
                _irsdk = new IRacingSdk();
                _irsdk.UpdateInterval = 1;
                _irsdk.OnConnected += () => _logger?.Info("iRacing connected (IRSDKSharper).");
                _irsdk.OnDisconnected += () => _logger?.Info("iRacing disconnected.");
                _irsdk.Start();
            }
            catch (Exception ex)
            {
                _logger.Error("iRacing SDK (IRSDKSharper) failed to start. Plugin will run without iRacing data.", ex);
                _irsdk = null;
            }

            _logger?.Info($"SimSteward ready. WebSocket on :{_wsPort}. Waiting for iRacing...");
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
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
                    else if (!string.IsNullOrEmpty(simMode))
                        _pluginMode = "Live";
                    else
                        _pluginMode = "Unknown";
                }
                catch
                {
                    _pluginMode = "Unknown";
                }
                _replayFrameNum = SafeGetInt("ReplayFrameNum");
                _replayFrameNumEnd = SafeGetInt("ReplayFrameNumEnd");
                _replayPlaySpeed = SafeGetInt("ReplayPlaySpeed");
                _replayPlaySlowMotion = SafeGetBool("ReplayPlaySlowMotion");
                _replaySessionNum = SafeGetInt("ReplaySessionNum");
                _replayIsPlaying = _replayPlaySpeed != 0;

                _tracker.Update(_irsdk, _lastSessionTime);

                // Drain and broadcast any new incidents as real-time push events
                var newIncidents = _tracker.DrainNewIncidents();
                if (newIncidents.Count > 0)
                {
                    foreach (var ev in newIncidents)
                    {
                        var cause = string.IsNullOrEmpty(ev.Cause) ? "" : $", cause={ev.Cause}";
                        var gInfo = ev.PeakG > 0 ? $", {ev.PeakG:F1}g" : "";
                        var other = string.IsNullOrEmpty(ev.OtherDriverName) ? "" : $", vs #{ev.OtherCarNumber} {ev.OtherDriverName}";
                        _logger?.Info($"Incident: {ev.Type} #{ev.CarNumber} {ev.DriverName}{cause}{gInfo}{other} (source={ev.Source}, t={ev.SessionTime:F1}s)");
                    }
                    if (_bridge != null)
                    {
                        try
                        {
                            var msg = new { type = "incidentEvents", events = newIncidents };
                            _bridge.Broadcast(JsonConvert.SerializeObject(msg));
                        }
                        catch { }
                    }
                }

                if (_tracker.BaselineJustEstablished && _bridge != null)
                {
                    try
                    {
                        var entry = new LogEntry
                        {
                            Level = "INFO",
                            Message = "Incident baseline established — tracking is active.",
                            Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
                        };
                        var msg = new { type = "logEvents", entries = new[] { entry } };
                        _bridge.Broadcast(JsonConvert.SerializeObject(msg));
                    }
                    catch { }
                }
            }
            else
            {
                _lastSessionTime = 0;
                _pluginMode = "Unknown";
                _replayFrameNum = 0;
                _replayFrameNumEnd = 0;
                _replayPlaySpeed = 0;
                _replayPlaySlowMotion = false;
                _replaySessionNum = 0;
                _replayIsPlaying = false;
                _tracker.Reset();
                if (_irsdk != null && (DateTime.UtcNow - _lastWaitingLogAt).TotalMilliseconds >= WaitingLogThrottleMs)
                {
                    _lastWaitingLogAt = DateTime.UtcNow;
                    _logger?.Info("Waiting for iRacing to connect...");
                }
            }

            pluginManager.SetPropertyValue("SimSteward.PluginMode", GetType(), _pluginMode);
            pluginManager.SetPropertyValue("SimSteward.IncidentCount", GetType(), _tracker.PlayerIncidentCount);
            pluginManager.SetPropertyValue("SimSteward.HasLiveIncidentData", GetType(), _irsdk != null && _irsdk.IsConnected && _tracker.BaselineEstablished);
            pluginManager.SetPropertyValue("SimSteward.ClientCount", GetType(), clientCount);

            var snapshot = BuildMemoryBankSnapshot();
            _memoryBankClient?.QueueSnapshot(snapshot);

            if (_bridge == null) return;
            var now = DateTime.UtcNow;
            if ((now - _lastBroadcastAt).TotalMilliseconds >= BroadcastThrottleMs)
            {
                _lastBroadcastAt = now;
                _bridge.BroadcastState(BuildStateJson(snapshot));
            }
        }

        public void End(PluginManager pluginManager)
        {
            // Unsubscribe before stopping the bridge to prevent callbacks on a dead bridge
            if (_logger != null)
                _logger.LogWritten -= OnLogWritten;

            if (_irsdk != null)
            {
                try
                {
                    _irsdk.Stop();
                }
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
            _logger?.Info("SimSteward plugin End");
        }

        /// <summary>For settings panel: number of connected WebSocket clients.</summary>
        public int ClientCountForSettings => _bridge?.ClientCount ?? 0;

        /// <summary>For settings panel: WebSocket server is running.</summary>
        public bool WsRunningForSettings => _bridge != null;

        /// <summary>For settings panel: WebSocket port.</summary>
        public int WsPortForSettings => _wsPort;

        /// <summary>For settings panel: iRacing SDK was started successfully.</summary>
        public bool IrsdkStartedForSettings => _irsdk != null;

        /// <summary>For settings panel: memory bank is available and writable.</summary>
        public bool MemoryBankAvailableForSettings => _memoryBankClient?.IsAvailable ?? false;

        /// <summary>For settings panel: absolute path of the memory bank directory.</summary>
        public string MemoryBankPathForSettings =>
            string.IsNullOrEmpty(_pluginDataPath)
                ? "(not set)"
                : System.IO.Path.Combine(_pluginDataPath, "memory-bank");

        /// <summary>For settings panel: iRacing SDK connection status.</summary>
        public string IracingConnectionStatus =>
            _irsdk == null ? "SDK not started" : (_irsdk.IsConnected ? "Connected" : "Not connected");

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
