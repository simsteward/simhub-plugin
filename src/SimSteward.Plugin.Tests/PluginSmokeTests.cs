using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class PluginSmokeTests
    {
        [Fact]
        public void PluginDiagnostics_Defaults()
        {
            var d = new PluginDiagnostics();
            Assert.False(d.IrsdkStarted);
            Assert.Equal("—", d.DashboardPing);
        }
    }
}
