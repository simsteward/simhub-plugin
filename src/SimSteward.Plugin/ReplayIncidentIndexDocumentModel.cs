using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace SimSteward.Plugin
{
    /// <summary>TR-019–TR-022 on-disk JSON root.</summary>
    public sealed class ReplayIncidentIndexFileRoot
    {
        [JsonProperty("subSessionId")]
        public int SubSessionId { get; set; }

        [JsonProperty("indexBuildTimeMs")]
        public long IndexBuildTimeMs { get; set; }

        [JsonProperty("totalRaceIncidents")]
        public int TotalRaceIncidents { get; set; }

        [JsonProperty("incidentCountByCarIdx")]
        public Dictionary<string, int> IncidentCountByCarIdx { get; set; } = new Dictionary<string, int>();

        [JsonProperty("incidents")]
        public List<ReplayIncidentIndexIncidentRow> Incidents { get; set; } = new List<ReplayIncidentIndexIncidentRow>();

        [JsonProperty("sessions")]
        public IReadOnlyList<ReplayIncidentIndexSessionEntry> Sessions { get; set; } = new List<ReplayIncidentIndexSessionEntry>();

        [JsonProperty("validation", NullValueHandling = NullValueHandling.Ignore)]
        public ReplayIncidentIndexValidationBlock Validation { get; set; }

        [JsonProperty("outputPath", NullValueHandling = NullValueHandling.Ignore)]
        public string OutputPath { get; set; }
    }

    /// <summary>
    /// One iRacing session lookup entry mirrored from <c>SessionInfo.Sessions[]</c>.
    /// <see cref="ImpactClass"/> is computed via <see cref="SessionTypeImpactClass.Classify"/>.
    /// </summary>
    public sealed class ReplayIncidentIndexSessionEntry
    {
        [JsonProperty("sessionNum")]
        public int SessionNum { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("impactClass")]
        public string ImpactClass { get; set; }
    }

    public sealed class ReplayIncidentIndexIncidentRow
    {
        [JsonProperty("fingerprint")]
        public string Fingerprint { get; set; }

        /// <summary>
        /// Sampling-rate-stable fingerprint (v2) used as the cloud dedup key. Populated additively
        /// alongside <see cref="Fingerprint"/> (v1); v1 is retained for existing local consumers.
        /// </summary>
        [JsonProperty("cloudFingerprint")]
        public string CloudFingerprint { get; set; }

        [JsonProperty("carIdx")]
        public int CarIdx { get; set; }

        [JsonProperty("sessionTimeMs")]
        public int SessionTimeMs { get; set; }

        [JsonProperty("detectionSource")]
        public string DetectionSource { get; set; }

        [JsonProperty("incidentPoints")]
        public int? IncidentPoints { get; set; }

        [JsonProperty("lap")]
        public int Lap { get; set; } = -1;

        [JsonProperty("replayFrame")]
        public int ReplayFrame { get; set; }

        [JsonProperty("sessionNum")]
        public int SessionNum { get; set; } = IncidentSample.SessionNumUnknown;

        [JsonProperty("lapDistPct", NullValueHandling = NullValueHandling.Ignore)]
        public float? LapDistPct { get; set; }

        [JsonProperty("carPosition", NullValueHandling = NullValueHandling.Ignore)]
        public int? CarPosition { get; set; }
    }

    public sealed class ReplayIncidentIndexValidationBlock
    {
        [JsonProperty("yamlResultsAvailable")]
        public bool YamlResultsAvailable { get; set; }

        [JsonProperty("yamlParseError", NullValueHandling = NullValueHandling.Ignore)]
        public string YamlParseError { get; set; }

        [JsonProperty("yamlSessionNumUsed", NullValueHandling = NullValueHandling.Ignore)]
        public int? YamlSessionNumUsed { get; set; }

        [JsonProperty("discrepancies")]
        public List<ReplayIncidentIndexDiscrepancyRow> Discrepancies { get; set; } = new List<ReplayIncidentIndexDiscrepancyRow>();

        [JsonProperty("cameraSeekAttempted")]
        public int CameraSeekAttempted { get; set; }

        [JsonProperty("cameraSeekMatches")]
        public int CameraSeekMatches { get; set; }

        [JsonProperty("cameraSeekMatchPercent", NullValueHandling = NullValueHandling.Ignore)]
        public double? CameraSeekMatchPercent { get; set; }
    }

    public sealed class ReplayIncidentIndexDiscrepancyRow
    {
        [JsonProperty("carIdx")]
        public int CarIdx { get; set; }

        [JsonProperty("detectedEventCount")]
        public int DetectedEventCount { get; set; }

        [JsonProperty("yamlIncidents")]
        public int YamlIncidents { get; set; }

        /// <summary>YamlIncidents - DetectedEventCount. Positive = under-detected; negative = over-detected.</summary>
        [JsonProperty("gap")]
        public int Gap { get; set; }

        /// <summary>Driver UserName from SessionInfo.DriverInfo.Drivers[] when available; empty otherwise.</summary>
        [JsonProperty("displayName")]
        public string DisplayName { get; set; } = "";

        /// <summary>iRacing CustID (UserID) for the driver in this slot at the time of the build.</summary>
        [JsonProperty("uniqueUserId")]
        public string UniqueUserId { get; set; } = "";

        /// <summary>Per-detection-source hit counts for this car (e.g. {"yaml_incident_delta":2,"track_surface":1}).</summary>
        [JsonProperty("sourcesHit")]
        public Dictionary<string, int> SourcesHit { get; set; } = new Dictionary<string, int>();
    }

    /// <summary>Maps <see cref="IncidentSample"/> list to TR-020 rows with fingerprints; TR-021 sort.</summary>
    public static class ReplayIncidentIndexDocumentBuilder
    {
        public static ReplayIncidentIndexFileRoot Build(
            int subSessionId,
            long indexBuildTimeMs,
            IReadOnlyList<IncidentSample> samples,
            ReplayIncidentIndexValidationBlock validation = null,
            string outputPath = null)
        {
            var ordered = samples
                .OrderBy(s => s.SessionTimeMs)
                .ThenBy(s => s.CarIdx)
                .ThenBy(s => s.DetectionSource, StringComparer.Ordinal)
                .ToList();

            var rows = new List<ReplayIncidentIndexIncidentRow>(ordered.Count);
            var perCar = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (IncidentSample s in ordered)
            {
                string fp = ReplayIncidentIndexFingerprint.ComputeHexV1(
                    subSessionId,
                    s.CarIdx,
                    s.SessionTimeMs,
                    s.DetectionSource,
                    s.IncidentPoints);

                string cloudFp = ReplayIncidentIndexFingerprint.ComputeHexV2(
                    subSessionId,
                    s.CarIdx,
                    s.SessionTimeMs,
                    s.DetectionSource,
                    s.IncidentPoints);

                rows.Add(new ReplayIncidentIndexIncidentRow
                {
                    Fingerprint = fp,
                    CloudFingerprint = cloudFp,
                    CarIdx = s.CarIdx,
                    SessionTimeMs = s.SessionTimeMs,
                    DetectionSource = s.DetectionSource,
                    IncidentPoints = s.IncidentPoints,
                    Lap = s.Lap,
                    ReplayFrame = s.ReplayFrame,
                    SessionNum = s.SessionNum,
                    LapDistPct = s.LapDistPct,
                    CarPosition = s.CarPosition
                });

                string key = s.CarIdx.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!perCar.TryGetValue(key, out int c))
                    c = 0;
                perCar[key] = c + 1;
            }

            return new ReplayIncidentIndexFileRoot
            {
                SubSessionId = subSessionId,
                IndexBuildTimeMs = indexBuildTimeMs,
                TotalRaceIncidents = rows.Count,
                IncidentCountByCarIdx = perCar,
                Incidents = rows,
                Validation = validation,
                OutputPath = outputPath
            };
        }

        public static string Serialize(ReplayIncidentIndexFileRoot root)
        {
            return JsonConvert.SerializeObject(root, Formatting.Indented);
        }
    }
}
