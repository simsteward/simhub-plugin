using System;
using System.Reflection;
using System.Threading;

namespace SimStewardPlugin.Telemetry
{
    public sealed class TelemetryManager : IDisposable
    {
        private readonly Guid _runId = Guid.NewGuid();
        private readonly LokiExporter _loki;
        private TelemetryConfig _config;
        private bool _userDisconnected;
        private bool _isConnecting;
        private long _logLinesWrittenTotal;

        public Guid RunId => _runId;

        private readonly DiskLogWriter _diskLog;

        public TelemetryManager(TelemetryConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _config.Normalize();
            _loki = new LokiExporter(_config);
            _diskLog = new DiskLogWriter(_config.LogToDisk, _config.LogDirectory);
        }

        public void Start()
        {
            _loki.Start();
            EnqueueHeartbeat("startup");
        }

        public void Disconnect()
        {
            _userDisconnected = true;
            _isConnecting = false;

            var cfg = new TelemetryConfig
            {
                Enabled = false,
                FlushIntervalSeconds = _config.FlushIntervalSeconds,
                MaxQueueBytes = _config.MaxQueueBytes,
                InstallId = _config.InstallId,
                DeviceId = _config.DeviceId,
                PluginVersion = _config.PluginVersion,
                SchemaVersion = _config.SchemaVersion,
                LokiUrl = _config.LokiUrl,
                LokiUsername = _config.LokiUsername,
                LokiApiKey = _config.LokiApiKey,
            };

            cfg.Normalize();
            _loki.ApplyConfig(cfg);
            string disconnectMsg = $"event=telemetry_disconnected run_id={_runId:N}";
            Interlocked.Increment(ref _logLinesWrittenTotal);
            _loki.Enqueue(disconnectMsg);
            _diskLog.WriteLine(disconnectMsg);
        }

        public async System.Threading.Tasks.Task<TelemetryStatusSnapshot> ConnectAndTestAsync(StatusManager status)
        {
            _userDisconnected = false;
            _isConnecting = true;

            var snapshot = GetLokiStatusSnapshot();
            snapshot.State = TelemetryConnectionState.Connecting;

            if (!_config.Enabled)
            {
                snapshot.State = TelemetryConnectionState.Disconnected;
                snapshot.LastError = "Telemetry disabled";
                _isConnecting = false;
                return snapshot;
            }

            if (!_config.HasLokiCredentials())
            {
                snapshot.State = TelemetryConnectionState.NotConfigured;
                snapshot.LastError = "Missing URL or API key";
                _isConnecting = false;
                return snapshot;
            }

            // Force timer creation and then push a single test line immediately.
            _loki.ApplyConfig(_config);
            EnqueueHeartbeat("manual_connect", status);
            await _loki.FlushAsync().ConfigureAwait(false);

            snapshot = GetLokiStatusSnapshot();
            _isConnecting = false;
            return snapshot;
        }

        public void ApplyConfig(TelemetryConfig config)
        {
            if (config == null)
            {
                return;
            }

            config.Normalize();
            _config = config;

            // Respect manual disconnect (UI) without losing stored creds.
            if (_userDisconnected)
            {
                var disabled = new TelemetryConfig
                {
                    Enabled = false,
                    FlushIntervalSeconds = config.FlushIntervalSeconds,
                    MaxQueueBytes = config.MaxQueueBytes,
                    InstallId = config.InstallId,
                    DeviceId = config.DeviceId,
                    PluginVersion = config.PluginVersion,
                    SchemaVersion = config.SchemaVersion,
                    LokiUrl = config.LokiUrl,
                    LokiUsername = config.LokiUsername,
                    LokiApiKey = config.LokiApiKey,
                };
                disabled.Normalize();
                _loki.ApplyConfig(disabled);
            }
            else
            {
                _loki.ApplyConfig(config);
            }
        }

