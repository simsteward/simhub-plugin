using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Lightweight fire-and-forget Loki push client used by <see cref="PluginLogger"/>.
    /// Replaces Alloy file-tailing for the simsteward stream.
    /// Only active when SIMSTEWARD_LOKI_URL is set; silently no-ops otherwise.
    /// </summary>
    public static class LokiPushClient
    {
        private static readonly HttpClient _client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(3),
        };

        /// <summary>
        /// Pushes a batch of log entries to Loki, grouped by stream labels.
        /// Fire-and-forget — never throws. Returns immediately.
        /// </summary>
        public static void Push(string lokiUrl, string appLabel, string envLabel, IEnumerable<LogEntry> entries)
        {
            if (string.IsNullOrEmpty(lokiUrl)) return;
            var list = entries?.ToList();
            if (list == null || list.Count == 0) return;

            // Capture for the async task — do not capture locals that may change
            Task.Run(() => PushInternalAsync(lokiUrl, appLabel, envLabel, list));
        }

        private static async Task PushInternalAsync(
            string lokiUrl, string appLabel, string envLabel, List<LogEntry> entries)
        {
            try
            {
                // Group entries by their label combination to minimise stream count per push.
                // Labels match what Alloy previously extracted: level, component, event, domain.
                var groups = entries
                    .GroupBy(e => (
                        level:     e.Level     ?? "INFO",
                        component: e.Component ?? "",
                        evt:       e.Event     ?? "",
                        domain:    e.Domain    ?? ""
                    ))
                    .ToList();

                var streams = new JArray();

                foreach (var g in groups)
                {
                    var streamLabels = new JObject
                    {
                        ["app"] = appLabel,
                        ["env"] = envLabel,
                        ["level"] = g.Key.level,
                    };
                    if (!string.IsNullOrEmpty(g.Key.component)) streamLabels["component"] = g.Key.component;
                    if (!string.IsNullOrEmpty(g.Key.evt))       streamLabels["event"]     = g.Key.evt;
                    if (!string.IsNullOrEmpty(g.Key.domain))    streamLabels["domain"]    = g.Key.domain;

                    var values = new JArray();
                    foreach (var entry in g)
                    {
                        // Loki timestamp in nanoseconds
                        long tsNs;
                        if (!string.IsNullOrEmpty(entry.Timestamp) &&
                            DateTimeOffset.TryParse(entry.Timestamp, out var dto))
                            tsNs = dto.ToUnixTimeMilliseconds() * 1_000_000L;
                        else
                            tsNs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;

                        values.Add(new JArray(tsNs.ToString(), JsonConvert.SerializeObject(entry)));
                    }

                    streams.Add(new JObject
                    {
                        ["stream"] = streamLabels,
                        ["values"] = values,
                    });
                }

                var body = new JObject { ["streams"] = streams }.ToString(Formatting.None);
                var url = lokiUrl.TrimEnd('/') + "/loki/api/v1/push";

                using (var content = new StringContent(body, Encoding.UTF8, "application/json"))
                {
                    await _client.PostAsync(url, content).ConfigureAwait(false);
                }
            }
            catch
            {
                // Fire-and-forget: swallow all errors — plugin must never fail due to observability
            }
        }
    }
}
