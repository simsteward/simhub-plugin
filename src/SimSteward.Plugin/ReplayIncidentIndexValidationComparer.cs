using System.Collections.Generic;
using System.Linq;

namespace SimSteward.Plugin
{
    /// <summary>TR-024: compare detected per-car event counts to YAML <c>ResultsPositions.Incidents</c>.</summary>
    public static class ReplayIncidentIndexValidationComparer
    {
        /// <summary>Legacy two-arg overload kept for unit tests; emits rows with empty identity/sources.</summary>
        public static List<ReplayIncidentIndexDiscrepancyRow> BuildDiscrepancies(
            IReadOnlyDictionary<int, int> detectedEventsByCarIdx,
            IReadOnlyDictionary<int, int> yamlIncidentsByCarIdx)
        {
            return BuildDiscrepancies(detectedEventsByCarIdx, yamlIncidentsByCarIdx, null, null);
        }

        /// <summary>
        /// Enriched discrepancy build: emits <see cref="ReplayIncidentIndexDiscrepancyRow"/> per car
        /// whose detected count differs from YAML, populating <see cref="ReplayIncidentIndexDiscrepancyRow.DisplayName"/>
        /// + <see cref="ReplayIncidentIndexDiscrepancyRow.UniqueUserId"/> from <paramref name="identities"/>
        /// (lookup by CarIdx), and <see cref="ReplayIncidentIndexDiscrepancyRow.SourcesHit"/> from
        /// <paramref name="detectedSourcesByCarIdx"/> (per-source hit count per car).
        /// </summary>
        public static List<ReplayIncidentIndexDiscrepancyRow> BuildDiscrepancies(
            IReadOnlyDictionary<int, int> detectedEventsByCarIdx,
            IReadOnlyDictionary<int, int> yamlIncidentsByCarIdx,
            IReadOnlyList<DriverIdentity> identities,
            IReadOnlyDictionary<int, Dictionary<string, int>> detectedSourcesByCarIdx)
        {
            var keys = new HashSet<int>();
            if (detectedEventsByCarIdx != null)
                foreach (int k in detectedEventsByCarIdx.Keys) keys.Add(k);
            if (yamlIncidentsByCarIdx != null)
                foreach (int k in yamlIncidentsByCarIdx.Keys) keys.Add(k);

            // Index identities by CarIdx for O(1) lookup. DriverIdentity is a struct, so no null check.
            Dictionary<int, DriverIdentity> identityByCar = null;
            if (identities != null && identities.Count > 0)
            {
                identityByCar = new Dictionary<int, DriverIdentity>();
                foreach (var id in identities)
                    if (id.CarIdx >= 0 && !identityByCar.ContainsKey(id.CarIdx))
                        identityByCar[id.CarIdx] = id;
            }

            var list = new List<ReplayIncidentIndexDiscrepancyRow>();
            foreach (int car in keys.OrderBy(c => c))
            {
                int d = 0;
                detectedEventsByCarIdx?.TryGetValue(car, out d);

                int y = 0;
                yamlIncidentsByCarIdx?.TryGetValue(car, out y);

                if (d == y) continue;

                string name = "";
                string custId = "";
                if (identityByCar != null && identityByCar.TryGetValue(car, out DriverIdentity ident))
                {
                    name = ident.Name ?? "";
                    custId = ident.CustId ?? "";
                }

                var sources = new Dictionary<string, int>();
                if (detectedSourcesByCarIdx != null
                    && detectedSourcesByCarIdx.TryGetValue(car, out var perSource)
                    && perSource != null)
                {
                    foreach (var kv in perSource)
                        sources[kv.Key] = kv.Value;
                }

                list.Add(new ReplayIncidentIndexDiscrepancyRow
                {
                    CarIdx = car,
                    DetectedEventCount = d,
                    YamlIncidents = y,
                    Gap = y - d,
                    DisplayName = name,
                    UniqueUserId = custId,
                    SourcesHit = sources
                });
            }

            return list;
        }
    }
}
