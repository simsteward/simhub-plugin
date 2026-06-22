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

                // Map standard single-event deltas (1=off-track, 2=loss-of-control, 4=heavy contact);
                // other values (e.g. multiple events between YAML snapshots) leave points null so
                // downstream queries can recognize the aggregated case.
                int? points = (delta == 1 || delta == 2 || delta == 4) ? (int?)delta : null;

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
                    sessionNum));
            }

            return result;
        }
    }
}
