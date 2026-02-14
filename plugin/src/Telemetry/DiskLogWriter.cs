using System;
using System.IO;
using System.Threading;

namespace SimStewardPlugin.Telemetry
{
    /// <summary>
    /// Writes telemetry log lines to a file. Same format as Loki (key=value per line).
    /// Thread-safe, best-effort; no retries. Optional daily rotation by filename.
    /// </summary>
    public sealed class DiskLogWriter : IDisposable
    {
        private readonly object _gate = new object();
        private readonly string _baseDirectory;
        private string _currentDateString;
        private StreamWriter _writer;
        private bool _disposed;

        private static readonly string DefaultDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sim Steward",
            "logs");

        public DiskLogWriter(bool enabled, string directory)
        {
            if (!enabled)
            {
                _baseDirectory = null;
                _currentDateString = null;
                _writer = null;
                return;
            }

            _baseDirectory = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : directory.Trim();
            _currentDateString = DateTime.UtcNow.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
            _writer = null;
        }

        public void WriteLine(string message)
        {
            if (string.IsNullOrEmpty(message) || _baseDirectory == null)
            {
                return;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                try
                {
                    string today = DateTime.UtcNow.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
                    if (_writer == null || today != _currentDateString)
                    {
                        _writer?.Dispose();
                        _writer = null;
                        _currentDateString = today;
                        if (!Directory.Exists(_baseDirectory))
                        {
                            Directory.CreateDirectory(_baseDirectory);
                        }
                        string path = Path.Combine(_baseDirectory, $"simsteward-{_currentDateString}.log");
                        _writer = new StreamWriter(path, true, System.Text.Encoding.UTF8)
                        {
                            AutoFlush = true
                        };
                    }

                    _writer.WriteLine(message);
                }
                catch
                {
                    // Best-effort; do not throw or retry.
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                try
                {
                    _writer?.Dispose();
                }
                catch
                {
                    // Ignore on dispose.
                }
                _writer = null;
            }
        }
    }
}
