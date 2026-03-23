using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

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

        /// <summary>
        /// First 16 hex chars of SHA-256(UTF-8 session YAML) for log correlation; empty if YAML missing.
        /// Does not embed raw YAML (size, PII).
        /// </summary>
        public static string ComputeSessionYamlFingerprint(string sessionInfoYaml)
        {
            if (string.IsNullOrEmpty(sessionInfoYaml))
                return "";

            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sessionInfoYaml));
                var sb = new StringBuilder(16);
                for (int i = 0; i < 8 && i < hash.Length; i++)
                    sb.AppendFormat("{0:x2}", hash[i]);
                return sb.ToString();
            }
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
            bool isReplayMode,
            string sessionInfoYaml,
            int sessionInfoUpdate)
        {
            string fp = ComputeSessionYamlFingerprint(sessionInfoYaml);
            var f = new Dictionary<string, object>
            {
                ["sim_mode"] = simMode ?? "",
                ["subsession_id"] = subSessionId > 0 ? subSessionId.ToString() : SessionLogging.NotInSession,
                ["parent_session_id"] = parentSessionId > 0 ? parentSessionId.ToString() : SessionLogging.NotInSession,
                ["session_num"] = sessionNum.ToString(),
                ["track_display_name"] = trackDisplayName ?? "",
                ["is_replay_mode"] = isReplayMode,
                ["session_yaml_fingerprint_sha256_16"] = fp,
                ["session_yaml_length"] = sessionInfoYaml?.Length ?? 0,
                ["session_info_update"] = sessionInfoUpdate
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
