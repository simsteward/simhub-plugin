using SimSteward.Plugin;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    /// <summary>
    /// "Session observed" best-effort estimate — a numeric guess shown live for other cars before
    /// any real points resolve (via player delta or, later, the Replay Index Build's YAML diff).
    /// Clearly separate from a resolved value: see LiveIncidentBoardEntry.EstimatedPoints vs
    /// Points/PointsResolved. User explicitly chose "estimate" over "null" — but only track_surface
    /// (off-track) maps confidently to iRacing's own published 1x rule; everything else is a
    /// deliberately conservative guess, documented per-case in IncidentPointsEstimate.cs.
    /// </summary>
    public class IncidentPointsEstimateTests
    {
        [Fact]
        public void TrackSurface_EstimatesOffTrack1x()
        {
            Assert.Equal(1, IncidentPointsEstimate.Estimate(ReplayIncidentIndexDetection.SourceTrackSurface));
        }

        [Fact]
        public void RepairFlag_EstimatesHeavyContact4x()
        {
            Assert.Equal(4, IncidentPointsEstimate.Estimate(ReplayIncidentIndexDetection.SourceRepairFlag));
        }

        [Fact]
        public void FastRepair_EstimatesHeavyContact4x()
        {
            Assert.Equal(4, IncidentPointsEstimate.Estimate(ReplayIncidentIndexDetection.SourceFastRepair));
        }

        [Fact]
        public void FurledFlag_EstimatesModerate2x()
        {
            Assert.Equal(2, IncidentPointsEstimate.Estimate(ReplayIncidentIndexDetection.SourceFurledFlag));
        }

        [Fact]
        public void BlackFlag_EstimatesModerate2x()
        {
            Assert.Equal(2, IncidentPointsEstimate.Estimate(ReplayIncidentIndexDetection.SourceBlackFlag));
        }

        [Fact]
        public void Disqualify_NoEstimate_NotAnIncidentPointsAnalog()
        {
            Assert.Null(IncidentPointsEstimate.Estimate(ReplayIncidentIndexDetection.SourceDisqualify));
        }

        [Fact]
        public void UnknownSource_NoEstimate()
        {
            Assert.Null(IncidentPointsEstimate.Estimate("some_future_source"));
            Assert.Null(IncidentPointsEstimate.Estimate(""));
            Assert.Null(IncidentPointsEstimate.Estimate(null));
        }

        [Fact]
        public void DirtCap_HeavyContactEstimateCapsAt2x()
        {
            Assert.Equal(2, IncidentPointsEstimate.Estimate(ReplayIncidentIndexDetection.SourceRepairFlag, isDirtSurface: true));
        }

        [Fact]
        public void DirtCap_DoesNotAffectOffTrackEstimate()
        {
            Assert.Equal(1, IncidentPointsEstimate.Estimate(ReplayIncidentIndexDetection.SourceTrackSurface, isDirtSurface: true));
        }
    }
}
