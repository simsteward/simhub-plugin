using System;
using System.Collections.Generic;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Milestone 3 (TR-012–TR-018): per-sample incident detection for replay fast-forward polling.
    /// Invoked from the M2 native IRSDK poll only (not SimHub <c>DataUpdate</c>).
    /// </summary>
    public sealed class ReplayIncidentIndexDetector
    {
        private readonly int[] _prevFlags = new int[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly int[] _prevTrackSurface = new int[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly double[] _lastRepairEmitSec = new double[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly double[] _lastFurledEmitSec = new double[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly double[] _lastPlayerEmitSec = new double[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly double[] _lastSurfaceEmitSec = new double[ReplayIncidentIndexBuild.CarSlotCount];

        private int _prevPlayerIncidents;

        private static void ValidateLength(string name, int[] arr, int required)
        {
            if (arr == null || arr.Length < required)
                throw new ArgumentException(name + " must have length >= " + required + ".", name);
        }

        /// <summary>
        /// TR-005/006/017 baseline: first <see cref="Process"/> compares against these arrays, not zeros.
        /// </summary>
        public void Reset(int[] baselineFlags, int baselinePlayerIncidents, int playerCarIdx, int[] baselineTrackSurface = null)
        {
            ValidateLength(nameof(baselineFlags), baselineFlags, ReplayIncidentIndexBuild.CarSlotCount);
            if (playerCarIdx < -1 || playerCarIdx >= ReplayIncidentIndexBuild.CarSlotCount)
                throw new ArgumentOutOfRangeException(nameof(playerCarIdx));

            Array.Copy(baselineFlags, 0, _prevFlags, 0, ReplayIncidentIndexBuild.CarSlotCount);
            _prevPlayerIncidents = baselinePlayerIncidents;

            if (baselineTrackSurface != null && baselineTrackSurface.Length >= ReplayIncidentIndexBuild.CarSlotCount)
                Array.Copy(baselineTrackSurface, 0, _prevTrackSurface, 0, ReplayIncidentIndexBuild.CarSlotCount);
            else
                Array.Clear(_prevTrackSurface, 0, _prevTrackSurface.Length);

            for (int i = 0; i < ReplayIncidentIndexBuild.CarSlotCount; i++)
            {
                _lastRepairEmitSec[i]  = -1;
                _lastFurledEmitSec[i]  = -1;
                _lastPlayerEmitSec[i]  = -1;
                _lastSurfaceEmitSec[i] = -1;
            }
        }

        private bool TryTakePrimarySlot(double[] lastEmitByCar, int carIdx, double replaySessionTimeSec)
        {
            if (carIdx < 0 || carIdx >= ReplayIncidentIndexBuild.CarSlotCount)
                return false;

            ref double last = ref lastEmitByCar[carIdx];
            if (last >= 0 && replaySessionTimeSec - last < ReplayIncidentIndexDetection.PrimaryDebounceSessionTimeSec)
                return false;

            last = replaySessionTimeSec;
            return true;
        }

        /// <summary>
        /// One native SDK sample: compare to previous frame, emit primary incidents.
        /// </summary>
        public List<IncidentSample> Process(
            double replaySessionTimeSec,
            int[] flags,
            int playerIncidents,
            int playerCarIdx,
            int replayFrame,
            int[] trackSurface = null,
            int[] carIdxLap = null,
            int sessionNum = IncidentSample.SessionNumUnknown,
            float[] carIdxLapDistPct = null,
            int[] carIdxPosition = null)
        {
            ValidateLength(nameof(flags), flags, ReplayIncidentIndexBuild.CarSlotCount);

            var results = new List<IncidentSample>();
            int sessionTimeMs = ReplayIncidentIndexDetection.ToSessionTimeMs(replaySessionTimeSec);

            for (int i = 0; i < ReplayIncidentIndexBuild.CarSlotCount; i++)
            {
                int prev = _prevFlags[i];
                int curr = flags[i];

                if (ReplayIncidentIndexDetection.IsRisingEdge(prev, curr, ReplayIncidentIndexDetection.RepairSessionFlag)
                    && TryTakePrimarySlot(_lastRepairEmitSec, i, replaySessionTimeSec))
                {
                    results.Add(new IncidentSample(
                        i,
                        sessionTimeMs,
                        ReplayIncidentIndexDetection.SourceRepairFlag,
                        null,
                        replayFrame,
                        carIdxLap != null && i < carIdxLap.Length ? carIdxLap[i] : SessionLogging.LapUnknown,
                        sessionNum,
                        lapDistPct: carIdxLapDistPct != null && i < carIdxLapDistPct.Length ? (float?)carIdxLapDistPct[i] : null,
                        carPosition: carIdxPosition != null && i < carIdxPosition.Length && carIdxPosition[i] > 0 ? (int?)carIdxPosition[i] : null));
                }

                if (ReplayIncidentIndexDetection.IsRisingEdge(prev, curr, ReplayIncidentIndexDetection.FurledSessionFlag)
                    && TryTakePrimarySlot(_lastFurledEmitSec, i, replaySessionTimeSec))
                {
                    results.Add(new IncidentSample(
                        i,
                        sessionTimeMs,
                        ReplayIncidentIndexDetection.SourceFurledFlag,
                        null,
                        replayFrame,
                        carIdxLap != null && i < carIdxLap.Length ? carIdxLap[i] : SessionLogging.LapUnknown,
                        sessionNum,
                        lapDistPct: carIdxLapDistPct != null && i < carIdxLapDistPct.Length ? (float?)carIdxLapDistPct[i] : null,
                        carPosition: carIdxPosition != null && i < carIdxPosition.Length && carIdxPosition[i] > 0 ? (int?)carIdxPosition[i] : null));
                }

                _prevFlags[i] = curr;

                // Off-track: OnTrack → OffTrack transition for any car (same signal Crew Chief uses).
                // Exclude NotInWorld (-1) as either side of the transition — that value appears during
                // replay seek / car loading and would produce spurious detections.
                if (trackSurface != null && trackSurface.Length > i)
                {
                    int prevSurf = _prevTrackSurface[i];
                    int currSurf = trackSurface[i];
                    if (prevSurf == ReplayIncidentIndexDetection.TrackSurfaceOnTrack
                        && currSurf == ReplayIncidentIndexDetection.TrackSurfaceOffTrack
                        && TryTakePrimarySlot(_lastSurfaceEmitSec, i, replaySessionTimeSec))
                    {
                        results.Add(new IncidentSample(
                            i,
                            sessionTimeMs,
                            ReplayIncidentIndexDetection.SourceTrackSurface,
                            null,
                            replayFrame,
                            carIdxLap != null && i < carIdxLap.Length ? carIdxLap[i] : SessionLogging.LapUnknown,
                            sessionNum,
                            lapDistPct: carIdxLapDistPct != null && i < carIdxLapDistPct.Length ? (float?)carIdxLapDistPct[i] : null,
                            carPosition: carIdxPosition != null && i < carIdxPosition.Length && carIdxPosition[i] > 0 ? (int?)carIdxPosition[i] : null));
                    }
                    _prevTrackSurface[i] = currSurf;
                }
            }

            if (playerCarIdx >= 0 && playerCarIdx < ReplayIncidentIndexBuild.CarSlotCount)
            {
                int delta = playerIncidents - _prevPlayerIncidents;
                // Baseline-reset guard: at very start of session (< 1s) a 0 → N jump is
                // almost certainly the field being populated late, not a real incident burst.
                // A single iRacing incident emits delta of 1, 2, or 4; deltas outside that
                // set this early in the sweep are treated as a baseline correction.
                bool baselineReset =
                    replaySessionTimeSec < 1.0
                    && _prevPlayerIncidents == 0
                    && delta > 0
                    && delta != 1 && delta != 2 && delta != 4;

                if (!baselineReset
                    && delta > 0
                    && TryTakePrimarySlot(_lastPlayerEmitSec, playerCarIdx, replaySessionTimeSec))
                {
                    int? points = (delta == 1 || delta == 2 || delta == 4) ? (int?)delta : null;
                    results.Add(new IncidentSample(
                        playerCarIdx,
                        sessionTimeMs,
                        ReplayIncidentIndexDetection.SourcePlayerIncidentCount,
                        points,
                        replayFrame,
                        carIdxLap != null && playerCarIdx < carIdxLap.Length ? carIdxLap[playerCarIdx] : SessionLogging.LapUnknown,
                        sessionNum,
                        lapDistPct: carIdxLapDistPct != null && playerCarIdx < carIdxLapDistPct.Length ? (float?)carIdxLapDistPct[playerCarIdx] : null,
                        carPosition: carIdxPosition != null && playerCarIdx < carIdxPosition.Length && carIdxPosition[playerCarIdx] > 0 ? (int?)carIdxPosition[playerCarIdx] : null));
                }

                _prevPlayerIncidents = playerIncidents;
            }
            else
                _prevPlayerIncidents = playerIncidents;

            return results;
        }
    }
}
