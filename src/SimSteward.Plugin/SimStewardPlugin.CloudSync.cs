using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Sentry;

namespace SimSteward.Plugin
{
    public partial class SimStewardPlugin
    {
        // ── Cloud sync singletons (constructed once in Init via InitCloudSync) ──────────────
        private CloudTokenStore _cloudTokenStore;
        private CloudAuthClient _cloudAuthClient;
        private CloudDataClient _cloudDataClient;
        private CloudOutbox _cloudOutbox;
        private string _cloudAppToken;

        // Device-pairing state (driven from the throttled sync cycle).
        private volatile bool _cloudPairingActive;
        private string _cloudPairingDeviceCode;
        private int _cloudPairingIntervalSec = 5;
        private DateTime _cloudPairingNextPollUtc = DateTime.MinValue;

        // Throttled drain, mirroring the capture-queue drain cadence (_captureQueueDrainTickCounter).
        private int _cloudSyncDrainTickCounter;
        private const int CloudSyncDrainIntervalTicks = 120; // ~2s at 60Hz
        private const int CloudSyncMaxDrainPerTick = 3;
        private volatile bool _cloudSyncCycleInFlight;

        private static readonly TimeSpan CloudAccessTokenRefreshLeadTime = TimeSpan.FromSeconds(60);

        // Last successful outbox push, for the cloudSyncStatus badge. Written from the cloud-sync
        // Task.Run cycle, read from the Fleck dispatch thread on new-client connect — volatile is
        // sufficient since it's a single reference-typed field with no cross-field invariant.
        private volatile string _cloudLastPushUtc;

#if SIMHUB_SDK
        /// <summary>Constructs the cloud-sync singletons. Safe when unconfigured — clients no-op on empty URL.</summary>
        private void InitCloudSync()
        {
            try
            {
                string baseUrl = Environment.GetEnvironmentVariable("SIMSTEWARD_CLOUD_API_URL")?.Trim() ?? "";
                _cloudAppToken = Environment.GetEnvironmentVariable("SIMSTEWARD_CLOUD_APP_TOKEN")?.Trim() ?? "";

                _cloudTokenStore = new CloudTokenStore();
                _cloudAuthClient = new CloudAuthClient(baseUrl, _logger);
                _cloudDataClient = new CloudDataClient(baseUrl, _logger);
                _cloudOutbox = new CloudOutbox();
                CloudSyncBridge.SharedOutbox = _cloudOutbox;

                if (_cloudOutbox.LastLoadCorruptLineCount > 0)
                {
                    _logger?.Structured("WARN", "cloud-sync", "cloud_outbox_corrupt_lines_skipped",
                        "Corrupt outbox lines skipped on load.",
                        new Dictionary<string, object>
                        {
                            ["corrupt_line_count"] = _cloudOutbox.LastLoadCorruptLineCount,
                            ["loaded_entry_count"] = _cloudOutbox.LastLoadEntryCount
                        }, domain: "cloud");
                }

                _logger?.Structured("INFO", "cloud-sync", "cloud_sync_ready",
                    "Cloud sync initialized.",
                    new Dictionary<string, object>
                    {
                        ["configured"] = _cloudAuthClient.IsConfigured,
                        ["pending_outbox"] = _cloudOutbox.PendingCount
                    },
                    domain: "cloud");
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
                _logger?.Structured("ERROR", "cloud-sync", "cloud_sync_init_failed",
                    $"Cloud sync init failed: {ex.Message}",
                    new Dictionary<string, object> { ["error"] = ex.Message }, domain: "cloud");
            }
        }

        /// <summary>
        /// Builds the cloudSyncStatus push sent on WS connect (via DashboardBridge's
        /// getCloudStatusForNewClient) and broadcast after pairing completion / outbox drains.
        /// Never includes tokens or device codes — paired status, last push time, pending count only.
        /// </summary>
        private string GetCloudSyncStatusJson()
        {
            if (_cloudTokenStore == null) return null;
            bool paired = _cloudTokenStore.TryLoad(out _);
            var obj = new JObject
            {
                ["type"] = "cloudSyncStatus",
                ["paired"] = paired,
                ["lastPushUtc"] = _cloudLastPushUtc,
                ["outboxPendingCount"] = _cloudOutbox?.PendingCount ?? 0
            };
            return obj.ToString(Formatting.None);
        }

