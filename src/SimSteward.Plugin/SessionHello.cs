using Newtonsoft.Json;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Builds the <c>session_hello</c> WS payload for test-rig subsession
    /// auto-discovery. See docs/RULES-TestRig-Contract.md and
    /// docs/superpowers/specs/2026-06-21-test-rig-subsession-autodiscovery-design.md.
    /// </summary>
    public static class SessionHello
    {
        public static string BuildJson(int? subSessionId, string simMode, string pluginMode)
        {
            int? sub = (subSessionId.HasValue && subSessionId.Value > 0) ? subSessionId : null;
            return JsonConvert.SerializeObject(new
            {
                type           = "session_hello",
                sub_session_id = sub,
                sim_mode       = string.IsNullOrEmpty(simMode) ? null : simMode,
                plugin_mode    = string.IsNullOrEmpty(pluginMode) ? "Unknown" : pluginMode,
            });
        }
    }
}
