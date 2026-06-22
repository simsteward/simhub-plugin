using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class ReplayIncidentYamlDiffTests
    {
        private static int[] Lap64(int defaultLap = SessionLogging.LapUnknown)
        {
            var a = new int[ReplayIncidentIndexBuild.CarSlotCount];
            for (int i = 0; i < a.Length; i++) a[i] = defaultLap;
            return a;
        }

        [Fact]
        public void Diff_NullPrev_EmitsNothing_BaselineOnly()
        {
            var curr = new Dictionary<int, int> { [1] = 2, [3] = 0 };
            var r = ReplayIncidentYamlDiff.Diff(null, curr, 60.0, 100, 2, Lap64());
            Assert.Empty(r);
        }

        [Fact]
        public void Diff_NullCurr_EmitsNothing()
        {
            var prev = new Dictionary<int, int> { [1] = 2 };
            var r = ReplayIncidentYamlDiff.Diff(prev, null, 60.0, 100, 2, Lap64());
            Assert.Empty(r);
        }

        [Fact]
        public void Diff_NoChange_EmitsNothing()
        {
            var prev = new Dictionary<int, int> { [1] = 4, [2] = 2, [5] = 6 };
            var curr = new Dictionary<int, int> { [1] = 4, [2] = 2, [5] = 6 };
            var r = ReplayIncidentYamlDiff.Diff(prev, curr, 60.0, 100, 2, Lap64());
            Assert.Empty(r);
        }

        [Fact]
        public void Diff_SingleCarRise_EmitsOneRowWithStandardPoints()
        {
            var prev = new Dictionary<int, int> { [3] = 4 };
            var curr = new Dictionary<int, int> { [3] = 6 };
            var lap = Lap64(); lap[3] = 12;

            var r = ReplayIncidentYamlDiff.Diff(prev, curr, 245.5, 14730, 2, lap);

            Assert.Single(r);
            Assert.Equal(3, r[0].CarIdx);
            Assert.Equal(245500, r[0].SessionTimeMs);
            Assert.Equal(ReplayIncidentIndexDetection.SourceYamlIncidentDelta, r[0].DetectionSource);
            Assert.Equal(2, r[0].IncidentPoints);
            Assert.Equal(14730, r[0].ReplayFrame);
            Assert.Equal(12, r[0].Lap);
            Assert.Equal(2, r[0].SessionNum);
        }

        [Theory]
        [InlineData(0, 1, 1)]
        [InlineData(0, 2, 2)]
        [InlineData(2, 6, 4)]
        public void Diff_StandardDeltas_MapToIncidentPoints(int prevCount, int currCount, int expectedPoints)
        {
            var prev = new Dictionary<int, int> { [1] = prevCount };
            var curr = new Dictionary<int, int> { [1] = currCount };
            var r = ReplayIncidentYamlDiff.Diff(prev, curr, 10.0, 600, 2, Lap64());
            Assert.Single(r);
            Assert.Equal(expectedPoints, r[0].IncidentPoints);
        }

        [Theory]
        [InlineData(0, 3)]
        [InlineData(2, 7)]
        [InlineData(4, 9)]
        public void Diff_NonStandardDelta_PointsNull(int prevCount, int currCount)
        {
            var prev = new Dictionary<int, int> { [4] = prevCount };
            var curr = new Dictionary<int, int> { [4] = currCount };
            var r = ReplayIncidentYamlDiff.Diff(prev, curr, 10.0, 600, 2, Lap64());
            Assert.Single(r);
            Assert.Null(r[0].IncidentPoints);
        }

        [Fact]
        public void Diff_MultipleCars_EmitsRowPerRiser()
        {
            var prev = new Dictionary<int, int> { [1] = 0, [3] = 4, [7] = 2 };
            var curr = new Dictionary<int, int> { [1] = 2, [3] = 4, [7] = 6 };
            var r = ReplayIncidentYamlDiff.Diff(prev, curr, 100.0, 6000, 2, Lap64());
            Assert.Equal(2, r.Count);
            Assert.Contains(r, x => x.CarIdx == 1 && x.IncidentPoints == 2);
            Assert.Contains(r, x => x.CarIdx == 7 && x.IncidentPoints == 4);
            Assert.DoesNotContain(r, x => x.CarIdx == 3);
        }

        [Fact]
        public void Diff_CountWentDown_Ignored()
        {
            // Session reset (or YAML snapshot weirdness) — caller resets baseline at session change,
            // but if a stale snapshot ever shows a count drop, must not emit a phantom row.
            var prev = new Dictionary<int, int> { [5] = 8 };
            var curr = new Dictionary<int, int> { [5] = 2 };
            var r = ReplayIncidentYamlDiff.Diff(prev, curr, 30.0, 1800, 2, Lap64());
            Assert.Empty(r);
        }

        [Fact]
        public void Diff_CarInCurrNotInPrev_TreatsPrevAsZero()
        {
            // Driver appeared after baseline (joined mid-race / SDK only just started reporting them).
            var prev = new Dictionary<int, int> { [1] = 0 };
            var curr = new Dictionary<int, int> { [1] = 0, [9] = 4 };
            var r = ReplayIncidentYamlDiff.Diff(prev, curr, 50.0, 3000, 2, Lap64());
            Assert.Single(r);
            Assert.Equal(9, r[0].CarIdx);
            Assert.Equal(4, r[0].IncidentPoints);
        }

        [Fact]
        public void Diff_CarInPrevNotInCurr_NoEmission()
        {
            // Driver dropped out — nothing to emit.
            var prev = new Dictionary<int, int> { [1] = 2, [5] = 4 };
            var curr = new Dictionary<int, int> { [1] = 3 };
            var r = ReplayIncidentYamlDiff.Diff(prev, curr, 70.0, 4200, 2, Lap64());
            Assert.Single(r);
            Assert.Equal(1, r.Single().CarIdx);
        }

        [Fact]
        public void Diff_LapTakenFromCarIdxLapArray()
        {
            var prev = new Dictionary<int, int> { [11] = 0 };
            var curr = new Dictionary<int, int> { [11] = 1 };
            var lap = Lap64(); lap[11] = 27;
            var r = ReplayIncidentYamlDiff.Diff(prev, curr, 1500.0, 90000, 2, lap);
            Assert.Single(r);
            Assert.Equal(27, r[0].Lap);
        }

        [Fact]
        public void Diff_NullLapArray_UsesUnknown()
        {
            var prev = new Dictionary<int, int> { [11] = 0 };
            var curr = new Dictionary<int, int> { [11] = 1 };
            var r = ReplayIncidentYamlDiff.Diff(prev, curr, 1500.0, 90000, 2, null);
            Assert.Single(r);
            Assert.Equal(SessionLogging.LapUnknown, r[0].Lap);
        }
    }
}
