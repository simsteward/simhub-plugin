using System;

namespace SimStewardPlugin.Telemetry
{
    public sealed class TelemetryConfig
    {
        public bool Enabled { get; set; }
        public int FlushIntervalSeconds { get; set; }
        public int MaxQueueBytes { get; set; }

        public string InstallId { get; set; }
        public string DeviceId { get; set; }
        public string PluginVersion { get; set; }
        public string SchemaVersion { get; set; }

        public string LokiUrl { get; set; }
        public string LokiUsername { get; set; }
        public string LokiApiKey { get; set; }

        public bool LogToDisk { get; set; }
        public string LogDirectory { get; set; }

        public static TelemetryConfig Disabled(string pluginVersion)
        {
            return new TelemetryConfig
            {
                Enabled = false,
                FlushIntervalSeconds = 5,
                MaxQueueBytes = 0,
                InstallId = string.Empty,
                DeviceId = string.Empty,
                PluginVersion = pluginVersion ?? string.Empty,
                SchemaVersion = "1",
                LokiUrl = string.Empty,
                LokiUsername = string.Empty,
                LokiApiKey = string.Empty,
                LogToDisk = false,
                LogDirectory = string.Empty,
            };
        }

        public void Normalize()
        {
            if (FlushIntervalSeconds < 1)
            {
                FlushIntervalSeconds = 1;
            }

            if (FlushIntervalSeconds > 300)
            {
                FlushIntervalSeconds = 300;
            }

            if (MaxQueueBytes < 0)
            {
                MaxQueueBytes = 0;
            }

            if (MaxQueueBytes > 5 * 1024 * 1024)
            {
                MaxQueueBytes = 5 * 1024 * 1024;
            }

            SchemaVersion = string.IsNullOrWhiteSpace(SchemaVersion) ? "1" : SchemaVersion;
            InstallId = InstallId ?? string.Empty;
            DeviceId = DeviceId ?? string.Empty;
            PluginVersion = PluginVersion ?? string.Empty;

            LokiUrl = LokiUrl ?? string.Empty;
            LokiUsername = LokiUsername ?? string.Empty;
            LokiApiKey = LokiApiKey ?? string.Empty;
            LogDirectory = LogDirectory ?? string.Empty;
        }

        /// <summary>
        /// True when we have enough to authenticate: URL + API key, and either a username (classic)
        /// or no username (pre-encoded Basic token in API key field).
        /// </summary>
        public bool HasLokiCredentials()
        {
            if (string.IsNullOrWhiteSpace(LokiUrl) || string.IsNullOrWhiteSpace(LokiApiKey))
            {
                return false;
            }
            // Classic: username + API key. Raw token: API key is already Base64(user:key), username empty.
            return true;
        }

        public override string ToString()
        {
            return $"Enabled={Enabled}, Flush={FlushIntervalSeconds}s, QueueBytes={MaxQueueBytes}, Loki={(string.IsNullOrWhiteSpace(LokiUrl) ? "<none>" : LokiUrl)}";
        }
    }
}
