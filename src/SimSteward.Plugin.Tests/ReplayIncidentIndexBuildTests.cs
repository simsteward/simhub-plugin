using Xunit;

namespace SimSteward.Plugin.Tests
{
    /// <summary>
    /// TR-042 / NFR-008 / TR-004–TR-011: unit tests for <see cref="ReplayIncidentIndexBuild"/> helpers.
    /// Spec: docs/IRACING-REPLAY-INCIDENT-INDEX-REQUIREMENTS.md §2.7, §4.2–§4.3.
    /// </summary>
    public class ReplayIncidentIndexBuildTests
    {
        [Fact]
        public void ComputeEffectiveSessionTimeSampleHz_16x_Is375()
        {
            double hz = ReplayIncidentIndexBuild.ComputeEffectiveSessionTimeSampleHz(16);
            Assert.Equal(3.75, hz);
        }

        [Fact]
        public void ComputeEffectiveSessionTimeSampleHz_1x_Is60HzVsSessionTime()
        {
            Assert.Equal(60.0, ReplayIncidentIndexBuild.ComputeEffectiveSessionTimeSampleHz(1));
        }

        [Fact]
        public void ComputeEffectiveSessionTimeSampleHz_8x_Is7_5()
        {
            Assert.Equal(7.5, ReplayIncidentIndexBuild.ComputeEffectiveSessionTimeSampleHz(8));
        }

        [Fact]
        public void ComputeEffectiveSessionTimeSampleHz_Invalid_IsZero()
        {
            Assert.Equal(0, ReplayIncidentIndexBuild.ComputeEffectiveSessionTimeSampleHz(0));
            Assert.Equal(0, ReplayIncidentIndexBuild.ComputeEffectiveSessionTimeSampleHz(-1));
            Assert.Equal(0, ReplayIncidentIndexBuild.ComputeEffectiveSessionTimeSampleHz(double.NaN));
            Assert.Equal(0, ReplayIncidentIndexBuild.ComputeEffectiveSessionTimeSampleHz(double.PositiveInfinity));
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
        public void InferCompletionReason_StillPlaying()
        {
            Assert.Equal("playing", ReplayIncidentIndexBuild.InferCompletionReason(true, 0, 1000, 0));
        }

        [Fact]
        public void InferCompletionReason_EndOfReplay_Via98PercentHeuristic()
        {
            // TR-010: second branch — not within 2 frames of end, but past 98% of frame range
            Assert.Equal(
                "replay_finished",
                ReplayIncidentIndexBuild.InferCompletionReason(false, 990, 1000, 500.0));
        }

        [Fact]
        public void DefaultFastForwardPlaySpeed_Is16()
        {
            Assert.Equal(16, ReplayIncidentIndexBuild.DefaultFastForwardPlaySpeed);
        }

        [Fact]
        public void FrameZeroStableConsecutiveSamples_Is4()
        {
            Assert.Equal(4, ReplayIncidentIndexBuild.FrameZeroStableConsecutiveSamples);
        }

        [Fact]
        public void EventConstants_MatchLoggingTaxonomy()
        {
            Assert.Equal("replay_incident_index_started", ReplayIncidentIndexBuild.EventStarted);
            Assert.Equal("replay_incident_index_baseline_ready", ReplayIncidentIndexBuild.EventBaselineReady);
            Assert.Equal("replay_incident_index_fast_forward_started", ReplayIncidentIndexBuild.EventFastForwardStarted);
            Assert.Equal("replay_incident_index_fast_forward_complete", ReplayIncidentIndexBuild.EventFastForwardComplete);
            Assert.Equal("replay_incident_index_build_error", ReplayIncidentIndexBuild.EventBuildError);
            Assert.Equal("replay_incident_index_build_cancelled", ReplayIncidentIndexBuild.EventBuildCancelled);
            Assert.Equal("replay_incident_index_detection", ReplayIncidentIndexBuild.EventDetection);
            Assert.Equal("replay_incident_index_validation_summary", ReplayIncidentIndexBuild.EventValidationSummary);
        }

        [Fact]
        public void CarSlotCount_Is64()
        {
            Assert.Equal(64, ReplayIncidentIndexBuild.CarSlotCount);
        }
    }
}
