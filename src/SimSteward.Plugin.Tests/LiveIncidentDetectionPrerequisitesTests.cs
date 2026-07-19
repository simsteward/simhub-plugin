using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class LiveIncidentDetectionPrerequisitesTests
    {
        [Fact]
        public void IsGenuinelyLive_Disconnected_False()
        {
            Assert.False(LiveIncidentDetectionPrerequisites.IsGenuinelyLive(false, "full"));
        }

        [Fact]
        public void IsGenuinelyLive_NullOrEmptyOrWhitespaceSimMode_False()
        {
            Assert.False(LiveIncidentDetectionPrerequisites.IsGenuinelyLive(true, null));
            Assert.False(LiveIncidentDetectionPrerequisites.IsGenuinelyLive(true, ""));
            Assert.False(LiveIncidentDetectionPrerequisites.IsGenuinelyLive(true, "   "));
        }

        [Fact]
        public void IsGenuinelyLive_ReplayMode_False()
        {
            Assert.False(LiveIncidentDetectionPrerequisites.IsGenuinelyLive(true, "replay"));
            Assert.False(LiveIncidentDetectionPrerequisites.IsGenuinelyLive(true, " RePlay "));
            Assert.False(LiveIncidentDetectionPrerequisites.IsGenuinelyLive(true, "REPLAY"));
        }

        [Fact]
        public void IsGenuinelyLive_ConnectedAndNotReplay_True()
        {
            Assert.True(LiveIncidentDetectionPrerequisites.IsGenuinelyLive(true, "full"));
        }

        [Fact]
        public void IsSessionBoundary_SameIds_False()
        {
            Assert.False(LiveIncidentDetectionPrerequisites.IsSessionBoundary(85380877, 2, 85380877, 2));
        }

        [Fact]
        public void IsSessionBoundary_DifferentSubSession_True()
        {
            Assert.True(LiveIncidentDetectionPrerequisites.IsSessionBoundary(85380877, 2, 85380878, 2));
        }

        [Fact]
        public void IsSessionBoundary_DifferentSessionNum_True()
        {
            // e.g. Practice (0) -> Qualify (1) within the same event.
            Assert.True(LiveIncidentDetectionPrerequisites.IsSessionBoundary(85380877, 0, 85380877, 1));
        }
    }
}
