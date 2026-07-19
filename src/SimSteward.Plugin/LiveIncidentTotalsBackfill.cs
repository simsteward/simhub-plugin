using System.Collections.Generic;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Pure gating logic for the live-session incident-totals backfill: once official results post
    /// while still connected live (SessionInfoYaml ResultsPositions[].Incidents flips from all-zero
    /// to at least one non-zero value), the Live tab can show each driver's real point TOTAL.
    ///
    /// Deliberately aggregate-only, never per-incident: a single, late-arriving YAML delta cannot be
    /// attributed to one specific earlier board row without a full chronological re-sweep — that is
    /// exactly what the Replay Index Build already does correctly once the replay is loaded. See
    /// docs/IRACING-DATA-AVAILABILITY.md Group 5.
    /// </summary>
    public static class LiveIncidentTotalsBackfill
    {
        /// <summary>
        /// True once the parsed live YAML incidents map represents finalized results (at least one
        /// non-zero value) rather than an in-progress session (every value still zero). Mirrors
        /// <see cref="ReplayIncidentLiveAggregator"/>'s existing "in-progress ⇒ all zero" convention.
        /// </summary>
        public static bool IsFinal(IReadOnlyDictionary<int, int> yamlByCarIdx)
        {
            if (yamlByCarIdx == null || yamlByCarIdx.Count == 0)
                return false;

            foreach (var kv in yamlByCarIdx)
                if (kv.Value != 0)
                    return true;

            return false;
        }

        /// <summary>
        /// True when <paramref name="curr"/> carries totals not already reflected by
        /// <paramref name="prevBroadcast"/> — guards the live poll (every ~5s) from re-broadcasting
        /// an unchanged snapshot once results have gone final.
        /// </summary>
        public static bool HasChanged(IReadOnlyDictionary<int, int> prevBroadcast, IReadOnlyDictionary<int, int> curr)
        {
            if (curr == null || curr.Count == 0)
                return false;
            if (prevBroadcast == null)
                return true;

            foreach (var kv in curr)
            {
                if (!prevBroadcast.TryGetValue(kv.Key, out int prevVal) || prevVal != kv.Value)
                    return true;
            }
            return curr.Count != prevBroadcast.Count;
        }
    }
}
