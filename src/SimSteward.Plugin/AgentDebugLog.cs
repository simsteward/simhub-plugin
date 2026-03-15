using System;
using System.IO;
using Newtonsoft.Json;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Writes one-off NDJSON lines to a debug log file for agent debugging sessions.
    /// Path: SIMSTEWARD_DEBUG_LOG_PATH env, or PluginsData/SimSteward/debug-{session}.log.
    /// </summary>
    internal static class AgentDebugLog
    {
        // #region agent log — session 3895b0 (fast-forward incident capture)
        private const string SessionId = "3895b0";
        private const string LogFileName = "debug-3895b0.log";

        /// <summary>Session b0c27e: write to debug-b0c27e.log for incident-capture debugging.</summary>
        private const string SessionIdB0C27E = "b0c27e";
        private const string LogFileNameB0C27E = "debug-b0c27e.log";

        private static string GetLogPathB0C27E()
        {
            var env = Environment.GetEnvironmentVariable("SIMSTEWARD_DEBUG_LOG_PATH");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            var workspace = Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? "", "dev", "sim-steward", "plugin");
            var workspaceLog = Path.Combine(workspace, LogFileNameB0C27E);
            if (Directory.Exists(Path.GetDirectoryName(workspaceLog)))
                return workspaceLog;
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SimHubWpf", "PluginsData", "SimSteward");
            return Path.Combine(dir, LogFileNameB0C27E);
        }

        public static void WriteB0C27E(string hypothesisId, string location, string message, object data = null)
        {
            try
            {
                var payload = new
                {
                    sessionId = SessionIdB0C27E,
                    hypothesisId,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    location,
                    message,
                    data
                };
                var path = GetLogPathB0C27E();
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(path, JsonConvert.SerializeObject(payload) + "\n", System.Text.Encoding.UTF8);
            }
            catch { }
        }

        private static string GetLogPath()
        {
            var env = Environment.GetEnvironmentVariable("SIMSTEWARD_DEBUG_LOG_PATH");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            var workspace = Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? "", "dev", "sim-steward", "plugin");
            var workspaceLog = Path.Combine(workspace, LogFileName);
            if (Directory.Exists(Path.GetDirectoryName(workspaceLog)))
                return workspaceLog;
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SimHubWpf", "PluginsData", "SimSteward");
            return Path.Combine(dir, LogFileName);
        }
        // #endregion

        public static void Write(string hypothesisId, string message, object data = null)
        {
            try
            {
                var payload = new
                {
                    sessionId = SessionId,
                    hypothesisId,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    location = "AgentDebugLog",
                    message,
                    data
                };
                var path = GetLogPath();
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(path, JsonConvert.SerializeObject(payload) + "\n", System.Text.Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>Write NDJSON for debug session. Uses SessionId/LogFileName (3895b0).</summary>
        public static void Write740824(string hypothesisId, string location, string message, object data = null)
        {
            Write(SessionId, hypothesisId, location, message, data);
        }

        private static void Write(string sessionId, string hypothesisId, string location, string message, object data)
        {
            try
            {
                var payload = new
                {
                    sessionId,
                    hypothesisId,
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    location,
                    message,
                    data
                };
                var path = GetLogPath();
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(path, JsonConvert.SerializeObject(payload) + "\n", System.Text.Encoding.UTF8);
            }
            catch { }
        }
    }
}
