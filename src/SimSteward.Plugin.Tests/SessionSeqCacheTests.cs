using System;
using SimSteward.Plugin;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class SessionSeqCacheTests
    {
        [Fact]
        public void Resolve_EmptyTrackName_ReturnsEmpty()
        {
            var cache = new SessionSeqCache();
            Assert.Equal("", cache.Resolve("", new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc)));
        }

        [Fact]
        public void Resolve_FirstCall_BuildsSanitizedSeq()
        {
            var cache = new SessionSeqCache();
            var result = cache.Resolve("Road America!", new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc));
            Assert.Equal("Road_America__20260726", result);
        }

        [Fact]
        public void Resolve_SameTrackSameDay_ReturnsCachedInstance()
        {
            var cache = new SessionSeqCache();
            var utc = new DateTime(2026, 7, 26, 10, 0, 0, DateTimeKind.Utc);
            var first = cache.Resolve("Road America", utc);
            var second = cache.Resolve("Road America", utc.AddSeconds(1));
            Assert.Same(first, second);
        }

        [Fact]
        public void Resolve_TrackChanges_Rebuilds()
        {
            var cache = new SessionSeqCache();
            var utc = new DateTime(2026, 7, 26, 10, 0, 0, DateTimeKind.Utc);
            cache.Resolve("Road America", utc);
            var result = cache.Resolve("Watkins Glen", utc);
            Assert.Equal("Watkins_Glen_20260726", result);
        }

        [Fact]
        public void Resolve_DayChanges_Rebuilds()
        {
            var cache = new SessionSeqCache();
            var day1 = new DateTime(2026, 7, 26, 23, 59, 0, DateTimeKind.Utc);
            var day2 = new DateTime(2026, 7, 27, 0, 1, 0, DateTimeKind.Utc);
            var first = cache.Resolve("Road America", day1);
            var second = cache.Resolve("Road America", day2);
            Assert.NotEqual(first, second);
        }
    }
}
