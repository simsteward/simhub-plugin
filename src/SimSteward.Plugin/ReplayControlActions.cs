using System;
using System.Collections.Generic;
using System.Globalization;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Pure helpers for the test-rig replay-control DispatchAction branches and misfire evaluator.
    /// All logic is SDK-free so it can be unit-tested without IRSDK.
    /// See <c>docs/RULES-TestRig-Contract.md</c> for the contract.
    /// </summary>
    public static class ReplayControlActions
    {
        /// <summary>Plugin caps replay speed to ±16× (iRacing's documented max). Out-of-range = bad_arg.</summary>
        public const int MaxReplaySpeedMagnitude = 16;

        /// <summary>Misfire tolerance: matched index row must be within ±500 ms session-time of landed frame.</summary>
        public const int MisfireToleranceMs = 500;

        /// <summary>Wall-clock settle window after issuing a jump before evaluating misfire.</summary>
        public const int MisfireSettleMs = 750;

        /// <summary>How long misfire stays visible on subsequent <c>replay_state_tick</c>s.</summary>
        public const int MisfireVisibilityMs = 2000;

        /// <summary>Live tick cadence (ms).</summary>
        public const int LiveTickCadenceMs = 250;

        // ── replay_set_speed ────────────────────────────────────────────────
        public readonly struct SetSpeedResult
        {
            public SetSpeedResult(bool ok, int magnitude, bool slowMotion, string error)
            {
                Ok = ok;
                Magnitude = magnitude;
                SlowMotion = slowMotion;
                Error = error;
            }
            public bool Ok { get; }
            public int Magnitude { get; }
            public bool SlowMotion { get; }
            public string Error { get; }
        }

        /// <summary>
        /// Parse a <c>replay_set_speed</c> arg. Accepted: <c>"1"</c>, <c>"2"</c>, <c>"4"</c>, <c>"8"</c>, <c>"16"</c>,
        /// <c>"0.5"</c> (slow-mo). <c>magnitude</c> is unsigned; direction comes from
        /// <see cref="SimStewardPlugin"/>'s <c>_lastReplayDirection</c> state (applied at the SDK call site).
        /// </summary>
        public static SetSpeedResult ParseSetSpeed(string arg)
        {
            if (!double.TryParse((arg ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var raw))
                return new SetSpeedResult(false, 0, false, "bad_arg");
            if (raw <= 0)
                return new SetSpeedResult(false, 0, false, "bad_arg");

            if (raw < 1.0)
            {
                // Slow-motion: arg is a multiplier <1 (0.5 → ½×). SDK takes the divisor.
                int divisor = (int)Math.Round(1.0 / raw);
                if (divisor < 1) divisor = 1;
                return new SetSpeedResult(true, divisor, true, null);
            }

            // arg ≥ 1. Allow exact 1/2/4/8/16; anything >16 is rejected per contract.
            int magnitude = (int)Math.Round(raw);
            if (magnitude < 1 || magnitude > MaxReplaySpeedMagnitude)
                return new SetSpeedResult(false, 0, false, "bad_arg");
            return new SetSpeedResult(true, magnitude, false, null);
        }

        // ── replay_set_direction ────────────────────────────────────────────
        public readonly struct SetDirectionResult
        {
            public SetDirectionResult(bool ok, string direction, string error)
            {
                Ok = ok;
                Direction = direction;
                Error = error;
            }
            public bool Ok { get; }
            public string Direction { get; }
            public string Error { get; }
        }

        public static SetDirectionResult ParseSetDirection(string arg)
        {
            var dir = (arg ?? "").Trim().ToLowerInvariant();
            if (dir != "forward" && dir != "reverse")
                return new SetDirectionResult(false, null, "bad_arg");
            return new SetDirectionResult(true, dir, null);
        }

        // ── replay_seek_frame ───────────────────────────────────────────────
        public readonly struct SeekFrameResult
        {
            public SeekFrameResult(bool ok, int frame, string error)
            {
                Ok = ok;
                Frame = frame;
                Error = error;
            }
            public bool Ok { get; }
            public int Frame { get; }
            public string Error { get; }
        }

        public static SeekFrameResult ParseSeekFrame(string arg)
        {
            if (!int.TryParse((arg ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var frame))
                return new SeekFrameResult(false, 0, "bad_arg");
            if (frame < 0)
                return new SeekFrameResult(false, 0, "bad_arg");
            return new SeekFrameResult(true, frame, null);
        }

        // ── jump-incident pickers ───────────────────────────────────────────
        public readonly struct JumpResolveResult
        {
            public JumpResolveResult(bool ok, ReplayIncidentIndexIncidentRow row, int rowIndex, string error)
            {
                Ok = ok;
                Row = row;
                RowIndex = rowIndex;
                Error = error;
            }
            public bool Ok { get; }
            public ReplayIncidentIndexIncidentRow Row { get; }
            public int RowIndex { get; }
            public string Error { get; }
        }

        /// <summary>
        /// Resolve the next-incident jump target — first row with <c>ReplayFrame &gt; currentFrame</c>.
        /// Returns <c>at_end</c> if already past the last incident.
        /// </summary>
        public static JumpResolveResult ResolveNextIncident(
            IReadOnlyList<ReplayIncidentIndexIncidentRow> rows, int currentFrame)
        {
            if (rows == null || rows.Count == 0)
                return new JumpResolveResult(false, null, -1, "index_unavailable");
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].ReplayFrame > currentFrame)
                    return new JumpResolveResult(true, rows[i], i, null);
            }
            return new JumpResolveResult(false, null, -1, "at_end");
        }

        /// <summary>
        /// Resolve the previous-incident jump target — last row with <c>ReplayFrame &lt; currentFrame</c>.
        /// </summary>
        public static JumpResolveResult ResolvePrevIncident(
            IReadOnlyList<ReplayIncidentIndexIncidentRow> rows, int currentFrame)
        {
            if (rows == null || rows.Count == 0)
                return new JumpResolveResult(false, null, -1, "index_unavailable");
            for (int i = rows.Count - 1; i >= 0; i--)
            {
                if (rows[i].ReplayFrame < currentFrame)
                    return new JumpResolveResult(true, rows[i], i, null);
            }
            return new JumpResolveResult(false, null, -1, "at_start");
        }

        /// <summary>
        /// Resolve the next-incident jump target scoped to one driver — first row with
        /// <c>CarIdx == carIdx</c> and <c>ReplayFrame &gt; currentFrame</c>. Distinguishes "this car
        /// has no incidents at all" (<c>no_incidents_for_car</c>) from "already past this car's last
        /// one" (<c>at_end</c>) so the dashboard can show an accurate message either way.
        /// </summary>
        public static JumpResolveResult ResolveNextIncidentForCar(
            IReadOnlyList<ReplayIncidentIndexIncidentRow> rows, int currentFrame, int carIdx)
        {
            if (rows == null || rows.Count == 0)
                return new JumpResolveResult(false, null, -1, "index_unavailable");
            if (carIdx < 0)
                return new JumpResolveResult(false, null, -1, "no_car_selected");
            bool carHasAny = false;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].CarIdx != carIdx) continue;
                carHasAny = true;
                if (rows[i].ReplayFrame > currentFrame)
                    return new JumpResolveResult(true, rows[i], i, null);
            }
            return new JumpResolveResult(false, null, -1, carHasAny ? "at_end" : "no_incidents_for_car");
        }

        /// <summary>Resolve the previous-incident jump target scoped to one driver — mirrors <see cref="ResolveNextIncidentForCar"/>.</summary>
        public static JumpResolveResult ResolvePrevIncidentForCar(
            IReadOnlyList<ReplayIncidentIndexIncidentRow> rows, int currentFrame, int carIdx)
        {
            if (rows == null || rows.Count == 0)
                return new JumpResolveResult(false, null, -1, "index_unavailable");
            if (carIdx < 0)
                return new JumpResolveResult(false, null, -1, "no_car_selected");
            bool carHasAny = false;
            for (int i = rows.Count - 1; i >= 0; i--)
            {
                if (rows[i].CarIdx != carIdx) continue;
                carHasAny = true;
                if (rows[i].ReplayFrame < currentFrame)
                    return new JumpResolveResult(true, rows[i], i, null);
            }
            return new JumpResolveResult(false, null, -1, carHasAny ? "at_start" : "no_incidents_for_car");
        }

        // ── misfire evaluator ───────────────────────────────────────────────
        public readonly struct MisfireResult
        {
            public MisfireResult(
                bool misfire,
                ReplayIncidentIndexIncidentRow nearestRow,
                int landedFrame,
                int landedSessionTimeMs,
                int deltaMs,
                string nearestFingerprint)
            {
                Misfire = misfire;
                NearestRow = nearestRow;
                LandedFrame = landedFrame;
                LandedSessionTimeMs = landedSessionTimeMs;
                DeltaMs = deltaMs;
                NearestFingerprint = nearestFingerprint;
            }
            public bool Misfire { get; }
            public ReplayIncidentIndexIncidentRow NearestRow { get; }
            public int LandedFrame { get; }
            public int LandedSessionTimeMs { get; }
            public int DeltaMs { get; }
            public string NearestFingerprint { get; }
        }

        /// <summary>
        /// Evaluate whether a jump landed on the expected incident.
        /// Misfire when no row exists within ±<see cref="MisfireToleranceMs"/> ms of the landed
        /// session-time, OR the matched row's fingerprint differs from <paramref name="expectedFingerprint"/>.
        /// </summary>
        public static MisfireResult EvaluateMisfire(
            IReadOnlyList<ReplayIncidentIndexIncidentRow> rows,
            int landedFrame,
            int landedSessionTimeMs,
            string expectedFingerprint)
        {
            if (rows == null || rows.Count == 0)
                return new MisfireResult(true, null, landedFrame, landedSessionTimeMs, int.MaxValue, null);

            ReplayIncidentIndexIncidentRow nearest = null;
            int bestDelta = int.MaxValue;
            for (int i = 0; i < rows.Count; i++)
            {
                int d = Math.Abs(rows[i].SessionTimeMs - landedSessionTimeMs);
                if (d < bestDelta)
                {
                    bestDelta = d;
                    nearest = rows[i];
                }
            }

            if (nearest == null || bestDelta > MisfireToleranceMs)
                return new MisfireResult(true, nearest, landedFrame, landedSessionTimeMs, bestDelta, nearest?.Fingerprint);

            bool fingerprintMatches = string.Equals(nearest.Fingerprint, expectedFingerprint, StringComparison.Ordinal);
            return new MisfireResult(!fingerprintMatches, nearest, landedFrame, landedSessionTimeMs, bestDelta, nearest.Fingerprint);
        }

        /// <summary>
        /// Compose the structured-log fields for <c>replay_jump_misfire</c>.
        /// The caller adds <c>session_yaml_fingerprint_sha256_16</c> via <c>MergeSessionAndRoutingFields</c>.
        /// </summary>
        public static Dictionary<string, object> BuildMisfireLogFields(
            string direction,
            int expectedFrame,
            int expectedSessionTimeMs,
            string expectedFingerprint,
            MisfireResult result,
            string indexPath)
        {
            return new Dictionary<string, object>
            {
                ["direction"] = direction ?? "",
                ["expected_frame"] = expectedFrame,
                ["expected_session_time_ms"] = expectedSessionTimeMs,
                ["expected_fingerprint"] = expectedFingerprint ?? "",
                ["landed_frame"] = result.LandedFrame,
                ["landed_session_time_ms"] = result.LandedSessionTimeMs,
                ["delta_ms"] = result.DeltaMs == int.MaxValue ? -1 : result.DeltaMs,
                ["nearest_fingerprint"] = result.NearestFingerprint ?? "",
                ["nearest_session_time_ms"] = result.NearestRow?.SessionTimeMs ?? -1,
                ["misfire"] = result.Misfire,
                ["index_path"] = indexPath ?? ""
            };
        }
    }
}
