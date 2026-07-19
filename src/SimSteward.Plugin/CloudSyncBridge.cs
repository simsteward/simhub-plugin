using Newtonsoft.Json;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Standalone integration point for the live incident-detection path. Kept OUT of
    /// <see cref="SimStewardPlugin"/> deliberately: that type implements SimHub interfaces and cannot
    /// load without <c>SimHub.Plugins.dll</c> at runtime, so anything the live path (or a unit test)
    /// calls must live on a SimHub-independent type.
    /// <para>
    /// <see cref="SimStewardPlugin"/> assigns <see cref="SharedOutbox"/> during init; the live-detection
    /// path (not yet on <c>main</c> — see the CLAUDE.md note) is meant to call
    /// <see cref="OnIncidentDetected"/> once it lands.
    /// </para>
    /// </summary>
    public static class CloudSyncBridge
    {
        /// <summary>Outbox the integration point enqueues into. Assigned by the plugin during init; null until then.</summary>
        public static CloudOutbox SharedOutbox { get; set; }

        /// <summary>
        /// Computes the v2 (sampling-rate-stable) fingerprint for a live incident, builds an outbox
        /// payload, and enqueues it durably. No-op when no outbox has been configured. Never throws.
        /// <para>
        /// NOTE: <see cref="IncidentSample"/> carries no subSessionId, so the v2 fingerprint is computed
        /// with subSessionId=0 (date-scoped offline key). When the live path lands it should thread the
        /// real subSessionId through so live and replay-index fingerprints for the same incident agree.
        /// </para>
        /// </summary>
        public static void OnIncidentDetected(IncidentSample sample, string source)
        {
            var outbox = SharedOutbox;
            if (outbox == null) return;
            try
            {
                outbox.Enqueue(CloudOutbox.KindLiveIncident, BuildLiveIncidentPayload(sample, source));
            }
            catch
            {
                // Enqueue must never throw into the detection path; a lost live push degrades gracefully.
            }
        }

        /// <summary>Serializes a live <see cref="IncidentSample"/> to the outbox payload (an <see cref="IncidentRowForCloud"/>).</summary>
        internal static string BuildLiveIncidentPayload(IncidentSample sample, string source)
        {
            string cloudFp = ReplayIncidentIndexFingerprint.ComputeHexV2(
                0, // subSessionId unknown in the live static path — see OnIncidentDetected note
                sample.CarIdx,
                sample.SessionTimeMs,
                sample.DetectionSource,
                sample.IncidentPoints);

            var row = new IncidentRowForCloud
            {
                CloudFingerprint = cloudFp,
                SubSessionId = "0",
                CarIdx = sample.CarIdx,
                SessionTimeMs = sample.SessionTimeMs,
                DetectionSource = string.IsNullOrEmpty(sample.DetectionSource) ? (source ?? "") : sample.DetectionSource,
                IncidentPoints = sample.IncidentPoints,
                Lap = sample.Lap,
                SessionNum = sample.SessionNum
            };
            return JsonConvert.SerializeObject(row);
        }
    }
}
