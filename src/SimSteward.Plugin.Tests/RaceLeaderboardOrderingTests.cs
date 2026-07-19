using System.Collections.Generic;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class RaceLeaderboardOrderingTests
    {
        [Fact]
        public void DeriveLiveOrder_OrdersByTotalDistance_Descending()
        {
            var cars = new List<RaceLeaderboardOrdering.CarProgress>
            {
                new RaceLeaderboardOrdering.CarProgress(5, 3, 0.10f),
                new RaceLeaderboardOrdering.CarProgress(6, 3, 0.90f),
                new RaceLeaderboardOrdering.CarProgress(7, 2, 0.99f)
            };
            var order = RaceLeaderboardOrdering.DeriveLiveOrder(cars);
            Assert.Equal(new List<int> { 6, 5, 7 }, order);
        }

        [Fact]
        public void DeriveLiveOrder_SameLap_HigherLapDistPctLeads()
        {
            var cars = new List<RaceLeaderboardOrdering.CarProgress>
            {
                new RaceLeaderboardOrdering.CarProgress(1, 5, 0.20f),
                new RaceLeaderboardOrdering.CarProgress(2, 5, 0.80f)
            };
            var order = RaceLeaderboardOrdering.DeriveLiveOrder(cars);
            Assert.Equal(new List<int> { 2, 1 }, order);
        }

        [Fact]
        public void DeriveLiveOrder_HigherLapAlwaysLeadsRegardlessOfLapDistPct()
        {
            var cars = new List<RaceLeaderboardOrdering.CarProgress>
            {
                new RaceLeaderboardOrdering.CarProgress(1, 4, 0.99f),
                new RaceLeaderboardOrdering.CarProgress(2, 5, 0.01f)
            };
            var order = RaceLeaderboardOrdering.DeriveLiveOrder(cars);
            Assert.Equal(new List<int> { 2, 1 }, order);
        }

        [Fact]
        public void DeriveLiveOrder_EmptyOrNull_ReturnsEmpty()
        {
            Assert.Empty(RaceLeaderboardOrdering.DeriveLiveOrder(null));
            Assert.Empty(RaceLeaderboardOrdering.DeriveLiveOrder(new List<RaceLeaderboardOrdering.CarProgress>()));
        }
    }
}
