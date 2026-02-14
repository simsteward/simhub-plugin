using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Media;
using GameReaderCommon;
using SimHub.Plugins;
using SimStewardPlugin.Settings;
using SimStewardPlugin.Telemetry;

namespace SimStewardPlugin
{
    [PluginDescription("Sim Steward iRacing incident clipping tool")]
    [PluginAuthor("Sim Steward")]
    [PluginName("Sim Steward")]
    public sealed class SimStewardPlugin : IPlugin, IDataPlugin, IWPFSettings, IWPFSettingsV2
    {
        private const string SessionTimePath = "DataCorePlugin.GameRawData.Telemetry.SessionTime";
        private const string SessionNumPath = "DataCorePlugin.GameRawData.Telemetry.SessionNum";
        private const string IncidentCountPath = "DataCorePlugin.GameRawData.Telemetry.PlayerCarTeamIncidentCount";
        private static readonly TimeSpan TelemetryLogInterval = TimeSpan.FromSeconds(5);
        private const int DefaultHeartbeatIntervalSeconds = 2;
        private const int MinHeartbeatIntervalSeconds = 1;
        private const int MaxHeartbeatIntervalSeconds = 60;

        private bool _isIRacingConnected;
        private DateTime _lastTelemetryLogUtc = DateTime.MinValue;
        private readonly StatusManager _statusManager = new StatusManager();
        private SimStewardSettings _settings;
        private TelemetryManager _telemetryManager;
        private DateTime _lastTelemetryHeartbeatUtc = DateTime.MinValue;
        private static object _runtimeLogger;
        private static MethodInfo _runtimeInfoMethod;
        private static MethodInfo _runtimeDebugMethod;
        private static MethodInfo _runtimeErrorMethod;
        private static MethodInfo _runtimeErrorWithExceptionMethod;
        private static bool _runtimeLoggerInitialized;
        private static bool _fallbackWarningLogged;
        private static readonly ImageSource MenuIcon = CreateMenuIcon();

        public PluginManager PluginManager { get; set; }
        public string LeftMenuTitle => "Sim Steward";
        public ImageSource PictureIcon => MenuIcon;

        public void Init(PluginManager pluginManager)
        {
            PluginManager = pluginManager;
            _isIRacingConnected = false;
            _lastTelemetryLogUtc = DateTime.MinValue;
            _lastTelemetryHeartbeatUtc = DateTime.MinValue;
            _statusManager.StatusTransition += OnStatusTransition;
            _statusManager.PluginState = PluginRuntimeState.Starting;
            _statusManager.IRacingConnection = ConnectionState.Disconnected;
            _statusManager.ObsState = FeatureState.NotConfigured;
            _statusManager.IncidentDetectionState = FeatureState.NotConfigured;
            _statusManager.RecordingState = FeatureState.NotConfigured;
            _statusManager.ReplayState = FeatureState.NotConfigured;

            RegisterStatusProperties(pluginManager);

            LoadSettings();

            InitializeTelemetry();

            string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.1.0";
            LogInfo($"Sim Steward: Init complete (v{version})");
            _statusManager.PluginState = PluginRuntimeState.Running;
            RefreshMonitoringState();
            PublishStatusProperties(pluginManager);
        }

        private void InitializeTelemetry()
        {
            try
            {
                if (_settings == null)
                {
                    _settings = new SimStewardSettings();
                }

                TelemetryConfig cfg = TelemetryManager.FromSettings(_settings);
                _settings.TelemetryInstallId = cfg.InstallId;
                SaveSettings();

                _telemetryManager?.Dispose();
                _telemetryManager = new TelemetryManager(cfg);
                _telemetryManager.Start();

                LogInfo($"Sim Steward: Telemetry configured ({cfg})");
            }
            catch (Exception ex)
            {
                LogError("Sim Steward: Failed to initialize telemetry", ex);
            }
        }

