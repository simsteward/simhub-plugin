namespace SimSteward.Plugin
{
    /// <summary>
    /// The dashboard snapshot's disk-load/auto-build path (see
    /// SimStewardPlugin.ReplayIncidentIndexDashboard.cs) is a one-shot-per-subsession gate:
    /// it reads the cached index file (or auto-triggers a build) at most once per subsession,
    /// tracked via _replayIndexDiskLoadAttemptedForSub, to avoid re-triggering a build every
    /// telemetry tick.
    ///
    /// If that auto-triggered build is aborted mid-flight (e.g. iRacing disconnects before
    /// FinalizeReplayIndexBuildLocked writes an index file), no file ever lands on disk — but
    /// without clearing the gate, the plugin permanently believes it already tried this
    /// subsession and never retries, even after iRacing reconnects to the same replay.
    /// </summary>
    public static class ReplayIncidentIndexDashboardGate
    {
        /// <summary>
        /// Called when the iRacing SDK disconnects. If a build was in progress (and therefore
        /// aborted before finalize), the gate is cleared so the next dashboard snapshot retries
        /// the disk read / auto-build. Otherwise the gate is left untouched.
        /// </summary>
        public static int OnBuildAbortedBeforeFinalize(bool buildWasInProgress, int currentDiskLoadAttemptedForSub)
        {
            return buildWasInProgress ? -1 : currentDiskLoadAttemptedForSub;
        }
    }
}
