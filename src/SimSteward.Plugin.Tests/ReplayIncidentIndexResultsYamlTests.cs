using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class ReplayIncidentIndexResultsYamlTests
    {
        private const string FixtureMinimal = @"
SessionInfo:
  Sessions:
  - SessionNum: 0
    SessionType: Practice
  - SessionNum: 1
    SessionType: Race
    ResultsPositions:
    - Position: 1
      CarIdx: 3
      Incidents: 2
    - Position: 2
      CarIdx: 7
      Incidents: 1
    ResultsFastestLap:
    - CarIdx: 3
";

        [Fact]
        public void TryParseOfficialIncidentsByCarIdx_PrefersSessionNum()
        {
            bool ok = ReplayIncidentIndexResultsYaml.TryParseOfficialIncidentsByCarIdx(
                FixtureMinimal,
                1,
                out var map,
                out int sessionUsed,
                out string err);

            Assert.True(ok, err);
            Assert.Equal(1, sessionUsed);
            Assert.Equal(2, map[3]);
            Assert.Equal(1, map[7]);
        }

        [Fact]
        public void TryParseOfficialIncidentsByCarIdx_FallbackToLastBlock()
        {
            bool ok = ReplayIncidentIndexResultsYaml.TryParseOfficialIncidentsByCarIdx(
                FixtureMinimal,
                99,
                out var map,
                out int sessionUsed,
                out string err);

            Assert.True(ok, err);
            Assert.Equal(1, sessionUsed);
            Assert.Equal(2, map.Count);
        }

        [Fact]
        public void TryParseOfficialIncidentsByCarIdx_EmptyYaml_Fails()
        {
            bool ok = ReplayIncidentIndexResultsYaml.TryParseOfficialIncidentsByCarIdx(
                "",
                0,
                out _,
                out _,
                out string err);
            Assert.False(ok);
            Assert.Equal("empty_yaml", err);
        }
    }
}
