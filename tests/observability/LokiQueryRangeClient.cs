using System;
using System.Collections.Generic;
using System.Net.Http;
using Newtonsoft.Json.Linq;

namespace SimSteward.Observability
{
    /// <summary>
    /// Minimal Loki <c>query_range</c> client for harness / integration tests (shared by AssertLokiQueries and SimSteward.Plugin.Tests).
    /// </summary>
    public static class LokiQueryRangeClient
    {
        /// <summary>Queries Loki for log lines in the lookback window; each line is the raw JSON log payload.</summary>
        public static bool TryQueryRange(
            string baseUrl,
            string logql,
            HttpClient client,
            TimeSpan lookback,
            out List<string> logLines,
            out string error)
        {
            logLines = new List<string>();
            error = null;
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                error = "baseUrl is empty.";
                return false;
            }

            baseUrl = baseUrl.TrimEnd('/');
            var endNs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000;
            var startNs = (DateTimeOffset.UtcNow - lookback).ToUnixTimeMilliseconds() * 1_000_000L;

            var url = $"{baseUrl}/loki/api/v1/query_range?query={Uri.EscapeDataString(logql)}&start={startNs}&end={endNs}&limit=500";
            HttpResponseMessage response;
            try
            {
                response = client.GetAsync(url).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                error = $"Loki query_range returned {response.StatusCode}.";
                return false;
            }

            var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var root = JObject.Parse(json);
            if (root["status"]?.ToString() != "success")
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
