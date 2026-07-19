using System;
using System.Collections.Generic;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Diff per-driver <c>Incidents</c> counts between two <c>SessionInfoYaml</c> snapshots and emit
    /// one <see cref="IncidentSample"/> row per car whose count went up. This is the authoritative
    /// per-driver-per-event source for other cars (the SDK does not expose per-car incident counters
    /// in telemetry — only via the YAML ResultsPositions[] block).
    /// </summary>
    public static class ReplayIncidentYamlDiff
    {
        /// <summary>
        /// Returns the set of rising-count deltas between <paramref name="prev"/> and <paramref name="curr"/>.
        /// If <paramref name="prev"/> is null, this snapshot establishes a baseline and no rows are emitted.
        /// Negative deltas (count went down — should not happen in practice but possible on session change)
        /// are ignored; callers must reset the baseline at session boundaries.
        /// </summary>
        public static List<IncidentSample> Diff(
            Dictionary<int, int> prev,
            Dictionary<int, int> curr,
            double replaySessionTimeSec,
            int replayFrame,
            int sessionNum,
            int[] carIdxLap)
        {
            var result = new List<IncidentSample>();
            if (curr == null) return result;
            if (prev == null) return result;

            int sessionTimeMs = ReplayIncidentIndexDetection.ToSessionTimeMs(replaySessionTimeSec);

            foreach (var kv in curr)
            {
                int carIdx = kv.Key;
                int currCount = kv.Value;
                int prevCount = 0;
                prev.TryGetValue(carIdx, out prevCount);

                int delta = currCount - prevCount;
                if (delta <= 0) continue;

                // Map standard single-event deltas directly (1=off-track, 2=loss-of-control, 4=heavy
                // contact). A delta spanning more than one iRacing-scored event between YAML snapshots
                // (e.g. 3, 5, 6, 7...) is resolved to the highest plausible single-event tier (capped at
                // 4) rather than nulled out — this matches iRacing's own official scoring rule ("if
                // multiple incidents happen in quick succession, only the highest-scoring incident will
                // be tallied"), so an aggregate should never be reported as *less* certain than its floor.
                // IsAggregateDelta flags the capped case so downstream consumers can still tell a clean
                // single-event read from a capped aggregate.
                bool isAggregate = delta != 1 && delta != 2 && delta != 4;
                int points = Math.Min(4, delta);

                int lap = SessionLogging.LapUnknown;
                if (carIdxLap != null && carIdx >= 0 && carIdx < carIdxLap.Length)
                    lap = carIdxLap[carIdx];

                result.Add(new IncidentSample(
                    carIdx,
                    sessionTimeMs,
                    ReplayIncidentIndexDetection.SourceYamlIncidentDelta,
                    points,
                    replayFrame,
                    lap,
                    sessionNum,
                    isAggregateDelta: isAggregate));
            }

            return result;
        }
    }
}
