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
        private readonly int[] _prevFastRepairs = new int[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly double[] _lastRepairEmitSec = new double[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly double[] _lastFurledEmitSec = new double[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly double[] _lastPlayerEmitSec = new double[ReplayIncidentIndexBuild.CarSlotCount];

        private int _prevPlayerIncidents;
        private readonly List<FastRepairDelta> _fastRepairDeltas = new List<FastRepairDelta>();

        private static void ValidateLength(string name, int[] arr, int required)
        {
            if (arr == null || arr.Length < required)
                throw new ArgumentException(name + " must have length >= " + required + ".", name);
        }

        /// <summary>
        /// TR-005/006/017 baseline: first <see cref="Process"/> compares against these arrays, not zeros.
        /// </summary>
        public void Reset(int[] baselineFlags, int baselinePlayerIncidents, int playerCarIdx, int[] baselineFastRepairs)
        {
            ValidateLength(nameof(baselineFlags), baselineFlags, ReplayIncidentIndexBuild.CarSlotCount);
            ValidateLength(nameof(baselineFastRepairs), baselineFastRepairs, ReplayIncidentIndexBuild.CarSlotCount);
            if (playerCarIdx < -1 || playerCarIdx >= ReplayIncidentIndexBuild.CarSlotCount)
                throw new ArgumentOutOfRangeException(nameof(playerCarIdx));

            Array.Copy(baselineFlags, 0, _prevFlags, 0, ReplayIncidentIndexBuild.CarSlotCount);
            Array.Copy(baselineFastRepairs, 0, _prevFastRepairs, 0, ReplayIncidentIndexBuild.CarSlotCount);
            _prevPlayerIncidents = baselinePlayerIncidents;

            for (int i = 0; i < ReplayIncidentIndexBuild.CarSlotCount; i++)
            {
                _lastRepairEmitSec[i] = -1;
                _lastFurledEmitSec[i] = -1;
                _lastPlayerEmitSec[i] = -1;
            }

            _fastRepairDeltas.Clear();
        }

        /// <summary>TR-017 observations since <see cref="Reset"/>.</summary>
        public IReadOnlyList<FastRepairDelta> FastRepairDeltas => _fastRepairDeltas;

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
        /// One native SDK sample: compare to previous frame, emit primary incidents and TR-017 side records.
        /// </summary>
        public List<IncidentSample> Process(
            double replaySessionTimeSec,
            int[] flags,
            int playerIncidents,
            int playerCarIdx,
            int[] fastRepairsUsed,
            int replayFrame)
        {
            ValidateLength(nameof(flags), flags, ReplayIncidentIndexBuild.CarSlotCount);
            ValidateLength(nameof(fastRepairsUsed), fastRepairsUsed, ReplayIncidentIndexBuild.CarSlotCount);

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
                        replayFrame));
                }

                if (ReplayIncidentIndexDetection.IsRisingEdge(prev, curr, ReplayIncidentIndexDetection.FurledSessionFlag)
                    && TryTakePrimarySlot(_lastFurledEmitSec, i, replaySessionTimeSec))
                {
                    results.Add(new IncidentSample(
                        i,
                        sessionTimeMs,
                        ReplayIncidentIndexDetection.SourceFurledFlag,
                        null,
                        replayFrame));
                }

                int prevFr = _prevFastRepairs[i];
                int currFr = fastRepairsUsed[i];
                if (currFr > prevFr)
                {
                    _fastRepairDeltas.Add(new FastRepairDelta(
                        i,
                        sessionTimeMs,
                        replayFrame,
                        prevFr,
                        currFr));
                }

                _prevFlags[i] = curr;
                _prevFastRepairs[i] = currFr;
            }

            if (playerCarIdx >= 0 && playerCarIdx < ReplayIncidentIndexBuild.CarSlotCount)
            {
                int delta = playerIncidents - _prevPlayerIncidents;
                if (delta > 0
                    && TryTakePrimarySlot(_lastPlayerEmitSec, playerCarIdx, replaySessionTimeSec))
                {
                    int? points = (delta == 1 || delta == 2 || delta == 4) ? (int?)delta : null;
                    results.Add(new IncidentSample(
                        playerCarIdx,
                        sessionTimeMs,
                        ReplayIncidentIndexDetection.SourcePlayerIncidentCount,
                        points,
                        replayFrame));
                }

                _prevPlayerIncidents = playerIncidents;
            }
            else
                _prevPlayerIncidents = playerIncidents;

            return results;
        }
    }
}
