using SimSteward.Plugin;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class LiveYamlProbeLogLevelTests
    {
        [Fact]
        public void IsInfoWorthy_ParseFailed_True()
        {
            Assert.True(LiveYamlProbeLogLevel.IsInfoWorthy(parseOk: false, deltaCount: 0));
        }

        [Fact]
        public void IsInfoWorthy_ParseOkWithDeltas_True()
        {
            Assert.True(LiveYamlProbeLogLevel.IsInfoWorthy(parseOk: true, deltaCount: 3));
        }

        [Fact]
        public void IsInfoWorthy_ParseOkNoDeltas_False()
        {
            Assert.False(LiveYamlProbeLogLevel.IsInfoWorthy(parseOk: true, deltaCount: 0));
        }
    }
}
