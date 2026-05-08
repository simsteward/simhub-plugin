using System.Collections.Generic;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Live aggregator for the test rig: counts <see cref="IncidentSample"/>s per detection source
    /// for both the session-wide <c>aggregates.ours</c> bucket and the per-driver rows. Pure C#;
    /// has no IRSDK dependency so it is fully unit-testable.
    /// </summary>
    /// <remarks>
    /// YAML-side parity (<c>aggregates.yaml</c>, per-driver <c>yaml_inc</c>) is layered on top by
    /// <see cref="BuildSnapshot"/>. When the YAML <c>ResultsPositions</c> are not yet final
    /// (in-progress race), every YAML field is emitted as <c>null</c> so the dashboard renders "—"
    /// instead of "0".
    /// </remarks>
    public sealed class ReplayIncidentLiveAggregator
    {
        /// <summary>Per-driver counter bucket — see <see cref="ReplayStateDriverRow"/> for the wire shape.</summary>
        public sealed class DriverBucket
        {
            public int Incidents;
            public int Points;
            public int OffTracks;
            public int CarContacts;
        }

        /// <summary>Session-wide counters; the <c>ours</c> half of <c>aggregates</c>.</summary>
        public int Incidents { get; private set; }
        public int Points { get; private set; }
        public int OffTracks { get; private set; }
        public int CarContacts { get; private set; }

        public IReadOnlyDictionary<int, DriverBucket> ByCarIdx => _byCarIdx;
        private readonly Dictionary<int, DriverBucket> _byCarIdx = new Dictionary<int, DriverBucket>();

        /// <summary>Wipe every counter — call on session change or backward-seek.</summary>
        public void Reset()
        {
            Incidents = 0;
            Points = 0;
            OffTracks = 0;
            CarContacts = 0;
            _byCarIdx.Clear();
        }

        /// <summary>
        /// Apply one <see cref="IncidentSample"/> to the session-wide and per-driver counters.
        /// Detection-source mapping:
        ///   <list type="bullet">
        ///     <item><c>repair_flag</c> / <c>furled_flag</c> / <c>player_incident_count</c> → <c>CarContacts</c></item>
        ///     <item><c>track_surface</c> → <c>OffTracks</c></item>
        ///   </list>
        /// Every sample bumps <c>Incidents</c>. Points: when <see cref="IncidentSample.IncidentPoints"/>
        /// is set (1/2/4 from player delta) we use it; otherwise the sample contributes 1 (rising-edge
        /// flag with unknown weight is treated as a minor incident).
        /// </summary>
        public void Apply(IncidentSample sample)
        {
            int delta = sample.IncidentPoints ?? 1;
            Incidents++;
            Points += delta;

            DriverBucket bucket;
            if (!_byCarIdx.TryGetValue(sample.CarIdx, out bucket))
            {
                bucket = new DriverBucket();
                _byCarIdx[sample.CarIdx] = bucket;
            }
            bucket.Incidents++;
            bucket.Points += delta;

            switch (sample.DetectionSource)
            {
                case ReplayIncidentIndexDetection.SourceTrackSurface:
                    OffTracks++;
                    bucket.OffTracks++;
                    break;
                case ReplayIncidentIndexDetection.SourceRepairFlag:
                case ReplayIncidentIndexDetection.SourceFurledFlag:
                case ReplayIncidentIndexDetection.SourcePlayerIncidentCount:
                    CarContacts++;
                    bucket.CarContacts++;
                    break;
            }
        }

        /// <summary>
        /// Build a wire-ready snapshot. <paramref name="drivers"/> is the resolved driver list (pos / car_idx
        /// / display name / cust_id). <paramref name="yamlByCarIdx"/> is the parsed
        /// <c>ResultsPositions[].Incidents</c> map; pass <c>null</c> when YAML is unavailable or the
        /// race is in-progress so the snapshot emits <c>null</c> for every YAML field.
        /// </summary>
        public ReplayStateAggregates BuildAggregates(IReadOnlyDictionary<int, int> yamlByCarIdx)
        {
            var aggregates = new ReplayStateAggregates
            {
                Ours = new ReplayStateAggregateBucket
                {
                    Incidents = Incidents,
                    Points = Points,
                    OffTracks = OffTracks,
                    CarContacts = CarContacts
                },
                Yaml = BuildYamlAggregateBucket(yamlByCarIdx)
            };
            return aggregates;
        }

        private static ReplayStateAggregateBucket BuildYamlAggregateBucket(IReadOnlyDictionary<int, int> yamlByCarIdx)
        {
            if (yamlByCarIdx == null || yamlByCarIdx.Count == 0)
            {
                return new ReplayStateAggregateBucket
                {
                    Incidents = null,
                    Points = null,
                    OffTracks = null,
                    CarContacts = null
                };
            }

            int total = 0;
            bool anyNonZero = false;
            foreach (var kv in yamlByCarIdx)
            {
                total += kv.Value;
                if (kv.Value != 0) anyNonZero = true;
            }

            // YAML in-progress rule: every value zero ≡ session not yet final → emit nulls so
            // the dashboard renders "—" instead of misleading 0/0/0.
            if (!anyNonZero)
            {
                return new ReplayStateAggregateBucket
                {
                    Incidents = null,
                    Points = null,
                    OffTracks = null,
                    CarContacts = null
                };
            }

            // YAML only carries an incident total per car — no per-source breakdown is available,
            // so off_tracks / car_contacts stay null on the YAML side.
            return new ReplayStateAggregateBucket
            {
                Incidents = total,
                Points = null,
                OffTracks = null,
                CarContacts = null
            };
        }

        /// <summary>
        /// Builds the per-driver row list. <paramref name="driverList"/> elements should be in
        /// finishing position order; <paramref name="yamlByCarIdx"/> is the YAML
        /// <c>ResultsPositions[].Incidents</c> map (or <c>null</c>).
        /// </summary>
        public List<ReplayStateDriverRow> BuildDriverRows(
            IReadOnlyList<DriverIdentity> driverList,
            IReadOnlyDictionary<int, int> yamlByCarIdx)
        {
            var rows = new List<ReplayStateDriverRow>(driverList?.Count ?? 0);
            if (driverList == null) return rows;

            bool yamlInProgress = IsYamlInProgress(yamlByCarIdx);

            for (int i = 0; i < driverList.Count; i++)
            {
                var d = driverList[i];
                _byCarIdx.TryGetValue(d.CarIdx, out var bucket);
                int? yamlInc = null;
                if (!yamlInProgress && yamlByCarIdx != null && yamlByCarIdx.TryGetValue(d.CarIdx, out int yi))
                    yamlInc = yi;

                rows.Add(new ReplayStateDriverRow
                {
                    Pos = i + 1,
                    CarIdx = d.CarIdx,
                    Name = d.Name ?? "",
                    CustId = d.CustId ?? "",
                    OurInc = bucket?.Incidents ?? 0,
                    YamlInc = yamlInc,
                    OurPts = bucket?.Points ?? 0,
                    OffTracks = bucket?.OffTracks ?? 0,
                    CarContacts = bucket?.CarContacts ?? 0
                });
            }
            return rows;
        }

        private static bool IsYamlInProgress(IReadOnlyDictionary<int, int> yamlByCarIdx)
        {
            if (yamlByCarIdx == null || yamlByCarIdx.Count == 0) return true;
            foreach (var kv in yamlByCarIdx)
                if (kv.Value != 0) return false;
            return true;
        }
    }

    /// <summary>Lightweight driver tuple consumed by <see cref="ReplayIncidentLiveAggregator.BuildDriverRows"/>.</summary>
    public readonly struct DriverIdentity
    {
        public DriverIdentity(int carIdx, string name, string custId)
        {
            CarIdx = carIdx;
            Name = name;
            CustId = custId;
        }
        public int CarIdx { get; }
        public string Name { get; }
        public string CustId { get; }
    }
}
