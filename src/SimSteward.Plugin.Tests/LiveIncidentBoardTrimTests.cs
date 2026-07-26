using System.Collections.Generic;
using SimSteward.Plugin;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class LiveIncidentBoardTrimTests
    {
        private static LiveIncidentBoardEntry Entry(string id) => new LiveIncidentBoardEntry { Id = id };

        [Fact]
        public void Trim_Null_ReturnsEmptyList()
        {
            var result = LiveIncidentBoardTrim.Trim(null, 3);
            Assert.Empty(result);
        }

        [Fact]
        public void Trim_FewerThanMax_ReturnsAllInOrder()
        {
            var entries = new List<LiveIncidentBoardEntry> { Entry("a"), Entry("b") };
            var result = LiveIncidentBoardTrim.Trim(entries, 5);
            Assert.Equal(new[] { "a", "b" }, new[] { result[0].Id, result[1].Id });
        }

        [Fact]
        public void Trim_ExactlyMax_ReturnsAll()
        {
            var entries = new List<LiveIncidentBoardEntry> { Entry("a"), Entry("b"), Entry("c") };
            var result = LiveIncidentBoardTrim.Trim(entries, 3);
            Assert.Equal(3, result.Count);
        }

        [Fact]
        public void Trim_MoreThanMax_ReturnsMostRecentOnlyInOrder()
        {
            var entries = new List<LiveIncidentBoardEntry> { Entry("a"), Entry("b"), Entry("c"), Entry("d") };
            var result = LiveIncidentBoardTrim.Trim(entries, 2);
            Assert.Equal(new[] { "c", "d" }, new[] { result[0].Id, result[1].Id });
        }
    }
}
