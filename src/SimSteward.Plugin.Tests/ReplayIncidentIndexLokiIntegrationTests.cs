using System;
using System.Net.Http;
using Newtonsoft.Json.Linq;
using SimSteward.Observability;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    /// <summary>
    /// Queries Loki for <c>replay_incident_index_detection</c> after the Grafana harness has run and logs were ingested.
    /// Set <c>RUN_REPLAY_INDEX_LOKI_ASSERT=1</c> and <c>LOKI_QUERY_URL</c> (or <c>SIMSTEWARD_LOKI_URL</c>) to enable; otherwise skipped.
    /// See docs/observability-testing.md.
    /// </summary>
    public class ReplayIncidentIndexLokiIntegrationTests
    {
        [SkippableFact]
        public void Loki_ContainsReplayIncidentIndexDetection_WhenHarnessIngested()
        {
            Skip.IfNot(string.Equals(Environment.GetEnvironmentVariable("RUN_REPLAY_INDEX_LOKI_ASSERT"), "1", StringComparison.Ordinal));

            var baseUrl = Environment.GetEnvironmentVariable("LOKI_QUERY_URL")
                ?? Environment.GetEnvironmentVariable("SIMSTEWARD_LOKI_URL");
            if (string.IsNullOrWhiteSpace(baseUrl))
                Assert.Fail("RUN_REPLAY_INDEX_LOKI_ASSERT=1 requires LOKI_QUERY_URL or SIMSTEWARD_LOKI_URL.");

            var testTag = Environment.GetEnvironmentVariable("TEST_TAG") ?? "grafana-harness";
            var logql =
                "{app=\"sim-steward\", component=\"simhub-plugin\"} | json | event = \"replay_incident_index_detection\" | testing = \"true\" | test_tag = \"" +
                testTag +
                "\"";

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            Assert.True(
                LokiQueryRangeClient.TryQueryRange(baseUrl.TrimEnd('/'), logql, client, TimeSpan.FromMinutes(10), out var lines, out var err),
                err ?? "Loki query_range failed.");

            Assert.True(
                lines.Count >= 1,
                $"Expected at least one replay_incident_index_detection line; got {lines.Count}. Run harness/SimSteward.GrafanaTestHarness, ingest to Loki, then re-run with RUN_REPLAY_INDEX_LOKI_ASSERT=1.");

            var ok = false;
            foreach (var line in lines)
            {
                try
                {
                    var j = JObject.Parse(line);
                    if (j["event"]?.ToString() != "replay_incident_index_detection")
                        continue;
                    var fp = (j["fields"] as JObject)?["fingerprint"]?.ToString();
                    if (!string.IsNullOrEmpty(fp) && fp.Length == 64)
                    {
                        ok = true;
                        break;
                    }
                }
                catch
                {
                    // ignore malformed
                }
            }

            Assert.True(ok, "No replay_incident_index_detection line had fields.fingerprint (64-char hex).");
        }
    }
}
