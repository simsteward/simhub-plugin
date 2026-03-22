using System.Collections.Generic;
using System.Linq;

namespace SimSteward.Plugin
{
    /// <summary>TR-024: compare detected per-car event counts to YAML <c>ResultsPositions.Incidents</c>.</summary>
    public static class ReplayIncidentIndexValidationComparer
    {
        public static List<ReplayIncidentIndexDiscrepancyRow> BuildDiscrepancies(
            IReadOnlyDictionary<int, int> detectedEventsByCarIdx,
            IReadOnlyDictionary<int, int> yamlIncidentsByCarIdx)
        {
            var keys = new HashSet<int>();
            if (detectedEventsByCarIdx != null)
            {
                foreach (int k in detectedEventsByCarIdx.Keys)
                    keys.Add(k);
            }

            if (yamlIncidentsByCarIdx != null)
            {
                foreach (int k in yamlIncidentsByCarIdx.Keys)
                    keys.Add(k);
            }

            var list = new List<ReplayIncidentIndexDiscrepancyRow>();
            foreach (int car in keys.OrderBy(c => c))
            {
                int d = 0;
                if (detectedEventsByCarIdx != null)
                    detectedEventsByCarIdx.TryGetValue(car, out d);

                int y = 0;
                if (yamlIncidentsByCarIdx != null)
                    yamlIncidentsByCarIdx.TryGetValue(car, out y);

                if (d != y)
                {
                    list.Add(new ReplayIncidentIndexDiscrepancyRow
                    {
                        CarIdx = car,
                        DetectedEventCount = d,
                        YamlIncidents = y
                    });
                }
            }

            return list;
        }
    }
}
