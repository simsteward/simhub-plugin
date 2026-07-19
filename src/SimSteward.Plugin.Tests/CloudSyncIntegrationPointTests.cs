using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    /// <summary>
    /// Exercises the live-detection integration point <see cref="CloudSyncBridge.OnIncidentDetected"/>
    /// directly (the live-detection call site does not yet exist on main). Proves a synthetic sample is
    /// durably enqueued with the v2 cloud fingerprint as the dedup key.
    /// </summary>
    public class CloudSyncIntegrationPointTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _path;

        public CloudSyncIntegrationPointTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ss-cloudsync-" + Guid.NewGuid().ToString("N"));
            _path = Path.Combine(_dir, "pending.ndjson");
        }

        public void Dispose()
        {
            CloudSyncBridge.SharedOutbox = null;
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void OnIncidentDetected_EnqueuesLiveIncidentRow_WithV2Fingerprint()
        {
            var outbox = new CloudOutbox(_path);
            CloudSyncBridge.SharedOutbox = outbox;

            var sample = new IncidentSample(
                carIdx: 12,
                sessionTimeMs: 88250,
                detectionSource: "track_surface",
                incidentPoints: 4,
                replayFrame: 5000,
                lap: 9,
                sessionNum: 1);

            CloudSyncBridge.OnIncidentDetected(sample, "live");

            Assert.Equal(1, outbox.PendingCount);
            Assert.True(outbox.TryDequeueReady(DateTime.UtcNow, out var ready));
            var entry = Assert.Single(ready);
            Assert.Equal(CloudOutbox.KindLiveIncident, entry.Kind);

            var row = JsonConvert.DeserializeObject<IncidentRowForCloud>(entry.PayloadJson);
            Assert.Equal(12, row.CarIdx);
            Assert.Equal(88250, row.SessionTimeMs);
            Assert.Equal("track_surface", row.DetectionSource);
            Assert.Equal(4, row.IncidentPoints);

            // Fingerprint must match the v2 computation (subSessionId=0 in the static live path).
            string expected = ReplayIncidentIndexFingerprint.ComputeHexV2(
                0, 12, 88250, "track_surface", 4);
            Assert.Equal(expected, row.CloudFingerprint);
        }

        [Fact]
        public void OnIncidentDetected_NoOutboxConfigured_DoesNotThrow()
        {
            CloudSyncBridge.SharedOutbox = null;
            var sample = new IncidentSample(1, 1000, "off_track", 1, 10);
            // Must be a safe no-op when the plugin has not initialized cloud sync.
            CloudSyncBridge.OnIncidentDetected(sample, "live");
        }
    }
}