        private void BroadcastCloudSyncStatus()
        {
            try { _bridge?.Broadcast(GetCloudSyncStatusJson(), "cloudSyncStatus"); }
            catch (Exception bex) { WriteBroadcastError("BroadcastCloudSyncStatus", bex); }
        }

        /// <summary>
        /// Throttled per-tick cloud pump (call from DataUpdate). Every <see cref="CloudSyncDrainIntervalTicks"/>
        /// ticks it kicks a single non-overlapping async cycle: proactively refresh the access token, poll a
        /// pending device pairing, then drain a few ready outbox entries.
        /// </summary>
        private void TickCloudSync()
        {
            if (_cloudOutbox == null) return;
            if (++_cloudSyncDrainTickCounter < CloudSyncDrainIntervalTicks) return;
            _cloudSyncDrainTickCounter = 0;
            if (_cloudSyncCycleInFlight) return;

            _cloudSyncCycleInFlight = true;
            Task.Run(async () =>
            {
                try { await RunCloudSyncCycleAsync().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    SentrySdk.CaptureException(ex);
                    _logger?.Structured("WARN", "cloud-sync", "cloud_sync_cycle_error",
                        ex.Message, new Dictionary<string, object> { ["error"] = ex.Message }, domain: "cloud");
                }
                finally { _cloudSyncCycleInFlight = false; }
            });
        }

        private async Task RunCloudSyncCycleAsync()
        {
            // Write-ahead: persist anything Enqueue()'d since the last cycle before we try to push it,
            // so a crash mid-push never loses data that was already durably queued (CloudOutbox contract).
            _cloudOutbox.PersistPendingIfDirty();
            await PollDevicePairingIfDueAsync().ConfigureAwait(false);
            await RefreshAccessTokenIfNeededAsync().ConfigureAwait(false);
            await DrainOutboxAsync(CloudSyncMaxDrainPerTick).ConfigureAwait(false);
            // Persist this cycle's Ack()/RecordFailure() state (both only flag dirty in memory).
            _cloudOutbox.PersistPendingIfDirty();
        }

        private async Task RefreshAccessTokenIfNeededAsync()
        {
            if (_cloudAuthClient == null || !_cloudAuthClient.IsConfigured) return;
            if (!_cloudTokenStore.NeedsRefresh(CloudAccessTokenRefreshLeadTime)) return;
            if (!_cloudTokenStore.TryLoad(out var record) || string.IsNullOrEmpty(record.UserToken)) return;

            var result = await _cloudAuthClient.ExchangeTokenAsync(_cloudAppToken, record.UserToken).ConfigureAwait(false);
            if (result.Success)
            {
                if (string.IsNullOrEmpty(result.UserToken))
                {
                    // Server contract violation — a successful exchange always rotates the user token.
                    // Do NOT set the access token either: using it without persisting its paired rotated
                    // token would desync local/server state further on the next refresh.
                    _logger?.Structured("ERROR", "cloud-sync", "cloud_token_rotation_missing",
                        "Token exchange succeeded but returned no rotated user_token; refresh skipped.",
                        new Dictionary<string, object>(), domain: "cloud");
                    return;
                }
                // Persist the rotated user token FIRST — the old one is invalidated server-side on
                // exchange, so losing this here would lock the install out permanently: the next
                // refresh would present the now-stale token and read as reuse (CloudAuthClient docs).
                _cloudTokenStore.Save(new CloudTokenRecord
                {
                    UserToken = result.UserToken,
                    UserId = record.UserId,
                    IssuedAtUtc = DateTime.UtcNow,
                    AppTokenVersion = record.AppTokenVersion
                });
                _cloudTokenStore.SetAccessToken(result.AccessToken, result.ExpiresAtUtc);
                return;
            }

            if (result.ErrorCode == CloudAuthErrorCode.TokenReuseDetected)
            {
                _cloudTokenStore.Clear();
                var ex = new InvalidOperationException(
                    "Cloud user token reuse detected by server; local token cleared. Re-pairing required.");
                SentrySdk.CaptureException(ex);
                _logger?.Structured("ERROR", "cloud-sync", "cloud_token_reuse_detected",
                    ex.Message,
                    new Dictionary<string, object> { ["error_code"] = result.ErrorCode.ToString() }, domain: "cloud");
            }
            else if (result.ErrorCode == CloudAuthErrorCode.SubscriptionInactive)
            {
                // Do NOT clear the token — subscription may reactivate; just back off and log.
                _logger?.Structured("WARN", "cloud-sync", "cloud_subscription_inactive",
                    "Cloud subscription inactive; deferring sync.",
                    new Dictionary<string, object> { ["error_code"] = result.ErrorCode.ToString() }, domain: "cloud");
            }
        }

