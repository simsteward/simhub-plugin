namespace SimSteward.Plugin
{
    /// <summary>
    /// Whether a live YAML incident-probe poll (fires every 5s for the life of a live session) is
    /// worth an INFO-level log line. A steady-state "nothing changed" poll is DEBUG-only — logging
    /// every poll at INFO unconditionally produced ~720 lines/hour/session with no new information.
    /// See docs/REVIEW-incident-points-implementation.md IMPROVEMENT 7.
    /// </summary>
    public static class LiveYamlProbeLogLevel
    {
        public static bool IsInfoWorthy(bool parseOk, int deltaCount)
        {
            return !parseOk || deltaCount > 0;
        }
    }
}
