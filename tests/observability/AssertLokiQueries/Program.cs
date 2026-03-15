using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

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
            if (!QueryLoki(baseUrl, query, client, out var lines, out var queryError))
            {
                error = queryError;
                return false;
            }

            if (lines.Count == 0)
            {
                error = "No log lines found with testing=true and test_tag=" + testTag;
                return false;
            }

            int actionResult = 0, incidentDetected = 0, sessionDigest = 0;
            foreach (var line in lines)
            {
                try
                {
                    var j = JObject.Parse(line);
                    var ev = j["event"]?.ToString();
                    if (ev == "action_result") actionResult++;
                    if (ev == "incident_detected") incidentDetected++;
                    if (ev == "session_digest") sessionDigest++;
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

            // Dashboard panel queries (same LogQL as provisioned dashboards, filtered to test data)
            var dashboardChecks = new[]
            {
                ("command-audit", $"{{app=\"sim-steward\", component=\"simhub-plugin\"}} | json | event = \"action_result\" | testing = \"true\" | test_tag = \"{testTag}\""),
                ("incident-timeline", $"{{app=\"sim-steward\", component=\"tracker\"}} | json | event = \"incident_detected\" | testing = \"true\" | test_tag = \"{testTag}\""),
                ("session-overview", $"{{app=\"sim-steward\", component=\"simhub-plugin\"}} | json | event = \"session_digest\" | testing = \"true\" | test_tag = \"{testTag}\"")
            };
            foreach (var (name, logql) in dashboardChecks)
            {
                if (!QueryLoki(baseUrl, logql, client, out var panelLines, out var panelErr))
                {
                    error = $"[{name}] {panelErr}";
                    return false;
                }
                if (panelLines.Count < 1)
                {
                    error = $"[{name}] Expected at least 1 row for dashboard query; got {panelLines.Count}.";
                    return false;
                }
            }

            return true;
        }

        private static bool QueryLoki(string baseUrl, string logql, HttpClient client, out List<string> logLines, out string error)
        {
            logLines = new List<string>();
            error = null;

            var endNs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
            var startNs = (DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeMilliseconds()) * 1_000_000L;

            var url = $"{baseUrl}/loki/api/v1/query_range?query={Uri.EscapeDataString(logql)}&start={startNs}&end={endNs}&limit=500";
            var response = client.GetAsync(url).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                error = $"Loki query_range returned {response.StatusCode}.";
                return false;
            }

            var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var root = JObject.Parse(json);
            var status = root["status"]?.ToString();
            if (status != "success")
            {
                error = "Loki returned status != success.";
                return false;
            }

            var result = root["data"]?["result"];
            if (result == null || !(result is JArray arr))
            {
                error = "Loki data.result missing or not array.";
                return false;
            }

            foreach (var item in arr)
            {
                var values = item["values"] as JArray;
                if (values == null) continue;
                foreach (var v in values)
                {
                    if (v is JArray pair && pair.Count >= 2)
                    {
                        var line = pair[1]?.ToString();
                        if (!string.IsNullOrEmpty(line))
                            logLines.Add(line);
                    }
                }
            }

            return true;
        }
    }
}