        private async Task DrainOutboxAsync(int maxEntries)
        {
            if (_cloudDataClient == null || !_cloudDataClient.IsConfigured) return;
            string accessToken = _cloudTokenStore?.AccessToken;
            if (string.IsNullOrEmpty(accessToken)) return; // no auth yet — leave entries for a later cycle
            if (!_cloudOutbox.TryDequeueReady(DateTime.UtcNow, out var ready) || ready.Count == 0) return;

            int processed = 0;
            bool anySucceeded = false;
            foreach (var entry in ready)
            {
                if (processed >= maxEntries) break;
                processed++;
                var outcome = await TryPushOutboxEntryAsync(entry, accessToken).ConfigureAwait(false);
                if (outcome == CloudPushOutcome.Success)
                {
                    _cloudOutbox.Ack(entry.Id);
                    anySucceeded = true;
                }
                else _cloudOutbox.RecordFailure(entry.Id, permanent: outcome == CloudPushOutcome.Permanent);
            }

            if (anySucceeded)
            {
                _cloudLastPushUtc = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
                BroadcastCloudSyncStatus();
            }
        }

        private async Task<CloudPushOutcome> TryPushOutboxEntryAsync(CloudOutboxEntry entry, string accessToken)
        {
            try
            {
                if (entry.Kind == CloudOutbox.KindLiveIncident)
                {
                    var row = JsonConvert.DeserializeObject<IncidentRowForCloud>(entry.PayloadJson);
                    if (row == null) return CloudPushOutcome.Success; // unparseable payload — drop rather than loop forever
                    return await _cloudDataClient.TryPushIncidentsAsync(new[] { row }, accessToken).ConfigureAwait(false);
                }
                if (entry.Kind == CloudOutbox.KindIndexFinalize)
                {
                    var wrapper = JsonConvert.DeserializeObject<IndexFinalizePayload>(entry.PayloadJson);
                    if (wrapper == null || string.IsNullOrEmpty(wrapper.SubSessionId)) return CloudPushOutcome.Success;
                    return await _cloudDataClient
                        .TryPushIncidentIndexAsync(wrapper.SubSessionId, wrapper.IndexJson, accessToken)
                        .ConfigureAwait(false);
                }
                return CloudPushOutcome.Success; // unknown kind — drop
            }
            catch (Exception ex)
            {
                _logger?.Structured("WARN", "cloud-sync", "cloud_outbox_push_error",
                    ex.Message,
                    new Dictionary<string, object> { ["error"] = ex.Message, ["kind"] = entry.Kind }, domain: "cloud");
                return CloudPushOutcome.Transient;
            }
        }

