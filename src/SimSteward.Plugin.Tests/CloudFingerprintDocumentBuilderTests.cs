using System.Collections.Generic;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    /// <summary>
    /// Proves the document builder now populates the additive v2 <c>CloudFingerprint</c> on every row,
    /// and that it is stable across two samples representing the same physical incident sampled at
    /// different cadences (timestamps that fall in the same 500ms bucket) — while the v1 fingerprint,
    /// which hashes the raw timestamp, differs. See <see cref="ReplayIncidentIndexFingerprintV2Tests"/>.
    /// </summary>
    public class CloudFingerprintDocumentBuilderTests
    {
        private const int SubSession = 55512345;

        private static IncidentSample Sample(int sessionTimeMs) =>
            new IncidentSample(
                carIdx: 7,
                sessionTimeMs: sessionTimeMs,
                detectionSource: "player_incident_count",
                incidentPoints: 2,
                replayFrame: 1000,
                lap: 4,
                sessionNum: 0);

        [Fact]
        public void Build_PopulatesCloudFingerprint_OnEveryRow()
        {
            var root = ReplayIncidentIndexDocumentBuilder.Build(
                SubSession, 123L, new List<IncidentSample> { Sample(42183) });

            var row = Assert.Single(root.Incidents);
            Assert.False(string.IsNullOrEmpty(row.CloudFingerprint));
            Assert.Equal(64, row.CloudFingerprint.Length); // SHA-256 hex
            // v2 must equal the direct computation for the same inputs.
            string expected = ReplayIncidentIndexFingerprint.ComputeHexV2(
                SubSession, 7, 42183, "player_incident_count", 2);
            Assert.Equal(expected, row.CloudFingerprint);
        }

        [Fact]
        public void Build_CloudFingerprintStableAcrossCadences_WhileV1Differs()
        {
            // 42183 and 42050 both round to bucket 42000 under the 500ms quantum.
            var rootFast = ReplayIncidentIndexDocumentBuilder.Build(
                SubSession, 1L, new List<IncidentSample> { Sample(42183) });
            var rootSlow = ReplayIncidentIndexDocumentBuilder.Build(
                SubSession, 1L, new List<IncidentSample> { Sample(42050) });

            var fast = Assert.Single(rootFast.Incidents);
            var slow = Assert.Single(rootSlow.Incidents);

            // Same physical incident, different sample cadence → same cloud fingerprint.
            Assert.Equal(fast.CloudFingerprint, slow.CloudFingerprint);
            // v1 hashes the raw timestamp, so it must differ across the two cadences.
            Assert.NotEqual(fast.Fingerprint, slow.Fingerprint);
        }
    }
}
