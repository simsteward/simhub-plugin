using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Minimal incident row shipped to the cloud. Wire field names are snake_case to match the Worker's
    /// <c>IncidentInput</c> contract; the C# property names stay descriptive.
    /// <para>
    /// <c>id</c> is the v2 fingerprint and is the server's dedup key — the Worker SKIPS any row without
    /// it, so this must never be omitted.
    /// </para>
    /// </summary>
    public sealed class IncidentRowForCloud
    {
        /// <summary>v2 cloud fingerprint — serialized as <c>id</c>, the Worker's primary/dedup key.</summary>
        [JsonProperty("id")]
        public string CloudFingerprint { get; set; }

        [JsonProperty("sub_session_id")]
        public string SubSessionId { get; set; }

        [JsonProperty("car_idx")]
        public int CarIdx { get; set; }

        /// <summary>Session time in milliseconds (the Worker column is a plain REAL and stores it verbatim).</summary>
        [JsonProperty("session_time")]
        public int SessionTimeMs { get; set; }

        /// <summary>
        /// Detection mechanism (<c>track_surface</c>, <c>off_track</c>, …) — serialized as <c>type</c>.
        /// Deliberately NOT <c>source</c>: on the Worker, <c>source</c> is provenance
        /// (<c>live</c> | <c>replay_reconciled</c>) and drives <c>rankForSource</c>, which gates whether an
        /// upsert may overwrite an existing row. Putting a detection mechanism there would make the rank a lie.
        /// </summary>
        [JsonProperty("type")]
        public string DetectionSource { get; set; }

        /// <summary>Incident points delta (1x/2x/4x) — the Worker's <c>delta</c> column.</summary>
        [JsonProperty("delta", NullValueHandling = NullValueHandling.Ignore)]
        public int? IncidentPoints { get; set; }

        /// <summary>Provenance. Omitted -&gt; the Worker applies the route default (<c>live</c> for /incidents/push).</summary>
        [JsonProperty("source", NullValueHandling = NullValueHandling.Ignore)]
        public string Source { get; set; }

        [JsonProperty("lap")]
        public int Lap { get; set; }

        [JsonProperty("session_num")]
        public int SessionNum { get; set; }
    }

    /// <summary>How the drain should treat a push failure.</summary>
    public enum CloudPushOutcome
    {
        /// <summary>2xx — accepted; the outbox entry can be acked.</summary>
        Success,
        /// <summary>Retry later: network error, 5xx, 401 (a token refresh may fix it) or 429 (back-pressure).</summary>
        Transient,
        /// <summary>The server rejected the payload itself (4xx other than 401/429) — retrying cannot help.</summary>
        Permanent
    }

    /// <summary>Outcome of a read-path fetch, so callers can tell "no data" from "not allowed".</summary>
    public enum CloudFetchOutcome
    {
        Found,
        NotFound,
        /// <summary>401/403 — the access token is missing, expired or the subscription is inactive.</summary>
        Unauthorized,
        /// <summary>Network error, 5xx, or an unparseable response.</summary>
        Failed
    }

    public sealed class CloudIndexFetchResult
    {
        public CloudFetchOutcome Outcome { get; set; } = CloudFetchOutcome.Failed;
        public string Json { get; set; }
        public int StatusCode { get; set; }
        /// <summary>The server's <c>error</c> body value when present (e.g. <c>subscription_inactive</c>).</summary>
        public string ErrorCode { get; set; }
    }

    /// <summary>Server-side session rollup returned by <c>GET /session/{subSessionId}</c>.</summary>
    public sealed class SessionSummaryDto
    {
        /// <summary>Raw session row (sub_session_id, session_id, series_id, track_name, session_type, captured_at).</summary>
        [JsonProperty("session")]
        public JObject Session { get; set; }

        [JsonProperty("incident_count")]
        public int IncidentCount { get; set; }
    }

    /// <summary>
    /// Data-plane client for the SimSteward Cloudflare Worker. Push methods are fire-and-forget
    /// (<see cref="Task.Run(Action)"/>, mirroring <see cref="LokiPushClient.Push"/>); read methods are
    /// awaited. Every path catches, logs, and never throws.
    /// </summary>
    public sealed class CloudDataClient
    {
        private static readonly HttpClient _client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5),
        };

        private readonly string _baseUrl;
        private readonly PluginLogger _logger;

        public CloudDataClient(string baseUrl, PluginLogger logger = null)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _logger = logger;
        }

        public bool IsConfigured => !string.IsNullOrEmpty(_baseUrl);

        public void PushIncidentsFireAndForget(IEnumerable<IncidentRowForCloud> rows, string accessToken)
        {
            if (!IsConfigured) return;
            var list = rows?.ToList();
            if (list == null || list.Count == 0) return;
            Task.Run(() => TryPushIncidentsAsync(list, accessToken));
        }

        /// <summary>Awaited incident push used by the outbox drain so it can ack, retry, or dead-letter.</summary>
        public async Task<CloudPushOutcome> TryPushIncidentsAsync(IEnumerable<IncidentRowForCloud> rows, string accessToken)
        {
            var list = rows?.ToList();
            if (!IsConfigured || list == null || list.Count == 0) return CloudPushOutcome.Permanent;
            try
            {
                var body = new JObject { ["incidents"] = JArray.FromObject(list) };
                var resp = await PostAsync("/incidents/push", body.ToString(Formatting.None), accessToken).ConfigureAwait(false);
                var outcome = Classify(resp.StatusCode);
                if (outcome != CloudPushOutcome.Success)
                {
                    Log(outcome == CloudPushOutcome.Permanent ? "ERROR" : "WARN", "cloud_push_incidents_failed",
                        $"status={(int)resp.StatusCode} count={list.Count} outcome={outcome}");
                }
                return outcome;
            }
            catch (Exception ex)
            {
                Log("WARN", "cloud_push_incidents_error", ex.Message);
                return CloudPushOutcome.Transient;
            }
        }

        public void PushIncidentIndexFireAndForget(string subSessionId, string indexJson, string accessToken)
        {
            if (!IsConfigured) return;
            if (string.IsNullOrEmpty(subSessionId) || string.IsNullOrEmpty(indexJson)) return;
            Task.Run(() => TryPushIncidentIndexAsync(subSessionId, indexJson, accessToken));
        }

        /// <summary>
        /// Awaited index upload used by the outbox drain. The Worker stores the body verbatim in R2, so the
        /// index JSON is sent unmodified. Route is PUT (the Worker only accepts PUT on this path).
        /// </summary>
        public async Task<CloudPushOutcome> TryPushIncidentIndexAsync(string subSessionId, string indexJson, string accessToken)
        {
            if (!IsConfigured || string.IsNullOrEmpty(subSessionId) || string.IsNullOrEmpty(indexJson))
                return CloudPushOutcome.Permanent;
            try
            {
                var resp = await SendAsync(HttpMethod.Put, $"/incident-index/{Uri.EscapeDataString(subSessionId)}",
                    indexJson, accessToken).ConfigureAwait(false);
                var outcome = Classify(resp.StatusCode);
                if (outcome != CloudPushOutcome.Success)
                {
                    Log(outcome == CloudPushOutcome.Permanent ? "ERROR" : "WARN", "cloud_push_index_failed",
                        $"status={(int)resp.StatusCode} subSession={subSessionId} outcome={outcome}");
                }
                return outcome;
            }
            catch (Exception ex)
            {
                Log("WARN", "cloud_push_index_error", ex.Message);
                return CloudPushOutcome.Transient;
            }
        }

        /// <summary>
        /// GET the merged incident index JSON for a subsession. Distinguishes 404 (never uploaded) from
        /// 401/403 (auth/subscription) so the caller can tell the user which one actually happened.
        /// </summary>
        public async Task<CloudIndexFetchResult> GetIncidentIndexAsync(string subSessionId, string accessToken)
        {
            var result = new CloudIndexFetchResult();
            if (!IsConfigured || string.IsNullOrEmpty(subSessionId))
            {
                result.Outcome = CloudFetchOutcome.Failed;
                result.ErrorCode = "cloud_not_configured";
                return result;
            }
            try
            {
                var resp = await SendAsync(HttpMethod.Get, $"/incident-index/{Uri.EscapeDataString(subSessionId)}",
                    null, accessToken).ConfigureAwait(false);
                result.StatusCode = (int)resp.StatusCode;

                if (resp.IsSuccessStatusCode)
                {
                    result.Json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    result.Outcome = CloudFetchOutcome.Found;
                    return result;
                }

                result.ErrorCode = await ReadErrorCodeAsync(resp).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.NotFound)
                {
                    result.Outcome = CloudFetchOutcome.NotFound;
                    return result;
                }
                if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                {
                    result.Outcome = CloudFetchOutcome.Unauthorized;
                    Log("WARN", "cloud_get_index_unauthorized",
                        $"status={result.StatusCode} error={result.ErrorCode} subSession={subSessionId}");
                    return result;
                }
                result.Outcome = CloudFetchOutcome.Failed;
                Log("WARN", "cloud_get_index_failed",
                    $"status={result.StatusCode} error={result.ErrorCode} subSession={subSessionId}");
                return result;
            }
            catch (Exception ex)
            {
                result.Outcome = CloudFetchOutcome.Failed;
                result.ErrorCode = "network";
                Log("WARN", "cloud_get_index_error", ex.Message);
                return result;
            }
        }

        /// <summary>GET the server session summary (<c>GET /session/{id}</c>). Null on 404 or any failure.</summary>
        public async Task<SessionSummaryDto> GetSessionSummaryAsync(string subSessionId, string accessToken)
        {
            if (!IsConfigured || string.IsNullOrEmpty(subSessionId)) return null;
            try
            {
                var resp = await SendAsync(HttpMethod.Get, $"/session/{Uri.EscapeDataString(subSessionId)}", null, accessToken)
                    .ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.NotFound) return null;
                if (!resp.IsSuccessStatusCode)
                {
                    Log("WARN", "cloud_get_summary_failed", $"status={(int)resp.StatusCode} subSession={subSessionId}");
                    return null;
                }
                string s = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(s)) return null;
                return JsonConvert.DeserializeObject<SessionSummaryDto>(s);
            }
            catch (Exception ex)
            {
                Log("WARN", "cloud_get_summary_error", ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 2xx acks. 401 and 429 stay retryable (a refreshed token / a later window can succeed); every other
        /// 4xx means the payload itself is bad, so retrying it forever would just poison the outbox.
        /// </summary>
        internal static CloudPushOutcome Classify(HttpStatusCode status)
        {
            int code = (int)status;
            if (code >= 200 && code < 300) return CloudPushOutcome.Success;
            if (code == 401 || code == 429) return CloudPushOutcome.Transient;
            if (code >= 400 && code < 500) return CloudPushOutcome.Permanent;
            return CloudPushOutcome.Transient;
        }

        private static async Task<string> ReadErrorCodeAsync(HttpResponseMessage resp)
        {
            try
            {
                string s = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(s)) return null;
                return (string)JObject.Parse(s)["error"];
            }
            catch
            {
                return null;
            }
        }

        private Task<HttpResponseMessage> PostAsync(string path, string jsonBody, string accessToken) =>
            SendAsync(HttpMethod.Post, path, jsonBody, accessToken);

        private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string jsonBody, string accessToken)
        {
            var req = new HttpRequestMessage(method, _baseUrl + path);
            if (jsonBody != null)
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            if (!string.IsNullOrEmpty(accessToken))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            return _client.SendAsync(req);
        }

        private void Log(string level, string evt, string message)
        {
            _logger?.Structured(level, "cloud-data", evt, message ?? "", null, domain: "cloud");
        }
    }
}
