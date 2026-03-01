using System;
using System.Collections.Generic;
using System.IO;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Entry produced by every Write call; consumed by the event stream.
    /// </summary>
    public class LogEntry
    {
        public string Level { get; set; }
        public string Message { get; set; }
        public string Timestamp { get; set; }
    }

    /// <summary>
    /// File-based logger for plugin operation. Writes to {basePath}/plugin.log.
    /// Also maintains a bounded in-memory ring buffer and fires LogWritten for
    /// real-time streaming to connected dashboards.
    /// Thread-safe.
    /// </summary>
    public class PluginLogger
    {
        private const int RingBufferCapacity = 200;
        private const long MaxLogBytes = 5 * 1024 * 1024; // 5 MB per file
        private const int  MaxLogFiles = 3;

        private readonly string _logPath;
        private readonly object _lock = new object();

        // Ring buffer: newest entries appended; oldest dropped when full.
        private readonly Queue<LogEntry> _ring = new Queue<LogEntry>();

        /// <summary>
        /// Raised on every log write (from the writing thread).
        /// Subscribers must not throw; exceptions are swallowed.
        /// </summary>
        public event Action<LogEntry> LogWritten;

        public PluginLogger(string basePath)
        {
            _logPath = string.IsNullOrEmpty(basePath) ? null : Path.Combine(basePath, "plugin.log");
        }

        public void Info(string message) => Write("INFO", message);
        public void Warn(string message) => Write("WARN", message);

        public void Error(string message, Exception ex = null)
        {
            var text = ex != null ? $"{message} | {ex.GetType().Name}: {ex.Message}" : message;
            Write("ERROR", text);
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

        private void Write(string level, string message)
        {
            var entry = new LogEntry
            {
                Level = level,
                Message = message,
                Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
            };

            lock (_lock)
            {
                // Append to ring buffer (drop oldest when at capacity)
                _ring.Enqueue(entry);
                if (_ring.Count > RingBufferCapacity)
                    _ring.Dequeue();

                // Write to file (with rotation)
                WriteToFile($"{entry.Timestamp} [{level}] {message}{Environment.NewLine}");
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
    }
}