        private void LoadSettings()
        {
            try
            {
                _settings = this.ReadCommonSettings<SimStewardSettings>(
                    "SimStewardSettings",
                    () => new SimStewardSettings());

                if (_settings != null && (int)_settings.ThemeMode == 0)
                {
                    _settings.ThemeMode = ThemeMode.Light;
                }
            }
            catch (Exception ex)
            {
                _settings = new SimStewardSettings();
                LogError("Sim Steward: Failed to load settings, using defaults", ex);
            }
        }

        private void SaveSettings()
        {
            try
            {
                this.SaveCommonSettings("SimStewardSettings", _settings);

                try
                {
                    TelemetryConfig cfg = TelemetryManager.FromSettings(_settings);
                    _telemetryManager?.ApplyConfig(cfg);
                }
                catch (Exception ex)
                {
                    LogError("Sim Steward: Failed to apply telemetry settings", ex);
                }
            }
            catch (Exception ex)
            {
                LogError("Sim Steward: Failed to save settings", ex);
            }
        }

        public void DataUpdate(PluginManager pluginManager, ref GameData data)
        {
            try
            {
                if (pluginManager == null)
                {
                    return;
                }

                _statusManager.LastGameName = pluginManager.GameName;

                bool isConnected = IsIRacingGame(pluginManager?.GameName);
                UpdateConnectionState(isConnected);

                if (!isConnected)
                {
                    PublishStatusProperties(pluginManager);
                    return;
                }

                object sessionTimeRaw = pluginManager.GetPropertyValue(SessionTimePath);
                object sessionNumRaw = pluginManager.GetPropertyValue(SessionNumPath);
                object incidentCountRaw = pluginManager.GetPropertyValue(IncidentCountPath);

                if (!TryConvertToDouble(sessionTimeRaw, out double sessionTime))
                {
                    PublishStatusProperties(pluginManager);
                    return;
                }

                if (!TryConvertToInt(sessionNumRaw, out int sessionNum) || !TryConvertToInt(incidentCountRaw, out int incidentCount))
                {
                    PublishStatusProperties(pluginManager);
                    return;
                }

                _statusManager.TelemetryUpdateCount += 1;
                _statusManager.LastSessionTime = sessionTime;
                _statusManager.LastSessionNum = sessionNum;
                _statusManager.LastIncidentCount = incidentCount;

                DateTime now = DateTime.UtcNow;
                if (now - _lastTelemetryLogUtc >= TelemetryLogInterval)
                {
                    LogDebug(
                        $"Sim Steward: Telemetry SessionTime={sessionTime:F3}, SessionNum={sessionNum}, Incidents={incidentCount}");
                    _lastTelemetryLogUtc = now;
                }

                PublishStatusProperties(pluginManager);

                int heartbeatSec = DefaultHeartbeatIntervalSeconds;
                if (_settings != null)
                {
                    heartbeatSec = _settings.TelemetryHeartbeatIntervalSeconds;
                    if (heartbeatSec < MinHeartbeatIntervalSeconds) heartbeatSec = MinHeartbeatIntervalSeconds;
                    if (heartbeatSec > MaxHeartbeatIntervalSeconds) heartbeatSec = MaxHeartbeatIntervalSeconds;
                }
                var heartbeatInterval = TimeSpan.FromSeconds(heartbeatSec);
                if (now - _lastTelemetryHeartbeatUtc >= heartbeatInterval)
                {
                    _telemetryManager?.EnqueueHeartbeat("periodic", _statusManager);
                    _lastTelemetryHeartbeatUtc = now;
                    RefreshMonitoringState();
                }
            }
            catch (Exception ex)
            {
                _statusManager.SetError(ex.Message);
                _telemetryManager?.EnqueueException(ex, "DataUpdate");
                LogError("Sim Steward: Unexpected error in DataUpdate", ex);
                PublishStatusProperties(pluginManager);
            }
        }

