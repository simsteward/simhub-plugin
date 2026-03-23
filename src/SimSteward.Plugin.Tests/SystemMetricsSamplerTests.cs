using System.IO;
using System.Threading;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class SystemMetricsSamplerTests
    {
        [Fact]
        public void TrySample_AfterElapsedWindow_ReturnsMetrics()
        {
            var tmp = Path.Combine(Path.GetTempPath(), "simsteward-metrics-test");
            Directory.CreateDirectory(tmp);
            try
            {
                var s = new SystemMetricsSampler();
                Thread.Sleep(400);
                var sample = s.TrySample(tmp, 0, 60);
                Assert.NotNull(sample);
                Assert.InRange(sample.ProcessCpuPct, 0, 100);
                Assert.True(sample.ProcessWorkingSetMb > 0);
                Assert.True(sample.ProcessPrivateMb > 0);
                Assert.NotNull(SystemMetricsSampler.ToLogFields(sample));
            }
            finally
            {
                try
                {
                    Directory.Delete(tmp, true);
                }
                catch
                {
                    // ignore
                }
            }
        }
    }
}
