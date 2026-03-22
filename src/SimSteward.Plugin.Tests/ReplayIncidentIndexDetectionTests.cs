using System;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class ReplayIncidentIndexDetectionTests
    {
        private static int[] Zeros64()
        {
            return new int[ReplayIncidentIndexBuild.CarSlotCount];
        }

        [Fact]
        public void IsRisingEdge_RepairMask_DetectsZeroToOne()
        {
            Assert.True(ReplayIncidentIndexDetection.IsRisingEdge(0, ReplayIncidentIndexDetection.RepairSessionFlag, ReplayIncidentIndexDetection.RepairSessionFlag));
            Assert.False(ReplayIncidentIndexDetection.IsRisingEdge(ReplayIncidentIndexDetection.RepairSessionFlag, ReplayIncidentIndexDetection.RepairSessionFlag, ReplayIncidentIndexDetection.RepairSessionFlag));
            Assert.False(ReplayIncidentIndexDetection.IsRisingEdge(ReplayIncidentIndexDetection.RepairSessionFlag, 0, ReplayIncidentIndexDetection.RepairSessionFlag));
        }

        [Fact]
        public void IsRisingEdge_FurledIndependentOfRepair()
        {
            int r = ReplayIncidentIndexDetection.RepairSessionFlag;
            int f = ReplayIncidentIndexDetection.FurledSessionFlag;
            Assert.True(ReplayIncidentIndexDetection.IsRisingEdge(r, r | f, f));
            Assert.False(ReplayIncidentIndexDetection.IsRisingEdge(r | f, r | f, f));
        }

        [Fact]
        public void ToSessionTimeMs_RoundsToIntegerMs()
        {
            Assert.Equal(1500, ReplayIncidentIndexDetection.ToSessionTimeMs(1.5));
            Assert.Equal(0, ReplayIncidentIndexDetection.ToSessionTimeMs(double.NaN));
        }

        [Fact]
        public void Process_RepairRisingEdgeOnly_EmitsOneRepairRow()
        {
            var d = new ReplayIncidentIndexDetector();
            var baseF = Zeros64();
            var fr = Zeros64();
            d.Reset(baseF, 0, 0, fr);

            var next = Zeros64();
            next[3] = ReplayIncidentIndexDetection.RepairSessionFlag;
            var r = d.Process(10.0, next, 0, 0, fr, 100);

            Assert.Single(r);
            Assert.Equal(3, r[0].CarIdx);
            Assert.Equal(10000, r[0].SessionTimeMs);
            Assert.Equal(ReplayIncidentIndexDetection.SourceRepairFlag, r[0].DetectionSource);
            Assert.Null(r[0].IncidentPoints);
            Assert.Equal(100, r[0].ReplayFrame);
        }

        [Fact]
        public void Process_FurledRisingEdgeOnly_EmitsOneFurledRow()
        {
            var d = new ReplayIncidentIndexDetector();
            var baseF = Zeros64();
            var fr = Zeros64();
            d.Reset(baseF, 0, 0, fr);

            var next = Zeros64();
            next[5] = ReplayIncidentIndexDetection.FurledSessionFlag;
            var r = d.Process(2.0, next, 0, 0, fr, 1);

            Assert.Single(r);
            Assert.Equal(5, r[0].CarIdx);
            Assert.Equal(ReplayIncidentIndexDetection.SourceFurledFlag, r[0].DetectionSource);
        }

        [Fact]
        public void Process_RepairAndFurledSameCarSameTick_TwoRows()
        {
            var d = new ReplayIncidentIndexDetector();
            var baseF = Zeros64();
            var fr = Zeros64();
            d.Reset(baseF, 0, 0, fr);

            var next = Zeros64();
            next[7] = ReplayIncidentIndexDetection.RepairSessionFlag | ReplayIncidentIndexDetection.FurledSessionFlag;
            var r = d.Process(0, next, 0, 0, fr, 0);

            Assert.Equal(2, r.Count);
            Assert.Contains(r, x => x.DetectionSource == ReplayIncidentIndexDetection.SourceRepairFlag && x.CarIdx == 7);
            Assert.Contains(r, x => x.DetectionSource == ReplayIncidentIndexDetection.SourceFurledFlag && x.CarIdx == 7);
        }

        [Fact]
        public void Process_BaselineAlreadyHasRepair_NoFireUntilClearThenSet()
        {
            var d = new ReplayIncidentIndexDetector();
            var baseF = Zeros64();
            baseF[2] = ReplayIncidentIndexDetection.RepairSessionFlag;
            var fr = Zeros64();
            d.Reset(baseF, 0, 0, fr);

            var same = (int[])baseF.Clone();
            Assert.Empty(d.Process(0, same, 0, 0, fr, 0));

            var cleared = (int[])baseF.Clone();
            cleared[2] = 0;
            Assert.Empty(d.Process(1, cleared, 0, 0, fr, 1));

            var again = (int[])cleared.Clone();
            again[2] = ReplayIncidentIndexDetection.RepairSessionFlag;
            var r = d.Process(2, again, 0, 0, fr, 2);
            Assert.Single(r);
            Assert.Equal(ReplayIncidentIndexDetection.SourceRepairFlag, r[0].DetectionSource);
            Assert.Equal(2, r[0].CarIdx);
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(2, 2)]
        [InlineData(4, 4)]
        public void Process_PlayerIncidentDelta_SetsPointsWhenStandard(int delta, int expectedPoints)
        {
            var d = new ReplayIncidentIndexDetector();
            var f = Zeros64();
            var fr = Zeros64();
            d.Reset(f, 0, 0, fr);

            var r = d.Process(0, f, delta, 0, fr, 0);
            Assert.Single(r);
            Assert.Equal(ReplayIncidentIndexDetection.SourcePlayerIncidentCount, r[0].DetectionSource);
            Assert.Equal(0, r[0].CarIdx);
            Assert.Equal(expectedPoints, r[0].IncidentPoints);
        }

        [Fact]
        public void Process_PlayerIncidentDeltaNonStandard_PointsNull()
        {
            var d = new ReplayIncidentIndexDetector();
            var f = Zeros64();
            var fr = Zeros64();
            d.Reset(f, 0, 0, fr);

            var r = d.Process(0, f, 3, 1, fr, 0);
            Assert.Single(r);
            Assert.Equal(1, r[0].CarIdx);
            Assert.Null(r[0].IncidentPoints);
        }

        [Fact]
        public void Process_InvalidPlayerCarIdx_SkipsPlayerChannelButUpdatesBaseline()
        {
            var d = new ReplayIncidentIndexDetector();
            var f = Zeros64();
            var fr = Zeros64();
            d.Reset(f, 0, -1, fr);

            var r = d.Process(0, f, 5, 99, fr, 0);
            Assert.Empty(r);

            var r2 = d.Process(1, f, 6, 0, fr, 1);
            Assert.Single(r2);
            Assert.Equal(1, r2[0].IncidentPoints);
        }

        [Fact]
        public void Process_DebounceSecondRepairWithinOneSessionSecond_Suppressed()
        {
            var d = new ReplayIncidentIndexDetector();
            var f = Zeros64();
            var fr = Zeros64();
            d.Reset(f, 0, 0, fr);

            var a = Zeros64();
            a[4] = ReplayIncidentIndexDetection.RepairSessionFlag;
            Assert.Single(d.Process(0, a, 0, 0, fr, 0));

            var b = Zeros64();
            b[4] = 0;
            Assert.Empty(d.Process(0.2, b, 0, 0, fr, 1));

            var c = Zeros64();
            c[4] = ReplayIncidentIndexDetection.RepairSessionFlag;
            Assert.Empty(d.Process(0.5, c, 0, 0, fr, 2));
        }

        [Fact]
        public void Process_DebounceRepairAfterOnePointTwoSeconds_EmitsAgain()
        {
            var d = new ReplayIncidentIndexDetector();
            var f = Zeros64();
            var fr = Zeros64();
            d.Reset(f, 0, 0, fr);

            var a = Zeros64();
            a[4] = ReplayIncidentIndexDetection.RepairSessionFlag;
            Assert.Single(d.Process(0, a, 0, 0, fr, 0));

            var b = Zeros64();
            Assert.Empty(d.Process(1.2, b, 0, 0, fr, 1));

            var c = Zeros64();
            c[4] = ReplayIncidentIndexDetection.RepairSessionFlag;
            var r = d.Process(1.2, c, 0, 0, fr, 2);
            Assert.Single(r);
        }

        [Fact]
        public void Process_PlayerDebounced_DoesNotReEmitSameIncrementEveryTick()
        {
            var d = new ReplayIncidentIndexDetector();
            var f = Zeros64();
            var fr = Zeros64();
            d.Reset(f, 0, 0, fr);

            var f1 = d.Process(0, f, 1, 0, fr, 0);
            Assert.Single(f1);

            var f2 = d.Process(0.1, f, 1, 0, fr, 1);
            Assert.Empty(f2);
        }

        [Fact]
        public void Process_FastRepairIncrement_AppendsFastRepairDelta()
        {
            var d = new ReplayIncidentIndexDetector();
            var f = Zeros64();
            var fr0 = Zeros64();
            d.Reset(f, 0, 0, fr0);

            var fr1 = Zeros64();
            fr1[8] = 1;
            Assert.Empty(d.Process(5, f, 0, 0, fr1, 7));

            Assert.Single(d.FastRepairDeltas);
            var x = d.FastRepairDeltas[0];
            Assert.Equal(8, x.CarIdx);
            Assert.Equal(5000, x.SessionTimeMs);
            Assert.Equal(7, x.ReplayFrame);
            Assert.Equal(0, x.PreviousCount);
            Assert.Equal(1, x.CurrentCount);
        }

        [Fact]
        public void Reset_ThrowsWhenArrayTooShort()
        {
            var d = new ReplayIncidentIndexDetector();
            Assert.Throws<ArgumentException>(() => d.Reset(new int[10], 0, 0, new int[64]));
        }

        [Fact]
        public void Reset_ThrowsWhenPlayerCarIdxOutOfRange()
        {
            var d = new ReplayIncidentIndexDetector();
            var z = Zeros64();
            Assert.Throws<ArgumentOutOfRangeException>(() => d.Reset(z, 0, 64, z));
        }
    }
}
