using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class ReplayIncidentLiveAggregatorTests
    {
        private static IncidentSample S(int carIdx, int sessionTimeMs, string source, int? points = null, int frame = 0)
            => new IncidentSample(carIdx, sessionTimeMs, source, points, frame);

        [Fact]
        public void Apply_TrackSurface_BumpsOffTracksAndIncidents()
        {
            var agg = new ReplayIncidentLiveAggregator();
            agg.Apply(S(7, 1000, ReplayIncidentIndexDetection.SourceTrackSurface));
            Assert.Equal(1, agg.Incidents);
            Assert.Equal(1, agg.OffTracks);
            Assert.Equal(0, agg.CarContacts);
            Assert.Equal(1, agg.Points);
            Assert.Equal(1, agg.ByCarIdx[7].OffTracks);
        }

        [Fact]
        public void Apply_RepairFlag_BumpsCarContactsAndIncidents()
        {
            var agg = new ReplayIncidentLiveAggregator();
            agg.Apply(S(3, 1000, ReplayIncidentIndexDetection.SourceRepairFlag));
            Assert.Equal(1, agg.Incidents);
            Assert.Equal(1, agg.CarContacts);
            Assert.Equal(0, agg.OffTracks);
            Assert.Equal(1, agg.ByCarIdx[3].CarContacts);
        }

        [Fact]
        public void Apply_FurledFlag_BumpsCarContacts()
        {
            var agg = new ReplayIncidentLiveAggregator();
            agg.Apply(S(5, 1000, ReplayIncidentIndexDetection.SourceFurledFlag));
            Assert.Equal(1, agg.CarContacts);
        }

        [Fact]
        public void Apply_PlayerIncidentCount_BumpsCarContactsAndUsesPoints()
        {
            var agg = new ReplayIncidentLiveAggregator();
            agg.Apply(S(0, 1000, ReplayIncidentIndexDetection.SourcePlayerIncidentCount, points: 4));
            Assert.Equal(1, agg.Incidents);
            Assert.Equal(1, agg.CarContacts);
            Assert.Equal(4, agg.Points);
            Assert.Equal(4, agg.ByCarIdx[0].Points);
        }

        [Fact]
        public void Apply_PointsSum_TwoOneFour()
        {
            var agg = new ReplayIncidentLiveAggregator();
            agg.Apply(S(1, 1000, ReplayIncidentIndexDetection.SourcePlayerIncidentCount, 2));
            agg.Apply(S(1, 2000, ReplayIncidentIndexDetection.SourcePlayerIncidentCount, 1));
            agg.Apply(S(1, 3000, ReplayIncidentIndexDetection.SourcePlayerIncidentCount, 4));
            Assert.Equal(7, agg.Points);
            Assert.Equal(3, agg.Incidents);
        }

        [Fact]
        public void Reset_WipesAllCounters()
        {
            var agg = new ReplayIncidentLiveAggregator();
            agg.Apply(S(1, 1000, ReplayIncidentIndexDetection.SourceTrackSurface));
            agg.Apply(S(2, 2000, ReplayIncidentIndexDetection.SourceRepairFlag));
            agg.Reset();
            Assert.Equal(0, agg.Incidents);
            Assert.Equal(0, agg.Points);
            Assert.Equal(0, agg.OffTracks);
            Assert.Equal(0, agg.CarContacts);
            Assert.Empty(agg.ByCarIdx);
        }

        [Fact]
        public void PerDriver_BucketsIsolatedByCarIdx()
        {
            var agg = new ReplayIncidentLiveAggregator();
            agg.Apply(S(1, 1000, ReplayIncidentIndexDetection.SourceTrackSurface));
            agg.Apply(S(1, 2000, ReplayIncidentIndexDetection.SourceTrackSurface));
            agg.Apply(S(2, 3000, ReplayIncidentIndexDetection.SourceRepairFlag));
            Assert.Equal(2, agg.ByCarIdx[1].OffTracks);
            Assert.Equal(0, agg.ByCarIdx[1].CarContacts);
            Assert.Equal(0, agg.ByCarIdx[2].OffTracks);
            Assert.Equal(1, agg.ByCarIdx[2].CarContacts);
        }

        [Fact]
        public void BuildAggregates_NullYaml_EmitsNullsOnYamlSide()
        {
            var agg = new ReplayIncidentLiveAggregator();
            agg.Apply(S(1, 1000, ReplayIncidentIndexDetection.SourceTrackSurface));
            var aggregates = agg.BuildAggregates(null);
            Assert.Equal(1, aggregates.Ours.Incidents);
            Assert.Null(aggregates.Yaml.Incidents);
            Assert.Null(aggregates.Yaml.Points);
            Assert.Null(aggregates.Yaml.OffTracks);
            Assert.Null(aggregates.Yaml.CarContacts);
        }

        [Fact]
        public void BuildAggregates_AllZeroYaml_TreatedAsInProgress()
        {
            var agg = new ReplayIncidentLiveAggregator();
            agg.Apply(S(1, 1000, ReplayIncidentIndexDetection.SourceTrackSurface));
            var yaml = new Dictionary<int, int> { [1] = 0, [2] = 0 };
            var aggregates = agg.BuildAggregates(yaml);
            Assert.Null(aggregates.Yaml.Incidents);
        }

        [Fact]
        public void BuildAggregates_FinalizedYaml_SumsToTotal()
        {
            var agg = new ReplayIncidentLiveAggregator();
            var yaml = new Dictionary<int, int> { [1] = 4, [2] = 3, [3] = 0 };
            var aggregates = agg.BuildAggregates(yaml);
            Assert.Equal(7, aggregates.Yaml.Incidents);
        }

        [Fact]
        public void BuildDriverRows_NoYaml_EmitsNullYamlInc()
        {
            var agg = new ReplayIncidentLiveAggregator();
            agg.Apply(S(1, 1000, ReplayIncidentIndexDetection.SourceTrackSurface));
            var drivers = new List<DriverIdentity> { new DriverIdentity(1, "A", "111"), new DriverIdentity(2, "B", "222") };
            var rows = agg.BuildDriverRows(drivers, null);
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Null(r.YamlInc));
            Assert.Equal(1, rows.First(r => r.CarIdx == 1).OurInc);
            Assert.Equal(0, rows.First(r => r.CarIdx == 2).OurInc);
        }

        [Fact]
        public void BuildDriverRows_FinalizedYaml_FillsYamlInc()
        {
            var agg = new ReplayIncidentLiveAggregator();
            agg.Apply(S(1, 1000, ReplayIncidentIndexDetection.SourceTrackSurface));
            var yaml = new Dictionary<int, int> { [1] = 5, [2] = 2 };
            var drivers = new List<DriverIdentity> { new DriverIdentity(1, "A", "111"), new DriverIdentity(2, "B", "222") };
            var rows = agg.BuildDriverRows(drivers, yaml);
            Assert.Equal(5, rows.First(r => r.CarIdx == 1).YamlInc);
            Assert.Equal(2, rows.First(r => r.CarIdx == 2).YamlInc);
        }
    }
}
