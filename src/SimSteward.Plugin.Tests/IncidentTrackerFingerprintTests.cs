using System;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    /// <summary>
    /// Tests for deterministic incident fingerprint (ComputeIncidentFingerprintV2) via reflection.
    /// </summary>
    public class IncidentTrackerFingerprintTests
    {
        private static string InvokeComputeIncidentFingerprintV2(
            int subSessionId, int sessionNum, double sessionTime, int userId, int delta)
        {
            var method = typeof(IncidentTracker).GetMethod("ComputeIncidentFingerprintV2",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null)
                throw new InvalidOperationException("ComputeIncidentFingerprintV2 not found (check IncidentTracker and method visibility).");

            var result = method.Invoke(null, new object[] { subSessionId, sessionNum, sessionTime, userId, delta });
            return (string)result;
        }

        [Fact]
        public void Same_inputs_produce_same_fingerprint()
        {
            string id1 = InvokeComputeIncidentFingerprintV2(12345, 2, 3072.5, 7001, 4);
            string id2 = InvokeComputeIncidentFingerprintV2(12345, 2, 3072.5, 7001, 4);
            Assert.Equal(id1, id2);
        }

        [Fact]
        public void Different_userId_produces_different_fingerprint()
        {
            string id1 = InvokeComputeIncidentFingerprintV2(12345, 2, 3072.5, 7001, 4);
            string id2 = InvokeComputeIncidentFingerprintV2(12345, 2, 3072.5, 7002, 4);
            Assert.NotEqual(id1, id2);
        }

        [Fact]
        public void Different_delta_produces_different_fingerprint()
        {
            string id1 = InvokeComputeIncidentFingerprintV2(12345, 2, 3072.5, 7001, 4);
            string id2 = InvokeComputeIncidentFingerprintV2(12345, 2, 3072.5, 7001, 2);
            Assert.NotEqual(id1, id2);
        }

        [Fact]
        public void Fingerprint_matches_v2_format()
        {
            string id = InvokeComputeIncidentFingerprintV2(1, 0, 60.0, 42, 1);
            Assert.Matches(new Regex(@"^ir_\d+_s\d+_t\d+_u\d+_d\d+$"), id);
        }

        [Fact]
        public void Same_grace_window_bucket_produces_same_fingerprint()
        {
            // Grace window 2s: 120.0 and 121.0 both map to timeKey 60
            string id1 = InvokeComputeIncidentFingerprintV2(99, 1, 120.0, 3, 2);
            string id2 = InvokeComputeIncidentFingerprintV2(99, 1, 121.0, 3, 2);
            Assert.Equal(id1, id2);
        }

        [Fact]
        public void Different_grace_window_buckets_produce_different_fingerprints()
        {
            // 120.0 -> t60, 122.0 -> t61
            string id1 = InvokeComputeIncidentFingerprintV2(1, 0, 120.0, 0, 1);
            string id2 = InvokeComputeIncidentFingerprintV2(1, 0, 122.0, 0, 1);
            Assert.NotEqual(id1, id2);
        }
    }
}
