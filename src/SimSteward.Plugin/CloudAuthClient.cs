using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SimSteward.Plugin
{
    /// <summary>Distinguishable server error codes the plugin must react to differently.</summary>
    public enum CloudAuthErrorCode
    {
        None = 0,
        /// <summary>Server detected a user token replay — the local token must be cleared + alerted.</summary>
        TokenReuseDetected,
        /// <summary>Server does not recognize the persisted user token — clear it and re-pair.</summary>
        InvalidUserToken,
        /// <summary>The configured app token is unknown/revoked — a deployment problem, not a user one.</summary>
        InvalidAppToken,
        /// <summary>The account's subscription is not active — sync should back off, not clear the token.</summary>
        SubscriptionInactive,
        /// <summary>Server-side rate limit (HTTP 429) — back off and retry later.</summary>
        RateLimited,
        /// <summary>Transport-level failure (timeout, DNS, connection reset).</summary>
        Network,
        /// <summary>Non-success HTTP status without a recognized error code.</summary>
        Http,
        /// <summary>Response body could not be parsed.</summary>
        Parse,
        Unknown
    }

    /// <summary>Device-pairing poll lifecycle status.</summary>
    public enum CloudDevicePollStatus
    {
        Pending = 0,
        Approved,
        Denied,
        Expired,
        Error
    }

    public sealed class DeviceStartResult
    {
        public bool Success { get; set; }
        public string DeviceCode { get; set; }
        public string UserCode { get; set; }
        public string VerificationUri { get; set; }
        public int IntervalSec { get; set; }
        public int ExpiresInSec { get; set; }
        public CloudAuthErrorCode ErrorCode { get; set; } = CloudAuthErrorCode.None;
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Result of one <c>/auth/device/poll</c>. The Worker returns only <c>status</c> plus, exactly once
    /// on approval, <c>user_token</c> — there is deliberately no user id or app-token version on this
    /// response, so neither is parsed here.
    /// </summary>
    public sealed class DevicePollResult
    {
        public CloudDevicePollStatus Status { get; set; } = CloudDevicePollStatus.Pending;
        public string UserToken { get; set; }
        public CloudAuthErrorCode ErrorCode { get; set; } = CloudAuthErrorCode.None;
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Result of one <c>/auth/token</c> exchange. <see cref="UserToken"/> is ROTATED on every successful
    /// exchange: the value sent in the request is invalidated server-side and this new one is the only
    /// token that will work next time. It MUST be persisted before <see cref="AccessToken"/> is used —
    /// dropping it locks the install out permanently (the next exchange reads as token reuse).
    /// </summary>
    public sealed class TokenExchangeResult
    {
        public bool Success { get; set; }
        public string AccessToken { get; set; }
        public string UserToken { get; set; }
        public DateTime? ExpiresAtUtc { get; set; }
        public CloudAuthErrorCode ErrorCode { get; set; } = CloudAuthErrorCode.None;
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Auth-plane client for the SimSteward Cloudflare Worker: device pairing and access-token exchange.
    /// Uses a shared long-lived <see cref="HttpClient"/> with a short timeout (matching
    /// <see cref="LokiPushClient"/>). Every method catches, logs, and never throws — auth failures must
    /// degrade sync, never crash the plugin.
    /// </summary>
    public sealed class CloudAuthClient
    {
        private static readonly HttpClient _client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5),
        };

        private readonly string _baseUrl;
        private readonly PluginLogger _logger;

        public CloudAuthClient(string baseUrl, PluginLogger logger = null)
        {
            _baseUrl = (baseUrl ?? "").TrimEnd('/');
            _logger = logger;
        }

        /// <summary>True when a non-empty base URL is configured (otherwise every call no-ops).</summary>
        public bool IsConfigured => !string.IsNullOrEmpty(_baseUrl);

        public async Task<DeviceStartResult> StartDevicePairingAsync(string appToken)
        {
            var result = new DeviceStartResult();
            if (!IsConfigured)
            {
                result.ErrorCode = CloudAuthErrorCode.Network;
                result.ErrorMessage = "cloud_api_url_unset";
                return result;
            }
            try
            {
                var body = new JObject { ["app_token"] = appToken ?? "" };
                var resp = await PostAsync("/auth/device/start", body, appToken).ConfigureAwait(false);
                var json = await ReadJsonAsync(resp).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    ApplyError(json, resp.StatusCode, out var ec, out var em);
                    result.ErrorCode = ec;
                    result.ErrorMessage = em;
                    Log("WARN", "cloud_pair_start_failed", em, ec);
                    return result;
                }
                result.Success = true;
                result.DeviceCode = (string)json["device_code"];
                result.UserCode = (string)json["user_code"];
                result.VerificationUri = (string)json["verification_uri"];
                result.IntervalSec = json["interval_sec"]?.Value<int>() ?? 5;
                result.ExpiresInSec = json["expires_in_sec"]?.Value<int>() ?? 300;
                return result;
            }
            catch (Exception ex)
            {
                result.ErrorCode = CloudAuthErrorCode.Network;
                result.ErrorMessage = ex.Message;
                Log("WARN", "cloud_pair_start_error", ex.Message, CloudAuthErrorCode.Network);
                return result;
            }
        }

        public async Task<DevicePollResult> PollDevicePairingAsync(string deviceCode)
        {
            var result = new DevicePollResult();
            if (!IsConfigured)
            {
                result.Status = CloudDevicePollStatus.Error;
                result.ErrorCode = CloudAuthErrorCode.Network;
                result.ErrorMessage = "cloud_api_url_unset";
                return result;
            }
            try
            {
                var body = new JObject { ["device_code"] = deviceCode ?? "" };
                var resp = await PostAsync("/auth/device/poll", body, null).ConfigureAwait(false);
                var json = await ReadJsonAsync(resp).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    ApplyError(json, resp.StatusCode, out var ec, out var em);
                    result.Status = CloudDevicePollStatus.Error;
                    result.ErrorCode = ec;
                    result.ErrorMessage = em;
                    Log("WARN", "cloud_pair_poll_failed", em, ec);
                    return result;
                }
                string status = ((string)json["status"] ?? "pending").ToLowerInvariant();
                switch (status)
                {
                    case "approved":
                    case "ok":
                        result.Status = CloudDevicePollStatus.Approved;
                        // user_token is returned exactly once, on this response only.
                        result.UserToken = (string)json["user_token"];
                        break;
                    case "denied":
                        result.Status = CloudDevicePollStatus.Denied;
                        break;
                    case "expired":
                        result.Status = CloudDevicePollStatus.Expired;
                        break;
                    default:
                        result.Status = CloudDevicePollStatus.Pending;
                        break;
                }
                return result;
            }
            catch (Exception ex)
            {
                result.Status = CloudDevicePollStatus.Error;
                result.ErrorCode = CloudAuthErrorCode.Network;
                result.ErrorMessage = ex.Message;
                Log("WARN", "cloud_pair_poll_error", ex.Message, CloudAuthErrorCode.Network);
                return result;
            }
        }

        public async Task<TokenExchangeResult> ExchangeTokenAsync(string appToken, string userToken)
        {
            var result = new TokenExchangeResult();
            if (!IsConfigured)
            {
                result.ErrorCode = CloudAuthErrorCode.Network;
                result.ErrorMessage = "cloud_api_url_unset";
                return result;
            }
            try
            {
                var body = new JObject
                {
                    ["app_token"] = appToken ?? "",
                    ["user_token"] = userToken ?? ""
                };
                var resp = await PostAsync("/auth/token", body, appToken).ConfigureAwait(false);
                var json = await ReadJsonAsync(resp).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    ApplyError(json, resp.StatusCode, out var ec, out var em);
                    result.ErrorCode = ec;
                    result.ErrorMessage = em;
                    Log(ec == CloudAuthErrorCode.TokenReuseDetected ? "ERROR" : "WARN",
                        "cloud_token_exchange_failed", em, ec);
                    return result;
                }
                result.Success = true;
                result.AccessToken = (string)json["access_token"];
                // Rotated on every exchange — the caller must persist this before using AccessToken.
                result.UserToken = (string)json["user_token"];
                int expiresInSec = json["expires_in_sec"]?.Value<int>() ?? 0;
                if (expiresInSec > 0)
                    result.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(expiresInSec);
                return result;
            }
            catch (Exception ex)
            {
                result.ErrorCode = CloudAuthErrorCode.Network;
                result.ErrorMessage = ex.Message;
                Log("WARN", "cloud_token_exchange_error", ex.Message, CloudAuthErrorCode.Network);
                return result;
            }
        }

        private Task<HttpResponseMessage> PostAsync(string path, JObject body, string bearer)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + path)
            {
                Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrEmpty(bearer))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
            return _client.SendAsync(req);
        }

        private static async Task<JObject> ReadJsonAsync(HttpResponseMessage resp)
        {
            try
            {
                string s = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(s)) return new JObject();
                return JObject.Parse(s);
            }
            catch
            {
                return new JObject();
            }
        }

        /// <summary>Maps a server <c>error</c> code (or HTTP status) onto a distinguishable enum.</summary>
        private static void ApplyError(JObject json, HttpStatusCode status, out CloudAuthErrorCode code, out string message)
        {
            string err = (string)json?["error"];
            message = (string)json?["message"] ?? err ?? ("http_" + (int)status);
            code = MapErrorCode(err);
            if (code != CloudAuthErrorCode.Unknown) return;
            // No recognized body code — fall back to the status line.
            code = (int)status == 429 ? CloudAuthErrorCode.RateLimited : CloudAuthErrorCode.Http;
        }

        /// <summary>The Worker's documented <c>error</c> vocabulary; anything else stays Unknown.</summary>
        internal static CloudAuthErrorCode MapErrorCode(string err)
        {
            if (string.IsNullOrEmpty(err)) return CloudAuthErrorCode.Unknown;
            switch (err.Trim().ToLowerInvariant())
            {
                case "token_reuse_detected": return CloudAuthErrorCode.TokenReuseDetected;
                case "invalid_user_token": return CloudAuthErrorCode.InvalidUserToken;
                case "invalid_app_token": return CloudAuthErrorCode.InvalidAppToken;
                case "subscription_inactive": return CloudAuthErrorCode.SubscriptionInactive;
                case "rate_limited": return CloudAuthErrorCode.RateLimited;
                default: return CloudAuthErrorCode.Unknown;
            }
        }

        private void Log(string level, string evt, string message, CloudAuthErrorCode code)
        {
            _logger?.Structured(level, "cloud-auth", evt, message ?? "",
                new System.Collections.Generic.Dictionary<string, object>
                {
                    ["error_code"] = code.ToString()
                },
                domain: "cloud");
        }
    }
}
