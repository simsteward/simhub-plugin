using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using Newtonsoft.Json.Linq;
using SimSteward.Observability;

namespace AssertLokiQueries
{
    /// <summary>
    /// Queries Loki HTTP API with LogQL and asserts test harness data is present.
    /// Env: LOKI_QUERY_URL (default http://localhost:3100), TEST_TAG (default grafana-harness).
    /// Retries up to 30s with 3s backoff.
    /// </summary>
    internal static class Program
    {
        private const string DefaultLokiUrl = "http://localhost:3100";
        private const string DefaultTestTag = "grafana-harness";
        private const int MaxWaitSeconds = 30;
        private const int RetryIntervalSeconds = 3;

        static int Main(string[] args)
        {
            var baseUrl = (Environment.GetEnvironmentVariable("LOKI_QUERY_URL") ?? DefaultLokiUrl).TrimEnd('/');
            var testTag = Environment.GetEnvironmentVariable("TEST_TAG") ?? DefaultTestTag;

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

            var deadline = DateTime.UtcNow.AddSeconds(MaxWaitSeconds);
            string lastError = null;

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (RunAssertions(baseUrl, testTag, client, out lastError))
                    {
                        Console.WriteLine("PASS: All Loki assertions passed.");
                        return 0;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }

                if (DateTime.UtcNow.AddSeconds(RetryIntervalSeconds) < deadline)
                {
                    Console.WriteLine($"Retrying in {RetryIntervalSeconds}s: {lastError}");
                    Thread.Sleep(RetryIntervalSeconds * 1000);
                }
            }

            Console.Error.WriteLine("FAIL: " + (lastError ?? "Timeout waiting for expected log lines."));
            return 1;
        }

        private static bool RunAssertions(string baseUrl, string testTag, HttpClient client, out string error)
        {
            error = null;

            // Query: all test-tagged lines in last 10 minutes
            var query = $"{{app=\"sim-steward\"}} | json | testing = \"true\" | test_tag = \"{testTag}\"";
            if (!LokiQueryRangeClient.TryQueryRange(baseUrl, query, client, TimeSpan.FromMinutes(10), out var lines, out var queryError))
            {
                error = queryError;
                return false;
            }

            if (lines.Count == 0)
            {
                error = "No log lines found with testing=true and test_tag=" + testTag;
                return false;
            }

            int actionResult = 0, incidentDetected = 0, sessionDigest = 0, replayIncidentIndexDetection = 0;
            foreach (var line in lines)
            {
                try
                {
                    var j = JObject.Parse(line);
                    var ev = j["event"]?.ToString();
                    if (ev == "action_result") actionResult++;
                    if (ev == "incident_detected") incidentDetected++;
                    if (ev == "session_digest") sessionDigest++;
                    if (ev == "replay_incident_index_detection") replayIncidentIndexDetection++;
                }
                catch
                {
                    // skip non-JSON or malformed
                }
            }

            if (actionResult < 2)
            {
                error = $"Expected at least 2 action_result lines (success + fail); got {actionResult}.";
                return false;
            }
            if (incidentDetected < 1)
            {
                error = $"Expected at least 1 incident_detected line; got {incidentDetected}.";
                return false;
            }
            if (sessionDigest < 1)
            {
                error = $"Expected at least 1 session_digest line; got {sessionDigest}.";
                return false;
            }
            if (replayIncidentIndexDetection < 1)
            {
                error = $"Expected at least 1 replay_incident_index_detection line (M5 TR-028 harness); got {replayIncidentIndexDetection}.";
                return false;
            }

            // Sanity: at least one line has required action_result fields
            var hasActionResultFields = false;
            foreach (var line in lines)
            {
                try
                {
                    var j = JObject.Parse(line);
                    if (j["event"]?.ToString() != "action_result") continue;
                    var fields = j["fields"] as JObject;
                    if (fields == null) continue;
                    if (fields["correlation_id"] != null && fields["success"] != null && fields["action"] != null)
                    {
                        hasActionResultFields = true;
                        break;
                    }
                }
                catch { }
            }
            if (!hasActionResultFields)
            {
                error = "No action_result line had required fields (correlation_id, success, action).";
                return false;
            }

            // TR-028: fingerprint v1 hex on replay_incident_index_detection
            var hasValidFingerprint = false;
            foreach (var line in lines)
            {
                try
                {
                    var j = JObject.Parse(line);
                    if (j["event"]?.ToString() != "replay_incident_index_detection") continue;
                    var fields = j["fields"] as JObject;
                    var fp = fields?["fingerprint"]?.ToString();
                    if (!string.IsNullOrEmpty(fp) && fp.Length == 64)
                    {
                        hasValidFingerprint = true;
                        break;
                    }
                }
                catch { }
            }
            if (!hasValidFingerprint)
            {
                error = "No replay_incident_index_detection line had fields.fingerprint (64-char hex).";
                return false;
            }

            // LogQL smoke queries (typical panel queries, filtered to test data)
            var logqlSmokeChecks = new[]
            {
                ("action_result", $"{{app=\"sim-steward\", component=\"simhub-plugin\"}} | json | event = \"action_result\" | testing = \"true\" | test_tag = \"{testTag}\""),
                ("incident_detected", $"{{app=\"sim-steward\", component=\"tracker\"}} | json | event = \"incident_detected\" | testing = \"true\" | test_tag = \"{testTag}\""),
                ("session_digest", $"{{app=\"sim-steward\", component=\"simhub-plugin\"}} | json | event = \"session_digest\" | testing = \"true\" | test_tag = \"{testTag}\""),
                ("replay_incident_index_detection", $"{{app=\"sim-steward\", component=\"simhub-plugin\"}} | json | event = \"replay_incident_index_detection\" | testing = \"true\" | test_tag = \"{testTag}\"")
            };
            foreach (var (name, logql) in logqlSmokeChecks)
            {
                if (!LokiQueryRangeClient.TryQueryRange(baseUrl, logql, client, TimeSpan.FromMinutes(10), out var panelLines, out var panelErr))
                {
                    error = $"[{name}] {panelErr}";
                    return false;
                }
                if (panelLines.Count < 1)
                {
                    error = $"[{name}] Expected at least 1 row for LogQL smoke query; got {panelLines.Count}.";
                    return false;
                }
            }

            return true;
        }
    }
}
