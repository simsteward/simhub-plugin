using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class PluginVersionInfoTests
    {
        [Fact]
        public void Display_IsNonEmpty()
        {
            Assert.False(string.IsNullOrWhiteSpace(PluginVersionInfo.Display));
        }
    }
}
