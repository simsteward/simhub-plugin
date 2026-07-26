using System;
using System.Threading.Tasks;
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

        [Fact]
        public void ShouldLog_ConcurrentCallsFromMultipleThreads_DoNotThrowAndRemainConsistent()
        {
            var t = new PerCarReadFailureThrottle(TimeSpan.FromSeconds(10));
            var t0 = new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc);
            string[] keys = { "CarIdxSessionFlags", "CarIdxTrackSurface", "CarIdxGForce" };

            var tasks = new Task[40];
            for (int i = 0; i < tasks.Length; i++)
            {
                int taskIndex = i;
                tasks[taskIndex] = Task.Run(() =>
                {
                    for (int j = 0; j < 200; j++)
                    {
                        var key = keys[(taskIndex + j) % keys.Length];
                        t.ShouldLog(key, t0.AddMilliseconds(j));
                    }
                });
            }

            var ex = Record.Exception(() => Task.WaitAll(tasks));

            Assert.Null(ex);
            // Dictionary is left in a consistent, usable state: a fresh key still logs on first call.
            Assert.True(t.ShouldLog("CarIdxNewKeyAfterConcurrency", t0));
            Assert.False(t.ShouldLog("CarIdxNewKeyAfterConcurrency", t0.AddSeconds(1)));
        }
    }
}
