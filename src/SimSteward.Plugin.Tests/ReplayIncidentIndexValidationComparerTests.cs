using System.Collections.Generic;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class ReplayIncidentIndexValidationComparerTests
    {
        [Fact]
        public void BuildDiscrepancies_ListsMismatchesOnly()
        {
            var detected = new Dictionary<int, int> { { 3, 2 }, { 5, 1 }, { 9, 1 } };
            var official = new Dictionary<int, int> { { 3, 2 }, { 5, 2 }, { 9, 0 } };

            var d = ReplayIncidentIndexValidationComparer.BuildDiscrepancies(detected, official);

            Assert.Equal(2, d.Count);
            Assert.Contains(d, x => x.CarIdx == 5 && x.DetectedEventCount == 1 && x.YamlIncidents == 2);
            Assert.Contains(d, x => x.CarIdx == 9 && x.DetectedEventCount == 1 && x.YamlIncidents == 0);
        }
    }
}
