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

        /// <summary>Default fast-forward multiplier (TR-008); tune empirically. Capped at iRacing's documented max (16×).</summary>
        public const int DefaultFastForwardPlaySpeed = 16;

        /// <summary>How often (in telemetry ticks) to verify iRacing is running at the requested fast-forward speed and re-issue if not (~1.67s at 60Hz).</summary>
        public const int FfSpeedCheckIntervalTicks = 100;

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
        /// <summary>TR-028: one structured line per primary detection during fast-forward (same fingerprint as TR-020 JSON rows).</summary>
        public const string EventDetection = "replay_incident_index_detection";
        public const string EventValidationSummary = "replay_incident_index_validation_summary";

        /// <summary>M6 TR-038 / TR-040: batched structured hint for 60Hz record mode (not per-tick).</summary>
        public const string EventRecordWindow = "replay_incident_index_record_window";

        /// <summary>Periodic (every FfSpeedCheckIntervalTicks) speed verification during fast-forward. DEBUG only.</summary>
        public const string EventFfSpeedCheck = "replay_index_ff_speed_check";
        /// <summary>First time ReplayPlaySpeed reads back at the requested value after fast-forward starts.</summary>
        public const string EventFfSpeedConfirmed = "replay_index_ff_speed_confirmed";
        /// <summary>Speed was confirmed but has since dropped away from the requested value.</summary>
        public const string EventFfSpeedLost = "replay_index_ff_speed_lost";

        /// <summary>How often (in telemetry ticks) to emit a Loki sweep-progress heartbeat during fast-forward (~16.7s at 60Hz).</summary>
        public const int FfProgressLogIntervalTicks = 1000;
        /// <summary>Periodic sweep progress heartbeat: frame position, %, sample count, speed (INFO).</summary>
        public const string EventFfProgress = "replay_index_ff_progress";
        /// <summary>IsReplayPlaying went false before natural end — unexpected mid-build stop (WARN).</summary>
        public const string EventFfUnexpectedStop = "replay_index_ff_unexpected_stop";
        /// <summary>Checkered flag detected during fast-forward sweep (INFO).</summary>
        public const string EventFfCheckeredDetected = "replay_index_ff_checkered_detected";
        /// <summary>90k telemetry sample cap reached — forcing build completion (WARN).</summary>
        public const string EventFfSampleCapHit = "replay_index_ff_sample_cap_hit";

        /// <summary>YAML ResultsPositions[] snapshot parsed during FF — fields document trigger, deltas, baseline state.</summary>
        public const string EventYamlSnapshot = "replay_index_ff_yaml_snapshot";

        /// <summary>Fallback periodic YAML re-poll cadence (telemetry ticks). Belt-and-suspenders for cases where SessionInfoUpdate doesn't tick during replay scrub.</summary>
        public const int FfYamlFallbackPollIntervalTicks = 600;

        /// <summary>Max telemetry ticks waiting for the end-of-replay seek to land and YAML to stabilize (~10s at 60Hz).</summary>
        public const int SeekEndTimeoutTelemetryTicks = 600;
        /// <summary>Consecutive telemetry samples with stable SessionInfoUpdate before we trust the end-state YAML.</summary>
        public const int SeekEndStableYamlSamples = 6;

        /// <summary>Phase transition log — fired whenever the build phase changes.</summary>
        public const string EventPhaseChanged = "replay_index_build_phase_changed";
        /// <summary>End-first pre-pass succeeded: final per-driver Incidents tallies captured before the sweep starts.</summary>
        public const string EventExpectedLedgerCaptured = "replay_index_expected_ledger_captured";
        /// <summary>End-first pre-pass failed (timeout, YAML parse error, etc.) — build proceeds without expected ledger.</summary>
        public const string EventExpectedLedgerSkipped = "replay_index_expected_ledger_skipped";
        /// <summary>Per-driver gap at finalize: detected count differs from YAML expected count. WARN when under-detected, INFO when over-detected.</summary>
        public const string EventDriverGap = "replay_index_driver_gap";
        /// <summary>Top-level completion audit emitted at finalize: total drivers with gaps, coverage %, summary metrics.</summary>
        public const string EventBuildCompletionAudit = "replay_index_build_completion_audit";
        /// <summary>Build start: camera focus snapped to the first entry in the focus-car list (today: player; future: race-director selection).</summary>
        public const string EventCameraLockedToPlayer = "replay_index_camera_locked_to_player";

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
