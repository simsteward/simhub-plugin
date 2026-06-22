using System.Collections.Generic;
using System.Linq;
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

        [Fact]
        public void BuildDiscrepancies_GapField_IsExpectedMinusDetected()
        {
            var detected = new Dictionary<int, int> { { 1, 2 }, { 2, 5 } };
            var official = new Dictionary<int, int> { { 1, 4 }, { 2, 2 } };

            var d = ReplayIncidentIndexValidationComparer.BuildDiscrepancies(detected, official, null, null);

            Assert.Equal(2, d.Count);
            var car1 = d.Single(r => r.CarIdx == 1);
            var car2 = d.Single(r => r.CarIdx == 2);
            Assert.Equal(2, car1.Gap);   // under-detected: 4 - 2 = 2
            Assert.Equal(-3, car2.Gap);  // over-detected:  2 - 5 = -3
        }

        [Fact]
        public void BuildDiscrepancies_EnrichesDisplayNameAndCustId_FromIdentities()
        {
            var detected = new Dictionary<int, int> { { 3, 1 } };
            var official = new Dictionary<int, int> { { 3, 4 } };
            var identities = new List<DriverIdentity>
            {
                new DriverIdentity(3, "Win Gutmann", "987654"),
                new DriverIdentity(7, "Other Driver", "123456")
            };

            var d = ReplayIncidentIndexValidationComparer.BuildDiscrepancies(detected, official, identities, null);

            Assert.Single(d);
            Assert.Equal("Win Gutmann", d[0].DisplayName);
            Assert.Equal("987654", d[0].UniqueUserId);
        }

        [Fact]
        public void BuildDiscrepancies_MissingIdentity_EmptyStrings()
        {
            var detected = new Dictionary<int, int> { { 99, 0 } };
            var official = new Dictionary<int, int> { { 99, 2 } };

            var d = ReplayIncidentIndexValidationComparer.BuildDiscrepancies(detected, official, new List<DriverIdentity>(), null);

            Assert.Single(d);
            Assert.Equal("", d[0].DisplayName);
            Assert.Equal("", d[0].UniqueUserId);
        }

        [Fact]
        public void BuildDiscrepancies_PopulatesSourcesHit_FromSourceMap()
        {
            var detected = new Dictionary<int, int> { { 3, 3 } };
            var official = new Dictionary<int, int> { { 3, 5 } };
            var sources = new Dictionary<int, Dictionary<string, int>>
            {
                [3] = new Dictionary<string, int>
                {
                    ["yaml_incident_delta"] = 2,
                    ["track_surface"] = 1
                }
            };

            var d = ReplayIncidentIndexValidationComparer.BuildDiscrepancies(detected, official, null, sources);

            Assert.Single(d);
            Assert.Equal(2, d[0].SourcesHit["yaml_incident_delta"]);
            Assert.Equal(1, d[0].SourcesHit["track_surface"]);
        }

        [Fact]
        public void BuildDiscrepancies_SourcesHit_NoEntry_EmptyDict()
        {
            var detected = new Dictionary<int, int> { { 5, 0 } };
            var official = new Dictionary<int, int> { { 5, 1 } };

            var d = ReplayIncidentIndexValidationComparer.BuildDiscrepancies(detected, official, null, new Dictionary<int, Dictionary<string, int>>());

            Assert.Single(d);
            Assert.Empty(d[0].SourcesHit);
        }

        [Fact]
        public void BuildDiscrepancies_LegacyOverload_StillWorks_EmptyEnrichment()
        {
            var detected = new Dictionary<int, int> { { 1, 0 } };
            var official = new Dictionary<int, int> { { 1, 3 } };

            var d = ReplayIncidentIndexValidationComparer.BuildDiscrepancies(detected, official);

            Assert.Single(d);
            Assert.Equal(3, d[0].Gap);
            Assert.Equal("", d[0].DisplayName);
            Assert.Equal("", d[0].UniqueUserId);
            Assert.Empty(d[0].SourcesHit);
        }
    }
}
