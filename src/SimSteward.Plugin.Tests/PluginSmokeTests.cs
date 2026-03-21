using System.Collections.Generic;
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

        [Fact]
        public void SessionLogging_AppendRouting_DisabledWhenNoUrl()
        {
            var f = new Dictionary<string, object>();
            SessionLogging.AppendRoutingAndDestination(f);
            Assert.True(f.ContainsKey("log_env"));
            Assert.True(f.ContainsKey("loki_push_target"));
        }
    }
}
