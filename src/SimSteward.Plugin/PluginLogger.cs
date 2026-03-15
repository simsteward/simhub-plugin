using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Entry produced by every Write call; consumed by the event stream.
    /// Uses camelCase for JSON so the dashboard (e.level, e.message) receives correct keys.
    /// </summary>
    public class LogEntry
    {
        [JsonProperty("level")]    public string Level    { get; set; }
        [JsonProperty("message")]  public string Message  { get; set; }
        [JsonProperty("timestamp")] public string Timestamp { get; set; }

        [JsonProperty("component", NullValueHandling = NullValueHandling.Ignore)]
        public string Component { get; set; }

        [JsonProperty("event", NullValueHandling = NullValueHandling.Ignore)]
        public string Event { get; set; }

        [JsonProperty("fields", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, object> Fields { get; set; }

        /// <summary>Tagging spine: iRacing SubSessionID as string (or session_seq fallback). Do not use as Loki label.</summary>
        [JsonProperty("session_id", NullValueHandling = NullValueHandling.Ignore)]
        public string SessionId { get; set; }
        /// <summary>Tagging spine: human-readable {trackName}_{yyyyMMdd}. Do not use as Loki label.</summary>
        [JsonProperty("session_seq", NullValueHandling = NullValueHandling.Ignore)]
        public string SessionSeq { get; set; }
        /// <summary>Tagging spine: event category — lifecycle, incident, replay, weather, telemetry, roster, action, session.</summary>
        [JsonProperty("domain", NullValueHandling = NullValueHandling.Ignore)]
        public string Domain { get; set; }
        /// <summary>Tagging spine: ReplayFrameNumEnd at log time (0 when not in replay).</summary>
        [JsonProperty("replay_frame", NullValueHandling = NullValueHandling.Ignore)]
        public int? ReplayFrame { get; set; }
        /// <summary>Tagging spine: incident_detected Id; also set on events within 2s of an incident. Do not use as Loki label.</summary>
        [JsonProperty("incident_id", NullValueHandling = NullValueHandling.Ignore)]
        public string IncidentId { get; set; }

        /// <summary>When "true", marks this line as test data. Use in LogQL: testing = "true" or testing != "true" to exclude. Never set in production.</summary>
        [JsonProperty("testing", NullValueHandling = NullValueHandling.Ignore)]
        public string Testing { get; set; }

        /// <summary>Stable tag for test harness (e.g. "grafana-harness"). Filter with test_tag = "grafana-harness".</summary>
        [JsonProperty("test_tag", NullValueHandling = NullValueHandling.Ignore)]
        public string TestTag { get; set; }
    }

    /// <summary>
    /// File-based logger for plugin operation. Writes to {basePath}/plugin.log and
    /// {basePath}/plugin-structured.jsonl (NDJSON for Alloy/Loki file-tail).
    /// Also maintains a bounded in-memory ring buffer and fires LogWritten for
    /// real-time streaming to connected dashboards.
    /// Thread-safe.
    /// </summary>
    public class PluginLogger
    {
        /// <summary>Recent tail for new clients; file has full history. No drop in normal use.</summary>
        private const int RingBufferCapacity = 10000;
        private const int IncidentRingCapacity = 200;
        private const long MaxLogBytes = 5 * 1024 * 1024; // 5 MB per file
        private const int  MaxLogFiles = 3;

        private readonly string _logPath;
        private readonly string _jsonLogPath;
        private readonly object _lock = new object();

        // Ring buffer: newest entries appended; oldest dropped when full.
        private readonly Queue<LogEntry> _ring = new Queue<LogEntry>();
        private readonly Queue<LogEntry> _incidentRing = new Queue<LogEntry>();
        private readonly Func<HashSet<string>> _getOmittedLevels;
        private readonly Func<HashSet<string>> _getOmittedEvents;
        private Func<(string sessionId, string sessionSeq, int replayFrame)> _getSpine;

        /// <summary>
        /// Raised on every log write (from the writing thread).
        /// Subscribers must not throw; exceptions are swallowed.
        /// </summary>
        public event Action<LogEntry> LogWritten;

        /// <param name="basePath">Plugin data directory for plugin.log.</param>
        /// <param name="isDebugMode">When true, Debug() calls are emitted.</param>
        /// <param name="getOmittedLevels">Optional. When non-null, entries whose Level is in the returned set are not written. Pass null for unencumbered streaming (Loki + dashboard get full stream; filter only in dashboard UI).</param>
        /// <param name="getOmittedEvents">Optional. When non-null, entries whose Event is in the returned set are not written. Pass null for unencumbered streaming.</param>
        public PluginLogger(string basePath, bool isDebugMode = false, Func<HashSet<string>> getOmittedLevels = null, Func<HashSet<string>> getOmittedEvents = null)
        {
            _logPath = string.IsNullOrEmpty(basePath) ? null : Path.Combine(basePath, "plugin.log");
            _jsonLogPath = string.IsNullOrEmpty(basePath) ? null : Path.Combine(basePath, "plugin-structured.jsonl");
            IsDebugMode = isDebugMode;
            _getOmittedLevels = getOmittedLevels;
            _getOmittedEvents = getOmittedEvents;
            // #region agent log
            DebugSessionLog("A", "PluginLogger.ctor", "init", new { jsonLogPath = _jsonLogPath ?? "(null)", basePath = basePath ?? "(null)" });
            // #endregion
        }

        public bool IsDebugMode { get; }

        /// <summary>Path to plugin-structured.jsonl (null when basePath was empty). For settings UI / Alloy mount.</summary>
        public string StructuredLogPath => _jsonLogPath;

        /// <summary>Set a provider for the tagging spine (session_id, session_seq, replay_frame). Called on each Write to fill spine when not already set.</summary>
        public void SetSpineProvider(Func<(string sessionId, string sessionSeq, int replayFrame)> getSpine)
        {
            _getSpine = getSpine;
        }

        public void Info(string message) => Structured("INFO", null, null, message);
        public void Warn(string message) => Structured("WARN", null, null, message);

        public void Error(string message, Exception ex = null)
        {
            var text = ex != null ? $"{message} | {ex.GetType().Name}: {ex.Message}" : message;
            Structured("ERROR", null, null, text);
        }

        public void Structured(string level, string component, string eventType, string message, Dictionary<string, object> fields = null, string domain = null, string incidentId = null)
        {
            var entry = new LogEntry
            {
                Level = level,
                Component = component,
                Event = eventType,
                Message = message,
                Fields = fields,
                Domain = domain,
                IncidentId = incidentId
            };
            Write(entry);
        }

        public void Debug(string message, string component = null, string eventType = null, Dictionary<string, object> fields = null)
        {
            if (!IsDebugMode)
                return;
            Structured("DEBUG", component, eventType, message, fields);
        }

        /// <summary>Emit a pre-built log entry (e.g. from IncidentTracker). Timestamp is set here.</summary>
        public void Emit(LogEntry entry)
        {
            Write(entry);
        }

        /// <summary>Returns a snapshot of the most recent entries (oldest first).</summary>
        public List<LogEntry> GetTail(int count)
        {
            lock (_lock)
            {
                var all = new List<LogEntry>(_ring);
                int skip = all.Count - count;
                return skip > 0 ? all.GetRange(skip, count) : all;
            }
        }

        /// <summary>Returns tail (oldest first) plus up to maxIncidents incident_detected entries from the ring that are not already in the tail, so new dashboard clients always see recent incidents even when high-volume events fill the last N slots.</summary>
        public List<LogEntry> GetTailIncludingIncidents(int tailCount, int maxIncidents)
        {
            lock (_lock)
            {
                var all = new List<LogEntry>(_ring);
                int skip = all.Count - tailCount;
                var tail = skip > 0 ? all.GetRange(skip, tailCount) : all;
                var incidentIdsInTail = new HashSet<string>(StringComparer.Ordinal);
                foreach (var e in tail)
                    if (!string.IsNullOrEmpty(e.IncidentId) && string.Equals(e.Event, "incident_detected", StringComparison.OrdinalIgnoreCase))
                        incidentIdsInTail.Add(e.IncidentId);
                var extra = new List<LogEntry>();
                foreach (var e in _incidentRing)
                {
                    if (!string.Equals(e?.Event, "incident_detected", StringComparison.OrdinalIgnoreCase) || e.IncidentId == null || incidentIdsInTail.Contains(e.IncidentId))
                        continue;
                    extra.Add(e);
                }
                if (extra.Count > maxIncidents)
                    extra = extra.GetRange(extra.Count - maxIncidents, maxIncidents);
                if (extra.Count == 0)
                    return tail;
                var combined = new List<LogEntry>(tail);
                combined.AddRange(extra);
                combined.Sort((a, b) => string.CompareOrdinal(a?.Timestamp ?? "", b?.Timestamp ?? ""));
                return combined;
            }
        }

        private void Write(LogEntry entry)
        {
            if (entry == null) return;
            entry.Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

            if (_getSpine != null)
            {
                try
                {
                    var s = _getSpine();
                    if (string.IsNullOrEmpty(entry.SessionId) && !string.IsNullOrEmpty(s.sessionId))
                        entry.SessionId = s.sessionId;
                    if (string.IsNullOrEmpty(entry.SessionSeq) && !string.IsNullOrEmpty(s.sessionSeq))
                        entry.SessionSeq = s.sessionSeq;
                    if (!entry.ReplayFrame.HasValue)
                        entry.ReplayFrame = s.replayFrame;
                }
                catch { }
            }

            // Grafana-first: always write structured JSON, regardless of omit filters.
            WriteJsonLine(entry);

            if (_getOmittedLevels != null)
            {
                var omitted = _getOmittedLevels();
                if (omitted != null && omitted.Count > 0 && !string.IsNullOrEmpty(entry.Level) &&
                    omitted.Contains(entry.Level.Trim()))
                {
                    // #region agent log
                    DebugSessionLog("C", "PluginLogger.Write", "dropped_omit_level", new { reason = "omitted_level", level = entry.Level, eventType = entry.Event });
                    if (string.Equals(entry.Event, "incident_detected", StringComparison.OrdinalIgnoreCase))
                        AgentDebugLog.WriteB0C27E("H3", "PluginLogger.Write", "incident_dropped_omit_level", new { level = entry.Level });
                    // #endregion
                    return;
                }
            }
            if (_getOmittedEvents != null && !string.IsNullOrEmpty(entry.Event))
            {
                var omitted = _getOmittedEvents();
                if (omitted != null && omitted.Count > 0 && omitted.Contains(entry.Event.Trim()))
                {
                    // #region agent log
                    DebugSessionLog("C", "PluginLogger.Write", "dropped_omit_event", new { reason = "omitted_event", eventType = entry.Event });
                    if (string.Equals(entry.Event, "incident_detected", StringComparison.OrdinalIgnoreCase))
                        AgentDebugLog.WriteB0C27E("H3", "PluginLogger.Write", "incident_dropped_omit_event", new { eventName = entry.Event });
                    // #endregion
                    return;
                }
            }

            lock (_lock)
            {
                // Append to ring buffer (drop oldest when at capacity)
                _ring.Enqueue(entry);
                if (_ring.Count > RingBufferCapacity)
                    _ring.Dequeue();

                if (string.Equals(entry.Event, "incident_detected", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(entry.IncidentId))
                {
                    _incidentRing.Enqueue(entry);
                    if (_incidentRing.Count > IncidentRingCapacity)
                        _incidentRing.Dequeue();
                }

                // Write to file (with rotation)
                WriteToFile($"{entry.Timestamp} [{entry.Level}] {entry.Message}{Environment.NewLine}");
            }

            // Fire event outside the lock to avoid deadlock risk
            try { LogWritten?.Invoke(entry); } catch { }
        }

        private void WriteToFile(string line)
        {
            if (string.IsNullOrEmpty(_logPath)) return;
            try
            {
                var dir = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                if (File.Exists(_logPath) && new FileInfo(_logPath).Length > MaxLogBytes)
                    RotateLogs();
                File.AppendAllText(_logPath, line, System.Text.Encoding.UTF8);
            }
            catch { }
        }

        private void RotateLogs()
        {
            try
            {
                for (int i = MaxLogFiles - 1; i >= 1; i--)
                {
                    var older = $"{_logPath}.{i}";
                    var newer = i == 1 ? _logPath : $"{_logPath}.{i - 1}";
                    if (File.Exists(newer))
                        File.Copy(newer, older, overwrite: true);
                }
                File.WriteAllText(_logPath, string.Empty, System.Text.Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>Append one NDJSON line (full LogEntry) for Alloy/Loki file-tail. Same rotation limits as plugin.log.</summary>
        private void WriteJsonLine(LogEntry entry)
        {
            if (string.IsNullOrEmpty(_jsonLogPath) || entry == null)
            {
                // #region agent log
                DebugSessionLog("A", "PluginLogger.WriteJsonLine", "skip_path_null", new { jsonLogPath = _jsonLogPath ?? "(null)" });
                if (entry != null && string.Equals(entry.Event, "incident_detected", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(_jsonLogPath))
                    AgentDebugLog.WriteB0C27E("H4", "PluginLogger.WriteJsonLine", "incident_structured_path_null", new { });
                // #endregion
                return;
            }
            // #region agent log
            if (System.Threading.Interlocked.Increment(ref _debugEntryLogCount) <= 3) DebugSessionLog("D", "PluginLogger.WriteJsonLine", "entry", new { jsonLogPath = _jsonLogPath });
            // #endregion
            try
            {
                var dir = Path.GetDirectoryName(_jsonLogPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                if (File.Exists(_jsonLogPath) && new FileInfo(_jsonLogPath).Length > MaxLogBytes)
                    RotateJsonLogs();
                File.AppendAllText(_jsonLogPath, JsonConvert.SerializeObject(entry) + "\n", System.Text.Encoding.UTF8);
                // #region agent log
                if (!_debugLoggedFirstWrite) { _debugLoggedFirstWrite = true; DebugSessionLog("D", "PluginLogger.WriteJsonLine", "written", new { jsonLogPath = _jsonLogPath, written = true }); }
                if (string.Equals(entry.Event, "incident_detected", StringComparison.OrdinalIgnoreCase))
                    AgentDebugLog.WriteB0C27E("H4", "PluginLogger.WriteJsonLine", "incident_written_to_structured_file", new { path = _jsonLogPath });
                // #endregion
            }
            catch (Exception ex)
            {
                // #region agent log
                DebugSessionLog("D", "PluginLogger.WriteJsonLine", "exception", new { jsonLogPath = _jsonLogPath, error = ex.Message });
                // #endregion
            }
        }

        private void RotateJsonLogs()
        {
            try
            {
                for (int i = MaxLogFiles - 1; i >= 1; i--)
                {
                    var older = $"{_jsonLogPath}.{i}";
                    var newer = i == 1 ? _jsonLogPath : $"{_jsonLogPath}.{i - 1}";
                    if (File.Exists(newer))
                        File.Copy(newer, older, overwrite: true);
                }
                File.WriteAllText(_jsonLogPath, string.Empty, System.Text.Encoding.UTF8);
            }
            catch { }
        }

        // #region agent log
        private static readonly object _debugLogLock = new object();
        private static bool _debugLoggedFirstWrite;
        private static int _debugEntryLogCount;
        private static void DebugSessionLog(string hypothesisId, string location, string message, object data)
        {
            var path = Environment.GetEnvironmentVariable("SIMSTEWARD_DEBUG_LOG_PATH");
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var payload = new Dictionary<string, object>
                {
                    ["sessionId"] = "2291d4",
                    ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["location"] = location,
                    ["message"] = message,
                    ["data"] = data,
                    ["hypothesisId"] = hypothesisId
                };
                var line = JsonConvert.SerializeObject(payload) + Environment.NewLine;
                lock (_debugLogLock) { File.AppendAllText(path, line, System.Text.Encoding.UTF8); }
            }
            catch { }
        }
        // #endregion
    }
}
