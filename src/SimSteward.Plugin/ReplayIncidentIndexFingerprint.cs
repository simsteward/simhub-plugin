using System;
using System.Security.Cryptography;
using System.Text;

namespace SimSteward.Plugin
{
    /// <summary>TR-020 / §4.5 Fingerprint (v1): SHA-256 hex of canonical UTF-8 string.</summary>
    public static class ReplayIncidentIndexFingerprint
    {
        /// <summary>
        /// Builds <c>v1|{subSessionId}|{carIdx}|{sessionTimeMs}|{detectionSource}|{points}</c>
        /// where points is a decimal string or the literal <c>null</c>.
        /// When <paramref name="subSessionId"/> is 0 (offline session), uses a date-scoped key
        /// to prevent cross-session collision.
        /// </summary>
        public static string BuildCanonicalV1(
            int subSessionId,
            int carIdx,
            int sessionTimeMs,
            string detectionSource,
            int? incidentPoints)
        {
            // subSessionId=0 means offline session — use date-scoped key to prevent cross-session collision
            var effectiveSubSessionId = subSessionId == 0
                ? $"offline:{DateTimeOffset.UtcNow:yyyyMMdd}"
                : subSessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);

            string points = incidentPoints.HasValue
                ? incidentPoints.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "null";
            return "v1|"
                + effectiveSubSessionId + "|"
                + carIdx.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|"
                + sessionTimeMs.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|"
                + (detectionSource ?? "") + "|"
                + points;
        }

        /// <summary>64-character lowercase hex SHA-256 of UTF-8 canonical string.</summary>
        public static string ComputeHexV1(
            int subSessionId,
            int carIdx,
            int sessionTimeMs,
            string detectionSource,
            int? incidentPoints)
        {
            string canonical = BuildCanonicalV1(subSessionId, carIdx, sessionTimeMs, detectionSource, incidentPoints);
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                    sb.AppendFormat("{0:x2}", b);
                return sb.ToString();
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // PROTOTYPE — v2 sampling-rate-stable fingerprint (ADDITIVE; not wired in)
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Default quantum (ms) for v2 <c>sessionTimeMs</c> bucketing.
        /// <para>
        /// WHY 500: the index build samples at a cadence that depends on the replay
        /// fast-forward speed (~16ms apart at 1x, ~267ms apart at 16x). v1 hashes the
        /// RAW sampled <c>sessionTimeMs</c>, so the SAME physical incident gets a
        /// DIFFERENT timestamp — and a DIFFERENT fingerprint — at different sweep
        /// speeds (measured overlap: only 13/163 between 16x and 1x). v2 snaps the
        /// timestamp onto a fixed grid so one incident lands in ONE bucket across
        /// speeds. The quantum MUST be &gt;= the coarsest sample spacing (~267ms at 16x)
        /// or a single incident can still split across two buckets; 500ms gives margin.
        /// </para>
        /// <para>
        /// TRADEOFF: too LARGE risks merging two genuinely distinct incidents for the
        /// same car into one fingerprint; too SMALL fails to merge the same incident
        /// observed at different cadences. 500ms balances both for the observed speeds,
        /// but see <c>ReplayIncidentIndexFingerprintV2Tests</c> for the boundary caveat:
        /// fixed-grid quantization can still split two near-identical times that straddle
        /// a grid boundary.
        /// </para>
        /// </summary>
        public const int DefaultFingerprintTimeQuantumMs = 500;

        /// <summary>
        /// Builds <c>v2|{subSessionId}|{carIdx}|{bucketMs}|{detectionSource}|{points}</c>.
        /// Identical to <see cref="BuildCanonicalV1"/> EXCEPT the prefix is <c>v2|</c> and
        /// the time field is the quantized bucket
        /// <c>(int)(Math.Round(sessionTimeMs / quantumMs) * quantumMs)</c> instead of the
        /// raw <paramref name="sessionTimeMs"/>. This makes the fingerprint stable across
        /// sample cadences (replay sweep speeds).
        /// When <paramref name="subSessionId"/> is 0 (offline session), uses the same
        /// date-scoped key as v1 to prevent cross-session collision.
        /// </summary>
        public static string BuildCanonicalV2(
            int subSessionId,
            int carIdx,
            int sessionTimeMs,
            string detectionSource,
            int? incidentPoints,
            int quantumMs = DefaultFingerprintTimeQuantumMs)
        {
            // subSessionId=0 means offline session — use date-scoped key to prevent cross-session collision
            var effectiveSubSessionId = subSessionId == 0
                ? $"offline:{DateTimeOffset.UtcNow:yyyyMMdd}"
                : subSessionId.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // Quantize the sampled sessionTimeMs onto a fixed grid so the same physical
            // incident lands in one bucket regardless of sweep speed / sample cadence.
            int bucketMs = (int)(Math.Round((double)sessionTimeMs / quantumMs) * quantumMs);

            string points = incidentPoints.HasValue
                ? incidentPoints.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "null";
            return "v2|"
                + effectiveSubSessionId + "|"
                + carIdx.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|"
                + bucketMs.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|"
                + (detectionSource ?? "") + "|"
                + points;
        }

        /// <summary>64-character lowercase hex SHA-256 of the v2 UTF-8 canonical string.</summary>
        public static string ComputeHexV2(
            int subSessionId,
            int carIdx,
            int sessionTimeMs,
            string detectionSource,
            int? incidentPoints,
            int quantumMs = DefaultFingerprintTimeQuantumMs)
        {
            string canonical = BuildCanonicalV2(subSessionId, carIdx, sessionTimeMs, detectionSource, incidentPoints, quantumMs);
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                    sb.AppendFormat("{0:x2}", b);
                return sb.ToString();
            }
        }
    }
}
