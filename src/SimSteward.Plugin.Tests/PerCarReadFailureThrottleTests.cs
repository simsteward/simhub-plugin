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
            Assert.True(t.ShouldLog("CarIdxSessionFlags", new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc)));
        }

        [Fact]
        public void ShouldLog_WithinInterval_False()
        {
            var t = new PerCarReadFailureThrottle(TimeSpan.FromSeconds(10));
            var t0 = new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc);
            t.ShouldLog("CarIdxSessionFlags", t0);
            Assert.False(t.ShouldLog("CarIdxSessionFlags", t0.AddSeconds(5)));
        }

        [Fact]
        public void ShouldLog_AfterInterval_True()
        {
            var t = new PerCarReadFailureThrottle(TimeSpan.FromSeconds(10));
            var t0 = new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc);
            t.ShouldLog("CarIdxSessionFlags", t0);
            Assert.True(t.ShouldLog("CarIdxSessionFlags", t0.AddSeconds(11)));
        }

        [Fact]
        public void ShouldLog_DifferentKeys_BothLogOnFirstFailure()
        {
            var t = new PerCarReadFailureThrottle(TimeSpan.FromSeconds(10));
            var t0 = new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc);
            Assert.True(t.ShouldLog("CarIdxSessionFlags", t0));
            Assert.True(t.ShouldLog("CarIdxTrackSurface", t0));
        }

        [Fact]
        public void ShouldLog_SameKeyStillThrottled_WithinInterval()
        {
            var t = new PerCarReadFailureThrottle(TimeSpan.FromSeconds(10));
            var t0 = new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc);
            t.ShouldLog("CarIdxSessionFlags", t0);
            t.ShouldLog("CarIdxTrackSurface", t0);
            Assert.False(t.ShouldLog("CarIdxSessionFlags", t0.AddSeconds(5)));
        }

        [Fact]
        public void ShouldLog_OneKeyThrottled_DoesNotBlockDifferentKeysFirstLog()
        {
            var t = new PerCarReadFailureThrottle(TimeSpan.FromSeconds(10));
            var t0 = new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc);
            t.ShouldLog("CarIdxSessionFlags", t0);
            Assert.False(t.ShouldLog("CarIdxSessionFlags", t0.AddSeconds(1)));
            Assert.True(t.ShouldLog("CarIdxTrackSurface", t0.AddSeconds(1)));
        }
    }
}
