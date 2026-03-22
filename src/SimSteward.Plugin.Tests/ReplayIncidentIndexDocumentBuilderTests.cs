using System.Collections.Generic;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class ReplayIncidentIndexDocumentBuilderTests
    {
        [Fact]
        public void Build_SortsBySessionTimeMs_ThenCar_ThenSource()
        {
            var samples = new List<IncidentSample>
            {
                new IncidentSample(5, 2000, ReplayIncidentIndexDetection.SourceRepairFlag, null, 9),
                new IncidentSample(1, 1000, ReplayIncidentIndexDetection.SourceFurledFlag, null, 8),
                new IncidentSample(1, 1000, ReplayIncidentIndexDetection.SourceRepairFlag, null, 7)
            };

            var root = ReplayIncidentIndexDocumentBuilder.Build(42, 99, samples, null, null);

            Assert.Equal(3, root.Incidents.Count);
            Assert.Equal(1000, root.Incidents[0].SessionTimeMs);
            Assert.Equal(1, root.Incidents[0].CarIdx);
            Assert.Equal(ReplayIncidentIndexDetection.SourceFurledFlag, root.Incidents[0].DetectionSource);
            Assert.Equal(1000, root.Incidents[1].SessionTimeMs);
            Assert.Equal(ReplayIncidentIndexDetection.SourceRepairFlag, root.Incidents[1].DetectionSource);
            Assert.Equal(2000, root.Incidents[2].SessionTimeMs);

            Assert.Equal(3, root.TotalRaceIncidents);
            Assert.True(root.IncidentCountByCarIdx.ContainsKey("1"));
            Assert.Equal(2, root.IncidentCountByCarIdx["1"]);
            Assert.Equal(1, root.IncidentCountByCarIdx["5"]);
        }

        [Fact]
        public void Fingerprint_MatchesPerRowCanonicalDigest()
        {
            var s = new IncidentSample(
                3,
                184320,
                ReplayIncidentIndexDetection.SourceRepairFlag,
                null,
                0);
            var root = ReplayIncidentIndexDocumentBuilder.Build(12345678, 1, new[] { s }, null, null);
            string expected = ReplayIncidentIndexFingerprint.ComputeHexV1(
                12345678,
                3,
                184320,
                ReplayIncidentIndexDetection.SourceRepairFlag,
                null);
            Assert.Equal(expected, root.Incidents[0].Fingerprint);
        }
    }
}
