using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class ReplayIncidentIndexPrerequisitesTests
    {
        [Fact]
        public void Evaluate_Disconnected_NotReady()
        {
            var e = ReplayIncidentIndexPrerequisites.Evaluate(false, "replay", 12345);
            Assert.False(e.SdkConnected);
            Assert.True(e.HasSubSessionId);
            Assert.True(e.IsReplayMode);
            Assert.False(e.IsFullyReady);
        }

        [Fact]
        public void Evaluate_Replay_WithSubSession_FullyReady()
        {
            var e = ReplayIncidentIndexPrerequisites.Evaluate(true, "replay", 999);
            Assert.True(e.SdkConnected);
            Assert.True(e.HasSubSessionId);
            Assert.True(e.IsReplayMode);
            Assert.True(e.IsFullyReady);
        }

        [Fact]
        public void Evaluate_Replay_CaseInsensitive()
        {
            var e = ReplayIncidentIndexPrerequisites.Evaluate(true, " RePlay ", 1);
            Assert.True(e.IsReplayMode);
            Assert.True(e.IsFullyReady);
        }

        [Fact]
        public void Evaluate_Live_NotReplayMode()
        {
            var e = ReplayIncidentIndexPrerequisites.Evaluate(true, "full", 555);
            Assert.False(e.IsReplayMode);
            Assert.False(e.IsFullyReady);
        }

        [Fact]
        public void Evaluate_MissingSubSession_NotFullyReady()
        {
            var e = ReplayIncidentIndexPrerequisites.Evaluate(true, "replay", 0);
            Assert.False(e.HasSubSessionId);
            Assert.False(e.IsFullyReady);
        }

        [Fact]
        public void SessionContextShouldWarn_SubSessionButNotReplay()
        {
            Assert.True(ReplayIncidentIndexPrerequisites.SessionContextShouldWarn(1, false));
            Assert.False(ReplayIncidentIndexPrerequisites.SessionContextShouldWarn(0, false));
            Assert.False(ReplayIncidentIndexPrerequisites.SessionContextShouldWarn(1, true));
        }

        [Fact]
        public void BuildSdkReadyFields_HasKeys()
        {
            var f = ReplayIncidentIndexPrerequisites.BuildSdkReadyFields(true, 1);
            Assert.True(f.ContainsKey("irsdk_connected"));
            Assert.True(f.ContainsKey("update_interval_ms"));
            Assert.True(f.ContainsKey("log_env"));
            Assert.True(f.ContainsKey("loki_push_target"));
        }

        [Fact]
        public void BuildSessionContextFields_SubsessionStrings()
        {
            const string yaml = "WeekendInfo:\n  SubSessionID: 42\n";
            var f = ReplayIncidentIndexPrerequisites.BuildSessionContextFields("replay", 42, 7, 2, "Test Track", true, yaml, 3);
            Assert.Equal("42", f["subsession_id"]);
            Assert.Equal("7", f["parent_session_id"]);
            Assert.Equal("2", f["session_num"]);
            Assert.Equal(true, f["is_replay_mode"]);
            Assert.Equal(3, f["session_info_update"]);
            Assert.Equal(yaml.Length, f["session_yaml_length"]);
            var fp = (string)f["session_yaml_fingerprint_sha256_16"];
            Assert.Equal(16, fp.Length);
            Assert.Equal(ReplayIncidentIndexPrerequisites.ComputeSessionYamlFingerprint(yaml), fp);
        }

        [Fact]
        public void ComputeSessionYamlFingerprint_Empty_YieldsEmpty()
        {
            Assert.Equal("", ReplayIncidentIndexPrerequisites.ComputeSessionYamlFingerprint(null));
            Assert.Equal("", ReplayIncidentIndexPrerequisites.ComputeSessionYamlFingerprint(""));
        }

        [Fact]
        public void ComputeSessionYamlFingerprint_Deterministic()
        {
            const string y = "---\nSessionInfo:\n  Sessions: []\n";
            Assert.Equal(
                ReplayIncidentIndexPrerequisites.ComputeSessionYamlFingerprint(y),
                ReplayIncidentIndexPrerequisites.ComputeSessionYamlFingerprint(y));
        }
    }
}