        public TelemetryStatusSnapshot GetLokiStatusSnapshot()
        {
            var snapshot = new TelemetryStatusSnapshot
            {
                LastAttemptUtc = _loki.LastAttemptUtc,
                LastSuccessUtc = _loki.LastSuccessUtc,
                LastError = _loki.LastError,
                SentLinesTotal = _loki.SentLinesTotal,
                SentBatchesTotal = _loki.SentBatchesTotal,
                SentExceptionLinesTotal = _loki.SentExceptionLinesTotal,
                DroppedLinesTotal = _loki.DroppedLinesTotal,
                LogLinesWrittenTotal = Interlocked.Read(ref _logLinesWrittenTotal),
                SentBytesTotal = _loki.SentBytesTotal,
            };

            if (_userDisconnected)
            {
                snapshot.State = TelemetryConnectionState.Disconnected;
                return snapshot;
            }

            if (!_config.Enabled)
            {
                snapshot.State = TelemetryConnectionState.Disconnected;
                return snapshot;
            }

            if (!_config.HasLokiCredentials())
            {
                snapshot.State = TelemetryConnectionState.NotConfigured;
                return snapshot;
            }

            if (_isConnecting)
            {
                snapshot.State = TelemetryConnectionState.Connecting;
                return snapshot;
            }

            if (!string.IsNullOrWhiteSpace(_loki.LastError))
            {
                snapshot.State = TelemetryConnectionState.Error;
                return snapshot;
            }

            if (_loki.LastSuccessUtc > DateTime.MinValue)
            {
                snapshot.State = TelemetryConnectionState.Connected;
                return snapshot;
            }

            snapshot.State = TelemetryConnectionState.Disconnected;
            return snapshot;
        }

        public void EnqueueStatusTransition(string transitionText)
        {
            if (string.IsNullOrWhiteSpace(transitionText))
            {
                return;
            }

            string msg = $"event=status_transition run_id={_runId:N} transition=\"{transitionText}\"";
            Interlocked.Increment(ref _logLinesWrittenTotal);
            _loki.Enqueue(msg);
            _diskLog.WriteLine(msg);
        }

        public void EnqueueHeartbeat(string reason, StatusManager status = null)
        {
            string pluginState = status?.PluginStateText ?? string.Empty;
            string iracing = status?.IRacingConnectionText ?? string.Empty;
            string game = status?.LastGameName ?? string.Empty;
            string updates = status?.TelemetryUpdateCount.ToString() ?? string.Empty;
            string hasError = status?.HasError.ToString() ?? string.Empty;

            string msg = $"event=heartbeat run_id={_runId:N} reason={Sanitize(reason)} plugin_state={Sanitize(pluginState)} iracing_state={Sanitize(iracing)} game={Sanitize(game)} updates={updates} has_error={hasError}";
            Interlocked.Increment(ref _logLinesWrittenTotal);
            _loki.Enqueue(msg);
            _diskLog.WriteLine(msg);
        }

        public void EnqueueException(Exception ex, string context)
        {
            if (ex == null)
            {
                return;
            }

            string type = ex.GetType().FullName ?? "Exception";
            string msg = $"event=exception run_id={_runId:N} context={Sanitize(context)} ex_type={Sanitize(type)} ex_msg=\"{Sanitize(ex.Message)}\"";
            Interlocked.Increment(ref _logLinesWrittenTotal);
            _loki.Enqueue(msg, true);
            _diskLog.WriteLine(msg);
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "";
            }

            // Keep it LogQL-friendly: collapse whitespace and remove quotes.
            string v = value.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            v = v.Replace("\"", "'");
            return v.Trim();
        }

        public static TelemetryConfig FromSettings(Settings.SimStewardSettings settings)
        {
            string pluginVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
            if (settings == null)
            {
                return TelemetryConfig.Disabled(pluginVersion);
            }

            // Enforce required telemetry (free tier) by overriding enabled.
            bool effectiveEnabled = settings.TelemetryRequired || settings.TelemetryEnabled;

            string installId = DeviceIdentity.EnsureInstallId(settings.TelemetryInstallId);
            string deviceId = DeviceIdentity.ComputeDeviceIdHash(installId);

            // Prefer protected secrets.
            string lokiApiKey = SecretStore.UnprotectFromBase64(settings.GrafanaLokiApiKeyProtected);
            if (string.IsNullOrWhiteSpace(lokiApiKey))
            {
                lokiApiKey = settings.GrafanaLokiApiKey;
            }

            return new TelemetryConfig
            {
                Enabled = effectiveEnabled,
                FlushIntervalSeconds = settings.TelemetryFlushIntervalSeconds,
                MaxQueueBytes = settings.TelemetryMaxQueueBytes,
                LogToDisk = settings.TelemetryLogToDisk,
                LogDirectory = settings.TelemetryLogDirectory ?? string.Empty,
                InstallId = installId,
                DeviceId = deviceId,
                PluginVersion = pluginVersion,
                SchemaVersion = "1",
                LokiUrl = settings.GrafanaLokiUrl,
                LokiUsername = settings.GrafanaLokiUsername,
                LokiApiKey = lokiApiKey,
            };
        }

        public void Dispose()
        {
            EnqueueHeartbeat("shutdown");
            _loki.Dispose();
            _diskLog.Dispose();
        }
    }
}