        private async Task PollDevicePairingIfDueAsync()
        {
            if (!_cloudPairingActive || _cloudAuthClient == null) return;
            if (string.IsNullOrEmpty(_cloudPairingDeviceCode)) return;
            if (DateTime.UtcNow < _cloudPairingNextPollUtc) return;
            _cloudPairingNextPollUtc = DateTime.UtcNow.AddSeconds(Math.Max(1, _cloudPairingIntervalSec));

            var poll = await _cloudAuthClient.PollDevicePairingAsync(_cloudPairingDeviceCode).ConfigureAwait(false);
            switch (poll.Status)
            {
                case CloudDevicePollStatus.Approved:
                    // The poll response deliberately carries only user_token — no user id or app-token
                    // version at this stage (CloudAuthClient.DevicePollResult). Those are only known
                    // once the first ExchangeTokenAsync call succeeds.
                    _cloudTokenStore.Save(new CloudTokenRecord
                    {
                        UserToken = poll.UserToken,
                        IssuedAtUtc = DateTime.UtcNow
                    });
                    _cloudPairingActive = false;
                    _cloudPairingDeviceCode = null;
                    _logger?.Structured("INFO", "cloud-sync", "cloud_pairing_approved",
                        "Cloud device pairing approved; user token stored.",
                        new Dictionary<string, object>(), domain: "cloud");
                    BroadcastCloudSyncStatus();
                    break;
                case CloudDevicePollStatus.Denied:
                case CloudDevicePollStatus.Expired:
                    _cloudPairingActive = false;
                    _cloudPairingDeviceCode = null;
                    _logger?.Structured("WARN", "cloud-sync", "cloud_pairing_ended",
                        $"Cloud device pairing {poll.Status.ToString().ToLowerInvariant()}.",
                        new Dictionary<string, object> { ["status"] = poll.Status.ToString() }, domain: "cloud");
                    BroadcastCloudSyncStatus();
                    break;
            }
        }

        /// <summary>
        /// Enqueues a finalized replay incident index for durable cloud upload. Called from
        /// <c>FinalizeReplayIndexBuildLocked</c> immediately after the local atomic JSON write succeeds.
        /// </summary>
        private void EnqueueReplayIndexForCloudSync(ReplayIncidentIndexFileRoot root, string indexJson)
        {
            if (_cloudOutbox == null || root == null || string.IsNullOrEmpty(indexJson)) return;
            try
            {
                var payload = new IndexFinalizePayload
                {
                    SubSessionId = root.SubSessionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    IndexJson = indexJson
                };
                _cloudOutbox.Enqueue(CloudOutbox.KindIndexFinalize, JsonConvert.SerializeObject(payload));
                _logger?.Structured("INFO", "cloud-sync", "cloud_index_enqueued",
                    "Replay incident index enqueued for cloud sync.",
                    new Dictionary<string, object>
                    {
                        ["sub_session_id"] = payload.SubSessionId,
                        ["incident_count"] = root.TotalRaceIncidents,
                        ["pending_outbox"] = _cloudOutbox.PendingCount
                    }, domain: "cloud");
                BroadcastCloudSyncStatus();
            }
            catch (Exception ex)
            {
                SentrySdk.CaptureException(ex);
                _logger?.Structured("WARN", "cloud-sync", "cloud_index_enqueue_error",
                    ex.Message, new Dictionary<string, object> { ["error"] = ex.Message }, domain: "cloud");
            }
        }

        private sealed class IndexFinalizePayload
        {
            [JsonProperty("subSessionId")] public string SubSessionId { get; set; }
            [JsonProperty("indexJson")] public string IndexJson { get; set; }
        }

        // ── DispatchAction branch handlers (invoked from DispatchAction's chain) ─────────────

