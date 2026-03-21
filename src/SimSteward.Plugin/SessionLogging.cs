using System;
using System.Collections.Generic;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Shared session/routing fields for structured logs. Routing uses the same env vars as Loki push
    /// (set in the process before SimHub starts, e.g. via launcher script loading .env — SimHub does not read .env itself).
    /// </summary>
    public static class SessionLogging
    {
        public const string NotInSession = "not in session";

        /// <summary>
        /// Adds <c>log_env</c> (SIMSTEWARD_LOG_ENV) and <c>loki_push_target</c> derived from SIMSTEWARD_LOKI_URL.
        /// </summary>
        public static void AppendRoutingAndDestination(Dictionary<string, object> fields)
        {
            if (fields == null) return;

            var env = Environment.GetEnvironmentVariable("SIMSTEWARD_LOG_ENV")?.Trim();
            fields["log_env"] = string.IsNullOrEmpty(env) ? "unset" : env;

            var url = Environment.GetEnvironmentVariable("SIMSTEWARD_LOKI_URL")?.Trim();
            if (string.IsNullOrEmpty(url))
                fields["loki_push_target"] = "disabled";
            else if (url.IndexOf("grafana.net", StringComparison.OrdinalIgnoreCase) >= 0)
                fields["loki_push_target"] = "grafana_cloud";
            else
                fields["loki_push_target"] = "local_or_custom";
        }
    }
}
