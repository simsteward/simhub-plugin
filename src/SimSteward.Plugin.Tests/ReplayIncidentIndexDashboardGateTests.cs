using Xunit;

namespace SimSteward.Plugin.Tests
{
    /// <summary>
    /// Regression coverage for the bug where an iRacing disconnect mid-build (before
    /// FinalizeReplayIndexBuildLocked writes an index file) left the dashboard's
    /// one-shot disk-load-attempted gate permanently marked "already tried" for that
    /// subsession — so after reconnecting, neither the disk read nor the auto-build
    /// retrigger ever ran again, and the index never loaded.
    /// </summary>
    public class ReplayIncidentIndexDashboardGateTests
    {
        [Fact]
        public void OnBuildAbortedBeforeFinalize_BuildWasInProgress_ClearsGate()
        {
            int result = ReplayIncidentIndexDashboardGate.OnBuildAbortedBeforeFinalize(
                buildWasInProgress: true,
                currentDiskLoadAttemptedForSub: 87323226);

            Assert.Equal(-1, result);
        }

        [Fact]
        public void OnBuildAbortedBeforeFinalize_NoBuildInProgress_LeavesGateUnchanged()
        {
            int result = ReplayIncidentIndexDashboardGate.OnBuildAbortedBeforeFinalize(
                buildWasInProgress: false,
                currentDiskLoadAttemptedForSub: 87323226);

            Assert.Equal(87323226, result);
        }

        [Fact]
        public void OnBuildAbortedBeforeFinalize_NoBuildInProgress_GateAlreadyUnset_StaysUnset()
        {
            int result = ReplayIncidentIndexDashboardGate.OnBuildAbortedBeforeFinalize(
                buildWasInProgress: false,
                currentDiskLoadAttemptedForSub: -1);

            Assert.Equal(-1, result);
        }
    }
}