        public void End(PluginManager pluginManager)
        {
            _isIRacingConnected = false;
            _statusManager.PluginState = PluginRuntimeState.Shutdown;
            PublishStatusProperties(pluginManager);
            _statusManager.StatusTransition -= OnStatusTransition;
            _telemetryManager?.Dispose();
            _telemetryManager = null;
            LogInfo("Sim Steward: Shutdown complete");
        }

        public Control GetWPFSettingsControl(PluginManager pluginManager)
        {
            if (_settings == null)
            {
                _settings = new SimStewardSettings();
            }

            return new SettingsControl(_statusManager, _settings, _telemetryManager, SaveSettings);
        }

        private void UpdateConnectionState(bool isConnected)
        {
            if (isConnected == _isIRacingConnected)
            {
                return;
            }

            _isIRacingConnected = isConnected;
            _statusManager.IRacingConnection = isConnected ? ConnectionState.Connected : ConnectionState.Disconnected;
            if (_isIRacingConnected)
            {
                LogInfo("Sim Steward: iRacing connected");
            }
            else
            {
                _statusManager.ResetTelemetry();
                LogInfo("Sim Steward: iRacing disconnected");
            }
        }

        private void OnStatusTransition(string transition)
        {
            LogInfo($"Sim Steward: {transition}");
            _telemetryManager?.EnqueueStatusTransition(transition);
        }

        private static void RegisterStatusProperties(PluginManager pluginManager)
        {
            if (pluginManager == null)
            {
                return;
            }

            pluginManager.AddProperty<string>("SimSteward.Status.Plugin.State", typeof(SimStewardPlugin), string.Empty, "");
            pluginManager.AddProperty<string>("SimSteward.Status.Plugin.LastStatusChangeUtc", typeof(SimStewardPlugin), string.Empty, "");
            pluginManager.AddProperty<string>("SimSteward.Status.Plugin.LastHeartbeatUtc", typeof(SimStewardPlugin), string.Empty, "");
            pluginManager.AddProperty<bool>("SimSteward.Status.iRacing.IsConnected", typeof(SimStewardPlugin), false, "");
            pluginManager.AddProperty<string>("SimSteward.Status.iRacing.State", typeof(SimStewardPlugin), string.Empty, "");
            pluginManager.AddProperty<string>("SimSteward.Status.iRacing.GameName", typeof(SimStewardPlugin), string.Empty, "");
            pluginManager.AddProperty<long>("SimSteward.Status.Telemetry.UpdateCount", typeof(SimStewardPlugin), 0L, "");
            pluginManager.AddProperty<double>("SimSteward.Status.Telemetry.SessionTime", typeof(SimStewardPlugin), 0.0d, "");
            pluginManager.AddProperty<int>("SimSteward.Status.Telemetry.SessionNum", typeof(SimStewardPlugin), 0, "");
            pluginManager.AddProperty<int>("SimSteward.Status.Telemetry.IncidentCount", typeof(SimStewardPlugin), 0, "");
            pluginManager.AddProperty<string>("SimSteward.Status.OBS.State", typeof(SimStewardPlugin), string.Empty, "");
            pluginManager.AddProperty<string>("SimSteward.Status.Incident.State", typeof(SimStewardPlugin), string.Empty, "");
            pluginManager.AddProperty<string>("SimSteward.Status.Recording.State", typeof(SimStewardPlugin), string.Empty, "");
            pluginManager.AddProperty<string>("SimSteward.Status.Replay.State", typeof(SimStewardPlugin), string.Empty, "");
            pluginManager.AddProperty<bool>("SimSteward.Status.Error.HasError", typeof(SimStewardPlugin), false, "");
            pluginManager.AddProperty<string>("SimSteward.Status.Error.LastMessage", typeof(SimStewardPlugin), string.Empty, "");
        }

