using Xunit;

namespace SimSteward.Plugin.Tests
{
    /// <summary>
    /// PROTOTYPE tests for the v2 sampling-rate-stable incident fingerprint.
    /// v1 hashes the RAW sampled <c>sessionTimeMs</c>, so the same physical incident
    /// sampled at different replay sweep speeds (~16ms apart at 1x vs ~267ms at 16x)
    /// gets a different timestamp and therefore a different fingerprint. v2 quantizes
    /// the timestamp onto a fixed 500ms grid so one incident lands in one bucket
    /// regardless of cadence. These tests prove the win and honestly document the
    /// fixed-grid boundary limitation.
    /// </summary>
    public class ReplayIncidentIndexFingerprintV2Tests
    {
        private const int SubSession = 12345678;
        private const int Car = 3;
        private const string Source = "player_incident_count";

        // 1. CROSS-SPEED STABILITY — the core proof.
        // 42183ms (sampled at one cadence) and 42050ms (the same incident sampled at a
        // coarser cadence) both round to bucket 42000 under a 500ms quantum, so v2 yields
        // the SAME hash — while v1 (raw timestamp) yields DIFFERENT hashes for the two.
        [Fact]
        public void V2_SameIncidentDifferentCadence_SameBucket_ProducesSameHash_WhileV1Differs()
        {
            const int tFast = 42183; // e.g. sampled during a 1x sweep
            const int tSlow = 42050; // e.g. the same incident sampled during a 16x sweep

            string v2a = ReplayIncidentIndexFingerprint.ComputeHexV2(SubSession, Car, tFast, Source, 2);
            string v2b = ReplayIncidentIndexFingerprint.ComputeHexV2(SubSession, Car, tSlow, Source, 2);

            // THE WIN: v2 collapses both cadence samples onto the same fingerprint.
            Assert.Equal(v2a, v2b);

            // Sanity: both actually landed in bucket 42000.
            Assert.Equal("v2|12345678|3|42000|player_incident_count|2",
                ReplayIncidentIndexFingerprint.BuildCanonicalV2(SubSession, Car, tFast, Source, 2));
            Assert.Equal("v2|12345678|3|42000|player_incident_count|2",
                ReplayIncidentIndexFingerprint.BuildCanonicalV2(SubSession, Car, tSlow, Source, 2));

            // THE PROBLEM v2 SOLVES: v1 hashes raw ms, so the same incident hashes differently.
            string v1a = ReplayIncidentIndexFingerprint.ComputeHexV1(SubSession, Car, tFast, Source, 2);
            string v1b = ReplayIncidentIndexFingerprint.ComputeHexV1(SubSession, Car, tSlow, Source, 2);
            Assert.NotEqual(v1a, v1b);
        }

        // 2. DISTINCT INCIDENTS NOT MERGED — times far apart land in different buckets.
        [Fact]
        public void V2_TimesFarApart_ProduceDifferentHashes()
        {
            string near = ReplayIncidentIndexFingerprint.ComputeHexV2(SubSession, Car, 42000, Source, 2);
            string far = ReplayIncidentIndexFingerprint.ComputeHexV2(SubSession, Car, 50000, Source, 2);

            Assert.NotEqual(near, far);

            // bucket(42000)=42000 ; bucket(50000)=50000
            Assert.Equal("v2|12345678|3|42000|player_incident_count|2",
                ReplayIncidentIndexFingerprint.BuildCanonicalV2(SubSession, Car, 42000, Source, 2));
            Assert.Equal("v2|12345678|3|50000|player_incident_count|2",
                ReplayIncidentIndexFingerprint.BuildCanonicalV2(SubSession, Car, 50000, Source, 2));
        }

        // 3a. BOUNDARY — the prompt's example pair MERGES (both inside one bucket).
        //     12400 -> round(24.8)=25 -> 12500 ; 12740 -> round(25.48)=25 -> 12500.
        //     Asserting the ACTUAL behavior: these two DO merge (same bucket, same hash).
        [Fact]
        public void V2_BoundaryCaveat_TimesInsideSameBucket_DoMerge()
        {
            const int t1 = 12400; // -> bucket 12500
            const int t2 = 12740; // -> bucket 12500 (still below the 12750 grid boundary)

            Assert.Equal("v2|12345678|3|12500|player_incident_count|2",
                ReplayIncidentIndexFingerprint.BuildCanonicalV2(SubSession, Car, t1, Source, 2));
            Assert.Equal("v2|12345678|3|12500|player_incident_count|2",
                ReplayIncidentIndexFingerprint.BuildCanonicalV2(SubSession, Car, t2, Source, 2));

            Assert.Equal(
                ReplayIncidentIndexFingerprint.ComputeHexV2(SubSession, Car, t1, Source, 2),
                ReplayIncidentIndexFingerprint.ComputeHexV2(SubSession, Car, t2, Source, 2));
        }

