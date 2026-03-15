using System;
using System.Collections.Generic;

namespace SimSteward.Plugin
{
    /// <summary>Per-session accumulator for session_digest: actions, latencies, incidents, errors. Reset on iracing_connected.</summary>
    public class SessionStats
    {
        private const int MaxLatencySamples = 1000;

        public int ActionsDispatched { get; private set; }
        public int ActionFailures { get; private set; }
        public int PluginErrors { get; private set; }
        public int PluginWarns { get; private set; }
        public int WsPeakClients { get; private set; }
        private readonly List<double> _actionLatenciesMs = new List<double>();
        private readonly List<string> _failedActions = new List<string>();
        private readonly List<IncidentSummaryEntry> _incidents = new List<IncidentSummaryEntry>();

        public void Record(string action, bool success, long durationMs)
        {
            ActionsDispatched++;
            if (!success)
            {
                ActionFailures++;
                if (!string.IsNullOrEmpty(action) && !_failedActions.Contains(action))
                    _failedActions.Add(action);
            }
            _actionLatenciesMs.Add(durationMs);
            if (_actionLatenciesMs.Count > MaxLatencySamples)
                _actionLatenciesMs.RemoveAt(0);
        }

        public void SetWsPeakClients(int count)
        {
            if (count > WsPeakClients)
                WsPeakClients = count;
        }

        public void IncrementErrors() => PluginErrors++;
        public void IncrementWarns() => PluginWarns++;

        public void AddIncident(IncidentSummaryEntry entry)
        {
            if (entry != null)
                _incidents.Add(entry);
        }

        public IReadOnlyList<double> ActionLatenciesMs => _actionLatenciesMs;
        public IReadOnlyList<string> FailedActions => _failedActions;
        public IReadOnlyList<IncidentSummaryEntry> Incidents => _incidents;

        public double P50LatencyMs => Percentile(_actionLatenciesMs, 0.5);
        public double P95LatencyMs => Percentile(_actionLatenciesMs, 0.95);

        private static double Percentile(List<double> sorted, double p)
        {
            if (sorted == null || sorted.Count == 0) return 0;
            var copy = new List<double>(sorted);
            copy.Sort();
            int idx = (int)Math.Round((copy.Count - 1) * p);
            idx = Math.Max(0, Math.Min(idx, copy.Count - 1));
            return copy[idx];
        }

        public void Reset()
        {
            ActionsDispatched = 0;
            ActionFailures = 0;
            PluginErrors = 0;
            PluginWarns = 0;
            WsPeakClients = 0;
            _actionLatenciesMs.Clear();
            _failedActions.Clear();
            _incidents.Clear();
        }
    }

    public class IncidentSummaryEntry
    {
        public string Type { get; set; }
        public string Driver { get; set; }
        public string Car { get; set; }
        public int Lap { get; set; }
        public double SessionTime { get; set; }
    }
}
