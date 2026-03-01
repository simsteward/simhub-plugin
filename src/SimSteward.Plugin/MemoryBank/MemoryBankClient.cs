using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace SimSteward.Plugin.MemoryBank
{
    internal class MemoryBankClient
    {
        private readonly string _rootPath;
        private readonly PluginLogger _logger;
        private readonly object _sync = new object();
        private readonly object _fileLock = new object();
        private string _lastSnapshotJson;
        private DateTime _lastWriteTime = DateTime.MinValue;
        private bool _isValid;

        public bool IsAvailable => _isValid;

        public MemoryBankClient(string pluginDataPath, PluginLogger logger)
        {
            _logger = logger;
            _rootPath = DeterminePath(pluginDataPath);
            Initialize();
        }

        private string DeterminePath(string pluginDataPath)
        {
            var envPath = Environment.GetEnvironmentVariable("MEMORY_BANK_PATH");
            if (!string.IsNullOrWhiteSpace(envPath))
                return envPath;
            if (!string.IsNullOrWhiteSpace(pluginDataPath))
                return Path.Combine(pluginDataPath, "memory-bank");
            return Path.Combine(Environment.CurrentDirectory, "memory-bank");
        }

        private void Initialize()
        {
            try
            {
                Directory.CreateDirectory(_rootPath);
                ValidatePath(_rootPath);
                _isValid = true;
                _logger?.Info($"MemoryBankClient initialized at {_rootPath}");
            }
            catch (Exception ex)
            {
                _logger?.Warn($"MemoryBankClient could not initialize at {_rootPath}: {ex.Message}");
                _isValid = false;
            }
        }

        private static void ValidatePath(string path)
        {
            var checkFile = Path.Combine(path, ".memorybank-write-check");
            File.WriteAllText(checkFile, DateTime.UtcNow.ToString("O"), Encoding.UTF8);
            File.Delete(checkFile);
        }

        public void QueueSnapshot(MemoryBankSnapshot snapshot)
        {
            if (!_isValid || snapshot == null) return;
            var json = JsonConvert.SerializeObject(snapshot, Formatting.Indented);
            lock (_sync)
            {
                if (string.Equals(json, _lastSnapshotJson, StringComparison.Ordinal) &&
                    (DateTime.UtcNow - _lastWriteTime).TotalSeconds < 1)
                {
                    return;
                }

                _lastSnapshotJson = json;
                _lastWriteTime = DateTime.UtcNow;
            }

            Task.Run(() => WriteSnapshotFiles(snapshot, json));
        }

        private void WriteSnapshotFiles(MemoryBankSnapshot snapshot, string json)
        {
            lock (_fileLock)
            {
                try
                {
                    WriteAtomic("snapshot.json", json);
                    WriteAtomic("metrics.json", BuildMetricsJson(snapshot));
                    WriteAtomic("HEALTH.md", BuildHealthMarkdown(snapshot));
                    WriteAtomic("tasks.md", BuildTasksMarkdown(snapshot));
                    WriteAtomic("activeContext.md", BuildActiveContextMarkdown(snapshot));
                    WriteAtomic("progress.md", BuildProgressMarkdown(snapshot));
                }
                catch (Exception ex)
                {
                    _logger?.Warn($"MemoryBankClient write failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Compact operational metrics JSON — the primary file for AI and tooling to query
        /// the plugin's health and detection accuracy at a glance.
        /// </summary>
        private string BuildMetricsJson(MemoryBankSnapshot snapshot)
        {
            var m  = snapshot.Metrics     ?? new DetectionMetrics();
            var d  = snapshot.Diagnostics ?? new PluginDiagnostics();
            var obj = new
            {
                timestamp   = snapshot.Timestamp.ToString("O"),
                session = new
                {
                    pluginMode            = snapshot.PluginMode ?? "Unknown",
                    sessionTime           = snapshot.CurrentSessionTimeFormatted,
                    replayIsPlaying       = snapshot.ReplayIsPlaying,
                    replaySpeed           = snapshot.ReplayPlaySpeed,
                    playerCarIdx          = snapshot.PlayerCarIdx,
                    playerIncidentCount   = snapshot.PlayerIncidentCount,
                    driversTracked        = snapshot.Drivers?.Count ?? 0,
                    incidentsInFeed       = snapshot.Incidents?.Count ?? 0,
                    trackName             = snapshot.TrackName ?? "",
                    trackCategory         = snapshot.TrackCategory ?? "Road",
                    trackLengthM          = snapshot.TrackLengthM,
                },
                detection = new
                {
                    l1PlayerEvents         = m.L1PlayerEvents,
                    l2PhysicsImpacts       = m.L2PhysicsImpacts,
                    l3OffTrackEvents       = m.L3OffTrackEvents,
                    l4YamlEvents           = m.L4YamlEvents,
                    zeroXEvents            = m.ZeroXEvents,
                    totalEvents            = m.TotalEvents,
                    yamlUpdates            = m.YamlUpdates,
                    lastDetectionSessionTime = m.LastDetectionSessionTime >= 0
                        ? FormatSessionTime(m.LastDetectionSessionTime)
                        : "none",
                },
                infrastructure = new
                {
                    irsdkStarted           = d.IrsdkStarted,
                    irsdkConnected         = d.IrsdkConnected,
                    wsRunning              = d.WsRunning,
                    wsPort                 = d.WsPort,
                    wsClients              = d.WsClients,
                    memoryBankAvailable    = d.MemoryBankAvailable,
                    memoryBankPath         = d.MemoryBankPath,
                },
            };
            return JsonConvert.SerializeObject(obj, Formatting.Indented);
        }

        private static string FormatSessionTime(double totalSeconds)
        {
            if (double.IsNaN(totalSeconds) || totalSeconds < 0 || double.IsInfinity(totalSeconds))
                return "0:00";
            int minutes = (int)(totalSeconds / 60);
            int seconds = (int)(totalSeconds % 60);
            return $"{minutes}:{seconds:D2}";
        }

        /// <summary>
        /// Human-readable operational health report — gives the AI (and developer) an
        /// at-a-glance status of every subsystem and detection layer without opening JSON.
        /// </summary>
        private string BuildHealthMarkdown(MemoryBankSnapshot snapshot)
        {
            var m  = snapshot.Metrics     ?? new DetectionMetrics();
            var d  = snapshot.Diagnostics ?? new PluginDiagnostics();
            var sb = new StringBuilder();

            string tick(bool ok) => ok ? "✓" : "✗";
            string na(int v) => v == 0 ? "—" : v.ToString();

            sb.AppendLine("# Sim Steward — System Health");
            sb.AppendLine();
            sb.AppendLine($"**Snapshot**: {snapshot.Timestamp:O}");
            sb.AppendLine($"**Session**: {snapshot.PluginMode ?? "Unknown"} · {snapshot.CurrentSessionTimeFormatted}");
            sb.AppendLine();

            sb.AppendLine("## Infrastructure");
            sb.AppendLine();
            sb.AppendLine($"| Component | Status |");
            sb.AppendLine($"|-----------|--------|");
            sb.AppendLine($"| iRacing SDK started  | {tick(d.IrsdkStarted)} |");
            sb.AppendLine($"| iRacing SDK connected | {tick(d.IrsdkConnected)} |");
            sb.AppendLine($"| WebSocket server      | {tick(d.WsRunning)} port {d.WsPort} |");
            sb.AppendLine($"| Dashboard clients     | {d.WsClients} connected |");
            sb.AppendLine($"| Memory bank           | {tick(d.MemoryBankAvailable)} {d.MemoryBankPath ?? "(not set)"} |");
            sb.AppendLine();

            sb.AppendLine("## Session");
            sb.AppendLine();
            sb.AppendLine($"| Field | Value |");
            sb.AppendLine($"|-------|-------|");
            sb.AppendLine($"| Player car index      | {(snapshot.PlayerCarIdx >= 0 ? snapshot.PlayerCarIdx.ToString() : "unknown")} |");
            sb.AppendLine($"| Player incidents      | {snapshot.PlayerIncidentCount}x |");
            sb.AppendLine($"| Drivers tracked       | {snapshot.Drivers?.Count ?? 0} |");
            sb.AppendLine($"| Incidents in feed     | {snapshot.Incidents?.Count ?? 0} |");
            if (!string.IsNullOrEmpty(snapshot.TrackName))
            {
                var lenKm = snapshot.TrackLengthM > 0 ? $" · {snapshot.TrackLengthM / 1000f:F2} km" : "";
                sb.AppendLine($"| Track                 | {snapshot.TrackName} ({snapshot.TrackCategory ?? "Road"}){lenKm} |");
            }
            sb.AppendLine();

            sb.AppendLine("## Detection Layer Counts");
            sb.AppendLine();
            sb.AppendLine("Counts accumulate from iRacing connection until disconnect.");
            sb.AppendLine();
            sb.AppendLine($"| Layer | Description | Events |");
            sb.AppendLine($"|-------|-------------|--------|");
            sb.AppendLine($"| L1    | PlayerCarMyIncidentCount (player/focused car, 60 Hz, exact type) | {na(m.L1PlayerEvents)} |");
            sb.AppendLine($"| L2    | Physics G-force impact (all cars) | {na(m.L2PhysicsImpacts)} |");
            sb.AppendLine($"| L3    | Track surface off-track (all cars) | {na(m.L3OffTrackEvents)} |");
            sb.AppendLine($"| L4    | Session YAML deltas (all drivers, batched) | {na(m.L4YamlEvents)} |");
            sb.AppendLine($"| 0x    | Player contact/off-track without count change | {na(m.ZeroXEvents)} |");
            sb.AppendLine($"| **Total** | | **{m.TotalEvents}** |");
            sb.AppendLine();
            sb.AppendLine($"**YAML session updates**: {m.YamlUpdates}");
            sb.AppendLine($"**Last detection**: {(m.LastDetectionSessionTime >= 0 ? FormatSessionTime(m.LastDetectionSessionTime) : "none")}");

            if (m.TotalEvents == 0 && d.IrsdkConnected)
            {
                sb.AppendLine();
                sb.AppendLine("## Diagnostic Note");
                sb.AppendLine();
                sb.AppendLine("> iRacing is connected but zero events have been detected. Possible causes:");
                sb.AppendLine("> - Replay has not advanced past any incident points yet");
                sb.AppendLine("> - No car is focused (switch to cockpit/TV camera so `PlayerCarMyIncidentCount` has a target)");
                sb.AppendLine("> - Session YAML has not yet populated `ResultsPositions` (wait for session to tick)");
                sb.AppendLine("> - `irsdkEnableMem=1` is not set in iRacing `app.ini`");
            }
            else if (m.TotalEvents == 0 && !d.IrsdkConnected)
            {
                sb.AppendLine();
                sb.AppendLine("## Diagnostic Note");
                sb.AppendLine();
                sb.AppendLine("> iRacing SDK is not connected. Start iRacing and load a replay or live session.");
                sb.AppendLine("> Ensure `irsdkEnableMem=1` is set in `%USERPROFILE%\\Documents\\iRacing\\app.ini`.");
            }

            return sb.ToString();
        }

        private string BuildTasksMarkdown(MemoryBankSnapshot snapshot)
        {
            var markers = snapshot.ProjectMarkers ?? new ProjectMarkers();
            var builder = new StringBuilder();
            builder.AppendLine("# Tasks");
            builder.AppendLine();
            builder.AppendLine($"- Current Task ID: {markers.CurrentTaskId ?? "n/a"}");
            builder.AppendLine($"- Task Description: {markers.CurrentTaskDescription ?? "Sim Steward monitoring"}");
            builder.AppendLine($"- Complexity Level: {markers.ComplexityLevel ?? "unknown"}");
            builder.AppendLine($"- Plugin Mode: {snapshot.PluginMode ?? "Unknown"}");
            if (!string.IsNullOrEmpty(snapshot.TrackName))
            {
                var lenKm = snapshot.TrackLengthM > 0 ? $" · {snapshot.TrackLengthM / 1000f:F2} km" : "";
                builder.AppendLine($"- Track: {snapshot.TrackName} ({snapshot.TrackCategory ?? "Road"}){lenKm}");
            }
            builder.AppendLine($"- Player Incident Count: {snapshot.PlayerIncidentCount}");
            builder.AppendLine($"- Drivers Tracked: {snapshot.Drivers?.Count ?? 0}");
            builder.AppendLine($"- Incidents Tracked: {snapshot.Incidents?.Count ?? 0}");
            builder.AppendLine($"- Last Action: {markers.LastAction ?? "none"}");
            builder.AppendLine($"- Last Action At: {markers.LastActionTimestamp?.ToString("O") ?? "n/a"}");
            builder.AppendLine($"- Snapshot Recorded: {snapshot.Timestamp:O}");
            return builder.ToString();
        }

        private string BuildActiveContextMarkdown(MemoryBankSnapshot snapshot)
        {
            var markers = snapshot.ProjectMarkers ?? new ProjectMarkers();
            var builder = new StringBuilder();
            builder.AppendLine("# Active Context");
            builder.AppendLine();
            builder.AppendLine($"**Current Task**: {markers.CurrentTaskDescription ?? "Sim Steward incident tracking"}");
            builder.AppendLine($"**Complexity**: {markers.ComplexityLevel ?? "auto"}");
            builder.AppendLine($"**Plugin Mode**: {snapshot.PluginMode ?? "Unknown"}");
            builder.AppendLine($"**Session Time**: {snapshot.CurrentSessionTimeFormatted}");
            if (!string.IsNullOrEmpty(snapshot.TrackName))
            {
                var lenKm = snapshot.TrackLengthM > 0 ? $" · {snapshot.TrackLengthM / 1000f:F2} km" : "";
                builder.AppendLine($"**Track**: {snapshot.TrackName} ({snapshot.TrackCategory ?? "Road"}){lenKm}");
            }
            builder.AppendLine($"**Player Car Index**: {snapshot.PlayerCarIdx}");
            builder.AppendLine($"**Player Incident Count**: {snapshot.PlayerIncidentCount}");
            if (snapshot.Incidents != null && snapshot.Incidents.Count > 0)
            {
                var top = snapshot.Incidents[0];
                builder.AppendLine($"**Most Recent Incident**: {top.SessionTimeFormatted} / {top.Type ?? "unknown"} / delta {top.Delta}");
            }
            builder.AppendLine($"**Last Sync**: {snapshot.Timestamp:O}");
            return builder.ToString();
        }

        private string BuildProgressMarkdown(MemoryBankSnapshot snapshot)
        {
            var markers = snapshot.ProjectMarkers ?? new ProjectMarkers();
            var builder = new StringBuilder();
            builder.AppendLine("# Progress");
            builder.AppendLine();
            builder.AppendLine($"- Snapshot Time: {snapshot.Timestamp:O}");
            builder.AppendLine($"- Incidents Captured: {snapshot.Incidents?.Count ?? 0}");
            builder.AppendLine($"- Drivers Tracked: {snapshot.Drivers?.Count ?? 0}");
            builder.AppendLine($"- Last Action: {markers.LastAction ?? "none"}");
            builder.AppendLine($"- Last Action At: {markers.LastActionTimestamp?.ToString("O") ?? "n/a"}");
            builder.AppendLine($"- Notes: {markers.Notes ?? "none"}");
            return builder.ToString();
        }

        private void WriteAtomic(string fileName, string content)
        {
            var targetPath = Path.Combine(_rootPath, fileName);
            var tempPath = Path.Combine(_rootPath, $"{fileName}.{Guid.NewGuid():N}.tmp");

            File.WriteAllText(tempPath, content, Encoding.UTF8);
            File.Copy(tempPath, targetPath, true);
            File.Delete(tempPath);
        }
    }
}
