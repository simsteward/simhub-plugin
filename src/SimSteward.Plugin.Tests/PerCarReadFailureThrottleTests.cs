using System;
using SimSteward.Plugin;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class PerCarReadFailureThrottleTests
    {
        [Fact]
        public void ShouldLog_FirstCall_True()
        {
            var t = new PerCarReadFailureThrottle(TimeSpan.FromSeconds(10));
            Assert.True(t.ShouldLog(new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc)));
        }

        [Fact]
        public void ShouldLog_WithinInterval_False()
        {
            var t = new PerCarReadFailureThrottle(TimeSpan.FromSeconds(10));
            var t0 = new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc);
            t.ShouldLog(t0);
            Assert.False(t.ShouldLog(t0.AddSeconds(5)));
        }

        [Fact]
        public void ShouldLog_AfterInterval_True()
        {
            var t = new PerCarReadFailureThrottle(TimeSpan.FromSeconds(10));
            var t0 = new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc);
            t.ShouldLog(t0);
            Assert.True(t.ShouldLog(t0.AddSeconds(11)));
        }
    }
}
