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
            d.Reset(baseF, 0, 0);

            var next = Zeros64();
            next[3] = ReplayIncidentIndexDetection.RepairSessionFlag;
            var r = d.Process(10.0, next, 0, 0, 100);

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
            d.Reset(baseF, 0, 0);

            var next = Zeros64();
            next[5] = ReplayIncidentIndexDetection.FurledSessionFlag;
            var r = d.Process(2.0, next, 0, 0, 1);

            Assert.Single(r);
            Assert.Equal(5, r[0].CarIdx);
            Assert.Equal(ReplayIncidentIndexDetection.SourceFurledFlag, r[0].DetectionSource);
        }

        [Fact]
        public void Process_RepairAndFurledSameCarSameTick_TwoRows()
        {
            var d = new ReplayIncidentIndexDetector();
            var baseF = Zeros64();
            d.Reset(baseF, 0, 0);

            var next = Zeros64();
            next[7] = ReplayIncidentIndexDetection.RepairSessionFlag | ReplayIncidentIndexDetection.FurledSessionFlag;
            var r = d.Process(0, next, 0, 0, 0);

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
            d.Reset(baseF, 0, 0);

            var same = (int[])baseF.Clone();
            Assert.Empty(d.Process(0, same, 0, 0, 0));

            var cleared = (int[])baseF.Clone();
            cleared[2] = 0;
            Assert.Empty(d.Process(1, cleared, 0, 0, 1));

            var again = (int[])cleared.Clone();
            again[2] = ReplayIncidentIndexDetection.RepairSessionFlag;
            var r = d.Process(2, again, 0, 0, 2);
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
            d.Reset(f, 0, 0);

            var r = d.Process(0, f, delta, 0, 0);
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
            d.Reset(f, 0, 0);

            // First sample establishes a non-zero baseline post-baseline-reset-window
            // (the guard only fires when prev == 0 and time < 1s).
            d.Process(2.0, f, 1, 1, 0);

            // Subsequent non-standard delta (5) — not in {1,2,4} so points should be null.
            var r = d.Process(5.0, f, 6, 1, 0);
            Assert.Single(r);
            Assert.Equal(1, r[0].CarIdx);
            Assert.Null(r[0].IncidentPoints);
        }

        [Fact]
        public void Process_PlayerIncidentBaselineLate_NonStandardDeltaSuppressed()
        {
            // Reproduces the Phoenix t=150ms false positive: at race start, _prevPlayerIncidents=0
            // (captured at frame 0 before iRacing populated the field); a sudden N-incident "jump"
            // within the first second with a non-standard delta is a baseline-init artifact, not real.
            var d = new ReplayIncidentIndexDetector();
            var f = Zeros64();
            d.Reset(f, 0, 0);

            var r = d.Process(0.15, f, 5, 0, 9);
            Assert.Empty(r);
        }

        [Fact]
        public void Process_TrackSurface_OnTrackToOffTrack_EmitsRow()
        {
            // Authoritative iRacing irsdk_TrkLoc values: OnTrack=3, OffTrack=0.
            var d = new ReplayIncidentIndexDetector();
            var baseFlags = Zeros64();
            var baseSurf = Zeros64();
            for (int i = 0; i < baseSurf.Length; i++) baseSurf[i] = ReplayIncidentIndexDetection.TrackSurfaceOnTrack;
            d.Reset(baseFlags, 0, 0, baseSurf);

            var nextSurf = (int[])baseSurf.Clone();
            nextSurf[12] = ReplayIncidentIndexDetection.TrackSurfaceOffTrack;
            var r = d.Process(5.0, baseFlags, 0, 0, 200, nextSurf);

            Assert.Single(r);
            Assert.Equal(12, r[0].CarIdx);
            Assert.Equal(ReplayIncidentIndexDetection.SourceTrackSurface, r[0].DetectionSource);
        }

        [Fact]
        public void Process_TrackSurface_PitStallNotTreatedAsOffTrack()
        {
            // Defends against the earlier constant bug where TrackSurfaceOffTrack was wrongly set to 1
            // (which is actually InPitStall). Going OnTrack → InPitStall is a normal pit entry,
            // NOT an off-track incident and must not emit.
            var d = new ReplayIncidentIndexDetector();
            var baseFlags = Zeros64();
            var baseSurf = Zeros64();
            for (int i = 0; i < baseSurf.Length; i++) baseSurf[i] = ReplayIncidentIndexDetection.TrackSurfaceOnTrack;
            d.Reset(baseFlags, 0, 0, baseSurf);

            var nextSurf = (int[])baseSurf.Clone();
            nextSurf[4] = 1; // irsdk_InPitStall
            var r = d.Process(5.0, baseFlags, 0, 0, 200, nextSurf);

            Assert.Empty(r);
        }

        [Fact]
        public void Process_TrackSurface_NotInWorldDoesNotEmit()
        {
            // -1 (NotInWorld) can appear during replay seek; treat as quiet.
            var d = new ReplayIncidentIndexDetector();
            var baseFlags = Zeros64();
            var baseSurf = Zeros64();
            for (int i = 0; i < baseSurf.Length; i++) baseSurf[i] = ReplayIncidentIndexDetection.TrackSurfaceOnTrack;
            d.Reset(baseFlags, 0, 0, baseSurf);

            var nextSurf = (int[])baseSurf.Clone();
            nextSurf[1] = ReplayIncidentIndexDetection.TrackSurfaceNotInWorld;
            var r = d.Process(5.0, baseFlags, 0, 0, 200, nextSurf);

            Assert.Empty(r);
        }

        [Fact]
        public void Process_InvalidPlayerCarIdx_SkipsPlayerChannelButUpdatesBaseline()
        {
            var d = new ReplayIncidentIndexDetector();
            var f = Zeros64();
            d.Reset(f, 0, -1);

            var r = d.Process(0, f, 5, 99, 0);
            Assert.Empty(r);

            var r2 = d.Process(1, f, 6, 0, 1);
            Assert.Single(r2);
            Assert.Equal(1, r2[0].IncidentPoints);
        }

        [Fact]
        public void Process_DebounceSecondRepairWithinOneSessionSecond_Suppressed()
        {
            var d = new ReplayIncidentIndexDetector();
            var f = Zeros64();
            d.Reset(f, 0, 0);

            var a = Zeros64();
            a[4] = ReplayIncidentIndexDetection.RepairSessionFlag;
            Assert.Single(d.Process(0, a, 0, 0, 0));

            var b = Zeros64();
            b[4] = 0;
            Assert.Empty(d.Process(0.2, b, 0, 0, 1));

            var c = Zeros64();
            c[4] = ReplayIncidentIndexDetection.RepairSessionFlag;
            Assert.Empty(d.Process(0.5, c, 0, 0, 2));
        }

        [Fact]
        public void Process_DebounceRepairAfterOnePointTwoSeconds_EmitsAgain()
        {
            var d = new ReplayIncidentIndexDetector();
            var f = Zeros64();
            d.Reset(f, 0, 0);

            var a = Zeros64();
            a[4] = ReplayIncidentIndexDetection.RepairSessionFlag;
            Assert.Single(d.Process(0, a, 0, 0, 0));

            var b = Zeros64();
            Assert.Empty(d.Process(1.2, b, 0, 0, 1));

            var c = Zeros64();
            c[4] = ReplayIncidentIndexDetection.RepairSessionFlag;
            var r = d.Process(1.2, c, 0, 0, 2);
            Assert.Single(r);
        }

        [Fact]
        public void Process_PlayerDebounced_DoesNotReEmitSameIncrementEveryTick()
        {
            var d = new ReplayIncidentIndexDetector();
            var f = Zeros64();
            d.Reset(f, 0, 0);

            var f1 = d.Process(0, f, 1, 0, 0);
            Assert.Single(f1);

            var f2 = d.Process(0.1, f, 1, 0, 1);
            Assert.Empty(f2);
        }

        [Fact]
        public void Reset_ThrowsWhenArrayTooShort()
        {
            var d = new ReplayIncidentIndexDetector();
            Assert.Throws<ArgumentException>(() => d.Reset(new int[10], 0, 0));
        }

        [Fact]
        public void Reset_ThrowsWhenPlayerCarIdxOutOfRange()
        {
            var d = new ReplayIncidentIndexDetector();
            var z = Zeros64();
            Assert.Throws<ArgumentOutOfRangeException>(() => d.Reset(z, 0, 64));
        }

        [Fact]
        public void IncidentSample_NewContextFields_DefaultToNull()
        {
            var s = new IncidentSample(carIdx: 1, sessionTimeMs: 5000, detectionSource: "test", incidentPoints: null, replayFrame: 100);
            Assert.Null(s.LapDistPct);
            Assert.Null(s.CarPosition);
        }

        [Fact]
        public void IncidentSample_NewContextFields_RoundTrip()
        {
            var s = new IncidentSample(carIdx: 1, sessionTimeMs: 5000, detectionSource: "test", incidentPoints: null, replayFrame: 100,
                lapDistPct: 0.45f, carPosition: 3);
            Assert.Equal(0.45f, s.LapDistPct);
            Assert.Equal(3, s.CarPosition);
        }

        [Fact]
        public void Process_RepairDetection_CapturesLapDistPctAndPosition()
        {
            var d = new ReplayIncidentIndexDetector();
            d.Reset(Zeros64(), 0, 0);

            var flags = Zeros64();
            flags[3] = ReplayIncidentIndexDetection.RepairSessionFlag;

            var lapDistPct = new float[64];
            lapDistPct[3] = 0.72f;
            var positions = Zeros64();
            positions[3] = 5;

            var r = d.Process(10.0, flags, 0, 0, 100,
                carIdxLapDistPct: lapDistPct, carIdxPosition: positions);

            Assert.Single(r);
            Assert.Equal(0.72f, r[0].LapDistPct);
            Assert.Equal(5, r[0].CarPosition);
        }
    }
}
