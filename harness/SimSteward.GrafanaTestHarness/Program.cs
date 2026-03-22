using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using SimSteward.Plugin;

namespace SimSteward.GrafanaTestHarness
{
    /// <summary>
    /// Emits representative structured log events (NDJSON) for the Grafana observability test harness.
    /// Writes to plugin-structured.jsonl (same format as plugin) for Loki ingestion outside the plugin.
    /// Env: SIMSTEWARD_DATA_PATH or SIMSTEWARD_STRUCTURED_LOG_PATH (output dir or full path); TEST_TAG (default grafana-harness).
    /// Args: --count N (number of action_result events to emit per type; default 3).
    /// </summary>
    internal static class Program
    {
        private const string DefaultTestTag = "grafana-harness";

        static int Main(string[] args)
        {
            int count = 3;
            for (int i = 0; i < args.Length; i++)
            {
                if ((args[i] == "--count" || args[i] == "-c") && i + 1 < args.Length && int.TryParse(args[i + 1], out var n) && n > 0)
                {
                    count = Math.Min(n, 100);
                    i++;
                }
            }

            var testTag = Environment.GetEnvironmentVariable("TEST_TAG") ?? DefaultTestTag;
            var dataPath = Environment.GetEnvironmentVariable("SIMSTEWARD_DATA_PATH");
            var structuredPath = Environment.GetEnvironmentVariable("SIMSTEWARD_STRUCTURED_LOG_PATH");
            if (string.IsNullOrWhiteSpace(structuredPath))
                structuredPath = string.IsNullOrWhiteSpace(dataPath) ? Path.Combine(".", "sample-logs", "plugin-structured.jsonl") : Path.Combine(dataPath.Trim(), "plugin-structured.jsonl");
            var dir = Path.GetDirectoryName(structuredPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var entries = new List<LogEntry>();

            // action_result (success = true and false)
            for (int i = 0; i < count; i++)
            {
                var cid = Guid.NewGuid().ToString("N").Substring(0, 8);
                var okFields = new Dictionary<string, object>
                {
                    ["action"] = "approve_penalty",
                    ["correlation_id"] = cid,
                    ["success"] = "true",
                    ["duration_ms"] = 10 + i,
                    ["result"] = "ok"
                };
                AddHarnessSessionAndRouting(okFields);
                entries.Add(MakeEntry("simhub-plugin", "INFO", "action_result", $"test action ok {i}", okFields, testTag));
            }
            for (int i = 0; i < count; i++)
            {
                var cid = Guid.NewGuid().ToString("N").Substring(0, 8);
                var failFields = new Dictionary<string, object>
                {
                    ["action"] = "ReplaySeekFrame",
                    ["correlation_id"] = cid,
                    ["success"] = "false",
                    ["duration_ms"] = 5,
                    ["error"] = "test harness simulated failure"
                };
                AddHarnessSessionAndRouting(failFields);
                entries.Add(MakeEntry("simhub-plugin", "INFO", "action_result", $"test action fail {i}", failFields, testTag));
            }

            // incident_detected
            var inc1 = new Dictionary<string, object>
            {
                ["incident_type"] = "4x",
                ["car_number"] = "42",
                ["driver_name"] = "Test Driver",
                ["unique_user_id"] = 123456,
                ["delta"] = 4,
                ["session_time"] = 123.45,
                ["session_num"] = 0,
                ["replay_frame"] = 1000,
                ["replay_frame_end"] = 1020,
                ["start_frame"] = 1000,
                ["end_frame"] = 1020,
                ["cause"] = "heavy_contact",
                ["lap"] = 2,
                ["cam_car_idx"] = 5,
                ["camera_group"] = "Chase",
                ["camera_view"] = "cam_car_idx=5;group=Chase"
            };
            AddHarnessSessionAndRouting(inc1);
            entries.Add(MakeEntry("tracker", "INFO", "incident_detected", "Incident detected: 4x #42 Test Driver", inc1, testTag, "iracing"));
            var inc2 = new Dictionary<string, object>
            {
                ["incident_type"] = "1x",
                ["car_number"] = "7",
                ["driver_name"] = "Other",
                ["unique_user_id"] = 999888,
                ["delta"] = 1,
                ["session_time"] = 200.0,
                ["session_num"] = 0,
                ["replay_frame"] = 2000,
                ["replay_frame_end"] = 2005,
                ["start_frame"] = 2000,
                ["end_frame"] = 2005,
                ["cause"] = "off_track",
                ["lap"] = 3,
                ["cam_car_idx"] = 2,
                ["camera_group"] = "Helicopter",
                ["camera_view"] = "cam_car_idx=2;group=Helicopter"
            };
            AddHarnessSessionAndRouting(inc2);
            entries.Add(MakeEntry("tracker", "INFO", "incident_detected", "Incident detected: 1x #7 Other", inc2, testTag, "iracing"));

            // session_digest
            var sessionId = "test-session-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            entries.Add(MakeEntry("simhub-plugin", "INFO", "session_digest", "Session digest",
                new Dictionary<string, object>
                {
                    ["session_id"] = sessionId,
                    ["session_num"] = 0,
                    ["track"] = "Test Track",
                    ["duration_minutes"] = 5,
                    ["total_incidents"] = 2,
                    ["incident_summary"] = "42: 4x; 7: 1x",
                    ["actions_dispatched"] = 6,
                    ["action_failures"] = 2,
                    ["p50_action_latency_ms"] = 12,
                    ["p95_action_latency_ms"] = 25,
                    ["ws_peak_clients"] = 1,
                    ["plugin_errors"] = 0,
                    ["plugin_warns"] = 0
                }, testTag));

            foreach (var e in entries)
            {
                var line = JsonConvert.SerializeObject(e) + "\n";
                File.AppendAllText(structuredPath, line, System.Text.Encoding.UTF8);
            }

            Console.WriteLine($"Emitted {entries.Count} test log entries to {structuredPath} (testing=true, test_tag={testTag}). Ingest this file to Loki per your setup.");
            return 0;
        }

        /// <summary>Sample iRacing-style IDs for Loki/Grafana harness rows (not real telemetry).</summary>
        private static void AddHarnessSessionAndRouting(Dictionary<string, object> fields)
        {
            fields["subsession_id"] = "42700101";
            fields["parent_session_id"] = "17001234";
            fields["track_display_name"] = "Test Track";
            fields["lap"] = SessionLogging.LapUnknown;
            SessionLogging.AppendRoutingAndDestination(fields);
        }

        private static LogEntry MakeEntry(string component, string level, string eventType, string message,
            Dictionary<string, object> fields, string testTag, string domain = null)
        {
            return new LogEntry
            {
                Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                Level = level,
                Component = component,
                Event = eventType,
                Message = message,
                Fields = fields,
                Domain = domain,
                Testing = "true",
                TestTag = testTag
            };
        }
    }
}