        // 3b. BOUNDARY CAVEAT — the honest limitation of fixed-grid quantization.
        //     12740 and 12760 are only 20ms apart (clearly the SAME incident), but they
        //     STRADDLE the 12750 grid boundary: 12740 -> 12500, 12760 -> 13000. Different
        //     buckets => different hashes, so v2 FAILS to merge them.
        //     NOTE: this is inherent to fixed-grid quantization — a production fix may need
        //     tolerance-window matching (cluster within +/- N ms) or event-onset time
        //     rather than snapping the raw sample onto a global grid.
        [Fact]
        public void V2_BoundaryCaveat_NearIdenticalTimesStraddlingGridBoundary_DoNotMerge()
        {
            const int t1 = 12740; // -> round(25.48)=25 -> bucket 12500
            const int t2 = 12760; // -> round(25.52)=26 -> bucket 13000

            // Assert the REAL (and undesirable) behavior — do not fake a pass.
            Assert.Equal("v2|12345678|3|12500|player_incident_count|2",
                ReplayIncidentIndexFingerprint.BuildCanonicalV2(SubSession, Car, t1, Source, 2));
            Assert.Equal("v2|12345678|3|13000|player_incident_count|2",
                ReplayIncidentIndexFingerprint.BuildCanonicalV2(SubSession, Car, t2, Source, 2));

            Assert.NotEqual(
                ReplayIncidentIndexFingerprint.ComputeHexV2(SubSession, Car, t1, Source, 2),
                ReplayIncidentIndexFingerprint.ComputeHexV2(SubSession, Car, t2, Source, 2));
        }

        // 4a. DETERMINISM — same inputs always produce the same hash.
        [Fact]
        public void V2_IsDeterministic_ForSameInputs()
        {
            string a = ReplayIncidentIndexFingerprint.ComputeHexV2(SubSession, Car, 42183, Source, 2);
            string b = ReplayIncidentIndexFingerprint.ComputeHexV2(SubSession, Car, 42183, Source, 2);
            Assert.Equal(a, b);
            Assert.Equal(64, a.Length); // lowercase SHA-256 hex
        }

        // 4b. PREFIX DIFFERENTIATION — even when the bucket equals the raw time (42000),
        //     v2's "v2|" prefix means its hash differs from v1's for the same logical inputs.
        [Fact]
        public void V2_DiffersFromV1_ForSameLogicalInputs()
        {
            const int t = 42000; // bucket(42000) == 42000, so only the version prefix differs
            string v1 = ReplayIncidentIndexFingerprint.ComputeHexV1(SubSession, Car, t, Source, 2);
            string v2 = ReplayIncidentIndexFingerprint.ComputeHexV2(SubSession, Car, t, Source, 2);
            Assert.NotEqual(v1, v2);
        }

        // 5a. QUANTUM PARAMETER respected — a different quantum yields a different bucket.
        [Fact]
        public void V2_QuantumParameter_ChangesBucketAndHash()
        {
            const int t = 42183;

            // 500ms grid -> 42000 ; 1000ms grid -> 42000 ; 250ms grid -> 42250.
            Assert.Equal("v2|12345678|3|42000|player_incident_count|2",
                ReplayIncidentIndexFingerprint.BuildCanonicalV2(SubSession, Car, t, Source, 2, quantumMs: 500));
            Assert.Equal("v2|12345678|3|42250|player_incident_count|2",
                ReplayIncidentIndexFingerprint.BuildCanonicalV2(SubSession, Car, t, Source, 2, quantumMs: 250));

            Assert.NotEqual(
                ReplayIncidentIndexFingerprint.ComputeHexV2(SubSession, Car, t, Source, 2, quantumMs: 500),
                ReplayIncidentIndexFingerprint.ComputeHexV2(SubSession, Car, t, Source, 2, quantumMs: 250));
        }

        // 5b. DEFAULT QUANTUM is 500ms.
        [Fact]
        public void V2_DefaultQuantum_Is500()
        {
            Assert.Equal(500, ReplayIncidentIndexFingerprint.DefaultFingerprintTimeQuantumMs);
        }

        // 5c. OFFLINE PATH (subSessionId == 0) still works — date-scoped key, v2 prefix,
        //     quantized bucket. Deterministic within a single UTC day.
        [Fact]
        public void V2_OfflineSubSession_UsesDateScopedKey_AndQuantizes()
        {
            string canonical = ReplayIncidentIndexFingerprint.BuildCanonicalV2(0, Car, 42183, Source, 2);

            // offline:yyyyMMdd key, "v2|" prefix, quantized bucket 42000.
            Assert.StartsWith("v2|offline:", canonical);
            Assert.EndsWith("|3|42000|player_incident_count|2", canonical);

            // Deterministic and 64-hex within the same call window.
            string h = ReplayIncidentIndexFingerprint.ComputeHexV2(0, Car, 42183, Source, 2);
            Assert.Equal(64, h.Length);
        }
    }
}
