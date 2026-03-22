using System;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Milestone 3 (TR-012–TR-013): bitmask helpers for <c>CarIdxSessionFlags</c> rising-edge detection.
    /// </summary>
    public static class ReplayIncidentIndexDetection
    {
        /// <summary>Repair / meatball-adjacent incident flag (§2.3 / TR-012).</summary>
        public const int RepairSessionFlag = 0x100000;

        /// <summary>Furled black flag (§2.3 / TR-013).</summary>
        public const int FurledSessionFlag = 0x80000;

        /// <summary>TR-018 / milestone: minimum replay session time gap (seconds) between duplicate primary emissions for the same car and source.</summary>
        public const double PrimaryDebounceSessionTimeSec = 1.0;

        public const string SourceRepairFlag = "repair_flag";
        public const string SourceFurledFlag = "furled_flag";
        public const string SourcePlayerIncidentCount = "player_incident_count";

        /// <summary>True when masked bits transition 0 → 1 between consecutive samples.</summary>
        public static bool IsRisingEdge(int prevRaw, int currRaw, int mask)
        {
            return (prevRaw & mask) == 0 && (currRaw & mask) != 0;
        }

        /// <summary>Milliseconds for index rows (TR-015); non-finite input becomes 0.</summary>
        public static int ToSessionTimeMs(double replaySessionTimeSec)
        {
            if (double.IsNaN(replaySessionTimeSec) || double.IsInfinity(replaySessionTimeSec))
                return 0;
            var ms = replaySessionTimeSec * 1000.0;
            if (ms >= int.MaxValue)
                return int.MaxValue;
            if (ms <= int.MinValue)
                return int.MinValue;
            return (int)Math.Round(ms);
        }
    }

    /// <summary>One primary incident detection (TR-012–TR-016); fingerprint added in M4.</summary>
    public readonly struct IncidentSample
    {
        public IncidentSample(
            int carIdx,
            int sessionTimeMs,
            string detectionSource,
            int? incidentPoints,
            int replayFrame)
        {
            CarIdx = carIdx;
            SessionTimeMs = sessionTimeMs;
            DetectionSource = detectionSource ?? "";
            IncidentPoints = incidentPoints;
            ReplayFrame = replayFrame;
        }

        public int CarIdx { get; }
        public int SessionTimeMs { get; }
        public string DetectionSource { get; }
        public int? IncidentPoints { get; }
        public int ReplayFrame { get; }
    }

    /// <summary>TR-017: fast-repair increment observed between samples (not a TR-020 row).</summary>
    public readonly struct FastRepairDelta
    {
        public FastRepairDelta(int carIdx, int sessionTimeMs, int replayFrame, int previousCount, int currentCount)
        {
            CarIdx = carIdx;
            SessionTimeMs = sessionTimeMs;
            ReplayFrame = replayFrame;
            PreviousCount = previousCount;
            CurrentCount = currentCount;
        }

        public int CarIdx { get; }
        public int SessionTimeMs { get; }
        public int ReplayFrame { get; }
        public int PreviousCount { get; }
        public int CurrentCount { get; }
    }
}