        private (bool success, string result, string error) DispatchCloudPairStart(string arg, string correlationId)
        {
            const string action = "cloud_pair_start";
            if (_cloudAuthClient == null || !_cloudAuthClient.IsConfigured)
            {
                LogActionResult(action, arg, correlationId, false, "cloud_not_configured");
                return (false, null, "cloud_not_configured");
            }
            _cloudPairingActive = true;
            _cloudPairingDeviceCode = null;
            string appToken = _cloudAppToken;
            Task.Run(async () =>
            {
                try
                {
                    var start = await _cloudAuthClient.StartDevicePairingAsync(appToken).ConfigureAwait(false);
                    if (start.Success)
                    {
                        _cloudPairingDeviceCode = start.DeviceCode;
                        _cloudPairingIntervalSec = start.IntervalSec > 0 ? start.IntervalSec : 5;
                        _cloudPairingNextPollUtc = DateTime.UtcNow.AddSeconds(_cloudPairingIntervalSec);
                        _logger?.Structured("INFO", "cloud-sync", "cloud_pairing_started",
                            "Cloud device pairing started; awaiting user approval.",
                            new Dictionary<string, object>
                            {
                                ["user_code"] = start.UserCode ?? "",
                                ["verification_uri"] = start.VerificationUri ?? "",
                                ["expires_in_sec"] = start.ExpiresInSec
                            }, domain: "cloud");
                        try
                        {
                            var pairingMsg = new JObject
                            {
                                ["type"] = "cloudPairing",
                                ["userCode"] = start.UserCode ?? "",
                                ["verificationUri"] = start.VerificationUri ?? "",
                                ["expiresInSec"] = start.ExpiresInSec
                            };
                            _bridge?.Broadcast(pairingMsg.ToString(Formatting.None), "cloudPairing");
                        }
                        catch (Exception bex) { WriteBroadcastError("DispatchCloudPairStart", bex); }
                    }
                    else
                    {
                        _cloudPairingActive = false;
                        _logger?.Structured("WARN", "cloud-sync", "cloud_pairing_start_failed",
                            start.ErrorMessage ?? "pairing_start_failed",
                            new Dictionary<string, object> { ["error_code"] = start.ErrorCode.ToString() }, domain: "cloud");
                    }
                }
                catch (Exception ex)
                {
                    _cloudPairingActive = false;
                    SentrySdk.CaptureException(ex);
                    _logger?.Structured("WARN", "cloud-sync", "cloud_pairing_start_error",
                        ex.Message, new Dictionary<string, object> { ["error"] = ex.Message }, domain: "cloud");
                }
            });
            LogActionResult(action, arg, correlationId, true, "");
            return (true, "ok", null);
        }

        private (bool success, string result, string error) DispatchCloudPairCancel(string arg, string correlationId)
        {
            const string action = "cloud_pair_cancel";
            _cloudPairingActive = false;
            _cloudPairingDeviceCode = null;
            LogActionResult(action, arg, correlationId, true, "");
            return (true, "ok", null);
        }

        private (bool success, string result, string error) DispatchCloudIncidentIndexFetch(string arg, string correlationId)
        {
            const string action = "cloud_incident_index_fetch";
            string subSessionId = (arg ?? "").Trim();
            if (string.IsNullOrEmpty(subSessionId))
            {
                LogActionResult(action, arg, correlationId, false, "bad_arg");
                return (false, null, "bad_arg");
            }
            if (_cloudDataClient == null || !_cloudDataClient.IsConfigured)
            {
                LogActionResult(action, arg, correlationId, false, "cloud_not_configured");
                return (false, null, "cloud_not_configured");
            }
            string accessToken = _cloudTokenStore?.AccessToken;
            Task.Run(async () =>
            {
                try
                {
                    var result = await _cloudDataClient.GetIncidentIndexAsync(subSessionId, accessToken).ConfigureAwait(false);
                    if (result.Outcome == CloudFetchOutcome.Found)
                    {
                        try
                        {
                            var wrapped = new JObject { ["type"] = "cloudIncidentIndex", ["root"] = JObject.Parse(result.Json) };
                            _bridge?.Broadcast(wrapped.ToString(Formatting.None), "cloudIncidentIndex");
                        }
                        catch (Exception bex) { WriteBroadcastError("DispatchCloudIncidentIndexFetch", bex); }
                    }
                    string logLevel = result.Outcome == CloudFetchOutcome.Unauthorized ? "WARN"
                        : result.Outcome == CloudFetchOutcome.Failed ? "WARN" : "INFO";
                    _logger?.Structured(logLevel, "cloud-sync", "cloud_index_fetch_result",
                        $"Cloud incident index fetch: {result.Outcome}.",
                        new Dictionary<string, object>
                        {
                            ["sub_session_id"] = subSessionId,
                            ["outcome"] = result.Outcome.ToString(),
                            ["status_code"] = result.StatusCode,
                            ["error_code"] = result.ErrorCode ?? ""
                        }, domain: "cloud");
                }
                catch (Exception ex)
                {
                    SentrySdk.CaptureException(ex);
                    _logger?.Structured("WARN", "cloud-sync", "cloud_index_fetch_error",
                        ex.Message, new Dictionary<string, object> { ["error"] = ex.Message }, domain: "cloud");
                }
            });
            LogActionResult(action, arg, correlationId, true, "");
            return (true, "ok", null);
        }
#endif
    }
}
