using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SimStewardPlugin.Telemetry
{
    public sealed class LokiExporter : IDisposable
    {
        private sealed class QueuedLine
        {
            public long TimestampNs;
            public string Message;
            public int EstimatedBytes;
            public bool IsException;
        }

        private static bool IsOtlpEndpoint(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
            {
                return false;
            }

            string path = uri.AbsolutePath ?? string.Empty;
            return path.EndsWith("/otlp/v1/logs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/v1/logs", StringComparison.OrdinalIgnoreCase)
                || path.IndexOf("/otlp/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private readonly object _gate = new object();
        private readonly Queue<QueuedLine> _queue = new Queue<QueuedLine>();
        private Timer _timer;

        private TelemetryConfig _config;
        private int _queuedBytes;

        public DateTime LastSuccessUtc { get; private set; } = DateTime.MinValue;
        public DateTime LastAttemptUtc { get; private set; } = DateTime.MinValue;
        public string LastError { get; private set; } = string.Empty;
        private long _droppedLinesTotal;
        private long _sentLinesTotal;
        private long _sentBatchesTotal;
        private long _sentExceptionLinesTotal;
        private long _sentBytesTotal;

        public long DroppedLinesTotal => Interlocked.Read(ref _droppedLinesTotal);
        public long SentLinesTotal => Interlocked.Read(ref _sentLinesTotal);
        public long SentBatchesTotal => Interlocked.Read(ref _sentBatchesTotal);
        public long SentExceptionLinesTotal => Interlocked.Read(ref _sentExceptionLinesTotal);
        public long SentBytesTotal => Interlocked.Read(ref _sentBytesTotal);

        public LokiExporter(TelemetryConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _config.Normalize();
        }

        public void ApplyConfig(TelemetryConfig config)
        {
            if (config == null)
            {
                return;
            }

            config.Normalize();
            lock (_gate)
            {
                _config = config;

                // Recreate timer with new interval.
                _timer?.Dispose();
                _timer = null;

                if (_config.Enabled && _config.HasLokiCredentials() && _config.FlushIntervalSeconds > 0)
                {
                    _timer = new Timer(_ => FlushSafe(), null, TimeSpan.FromSeconds(_config.FlushIntervalSeconds), TimeSpan.FromSeconds(_config.FlushIntervalSeconds));
                }
            }
        }

        public void Start()
        {
            ApplyConfig(_config);
        }

        public void Enqueue(string message)
        {
            Enqueue(message, false);
        }

        public void Enqueue(string message, bool isException)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            TelemetryConfig cfg;
            lock (_gate)
            {
                cfg = _config;
            }

            if (!cfg.Enabled || !cfg.HasLokiCredentials())
            {
                return;
            }

            long tsNs = ToUnixNs(DateTime.UtcNow);
            int estimatedBytes = EstimateUtf8Bytes(message) + 32;

            lock (_gate)
            {
                if (cfg.MaxQueueBytes == 0)
                {
                    Interlocked.Increment(ref _droppedLinesTotal);
                    return;
                }

                while (_queuedBytes + estimatedBytes > cfg.MaxQueueBytes && _queue.Count > 0)
                {
                    QueuedLine dropped = _queue.Dequeue();
                    _queuedBytes -= dropped.EstimatedBytes;
                    Interlocked.Increment(ref _droppedLinesTotal);
                }

                if (_queuedBytes + estimatedBytes > cfg.MaxQueueBytes)
                {
                    Interlocked.Increment(ref _droppedLinesTotal);
                    return;
                }

                _queue.Enqueue(new QueuedLine { TimestampNs = tsNs, Message = message, EstimatedBytes = estimatedBytes, IsException = isException });
                _queuedBytes += estimatedBytes;
            }
        }

        public void FlushSafe()
        {
            _ = Task.Run(async () => await FlushAsync().ConfigureAwait(false));
        }

        public async Task FlushAsync()
        {
            TelemetryConfig cfg;
            List<QueuedLine> batch;

            lock (_gate)
            {
                cfg = _config;
                if (!cfg.Enabled || !cfg.HasLokiCredentials() || _queue.Count == 0)
                {
                    return;
                }

                // Small batches to stay under typical MTU and Loki limits.
                batch = new List<QueuedLine>(Math.Min(_queue.Count, 50));
                while (_queue.Count > 0 && batch.Count < 50)
                {
                    QueuedLine line = _queue.Dequeue();
                    _queuedBytes -= line.EstimatedBytes;
                    batch.Add(line);
                }
            }

            LastAttemptUtc = DateTime.UtcNow;

            try
            {
                bool useOtlp = IsOtlpEndpoint(cfg.LokiUrl);
                string payload = useOtlp ? BuildOtlpPushPayload(cfg, batch) : BuildLokiPushPayload(cfg, batch);

                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                var request = (HttpWebRequest)WebRequest.Create(cfg.LokiUrl);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.UserAgent = "SimStewardPlugin/telemetry";
                request.Timeout = 5000;
                request.ReadWriteTimeout = 5000;
                request.ContentLength = bytes.Length;

                // Support pre-encoded Basic token: when username is empty, API key is the raw Base64 value (stick "Basic " in front).
                string authHeader = string.IsNullOrWhiteSpace(cfg.LokiUsername)
                    ? "Basic " + (cfg.LokiApiKey ?? string.Empty).Trim()
                    : "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cfg.LokiUsername}:{cfg.LokiApiKey}"));
                request.Headers[HttpRequestHeader.Authorization] = authHeader;

                using (Stream stream = await request.GetRequestStreamAsync().ConfigureAwait(false))
                {
                    await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                }

                using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                {
                    if (response.StatusCode < HttpStatusCode.OK || response.StatusCode >= HttpStatusCode.MultipleChoices)
                    {
                        string body = string.Empty;
                        try
                        {
                            using (var reader = new StreamReader(response.GetResponseStream() ?? Stream.Null))
                            {
                                body = await reader.ReadToEndAsync().ConfigureAwait(false);
                            }
                        }
                        catch
                        {
                            body = string.Empty;
                        }

                        string bodyPreview = string.IsNullOrWhiteSpace(body) ? string.Empty : (" " + body.Trim());
                        if (bodyPreview.Length > 200)
                        {
                            bodyPreview = bodyPreview.Substring(0, 200) + "...";
                        }
                        LastError = $"HTTP {(int)response.StatusCode} {response.StatusDescription}{bodyPreview}".Trim();

                        // Put batch back so we can retry later (bounded by queue cap).
                        lock (_gate)
                        {
                            foreach (QueuedLine line in batch)
                            {
                                _queue.Enqueue(line);
                                _queuedBytes += line.EstimatedBytes;
                            }
                        }

                        return;
                    }
                }

                LastSuccessUtc = DateTime.UtcNow;
                LastError = string.Empty;
                int exceptionLinesInBatch = 0;
                for (int i = 0; i < batch.Count; i++)
                {
                    if (batch[i].IsException)
                    {
                        exceptionLinesInBatch++;
                    }
                }

                Interlocked.Add(ref _sentLinesTotal, batch.Count);
                Interlocked.Increment(ref _sentBatchesTotal);
                Interlocked.Add(ref _sentBytesTotal, bytes.Length);
                if (exceptionLinesInBatch > 0)
                {
                    Interlocked.Add(ref _sentExceptionLinesTotal, exceptionLinesInBatch);
                }
            }
            catch (Exception ex)
            {
                if (ex is WebException webEx)
                {
                    string body = string.Empty;
                    try
                    {
                        if (webEx.Response is HttpWebResponse resp)
                        {
                            using (var reader = new StreamReader(resp.GetResponseStream() ?? Stream.Null))
                            {
                                body = await reader.ReadToEndAsync().ConfigureAwait(false);
                            }
                        }
                    }
                    catch
                    {
                        body = string.Empty;
                    }

                    int statusCode = 0;
                    if (webEx.Response is HttpWebResponse httpResponse)
                    {
                        statusCode = (int)httpResponse.StatusCode;
                    }

                    string bodyPreview = string.IsNullOrWhiteSpace(body) ? string.Empty : (" " + body.Trim());
                    if (bodyPreview.Length > 200)
                    {
                        bodyPreview = bodyPreview.Substring(0, 200) + "...";
                    }

                    LastError = statusCode > 0
                        ? $"HTTP {statusCode} {webEx.Message}{bodyPreview}".Trim()
                        : (string.IsNullOrWhiteSpace(body) ? webEx.Message : $"{webEx.Message}{bodyPreview}").Trim();
                }
                else
                {
                    LastError = ex.Message;
                }

                lock (_gate)
                {
                    foreach (QueuedLine line in batch)
                    {
                        _queue.Enqueue(line);
                        _queuedBytes += line.EstimatedBytes;
                    }
                }
            }
        }

        private static string BuildLokiPushPayload(TelemetryConfig cfg, List<QueuedLine> lines)
        {
            // Loki push API expects: {"streams":[{"stream":{...labels...},"values":[["<ns>","<line>"], ...]}]}
            var sb = new StringBuilder(lines.Count * 96);
            sb.Append("{\"streams\":[{\"stream\":{");
            AppendJsonProp(sb, "app", "simsteward");
            sb.Append(",");
            AppendJsonProp(sb, "device_id", cfg.DeviceId);
            sb.Append(",");
            AppendJsonProp(sb, "install_id", cfg.InstallId);
            sb.Append(",");
            AppendJsonProp(sb, "plugin_version", cfg.PluginVersion);
            sb.Append(",");
            AppendJsonProp(sb, "schema", cfg.SchemaVersion);
            sb.Append("},\"values\":[");

            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(",");
                }

                sb.Append("[\"");
                sb.Append(lines[i].TimestampNs);
                sb.Append("\",\"");
                sb.Append(JsonEscape(lines[i].Message));
                sb.Append("\"]");
            }

            sb.Append("]}]}" );
            return sb.ToString();
        }

        private static string BuildOtlpPushPayload(TelemetryConfig cfg, List<QueuedLine> lines)
        {
            // OTLP/HTTP JSON payload for logs endpoint (/otlp/v1/logs).
            var sb = new StringBuilder(lines.Count * 160);
            sb.Append("{\"resourceLogs\":[{\"resource\":{\"attributes\":[");
            sb.Append("{\"key\":\"service.name\",\"value\":{\"stringValue\":\"simsteward\"}}");
            sb.Append(",{\"key\":\"device_id\",\"value\":{\"stringValue\":\"");
            sb.Append(JsonEscape(cfg.DeviceId ?? string.Empty));
            sb.Append("\"}}");
            sb.Append(",{\"key\":\"install_id\",\"value\":{\"stringValue\":\"");
            sb.Append(JsonEscape(cfg.InstallId ?? string.Empty));
            sb.Append("\"}}");
            sb.Append(",{\"key\":\"plugin_version\",\"value\":{\"stringValue\":\"");
            sb.Append(JsonEscape(cfg.PluginVersion ?? string.Empty));
            sb.Append("\"}}");
            sb.Append(",{\"key\":\"schema\",\"value\":{\"stringValue\":\"");
            sb.Append(JsonEscape(cfg.SchemaVersion ?? "1"));
            sb.Append("\"}}");
            sb.Append("]},\"scopeLogs\":[{\"scope\":{\"name\":\"simsteward.plugin\",\"version\":\"1.0.0\"},\"logRecords\":[");

            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(",");
                }

                int severityNumber = lines[i].IsException ? 17 : 9;
                string severityText = lines[i].IsException ? "Error" : "Info";
                sb.Append("{\"timeUnixNano\":\"");
                sb.Append(lines[i].TimestampNs);
                sb.Append("\",\"observedTimeUnixNano\":\"");
                sb.Append(lines[i].TimestampNs);
                sb.Append("\",\"severityNumber\":");
                sb.Append(severityNumber);
                sb.Append(",\"severityText\":\"");
                sb.Append(severityText);
                sb.Append("\",\"body\":{\"stringValue\":\"");
                sb.Append(JsonEscape(lines[i].Message));
                sb.Append("\"}}");
            }

            sb.Append("]}]}]}");
            return sb.ToString();
        }

        private static void AppendJsonProp(StringBuilder sb, string key, string value)
        {
            sb.Append("\"");
            sb.Append(JsonEscape(key));
            sb.Append("\":\"");
            sb.Append(JsonEscape(value ?? string.Empty));
            sb.Append("\"");
        }

        private static string JsonEscape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(value.Length + 16);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u");
                            sb.Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }

            return sb.ToString();
        }

        private static long ToUnixNs(DateTime utc)
        {
            DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            long ticks = (utc.ToUniversalTime() - epoch).Ticks; // 100ns
            return ticks * 100; // ns
        }

        private static int EstimateUtf8Bytes(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return 0;
            }

            return Encoding.UTF8.GetByteCount(value);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _timer?.Dispose();
                _timer = null;
            }
            try
            {
                FlushAsync().Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Best-effort on shutdown; do not throw.
            }
        }
    }
}
