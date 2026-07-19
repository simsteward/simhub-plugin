using System;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Pure gating logic for the live (non-replay) field-wide incident detector.
    /// Gate on NOT-replay rather than a specific live-mode string, so future SimMode
    /// values stay covered automatically.
    /// </summary>
    public static class LiveIncidentDetectionPrerequisites
    {
        public static bool IsGenuinelyLive(bool isConnected, string weekendSimMode)
        {
            if (!isConnected)
                return false;
            if (string.IsNullOrWhiteSpace(weekendSimMode))
                return false;
            return !string.Equals(weekendSimMode.Trim(), "replay", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when either the subsession or the session number has changed since the last tick — incident flags/counts reset at these boundaries.</summary>
        public static bool IsSessionBoundary(int lastSubSessionId, int lastSessionNum, int currentSubSessionId, int currentSessionNum)
        {
            return currentSubSessionId != lastSubSessionId || currentSessionNum != lastSessionNum;
        }
    }
}
