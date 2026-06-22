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

        /// <summary>Black flag (§2.3 / TR-014).</summary>
        public const int BlackSessionFlag = 0x00010000;

        /// <summary>Disqualification (§2.3 / TR-014).</summary>
        public const int DisqualifySessionFlag = 0x00020000;

        /// <summary>TR-018 / milestone: minimum replay session time gap (seconds) between duplicate primary emissions for the same car and source.</summary>
        public const double PrimaryDebounceSessionTimeSec = 1.0;

        public const string SourceRepairFlag = "repair_flag";
        public const string SourceFurledFlag = "furled_flag";
        public const string SourceBlackFlag = "black_flag";
        public const string SourceDisqualify = "disqualify";
        public const string SourcePlayerIncidentCount = "player_incident_count";
        public const string SourceTrackSurface = "track_surface";
        /// <summary>Per-driver Incidents delta observed in SessionInfoYaml ResultsPositions[] between snapshots.</summary>
        public const string SourceYamlIncidentDelta = "yaml_incident_delta";
        public const string SourceFastRepair = "fast_repair";

        /// <summary>iRacing SessionFlags bit: checkered flag shown.</summary>
        public const int CheckeredSessionFlag = 0x0001;

        // iRacing irsdk_TrkLoc enum (from irsdk_defines.h):
        //   NotInWorld = -1, OffTrack = 0, InPitStall = 1, ApproachingPits = 2, OnTrack = 3
        /// <summary>iRacing CarIdxTrackSurface value: car on the racing surface (irsdk_OnTrack = 3).</summary>
        public const int TrackSurfaceOnTrack  = 3;
        /// <summary>iRacing CarIdxTrackSurface value: car off the racing surface (irsdk_OffTrack = 0).</summary>
        public const int TrackSurfaceOffTrack = 0;
        /// <summary>iRacing CarIdxTrackSurface value: car not in world / loading (irsdk_NotInWorld = -1). Exclude from transitions.</summary>
        public const int TrackSurfaceNotInWorld = -1;

        /// <summary>Rumble strip material range (11-14). Off-track transitions onto rumble strips are suppressed as false positives.</summary>
        public const int Rumble1Material = 11;
        /// <summary>Rumble strip material range (11-14). Off-track transitions onto rumble strips are suppressed as false positives.</summary>
        public const int Rumble4Material = 14;

        /// <summary>True when the material value indicates a rumble strip (kerb).</summary>
        public static bool IsRumbleStrip(int material) => material >= Rumble1Material && material <= Rumble4Material;

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
        /// <summary>Default sentinel for unknown session number (e.g. live ticks before SessionNum is read).</summary>
        public const int SessionNumUnknown = -1;

        public IncidentSample(
            int carIdx,
            int sessionTimeMs,
            string detectionSource,
            int? incidentPoints,
            int replayFrame,
            int lap = SessionLogging.LapUnknown,
            int sessionNum = SessionNumUnknown,
            float? lapDistPct = null,
            int? carPosition = null)
        {
            CarIdx = carIdx;
            SessionTimeMs = sessionTimeMs;
            DetectionSource = detectionSource ?? "";
            IncidentPoints = incidentPoints;
            ReplayFrame = replayFrame;
            Lap = lap;
            SessionNum = sessionNum;
            LapDistPct = lapDistPct;
            CarPosition = carPosition;
        }

        public int CarIdx { get; }
        public int SessionTimeMs { get; }
        public string DetectionSource { get; }
        public int? IncidentPoints { get; }
        public int ReplayFrame { get; }
        public int Lap { get; }
        public int SessionNum { get; }
        /// <summary>Track position 0.0-1.0 at detection time (from CarIdxLapDistPct).</summary>
        public float? LapDistPct { get; }
        /// <summary>Race position at detection time (from CarIdxPosition). 0 = not classified.</summary>
        public int? CarPosition { get; }
    }
}
