namespace SimSteward.Plugin
{
    /// <summary>
    /// "Session observed" best-effort point estimate for an incident whose real value hasn't (and,
    /// live, for other cars, structurally can't yet) resolved — see docs/IRACING-DATA-AVAILABILITY.md
    /// and LiveIncidentTotalsBackfill. Distinct from a resolved value everywhere it's surfaced
    /// (LiveIncidentBoardEntry.EstimatedPoints vs Points/PointsResolved): never conflate a guess with
    /// a confirmed iRacing-adjudicated number.
    ///
    /// Confidence varies sharply by source — only off-track maps to iRacing's own published rule with
    /// any real confidence. Everything else is a deliberately conservative single-value guess across
    /// what iRacing docs describe as a real 0x-4x range for that category; it will visibly be wrong
    /// some of the time. That tradeoff was an explicit user choice (estimate over null).
    /// </summary>
    public static class IncidentPointsEstimate
    {
        /// <summary>
        /// Returns a best-effort point guess for <paramref name="detectionSource"/>, or null when no
        /// defensible single-value guess exists (see per-case reasoning below). Applies the same 4→2
        /// dirt cap as resolved values (IncidentSeverityCorrelator.ApplyDirtCap) when
        /// <paramref name="isDirtSurface"/> is true.
        /// </summary>
        public static int? Estimate(string detectionSource, bool isDirtSurface = false)
        {
            int? raw = EstimateRaw(detectionSource);
            if (isDirtSurface && raw.HasValue && raw.Value == 4)
                return 2;
            return raw;
        }

        private static int? EstimateRaw(string detectionSource)
        {
            switch ((detectionSource ?? "").Trim().ToLowerInvariant())
            {
                // Off-track: iRacing's own definition is literally "car's centerline crosses the
                // track boundary" — this is what the detector already checks, so 1x is a direct
                // read of the rule, not really a guess.
                case ReplayIncidentIndexDetection.SourceTrackSurface:
                    return 1;

                // A fast repair being used is strong evidence real damage occurred — heavy contact
                // is the most likely tier, though light contact (0x) or a lucky 2x can't be ruled out.
                case ReplayIncidentIndexDetection.SourceRepairFlag:
                case ReplayIncidentIndexDetection.SourceFastRepair:
                    return 4;

                // A furled (not fully shown) flag is typically a preliminary warning rather than a
                // confirmed serious infraction — guess the middle tier rather than either extreme.
                case ReplayIncidentIndexDetection.SourceFurledFlag:
                    return 2;

                // A shown black flag covers a wide range of causes (contact, unsportsmanlike conduct,
                // procedural violations) — deliberately conservative middle-tier guess rather than
                // assuming the worst case.
                case ReplayIncidentIndexDetection.SourceBlackFlag:
                    return 2;

                // Disqualification is not a reliable incident-points analog at all — DQs can stem
                // from technical/admin violations with no points relationship whatsoever. No
                // defensible single-value guess exists; stays null rather than inventing one.
                case ReplayIncidentIndexDetection.SourceDisqualify:
                    return null;

                default:
                    // player_incident_count / yaml_incident_delta already carry a real resolved
                    // value when present, so callers won't reach here for those in practice; any
                    // future/unrecognized source also stays null rather than guessing blind.
                    return null;
            }
        }
    }
}
