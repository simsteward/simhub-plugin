using Xunit;

namespace SimSteward.IncidentEngine.Tests
{
    public class EngineInfoTests
    {
        [Fact]
        public void Engine_HasNameAndVersion()
        {
            Assert.Equal("SimSteward.IncidentEngine", EngineInfo.Name);
            Assert.False(string.IsNullOrWhiteSpace(EngineInfo.Version));
        }
    }
}
