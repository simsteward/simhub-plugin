using System.Collections.Generic;
using System.Linq;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Pure logic for deriving a live-accurate race running order. CarIdxPosition only updates
    /// at the S/F line crossing (docs/IRACING-DATA-AVAILABILITY.md), so between crossings it's
    /// stale — this derives order from total distance covered instead, which updates every tick.
    /// </summary>
    public static class RaceLeaderboardOrdering
    {
        public readonly struct CarProgress
        {
            public CarProgress(int carIdx, int lap, float lapDistPct)
            {
                CarIdx = carIdx;
                Lap = lap;
                LapDistPct = lapDistPct;
            }

            public int CarIdx { get; }
            public int Lap { get; }
            public float LapDistPct { get; }
        }

        /// <summary>Car indices ordered by total distance covered (lap + lapDistPct), leader first.</summary>
        public static List<int> DeriveLiveOrder(IEnumerable<CarProgress> cars)
        {
            if (cars == null) return new List<int>();
            return cars
                .OrderByDescending(c => c.Lap + c.LapDistPct)
                .Select(c => c.CarIdx)
                .ToList();
        }
    }
}
