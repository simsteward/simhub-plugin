using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class ReplayIncidentIndexFingerprintTests
    {
        [Fact]
        public void BuildCanonicalV1_MatchesSpecExampleShape()
        {
            string c = ReplayIncidentIndexFingerprint.BuildCanonicalV1(
                12345678,
                3,
                184320,
                "repair_flag",
                null);
            Assert.Equal("v1|12345678|3|184320|repair_flag|null", c);
        }

        [Fact]
        public void ComputeHexV1_GoldenVector_FromPythonSha256()
        {
            string hex = ReplayIncidentIndexFingerprint.ComputeHexV1(
                12345678,
                3,
                184320,
                "repair_flag",
                null);
            Assert.Equal(
                "a34e9c648098f5b549f97918c54ce5477d168f408d96e1edfdac614f766a4fdb",
                hex);
        }

        [Fact]
        public void ComputeHexV1_WithIncidentPoints_UsesInvariantDigits()
        {
            string c = ReplayIncidentIndexFingerprint.BuildCanonicalV1(
                1,
                0,
                312800,
                "player_incident_count",
                2);
            Assert.Equal("v1|1|0|312800|player_incident_count|2", c);
        }
    }
}
