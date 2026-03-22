using System;
using System.Collections.Generic;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Milestone 1 (TR-001–TR-003): replay incident index SDK readiness from IRSDKSharper session YAML
    /// (parsed WeekendInfo), not SimHub DataUpdate alone.
    /// </summary>
    public static class ReplayIncidentIndexPrerequisites
    {
        public const string EventSdkReady = "replay_incident_index_sdk_ready";
        public const string EventSessionContext = "replay_incident_index_session_context";

        /// <summary>Outcome of evaluating whether replay incident indexing prerequisites are met.</summary>
        public readonly struct Evaluation
        {
            public Evaluation(bool sdkConnected, bool hasSubSessionId, bool isReplayMode)
            {
                SdkConnected = sdkConnected;
                HasSubSessionId = hasSubSessionId;
                IsReplayMode = isReplayMode;
            }

            public bool SdkConnected { get; }
            public bool HasSubSessionId { get; }
            public bool IsReplayMode { get; }

            /// <summary>True when connected, subsession &gt; 0, and SimMode is replay (TR-002/003 aligned).</summary>
            public bool IsFullyReady => SdkConnected && HasSubSessionId && IsReplayMode;
        }

        public static Evaluation Evaluate(bool isConnected, string weekendSimMode, int subSessionId)
        {
            bool replay = string.Equals(weekendSimMode?.Trim(), "replay", StringComparison.OrdinalIgnoreCase);
            return new Evaluation(isConnected, subSessionId > 0, replay);
        }

        public static Dictionary<string, object> BuildSdkReadyFields(bool irsdkConnected, int updateIntervalMs)
        {
            var f = new Dictionary<string, object>
            {
                ["irsdk_connected"] = irsdkConnected,
                ["update_interval_ms"] = updateIntervalMs
            };
            SessionLogging.AppendRoutingAndDestination(f);
            return f;
        }

        /// <summary>Fields for <see cref="EventSessionContext"/>; caller supplies session spine fields.</summary>
        public static Dictionary<string, object> BuildSessionContextFields(
            string simMode,
            int subSessionId,
            int parentSessionId,
            int sessionNum,
            string trackDisplayName,
            bool isReplayMode)
        {
            var f = new Dictionary<string, object>
            {
                ["sim_mode"] = simMode ?? "",
                ["subsession_id"] = subSessionId > 0 ? subSessionId.ToString() : SessionLogging.NotInSession,
                ["parent_session_id"] = parentSessionId > 0 ? parentSessionId.ToString() : SessionLogging.NotInSession,
                ["session_num"] = sessionNum,
                ["track_display_name"] = trackDisplayName ?? "",
                ["is_replay_mode"] = isReplayMode
            };
            SessionLogging.AppendRoutingAndDestination(f);
            return f;
        }

        /// <summary>WARN when a session is loaded (subsession &gt; 0) but SimMode is not replay — index build must not proceed (TR-002).</summary>
        public static bool SessionContextShouldWarn(int subSessionId, bool isReplayMode)
        {
            return subSessionId > 0 && !isReplayMode;
        }
    }
}