        private void RefreshMonitoringState()
        {
            if (_telemetryManager == null)
            {
                _statusManager.MonitoringState = FeatureState.NotConfigured;
                return;
            }

            var snapshot = _telemetryManager.GetLokiStatusSnapshot();
            if (snapshot == null)
            {
                _statusManager.MonitoringState = FeatureState.NotConfigured;
                return;
            }

            switch (snapshot.State)
            {
                case TelemetryConnectionState.Connected:
                    _statusManager.MonitoringState = FeatureState.Active;
                    break;
                case TelemetryConnectionState.Connecting:
                    _statusManager.MonitoringState = FeatureState.Waiting;
                    break;
                case TelemetryConnectionState.Error:
                    _statusManager.MonitoringState = FeatureState.Error;
                    break;
                default:
                    _statusManager.MonitoringState = FeatureState.NotConfigured;
                    break;
            }
        }

        private void PublishStatusProperties(PluginManager pluginManager)
        {
            if (pluginManager == null)
            {
                return;
            }

            pluginManager.SetPropertyValue<SimStewardPlugin>("SimSteward.Status.Plugin.State", _statusManager.PluginStateText);
            pluginManager.SetPropertyValue<SimStewardPlugin>("SimSteward.Status.Plugin.LastStatusChangeUtc", _statusManager.LastStatusChangeUtcFormatted);
            _statusManager.RecordHeartbeat();
            pluginManager.SetPropertyValue<SimStewardPlugin>("SimSteward.Status.Plugin.LastHeartbeatUtc", _statusManager.LastHeartbeatUtcFormatted);
            pluginManager.SetPropertyValue<SimStewardPlugin>("SimSteward.Status.iRacing.IsConnected", _statusManager.IsIRacingConnected);
            pluginManager.SetPropertyValue<SimStewardPlugin>("SimSteward.Status.iRacing.State", _statusManager.IRacingConnectionText);
            pluginManager.SetPropertyValue<SimStewardPlugin>("SimSteward.Status.iRacing.GameName", _statusManager.LastGameName);
            pluginManager.SetPropertyValue<SimStewardPlugin>("SimSteward.Status.Telemetry.UpdateCount", _statusManager.TelemetryUpdateCount);
            pluginManager.SetPropertyValue<SimStewardPlugin>("SimSteward.Status.Telemetry.SessionTime", _statusManager.LastSessionTime);
            pluginManager.SetPropertyValue<SimStewardPlugin>("SimSteward.Status.Telemetry.SessionNum", _statusManager.LastSessionNum);
            pluginManager.SetPropertyValue<SimStewardPlugin>("SimSteward.Status.Telemetry.IncidentCount", _statusManager.LastIncidentCount);
            pluginManager.SetPropertyValue<SimStewardPlugin>("SimSteward.Status.Monitoring.State", _statusManager.MonitoringStateText);
            pluginManager.SetPropertyValue<SimStewardPlugin>("SimSteward.Status.OBS.State", _statusManager.ObsStateText);
            pluginManager.SetPropertyValue<SimStewardPlugin>("SimSteward.Status.Incident.State", _statusManager.IncidentDetectionStateText);
            pluginManager.SetPropertyValue<SimStewardPlugin>("SimSteward.Status.Recording.State", _statusManager.RecordingStateText);
            pluginManager.SetPropertyValue<SimStewardPlugin>("SimSteward.Status.Replay.State", _statusManager.ReplayStateText);
            pluginManager.SetPropertyValue<SimStewardPlugin>("SimSteward.Status.Error.HasError", _statusManager.HasError);
            pluginManager.SetPropertyValue<SimStewardPlugin>("SimSteward.Status.Error.LastMessage", _statusManager.LastErrorMessage);
        }

        private static void LogInfo(string message)
        {
            if (TryLogRuntime("Info", message, null))
            {
                return;
            }

            Trace.WriteLine(message);
        }

        private static void LogDebug(string message)
        {
            if (TryLogRuntime("Debug", message, null))
            {
                return;
            }

            Trace.WriteLine(message);
        }

        private static void LogError(string message, Exception ex)
        {
            if (TryLogRuntime("Error", message, ex))
            {
                return;
            }

            Trace.WriteLine($"{message} :: {ex}");
        }

