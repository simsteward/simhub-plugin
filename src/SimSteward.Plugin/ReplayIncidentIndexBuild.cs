using System;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Milestone 2 (TR-004–TR-011, NFR-008): helpers for replay incident index fast-forward / baseline.
    /// iRacing uses 64 car slots in telemetry (CarIdx 0–63).
    /// </summary>
    public static class ReplayIncidentIndexBuild
    {
        /// <summary>Number of <c>CarIdx*</c> array slots in iRacing telemetry.</summary>
        public const int CarSlotCount = 64;

        /// <summary>Default fast-forward multiplier (TR-008); tune empirically.</summary>
        public const int DefaultFastForwardPlaySpeed = 16;

        /// <summary>Consecutive telemetry samples with <c>ReplayFrameNum == 0</c> before baseline (TR-004).</summary>
        public const int FrameZeroStableConsecutiveSamples = 4;

        /// <summary>Max telemetry ticks waiting for frame 0 after <c>ToStart</c> (~10s at 60Hz).</summary>
        public const int SeekStartTimeoutTelemetryTicks = 600;

        public const string EventStarted = "replay_incident_index_started";
        public const string EventBaselineReady = "replay_incident_index_baseline_ready";
        public const string EventFastForwardStarted = "replay_incident_index_fast_forward_started";
        public const string EventFastForwardComplete = "replay_incident_index_fast_forward_complete";
        public const string EventBuildError = "replay_incident_index_build_error";
        public const string EventBuildCancelled = "replay_incident_index_build_cancelled";

        /// <summary>
        /// Effective SDK sample rate relative to <strong>replay session time</strong> when replay plays at
        /// <paramref name="playSpeed"/>× (real-time poll ~60Hz). NFR-008 / §2.7.
        /// </summary>
        public static double ComputeEffectiveSessionTimeSampleHz(double playSpeed)
        {
            if (playSpeed <= 0 || double.IsNaN(playSpeed) || double.IsInfinity(playSpeed))
                return 0;
            return 60.0 / playSpeed;
        }

        /// <summary>
        /// Updates consecutive-zero count for <c>ReplayFrameNum</c> stabilization (TR-004).
        /// </summary>
        public static int NextFrameZeroConsecutiveCount(int replayFrameNum, int consecutiveSoFar)
        {
            return replayFrameNum == 0 ? consecutiveSoFar + 1 : 0;
        }

        /// <summary>
        /// Classify why playback stopped: natural end vs pause/stop (TR-010 ambiguity).
        /// </summary>
        public static string InferCompletionReason(
            bool replayPlaying,
            int replayFrameNum,
            int replayFrameNumEnd,
            double replaySessionTimeSec)
        {
            if (replayPlaying)
                return "playing";

            int end = Math.Max(0, replayFrameNumEnd);
            if (end > 0 && replayFrameNum >= end - 2)
                return "replay_finished";

            // Heuristic: very late in session time (full race often 2400–12000+ s) — optional
            if (replaySessionTimeSec > 1.0 && end > 0 && replayFrameNum >= (int)(end * 0.98))
                return "replay_finished";

            return "paused_or_stopped";
        }
    }
}
