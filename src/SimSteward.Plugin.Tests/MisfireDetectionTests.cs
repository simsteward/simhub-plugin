using System.Collections.Generic;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class MisfireDetectionTests
    {
        private static List<ReplayIncidentIndexIncidentRow> Idx(params (int frame, int sessTimeMs, string fp)[] rows)
        {
            var l = new List<ReplayIncidentIndexIncidentRow>();
            foreach (var (f, t, fp) in rows)
                l.Add(new ReplayIncidentIndexIncidentRow { ReplayFrame = f, SessionTimeMs = t, Fingerprint = fp });
            return l;
        }

        [Fact]
        public void EvaluateMisfire_NullIndex_FlagsMisfire()
        {
            var r = ReplayControlActions.EvaluateMisfire(null, landedFrame: 100, landedSessionTimeMs: 5000, expectedFingerprint: "abc");
            Assert.True(r.Misfire);
            Assert.Null(r.NearestRow);
        }

        [Fact]
        public void EvaluateMisfire_EmptyIndex_FlagsMisfire()
        {
            var r = ReplayControlActions.EvaluateMisfire(new List<ReplayIncidentIndexIncidentRow>(), 100, 5000, "abc");
            Assert.True(r.Misfire);
        }

        [Fact]
        public void EvaluateMisfire_LandedOnExpected_NoMisfire()
        {
            var idx = Idx((100, 5000, "abc"), (200, 6000, "def"));
            var r = ReplayControlActions.EvaluateMisfire(idx, landedFrame: 100, landedSessionTimeMs: 5050, expectedFingerprint: "abc");
            Assert.False(r.Misfire);
            Assert.Equal("abc", r.NearestRow.Fingerprint);
            Assert.Equal(50, r.DeltaMs);
        }

        [Fact]
        public void EvaluateMisfire_LandedOnDifferentFingerprint_FlagsMisfire()
        {
            var idx = Idx((100, 5000, "abc"), (200, 6000, "def"));
            var r = ReplayControlActions.EvaluateMisfire(idx, 200, 6000, expectedFingerprint: "abc");
            Assert.True(r.Misfire);
            Assert.Equal("def", r.NearestRow.Fingerprint);
        }

        [Fact]
        public void EvaluateMisfire_OutsideTolerance_FlagsMisfire()
        {
            var idx = Idx((100, 5000, "abc"));
            var r = ReplayControlActions.EvaluateMisfire(idx, landedFrame: 100, landedSessionTimeMs: 7000, expectedFingerprint: "abc");
            // |7000 - 5000| = 2000 > 500 ms → misfire
            Assert.True(r.Misfire);
            Assert.Equal(2000, r.DeltaMs);
        }

        [Fact]
        public void EvaluateMisfire_AtToleranceBoundary_NotMisfire()
        {
            var idx = Idx((100, 5000, "abc"));
            // |5500 - 5000| = 500 (== tolerance, treated as match)
            var r = ReplayControlActions.EvaluateMisfire(idx, 100, 5500, expectedFingerprint: "abc");
            Assert.False(r.Misfire);
            Assert.Equal(500, r.DeltaMs);
        }

        [Fact]
        public void EvaluateMisfire_PicksClosestRow()
        {
            var idx = Idx((100, 5000, "a"), (110, 5100, "b"), (200, 6000, "c"));
            var r = ReplayControlActions.EvaluateMisfire(idx, 110, 5050, expectedFingerprint: "b");
            // landed @5050 → closer to row b (5100, delta 50) than row a (5000, delta 50). Either way fingerprint match wins.
            // Whichever comes first should win on tie; row a delta=50, row b delta=50; first scanned wins so a.
            // Test expectation: fingerprint mismatch → misfire (we expected b, got a).
            Assert.True(r.Misfire);
            Assert.Equal("a", r.NearestRow.Fingerprint);
        }

        [Fact]
        public void BuildMisfireLogFields_PopulatesContractFields()
        {
            var idx = Idx((100, 5000, "abc"));
            var r = ReplayControlActions.EvaluateMisfire(idx, 100, 5050, "abc");
            var f = ReplayControlActions.BuildMisfireLogFields(
                "next", expectedFrame: 100, expectedSessionTimeMs: 5000,
                expectedFingerprint: "abc", result: r, indexPath: "C:/idx.json");
            Assert.Equal("next", f["direction"]);
            Assert.Equal(100, f["expected_frame"]);
            Assert.Equal(5000, f["expected_session_time_ms"]);
            Assert.Equal("abc", f["expected_fingerprint"]);
            Assert.Equal(100, f["landed_frame"]);
            Assert.Equal(5050, f["landed_session_time_ms"]);
            Assert.Equal(50, f["delta_ms"]);
            Assert.Equal("abc", f["nearest_fingerprint"]);
            Assert.Equal(5000, f["nearest_session_time_ms"]);
            Assert.Equal(false, f["misfire"]);
            Assert.Equal("C:/idx.json", f["index_path"]);
        }
    }
}
