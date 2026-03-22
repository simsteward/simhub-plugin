using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class ReplayIncidentIndexBuildTests
    {
        [Fact]
        public void ComputeEffectiveSessionTimeSampleHz_16x_Is375()
        {
            double hz = ReplayIncidentIndexBuild.ComputeEffectiveSessionTimeSampleHz(16);
            Assert.Equal(3.75, hz);
        }

        [Fact]
        public void ComputeEffectiveSessionTimeSampleHz_Invalid_IsZero()
        {
            Assert.Equal(0, ReplayIncidentIndexBuild.ComputeEffectiveSessionTimeSampleHz(0));
            Assert.Equal(0, ReplayIncidentIndexBuild.ComputeEffectiveSessionTimeSampleHz(-1));
        }

        [Fact]
        public void NextFrameZeroConsecutiveCount_ResetsOnNonZero()
        {
            Assert.Equal(1, ReplayIncidentIndexBuild.NextFrameZeroConsecutiveCount(0, 0));
            Assert.Equal(2, ReplayIncidentIndexBuild.NextFrameZeroConsecutiveCount(0, 1));
            Assert.Equal(0, ReplayIncidentIndexBuild.NextFrameZeroConsecutiveCount(1, 3));
        }

        [Fact]
        public void InferCompletionReason_EndOfReplay()
        {
            Assert.Equal("replay_finished", ReplayIncidentIndexBuild.InferCompletionReason(false, 998, 1000, 120.0));
        }

        [Fact]
        public void InferCompletionReason_Paused()
        {
            Assert.Equal("paused_or_stopped", ReplayIncidentIndexBuild.InferCompletionReason(false, 100, 10000, 50.0));
        }

        [Fact]
        public void CarSlotCount_Is64()
        {
            Assert.Equal(64, ReplayIncidentIndexBuild.CarSlotCount);
        }
    }
}
