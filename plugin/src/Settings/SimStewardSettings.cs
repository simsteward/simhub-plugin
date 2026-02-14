namespace SimStewardPlugin.Settings
{
    public enum ThemeMode
    {
        Dark = 1,
        Light = 2
    }

    public sealed class SimStewardSettings
    {
        public ThemeMode ThemeMode { get; set; } = ThemeMode.Dark;

        // Cloud telemetry (Grafana) - agentless export from within SimHub.
        // Note: values are stored in SimHub plugin settings (not a secure store).
        public bool TelemetryEnabled { get; set; } = true;

        // For pre-v1 free builds: enforce telemetry enabled.
        // Later, paid tiers can set this false and allow opt-out.
        public bool TelemetryRequired { get; set; } = true;

        // Flush interval for background exports (avoid per-tick network IO).
        public int TelemetryFlushIntervalSeconds { get; set; } = 5;

        // How often to enqueue a heartbeat when telemetry is active (1–60 seconds).
        public int TelemetryHeartbeatIntervalSeconds { get; set; } = 2;

        // Upper bound for in-memory queued log events (approx UTF-8 bytes).
        public int TelemetryMaxQueueBytes { get; set; } = 128 * 1024;

        // When true, write the same log lines (same schema as Loki) to a file.
        public bool TelemetryLogToDisk { get; set; } = false;

        // Directory for disk log files. Empty = use default under SimHub PluginsData (e.g. Sim Steward/logs).
        public string TelemetryLogDirectory { get; set; } = string.Empty;

        // Stable per-install id (generated on first run).
        public string TelemetryInstallId { get; set; } = string.Empty;

        // Grafana Cloud Loki
        public string GrafanaLokiUrl { get; set; } = string.Empty;
        public string GrafanaLokiUsername { get; set; } = string.Empty;
        public string GrafanaLokiApiKey { get; set; } = string.Empty; // legacy plaintext (migrated to GrafanaLokiApiKeyProtected)
        public string GrafanaLokiApiKeyProtected { get; set; } = string.Empty;

        // Grafana Cloud Prometheus (remote_write) - planned; not yet used.
        public string GrafanaPrometheusRemoteWriteUrl { get; set; } = string.Empty;
        public string GrafanaPrometheusUsername { get; set; } = string.Empty;
        public string GrafanaPrometheusApiKey { get; set; } = string.Empty; // legacy plaintext
        public string GrafanaPrometheusApiKeyProtected { get; set; } = string.Empty;
    }
}
