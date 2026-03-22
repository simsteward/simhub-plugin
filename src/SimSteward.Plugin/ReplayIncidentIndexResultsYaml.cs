using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SimSteward.Plugin
{
    /// <summary>TR-023: extract per-car <c>Incidents</c> from <c>ResultsPositions</c> in raw session YAML.</summary>
    public static class ReplayIncidentIndexResultsYaml
    {
        private static readonly Regex SessionNumItem = new Regex(
            @"^\s*-\s*SessionNum:\s*(\d+)\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static int LeadingWhitespaceCount(string line)
        {
            int n = 0;
            foreach (char c in line)
            {
                if (c == ' ')
                    n++;
                else if (c == '\t')
                    n += 4;
                else
                    break;
            }
            return n;
        }

        /// <summary>
        /// Prefers <paramref name="preferredSessionNum"/>; if that session has no ResultsPositions,
        /// uses the last non-empty ResultsPositions block found in file order.
        /// </summary>
        public static bool TryParseOfficialIncidentsByCarIdx(
            string sessionInfoYaml,
            int preferredSessionNum,
            out Dictionary<int, int> byCarIdx,
            out int sessionNumUsed,
            out string error)
        {
            byCarIdx = new Dictionary<int, int>();
            sessionNumUsed = preferredSessionNum;
            error = null;

            if (string.IsNullOrWhiteSpace(sessionInfoYaml))
            {
                error = "empty_yaml";
                return false;
            }

            string[] lines = sessionInfoYaml.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            bool sawSessions = false;
            int? currentSession = null;
            Dictionary<int, int> preferredMap = null;
            Dictionary<int, int> lastMap = null;
            int lastSession = -1;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                string trimmedStart = line.TrimStart();
                if (trimmedStart.StartsWith("#", StringComparison.Ordinal))
                    continue;

                if (trimmedStart.StartsWith("Sessions:", StringComparison.Ordinal))
                {
                    sawSessions = true;
                    currentSession = null;
                    continue;
                }

                if (!sawSessions)
                    continue;

                Match sm = SessionNumItem.Match(line);
                if (sm.Success)
                {
                    currentSession = int.Parse(sm.Groups[1].Value, CultureInfo.InvariantCulture);
                    continue;
                }

                if (currentSession == null)
                    continue;

                if (trimmedStart.StartsWith("ResultsPositions:", StringComparison.Ordinal))
                {
                    int rpIndent = LeadingWhitespaceCount(line);
                    Dictionary<int, int> block = ParseResultsPositionsBlock(lines, i + 1, rpIndent);
                    if (block != null && block.Count > 0)
                    {
                        lastMap = block;
                        lastSession = currentSession.Value;
                        if (currentSession.Value == preferredSessionNum)
                            preferredMap = new Dictionary<int, int>(block);
                    }

                    i = SkipPastResultsPositions(lines, i + 1, rpIndent) - 1;
                }
            }

            if (preferredMap != null && preferredMap.Count > 0)
            {
                byCarIdx = preferredMap;
                sessionNumUsed = preferredSessionNum;
                return true;
            }

            if (lastMap != null && lastMap.Count > 0)
            {
                byCarIdx = lastMap;
                sessionNumUsed = lastSession;
                return true;
            }

            error = "no_results_positions";
            return false;
        }

        private static int SkipPastResultsPositions(string[] lines, int start, int resultsPositionsDeclIndent)
        {
            int i = start;
            for (; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                string trimmedStart = line.TrimStart();
                if (trimmedStart.StartsWith("#", StringComparison.Ordinal))
                    continue;

                int li = LeadingWhitespaceCount(line);
                if (li < resultsPositionsDeclIndent)
                    return i;

                if (li == resultsPositionsDeclIndent)
                {
                    if (trimmedStart.StartsWith("-", StringComparison.Ordinal) && SessionNumItem.IsMatch(line))
                        return i;
                    if (trimmedStart.EndsWith(":", StringComparison.Ordinal)
                        && !trimmedStart.StartsWith("-", StringComparison.Ordinal))
                        return i;
                }
            }

            return lines.Length;
        }

        private static Dictionary<int, int> ParseResultsPositionsBlock(string[] lines, int start, int resultsPositionsDeclIndent)
        {
            var map = new Dictionary<int, int>();
            int? carIdx = null;
            int? incidents = null;

            void TryFlushEntry()
            {
                if (carIdx.HasValue && incidents.HasValue)
                {
                    map[carIdx.Value] = incidents.Value;
                    carIdx = null;
                    incidents = null;
                }
            }

            for (int i = start; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                string trimmedStart = line.TrimStart();
                if (trimmedStart.StartsWith("#", StringComparison.Ordinal))
                    continue;

                int li = LeadingWhitespaceCount(line);

                if (li < resultsPositionsDeclIndent)
                    break;

                if (li == resultsPositionsDeclIndent)
                {
                    if (trimmedStart.StartsWith("-", StringComparison.Ordinal) && SessionNumItem.IsMatch(line))
                        break;
                    if (trimmedStart.EndsWith(":", StringComparison.Ordinal)
                        && !trimmedStart.StartsWith("-", StringComparison.Ordinal))
                        break;
                }

                if (trimmedStart.StartsWith("-", StringComparison.Ordinal))
                {
                    TryFlushEntry();
                    carIdx = null;
                    incidents = null;
                    continue;
                }

                if (trimmedStart.StartsWith("CarIdx:", StringComparison.Ordinal))
                {
                    string v = trimmedStart.Substring("CarIdx:".Length).Trim();
                    if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int c))
                        carIdx = c;
                    TryFlushEntry();
                    continue;
                }

                if (trimmedStart.StartsWith("Incidents:", StringComparison.Ordinal))
                {
                    string v = trimmedStart.Substring("Incidents:".Length).Trim();
                    if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                        incidents = n;
                    TryFlushEntry();
                }
            }

            TryFlushEntry();
            return map;
        }
    }
}
