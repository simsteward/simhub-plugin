using System.Collections.Generic;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Bounds the Live tab's incident-board broadcast to the most recent N entries. The full,
    /// unbounded list is still held in memory in-process and remains available to the post-session
    /// Replay Index Build — this only limits what gets re-serialized and pushed to every connected
    /// dashboard client on each new incident/escalation.
    /// See docs/REVIEW-incident-points-implementation.md IMPROVEMENT 8.
    /// </summary>
    public static class LiveIncidentBoardTrim
    {
        public const int DefaultMaxBroadcastEntries = 150;

        public static List<LiveIncidentBoardEntry> Trim(IReadOnlyList<LiveIncidentBoardEntry> entries, int maxEntries)
        {
            if (entries == null || entries.Count == 0)
                return new List<LiveIncidentBoardEntry>();
            if (entries.Count <= maxEntries)
                return new List<LiveIncidentBoardEntry>(entries);

            int start = entries.Count - maxEntries;
            var trimmed = new List<LiveIncidentBoardEntry>(maxEntries);
            for (int i = start; i < entries.Count; i++)
                trimmed.Add(entries[i]);
            return trimmed;
        }
    }
}
