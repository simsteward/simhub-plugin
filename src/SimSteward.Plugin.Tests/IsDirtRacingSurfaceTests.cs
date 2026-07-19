using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class IsDirtRacingSurfaceTests
    {
        [Theory]
        [InlineData(7, true)]   // RacingDirt1
        [InlineData(8, true)]   // RacingDirt2
        [InlineData(19, false)] // Dirt1 (generic off-track shoulder — not the racing groove)
        [InlineData(20, false)] // Dirt2
        [InlineData(21, false)] // Dirt3
        [InlineData(22, false)] // Dirt4
        [InlineData(0, false)]  // asphalt
        [InlineData(11, false)] // rumble strip
        [InlineData(-1, false)]
        public void IsDirtRacingSurface_MatchesOnlyRacingGrooveDirt(int material, bool expected)
        {
            Assert.Equal(expected, ReplayIncidentIndexDetection.IsDirtRacingSurface(material));
        }
    }
}
