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
        /// </summary>
        public static string BuildCanonicalV1(
            int subSessionId,
            int carIdx,
            int sessionTimeMs,
            string detectionSource,
            int? incidentPoints)
        {
            string points = incidentPoints.HasValue
                ? incidentPoints.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "null";
            return "v1|"
                + subSessionId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|"
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
    }
}
