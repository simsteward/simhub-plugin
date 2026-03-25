using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Thin HTTP client for Loki <c>/loki/api/v1/query_range</c> used by the data-capture suite
    /// to verify structured log events reached Loki within the expected window.
    /// </summary>
    public static class LokiQueryClient
    {
        private static readonly HttpClient LokiClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        /// <summary>
        /// Returns the number of log lines that match <paramref name="logql"/> in the given
        /// nanosecond time window. Returns <c>-1</c> on any error/timeout.
        /// </summary>
        public static async Task<int> CountMatchingAsync(
            string lokiReadUrl,
            string logql,
            long startNs,
            long endNs,
            string basicAuthUser = null,
            string basicAuthPass = null)
        {
            if (string.IsNullOrEmpty(lokiReadUrl)) return -1;
            try
            {
                var encoded = Uri.EscapeDataString(logql);
                var url = $"{lokiReadUrl.TrimEnd('/')}/loki/api/v1/query_range"
                        + $"?query={encoded}&start={startNs}&end={endNs}&limit=1000&direction=forward";

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(basicAuthUser) && basicAuthPass != null)
                {
                    var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes(basicAuthUser + ":" + basicAuthPass));
                    req.Headers.Authorization = new AuthenticationHeaderValue("Basic", creds);
                }

                using var resp = await LokiClient.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return -1;

                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var jo = JObject.Parse(body);
                var results = jo["data"]?["result"] as JArray;
                if (results == null) return 0;

                int total = 0;
                foreach (var stream in results)
                {
                    if (stream["values"] is JArray vals) total += vals.Count;
                }
                return total;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// Builds a LogQL query that selects all events for a given <paramref name="testRunId"/>,
        /// optionally filtered to a single <paramref name="eventName"/>.
        /// </summary>
        public static string BuildTestRunQuery(string testRunId, string eventName = null)
        {
            var q = $"{{app=\"sim-steward\"}}|json|test_run_id=\"{testRunId}\"";
            if (!string.IsNullOrEmpty(eventName))
                q += $"|event=\"{eventName}\"";
            return q;
        }

        /// <summary>
        /// Builds a Grafana Explore deep-link URL filtered to a specific <paramref name="testRunId"/>.
        /// Returns empty string if either argument is missing.
        /// </summary>
        public static string BuildGrafanaExploreUrl(string grafanaBaseUrl, string testRunId)
        {
            return BuildGrafanaExploreUrl(grafanaBaseUrl, testRunId, null);
        }

        /// <summary>
        /// Builds a Grafana Explore deep-link URL filtered to a specific <paramref name="testRunId"/> and optional <paramref name="eventName"/>.
        /// Uses Grafana 9+ object format for the <c>left</c> parameter. Returns empty string if base URL or run ID is missing.
        /// </summary>
        public static string BuildGrafanaExploreUrl(string grafanaBaseUrl, string testRunId, string eventName)
        {
            if (string.IsNullOrEmpty(grafanaBaseUrl) || string.IsNullOrEmpty(testRunId)) return "";
            var logql = $"{{app=\"sim-steward\"}} |json |test_run_id=\"{testRunId}\"";
            if (!string.IsNullOrEmpty(eventName))
                logql += $" |event=\"{eventName}\"";
            var exprJson = Newtonsoft.Json.JsonConvert.SerializeObject(logql);
            var left = $"{{\"datasource\":\"loki_local\",\"queries\":[{{\"refId\":\"A\",\"expr\":{exprJson},\"queryType\":\"range\"}}],\"range\":{{\"from\":\"now-1h\",\"to\":\"now\"}}}}";
            return $"{grafanaBaseUrl.TrimEnd('/')}/explore?orgId=1&left={Uri.EscapeDataString(left)}";
        }

        /// <summary>
        /// Queries Loki and returns parsed JSON log line objects. Returns empty list on error.
        /// </summary>
        public static async Task<List<JObject>> QueryLinesAsync(
            string lokiReadUrl,
            string logql,
            long startNs,
            long endNs,
            string basicAuthUser = null,
            string basicAuthPass = null)
        {
            var lines = new List<JObject>();
            if (string.IsNullOrEmpty(lokiReadUrl)) return lines;
            try
            {
                var encoded = Uri.EscapeDataString(logql);
                var url = $"{lokiReadUrl.TrimEnd('/')}/loki/api/v1/query_range"
                        + $"?query={encoded}&start={startNs}&end={endNs}&limit=1000&direction=forward";

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrEmpty(basicAuthUser) && basicAuthPass != null)
                {
                    var creds = Convert.ToBase64String(Encoding.UTF8.GetBytes(basicAuthUser + ":" + basicAuthPass));
                    req.Headers.Authorization = new AuthenticationHeaderValue("Basic", creds);
                }

                using var resp = await LokiClient.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return lines;

                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var jo = JObject.Parse(body);
                var results = jo["data"]?["result"] as JArray;
                if (results == null) return lines;

                foreach (var stream in results)
                {
                    if (!(stream["values"] is JArray vals)) continue;
                    foreach (var v in vals)
                    {
                        if (v is JArray pair && pair.Count >= 2)
                        {
                            var line = pair[1]?.ToString();
                            if (!string.IsNullOrEmpty(line))
                            {
                                try { lines.Add(JObject.Parse(line)); }
                                catch { /* skip malformed lines */ }
                            }
                        }
                    }
                }
            }
            catch { /* return empty list on any error */ }
            return lines;
        }

        /// <summary>Returns nanoseconds since UNIX epoch for <c>UtcNow</c>.</summary>
        public static long NowNs() =>
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;

        /// <summary>Returns nanoseconds since UNIX epoch for <c>UtcNow - offsetMs</c>.</summary>
        public static long NowMinusMs(long offsetMs) =>
            (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - offsetMs) * 1_000_000L;
    }
}
