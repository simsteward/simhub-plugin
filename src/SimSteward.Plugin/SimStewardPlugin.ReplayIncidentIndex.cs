#if SIMHUB_SDK
using System;

namespace SimSteward.Plugin
{
    public partial class SimStewardPlugin
    {
        /// <summary>Throttle key for replay_incident_index_session_context (subsession + session + SimMode).</summary>
        private string _replayIncidentIndexPrereqLogKey = "";

        private void MaybeLogReplayIncidentIndexSessionContext()
        {
            if (_logger == null || _irsdk == null || !_irsdk.IsConnected)
                return;

            try
            {
                string simMode = _irsdk.Data?.SessionInfo?.WeekendInfo?.SimMode ?? "";
                int subId = _irsdk.Data?.SessionInfo?.WeekendInfo?.SubSessionID ?? 0;
                int sessionNum = SafeGetInt("SessionNum");
                string key = subId + "_" + sessionNum + "_" + simMode;
                if (key == _replayIncidentIndexPrereqLogKey)
                    return;
                _replayIncidentIndexPrereqLogKey = key;

                int parentId = 0;
                try
                {
                    parentId = _irsdk.Data?.SessionInfo?.WeekendInfo?.SessionID ?? 0;
                }
                catch
                {
                    // ignored
                }

                string trackName = "";
                try
                {
                    trackName = _irsdk.Data?.SessionInfo?.WeekendInfo?.TrackDisplayName ?? "";
                }
                catch
                {
                    // ignored
                }

                string sessionYaml = null;
                int sessionInfoUpdate = 0;
                try
                {
                    sessionYaml = _irsdk.Data?.SessionInfoYaml;
                    sessionInfoUpdate = _irsdk.Data?.SessionInfoUpdate ?? 0;
                }
                catch
                {
                    // ignored
                }

                var eval = ReplayIncidentIndexPrerequisites.Evaluate(true, simMode, subId);
                var fields = ReplayIncidentIndexPrerequisites.BuildSessionContextFields(
                    simMode,
                    subId,
                    parentId,
                    sessionNum,
                    trackName,
                    eval.IsReplayMode,
                    sessionYaml,
                    sessionInfoUpdate);

                bool warn = ReplayIncidentIndexPrerequisites.SessionContextShouldWarn(subId, eval.IsReplayMode);
                string level = warn ? "WARN" : "INFO";
                string msg = eval.IsReplayMode
                    ? "Replay incident index: session YAML context (WeekendInfo.SimMode=replay, TR-002/003)."
                    : "Replay incident index: session loaded but SimMode is not replay; index build must not run until in replay (TR-002).";

                _logger.Structured(level, "simhub-plugin", ReplayIncidentIndexPrerequisites.EventSessionContext, msg, fields, "lifecycle", null);
            }
            catch
            {
                // never break OnSessionInfo
            }
        }
    }
}
#endif