        private static bool TryLogRuntime(string level, string message, Exception ex)
        {
            EnsureRuntimeLoggerInitialized();

            if (_runtimeLogger == null)
            {
                if (!_fallbackWarningLogged)
                {
                    _fallbackWarningLogged = true;
                    Trace.WriteLine("Sim Steward: SimHub runtime logger unavailable, using Trace fallback.");
                }

                return false;
            }

            MethodInfo method = null;
            switch (level)
            {
                case "Info":
                    method = _runtimeInfoMethod;
                    break;
                case "Debug":
                    method = _runtimeDebugMethod;
                    break;
                case "Error":
                    method = ex == null ? _runtimeErrorMethod : _runtimeErrorWithExceptionMethod ?? _runtimeErrorMethod;
                    break;
            }

            if (method == null)
            {
                return false;
            }

            try
            {
                if (method == _runtimeErrorWithExceptionMethod)
                {
                    method.Invoke(_runtimeLogger, new object[] { message, ex });
                }
                else
                {
                    string finalMessage = ex == null ? message : $"{message} :: {ex.Message}";
                    method.Invoke(_runtimeLogger, new object[] { finalMessage });
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void EnsureRuntimeLoggerInitialized()
        {
            if (_runtimeLoggerInitialized)
            {
                return;
            }

            _runtimeLoggerInitialized = true;

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type loggingType = assembly.GetType("SimHub.Logging", false);
                if (loggingType == null)
                {
                    continue;
                }

                PropertyInfo currentProperty = loggingType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
                object logger = currentProperty?.GetValue(null);
                if (logger == null)
                {
                    continue;
                }

                Type loggerType = logger.GetType();
                _runtimeInfoMethod = loggerType.GetMethod("Info", new[] { typeof(string) });
                _runtimeDebugMethod = loggerType.GetMethod("Debug", new[] { typeof(string) });
                _runtimeErrorMethod = loggerType.GetMethod("Error", new[] { typeof(string) });
                _runtimeErrorWithExceptionMethod = loggerType.GetMethod("Error", new[] { typeof(string), typeof(Exception) });
                _runtimeLogger = logger;
                break;
            }
        }

        private static ImageSource CreateMenuIcon()
        {
            Geometry geometry = Geometry.Parse("M4,4 L20,4 L20,20 L4,20 Z M8,8 L16,8 L16,16 L8,16 Z");
            var drawing = new GeometryDrawing(Brushes.White, null, geometry);
            drawing.Freeze();
            var image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }

        private static bool IsIRacingGame(string gameName)
        {
            if (string.IsNullOrWhiteSpace(gameName))
            {
                return false;
            }

            return gameName.IndexOf("iracing", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool TryConvertToInt(object value, out int result)
        {
            result = 0;
            if (value == null)
            {
                return false;
            }

            switch (value)
            {
                case int intValue:
                    result = intValue;
                    return true;
                case long longValue:
                    if (longValue > int.MaxValue || longValue < int.MinValue)
                    {
                        return false;
                    }

                    result = (int)longValue;
                    return true;
                case short shortValue:
                    result = shortValue;
                    return true;
                case byte byteValue:
                    result = byteValue;
                    return true;
                case double doubleValue:
                    result = (int)Math.Round(doubleValue);
                    return true;
                case float floatValue:
                    result = (int)Math.Round(floatValue);
                    return true;
                case decimal decimalValue:
                    result = (int)Math.Round(decimalValue);
                    return true;
                default:
                    return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
            }
        }

        private static bool TryConvertToDouble(object value, out double result)
        {
            result = 0;
            if (value == null)
            {
                return false;
            }

            switch (value)
            {
                case double doubleValue:
                    result = doubleValue;
                    return true;
                case float floatValue:
                    result = floatValue;
                    return true;
                case decimal decimalValue:
                    result = (double)decimalValue;
                    return true;
                case int intValue:
                    result = intValue;
                    return true;
                case long longValue:
                    result = longValue;
                    return true;
                default:
                    return double.TryParse(value.ToString(), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out result);
            }
        }
    }
}