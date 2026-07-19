using System;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Merges a per-car sequence of raw <see cref="IncidentSample"/>s (off-track, flags, player/YAML
    /// point deltas) into escalation-aware incidents, matching iRacing's official scoring rule: "if
    /// multiple incidents happen in quick succession, only the highest-scoring incident will be
    /// tallied" — e.g. off-track (1x) -> loss of control (2x) -> heavy contact (4x) in one continuous
    /// sequence scores 4x total, never a sum. Stateful, SDK-free, same shape/justification as
    /// <see cref="ReplayIncidentIndexDetector"/> (must hold rolling per-car state across ticks).
    /// </summary>
    public sealed class IncidentSeverityCorrelator
    {
        /// <summary>
        /// Quick-succession merge window (seconds). iRacing publishes no numeric threshold for "quick
        /// succession" — this is an inferred proxy, same treatment as
        /// <see cref="ReplayIncidentIndexDetection.PrimaryDebounceSessionTimeSec"/>.
        /// </summary>
        public const double DefaultWindowSec = 6.0;

        private const int NoPoints = -1;
        private const double NoPending = -1;

        private readonly double[] _pendingLastSampleTimeSec = new double[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly int[] _pendingBestPoints = new int[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly int[] _pendingBestCauseRank = new int[ReplayIncidentIndexBuild.CarSlotCount];
        private readonly string[] _pendingBestSource = new string[ReplayIncidentIndexBuild.CarSlotCount];

        public IncidentSeverityCorrelator()
        {
            Reset();
        }

        /// <summary>Clears all pending per-car state. Call at session boundaries alongside the detector's own Reset.</summary>
        public void Reset()
        {
            for (int i = 0; i < ReplayIncidentIndexBuild.CarSlotCount; i++)
            {
                _pendingLastSampleTimeSec[i] = NoPending;
                _pendingBestPoints[i] = NoPoints;
                _pendingBestCauseRank[i] = 0;
                _pendingBestSource[i] = null;
            }
        }

        /// <summary>
        /// Correlate one raw sample against this car's currently-open incident window (if any), sliding
        /// the window forward from the *last* sample seen for that car (not fixed from the first) — an
        /// escalation can legitimately take several seconds to resolve via YAML/player-count catch-up.
        /// </summary>
        public CorrelationResult Correlate(IncidentSample sample, double sessionTimeSec, bool isDirtSurface, double windowSec = DefaultWindowSec)
        {
            int carIdx = sample.CarIdx;
            bool inRange = carIdx >= 0 && carIdx < ReplayIncidentIndexBuild.CarSlotCount;

            int? cappedPoints = ApplyDirtCap(sample.IncidentPoints, isDirtSurface);
            string sampleCause = IncidentCauseMapping.Resolve(sample.DetectionSource, cappedPoints);
            int sampleCauseRank = CauseSeverityRank(sampleCause);

            double lastSec = inRange ? _pendingLastSampleTimeSec[carIdx] : NoPending;
            bool hasPending = lastSec >= 0 && (sessionTimeSec - lastSec) <= windowSec;

            int prevBestPoints = hasPending ? _pendingBestPoints[carIdx] : NoPoints;
            int prevBestCauseRank = hasPending ? _pendingBestCauseRank[carIdx] : 0;

            int newBestPoints = Math.Max(prevBestPoints, cappedPoints ?? NoPoints);
            int newBestCauseRank = Math.Max(prevBestCauseRank, sampleCauseRank);
            // Ties prefer the newest sample so the reported source stays traceable to what just happened.
            string newBestSource = (!hasPending || sampleCauseRank >= prevBestCauseRank)
                ? sample.DetectionSource
                : _pendingBestSource[carIdx];

            int? newPoints = newBestPoints == NoPoints ? (int?)null : newBestPoints;
            string newCause = newPoints.HasValue
                ? IncidentCauseMapping.Resolve(newBestSource, newPoints) // points override — source irrelevant here
                : CauseFromRank(newBestCauseRank);

            bool isNew = !hasPending;
            bool changed = hasPending && (newBestPoints != prevBestPoints || newBestCauseRank != prevBestCauseRank);

            if (inRange)
            {
                _pendingLastSampleTimeSec[carIdx] = sessionTimeSec;
                _pendingBestPoints[carIdx] = newBestPoints;
                _pendingBestCauseRank[carIdx] = newBestCauseRank;
                _pendingBestSource[carIdx] = newBestSource;
            }

            var merged = new IncidentSample(
                sample.CarIdx, sample.SessionTimeMs, newBestSource, newPoints, sample.ReplayFrame,
                sample.Lap, sample.SessionNum, sample.LapDistPct, sample.CarPosition, sample.IsAggregateDelta);

            return new CorrelationResult(isNew, changed, merged, newCause);
        }

        /// <summary>iRacing's official rule: heavy contact (4x) caps at 2x on Dirt Oval/Dirt Road. Unvalidated heuristic — see SimStewardPlugin.LiveIncidentDetection.cs for the dirt-session detection signal.</summary>
        private static int? ApplyDirtCap(int? points, bool isDirtSurface)
        {
            if (isDirtSurface && points.HasValue && points.Value == 4)
                return 2;
            return points;
        }

        private static int CauseSeverityRank(string cause)
        {
            if (cause == IncidentCauseMapping.CauseOffTrack) return 1;
            if (cause == IncidentCauseMapping.CauseFlagged) return 2;
            if (cause == IncidentCauseMapping.CauseSpin) return 3;
            if (cause == IncidentCauseMapping.CauseContact) return 4;
            return 0; // unknown
        }

        private static string CauseFromRank(int rank)
        {
            switch (rank)
            {
                case 1: return IncidentCauseMapping.CauseOffTrack;
                case 2: return IncidentCauseMapping.CauseFlagged;
                case 3: return IncidentCauseMapping.CauseSpin;
                case 4: return IncidentCauseMapping.CauseContact;
                default: return IncidentCauseMapping.CauseUnknown;
            }
        }
    }

    /// <summary>Result of <see cref="IncidentSeverityCorrelator.Correlate"/> — what the caller should do with this sample.</summary>
    public readonly struct CorrelationResult
    {
        public CorrelationResult(bool isNewIncident, bool isEscalation, IncidentSample merged, string cause)
        {
            IsNewIncident = isNewIncident;
            IsEscalation = isEscalation;
            Merged = merged;
            Cause = cause;
        }

        /// <summary>No prior pending incident was open for this carIdx — treat as a fresh board row.</summary>
        public bool IsNewIncident { get; }
        /// <summary>Merged into an existing pending incident AND points/cause changed — amend, don't duplicate.</summary>
        public bool IsEscalation { get; }
        /// <summary>Final sample to log/broadcast: positional fields passthrough from the triggering sample; IncidentPoints is the running max (dirt-capped), DetectionSource is whichever sample currently drives the classification.</summary>
        public IncidentSample Merged { get; }
        /// <summary>Final cause label for <see cref="Merged"/> — computed once here so callers never need to re-derive it (re-deriving from Merged.DetectionSource alone can pick the wrong cause when the winning sample isn't the latest one).</summary>
        public string Cause { get; }
    }
}
