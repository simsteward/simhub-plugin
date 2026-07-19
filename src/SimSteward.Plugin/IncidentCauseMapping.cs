using System;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Maps a detection to a human-readable "cause" label for the dashboard's <c>.cause-tag</c> classes
    /// (off-track/spin/contact/flagged/unknown). Classification is source-driven first (so flag-based
    /// detections stop defaulting to "unknown" just because points are usually unresolved live), with an
    /// authoritative points-value override applied whenever a real incident point value is known — see
    /// CLAUDE.md's documented convention (1x off-track, 2x wall/spin, 4x heavy contact).
    /// </summary>
    public static class IncidentCauseMapping
    {
        public const string CauseOffTrack = "off-track";
        public const string CauseSpin = "spin";
        public const string CauseContact = "contact";
        public const string CauseFlagged = "flagged";
        public const string CauseUnknown = "unknown";

        /// <summary>
        /// Resolve the cause label. <paramref name="incidentPoints"/>, when present, is authoritative and
        /// overrides the source-driven guess (a resolved point value is a stronger signal than the raw
        /// detection source alone).
        /// </summary>
        public static string Resolve(string detectionSource, int? incidentPoints)
        {
            if (incidentPoints.HasValue)
            {
                switch (incidentPoints.Value)
                {
                    case 1: return CauseOffTrack;
                    case 2: return CauseSpin;
                    case 4: return CauseContact;
                }
            }

            switch ((detectionSource ?? "").Trim().ToLowerInvariant())
            {
                case ReplayIncidentIndexDetection.SourceTrackSurface:
                    return CauseOffTrack;
                case ReplayIncidentIndexDetection.SourceFastRepair:
                case ReplayIncidentIndexDetection.SourceRepairFlag:
                    return CauseContact;
                case ReplayIncidentIndexDetection.SourceFurledFlag:
                case ReplayIncidentIndexDetection.SourceBlackFlag:
                case ReplayIncidentIndexDetection.SourceDisqualify:
                    return CauseFlagged;
                default:
                    // player_incident_count / yaml_incident_delta (no source-native cause — meaningful
                    // only once points resolve, handled by the override above) and any unrecognized source.
                    return CauseUnknown;
            }
        }
    }
}
