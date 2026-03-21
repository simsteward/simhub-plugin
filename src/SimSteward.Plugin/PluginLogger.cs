using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Entry produced by every Write call; consumed by the event stream.
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

        [JsonProperty("session_id", NullValueHandling = NullValueHandling.Ignore)]
        public string SessionId { get; set; }

        [JsonProperty("session_seq", NullValueHandling = NullValueHandling.Ignore)]
        public string SessionSeq { get; set; }

        [JsonProperty("domain", NullValueHandling = NullValueHandling.Ignore)]
        public string Domain { get; set; }

        [JsonProperty("replay_frame", NullValueHandling = NullValueHandling.Ignore)]
        public int? ReplayFrame { get; set; }

        [JsonProperty("incident_id", NullValueHandling = NullValueHandling.Ignore)]
        public string IncidentId { get; set; }

        [JsonProperty("testing", NullValueHandling = NullValueHandling.Ignore)]
        public string Testing { get; set; }

        [JsonProperty("test_tag", NullValueHandling = NullValueHandling.Ignore)]
        public string TestTag { get; set; }
    }

    /// <summary>
    /// File-based logger. Writes plugin.log and plugin-structured.jsonl.
    /// Thread-safe.
    /// </summary>
    public class PluginLogger
    {
        private const int RingBufferCapacity = 10000;
        private const long MaxLogBytes = 5 * 1024 * 1024;
        private const int  MaxLogFiles = 3;

        private readonly string _logPath;
        private readonly string _jsonLogPath;
        private readonly object _lock = new object();
        private readonly Queue<LogEntry> _ring = new Queue<LogEntry>();
        private Func<(string sessionId, string sessionSeq, int replayFrame)> _getSpine;

        public event Action<LogEntry> LogWritten;

        public PluginLogger(string basePath, bool isDebugMode = false)
        {
            _logPath = string.IsNullOrEmpty(basePath) ? null : Path.Combine(basePath, "plugin.log");
            _jsonLogPath = string.IsNullOrEmpty(basePath) ? null : Path.Combine(basePath, "plugin-structured.jsonl");
            IsDebugMode = isDebugMode;
        }

        public bool IsDebugMode { get; }
        public string StructuredLogPath => _jsonLogPath;

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
            if (!IsDebugMode) return;
            Structured("DEBUG", component, eventType, message, fields);
        }

        public void Emit(LogEntry entry) => Write(entry);

        public List<LogEntry> GetTail(int count)
        {
            lock (_lock)
            {
                var all = new List<LogEntry>(_ring);
                int skip = all.Count - count;
                return skip > 0 ? all.GetRange(skip, count) : all;
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

            WriteJsonLine(entry);

            lock (_lock)
            {
                _ring.Enqueue(entry);
                if (_ring.Count > RingBufferCapacity)
                    _ring.Dequeue();
                WriteToFile($"{entry.Timestamp} [{entry.Level}] {entry.Message}{Environment.NewLine}");
            }

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

        private void WriteJsonLine(LogEntry entry)
        {
            if (string.IsNullOrEmpty(_jsonLogPath) || entry == null) return;
            try
            {
                var dir = Path.GetDirectoryName(_jsonLogPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                if (File.Exists(_jsonLogPath) && new FileInfo(_jsonLogPath).Length > MaxLogBytes)
                    RotateJsonLogs();
                File.AppendAllText(_jsonLogPath, JsonConvert.SerializeObject(entry) + "\n", System.Text.Encoding.UTF8);
            }
            catch { }
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
    }
}
