using System;
using System.Reflection;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    /// <summary>
    /// Tests for deterministic incident fingerprint (ComputeIncidentFingerprint) via reflection.
    /// </summary>
    public class IncidentTrackerFingerprintTests
    {
        private static string InvokeComputeIncidentFingerprint(
            int subSessionId, int sessionNum, int carIdx, double sessionTime, int delta)
        {
            var method = typeof(IncidentTracker).GetMethod("ComputeIncidentFingerprint",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null)
                throw new InvalidOperationException("ComputeIncidentFingerprint not found (check SIMHUB_SDK and method visibility).");

            var result = method.Invoke(null, new object[] { subSessionId, sessionNum, carIdx, sessionTime, delta });
            return (string)result;
        }

        [Fact]
        public void Same_inputs_produce_same_fingerprint()
        {
            string id1 = InvokeComputeIncidentFingerprint(12345, 2, 7, 3072.5, 4);
            string id2 = InvokeComputeIncidentFingerprint(12345, 2, 7, 3072.5, 4);
            Assert.Equal(id1, id2);
        }

        [Fact]
        public void Different_carIdx_produces_different_fingerprint()
        {
            string id1 = InvokeComputeIncidentFingerprint(12345, 2, 7, 3072.5, 4);
            string id2 = InvokeComputeIncidentFingerprint(12345, 2, 8, 3072.5, 4);
            Assert.NotEqual(id1, id2);
        }

        [Fact]
        public void Different_delta_produces_different_fingerprint()
        {
            string id1 = InvokeComputeIncidentFingerprint(12345, 2, 7, 3072.5, 4);
            string id2 = InvokeComputeIncidentFingerprint(12345, 2, 7, 3072.5, 2);
            Assert.NotEqual(id1, id2);
        }

        [Fact]
        public void Fingerprint_is_16_hex_chars_lowercase()
        {
            string id = InvokeComputeIncidentFingerprint(1, 0, 0, 60.0, 1);
            Assert.Equal(16, id.Length);
            Assert.Matches("^[0-9a-f]{16}$", id);
        }

        [Fact]
        public void Same_grace_window_bucket_produces_same_fingerprint()
        {
            // Grace window 2s: 120.0 and 121.0 both map to bucket 60
            string id1 = InvokeComputeIncidentFingerprint(99, 1, 3, 120.0, 2);
            string id2 = InvokeComputeIncidentFingerprint(99, 1, 3, 121.0, 2);
            Assert.Equal(id1, id2);
        }

        [Fact]
        public void Different_grace_window_buckets_produce_different_fingerprints()
        {
            // 120.0 -> bucket 60, 122.0 -> bucket 61
            string id1 = InvokeComputeIncidentFingerprint(1, 0, 0, 120.0, 1);
            string id2 = InvokeComputeIncidentFingerprint(1, 0, 0, 122.0, 1);
            Assert.NotEqual(id1, id2);
        }
    }
}
