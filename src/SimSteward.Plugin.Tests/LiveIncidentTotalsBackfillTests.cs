using System.Collections.Generic;
using SimSteward.Plugin;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    /// <summary>
    /// Live-session backfill: once official results post while still connected live (all-zero flips
    /// to at least one non-zero total), the dashboard's Live tab should show each driver's real
    /// point TOTAL — never per-incident, since a single late YAML delta can't be attributed to a
    /// specific earlier board row without the full chronological re-sweep the Replay Index Build
    /// already does. See SimStewardPlugin.LiveIncidentDetection.cs / IRACING-DATA-AVAILABILITY.md.
    /// </summary>
    public class LiveIncidentTotalsBackfillTests
    {
        [Fact]
        public void IsFinal_Null_NotFinal()
        {
            Assert.False(LiveIncidentTotalsBackfill.IsFinal(null));
        }

        [Fact]
        public void IsFinal_Empty_NotFinal()
        {
            Assert.False(LiveIncidentTotalsBackfill.IsFinal(new Dictionary<int, int>()));
        }

        [Fact]
        public void IsFinal_AllZero_NotFinal()
        {
            var m = new Dictionary<int, int> { [0] = 0, [1] = 0, [2] = 0 };
            Assert.False(LiveIncidentTotalsBackfill.IsFinal(m));
        }

        [Fact]
        public void IsFinal_AnyNonZero_IsFinal()
        {
            var m = new Dictionary<int, int> { [0] = 0, [1] = 6, [2] = 0 };
            Assert.True(LiveIncidentTotalsBackfill.IsFinal(m));
        }

        [Fact]
        public void HasChanged_DifferentCounts_True()
        {
            var prev = new Dictionary<int, int> { [0] = 1 };
            var curr = new Dictionary<int, int> { [0] = 1, [1] = 2 };
            Assert.True(LiveIncidentTotalsBackfill.HasChanged(prev, curr));
        }

        [Fact]
        public void HasChanged_SameValues_False()
        {
            var prev = new Dictionary<int, int> { [0] = 1, [1] = 6 };
            var curr = new Dictionary<int, int> { [0] = 1, [1] = 6 };
            Assert.False(LiveIncidentTotalsBackfill.HasChanged(prev, curr));
        }

        [Fact]
        public void HasChanged_ValueDiffers_True()
        {
            var prev = new Dictionary<int, int> { [0] = 1, [1] = 6 };
            var curr = new Dictionary<int, int> { [0] = 1, [1] = 8 };
            Assert.True(LiveIncidentTotalsBackfill.HasChanged(prev, curr));
        }

        [Fact]
        public void HasChanged_PrevNull_TrueWhenCurrHasData()
        {
            Assert.True(LiveIncidentTotalsBackfill.HasChanged(null, new Dictionary<int, int> { [0] = 1 }));
        }

        [Fact]
        public void HasChanged_PrevNull_FalseWhenCurrEmpty()
        {
            Assert.False(LiveIncidentTotalsBackfill.HasChanged(null, new Dictionary<int, int>()));
        }
    }
}
