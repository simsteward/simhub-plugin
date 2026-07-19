using System;
using System.Collections.Generic;
using System.Globalization;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Maps a point on track (lap-distance fraction + track length) to a human-readable location —
    /// a named corner ("Hell Corner") when data exists, otherwise a generic sector fallback.
    /// </summary>
    /// <remarks>
    /// The named-corner data is a subset of CrewChiefV4's <c>trackLandmarksData.json</c>
    /// (github.com/mrbelowski/CrewChiefV4, MIT licensed), limited to entries carrying an
    /// <c>irTrackName</c> key. iRacing does not expose named corners via the SDK — only
    /// <c>CarIdxLapDistPct</c> (0.0-1.0) — so this table is the same mechanism CrewChief itself
    /// uses: match <c>WeekendInfo.TrackName</c>, convert the fraction to meters via track length,
    /// then find the bracketing landmark. Coverage is intentionally partial (~18 iRacing
    /// track/layout combos as of the source data) — every other track falls back to a generic
    /// Sector 1/2/3 division, which CrewChief itself does not provide (this project's own addition).
    /// Track names match iRacing's own internal <c>WeekendInfo.TrackName</c> convention
    /// (lowercase, space-separated, e.g. "winton national") — confirmed against a live YAML dump
    /// this session, not assumed.
    /// </remarks>
    public static class TrackLandmarks
    {
        public readonly struct Landmark
        {
            public Landmark(string name, float startM, float endM)
            {
                Name = name;
                StartM = startM;
                EndM = endM;
            }

            public string Name { get; }
            public float StartM { get; }
            public float EndM { get; }
        }

        /// <summary>CrewChief's own tolerance for near-miss matches at landmark boundaries.</summary>
        public const float BoundaryToleranceM = 70f;

        private static readonly Dictionary<string, Landmark[]> ByTrack = new Dictionary<string, Landmark[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["bathurst"] = new[]
            {
                new Landmark("Hell Corner", 210, 310),
                new Landmark("Griffins Bend", 1300, 1490),
                new Landmark("The Cutting", 1710, 1910),
                new Landmark("Quarry", 2110, 2230),
                new Landmark("McPhillamy Park", 2750, 2950),
                new Landmark("Skyline", 3100, 3160),
                new Landmark("The Esses", 3230, 3310),
                new Landmark("The Dipper", 3360, 3450),
                new Landmark("Forrests Elbow", 3690, 3800),
                new Landmark("The Chase", 5030, 5510),
                new Landmark("Murrays Corner", 5880, 5970)
            },
            ["spa up"] = new[]
            {
                new Landmark("La Source", 360, 430),
                new Landmark("Eau Rouge", 1000, 1230),
                new Landmark("Radillion", 1255, 1370),
                new Landmark("Kemmel", 1580, 1710),
                new Landmark("Les Combes", 2370, 2530),
                new Landmark("Turn 9", 2600, 2700),
                new Landmark("Bruxelles", 2940, 3150),
                new Landmark("Turn 11", 3230, 3367),
                new Landmark("Pouhon", 3700, 4140),
                new Landmark("Turn 13", 4425, 4560),
                new Landmark("Campus", 4588, 4730),
                new Landmark("Stavelot", 4850, 5240),
                new Landmark("Blanchimont", 6070, 6230),
                new Landmark("The Chicane", 6670, 6800)
            },
            ["imola gp"] = new[]
            {
                new Landmark("Tamburello", 660, 910),
                new Landmark("Villeneuve", 1290, 1430),
                new Landmark("Tosa", 1640, 1760),
                new Landmark("Piratella", 2250, 2380),
                new Landmark("Acque Minerali", 2690, 2870),
                new Landmark("Variante Alta", 3300, 3380),
                new Landmark("Rivazza 1", 4070, 4120),
                new Landmark("Rivazza 2", 4190, 4270)
            },
            ["zandvoort grandprix"] = new[]
            {
                new Landmark("Tarzanbocht", 370, 510),
                new Landmark("Turn 2", 720, 780),
                new Landmark("Hugenholtz", 860, 970),
                new Landmark("Scheivlak", 1660, 1970),
                new Landmark("Turn 8", 2040, 2150),
                new Landmark("Turn 9", 2280, 2390),
                new Landmark("Turn 10", 2520, 2650),
                new Landmark("The Chicane", 3100, 3250),
                new Landmark("Turn 12", 3480, 3590),
                new Landmark("Luyendijk", 3720, 3940)
            },
            ["lagunaseca"] = new[]
            {
                new Landmark("Turn 1", 160, 260),
                new Landmark("The Andretti Hairpin", 410, 560),
                new Landmark("Turn 3", 730, 850),
                new Landmark("Turn 4", 1000, 1130),
                new Landmark("Turn 5", 1480, 1640),
                new Landmark("Turn 6", 1910, 2000),
                new Landmark("The Corkscrew", 2420, 2530),
                new Landmark("Rainey Curve", 2640, 2820),
                new Landmark("Turn 10", 2930, 3030),
                new Landmark("Turn 11", 3230, 3310)
            },
            ["monza full"] = new[]
            {
                new Landmark("The First Chicane", 590, 690),
                new Landmark("Curva Grande", 1010, 1440),
                new Landmark("Della Roggia", 1800, 1910),
                new Landmark("Lesmo 1", 2160, 2330),
                new Landmark("Lesmo 2", 2520, 2610),
                new Landmark("Ascari", 3600, 3850),
                new Landmark("Parabolica", 4780, 5170)
            },
            ["zolder gp"] = new[]
            {
                new Landmark("Turn 1", 250, 410),
                new Landmark("Turn 2", 560, 900),
                new Landmark("Turn 3", 1050, 1200),
                new Landmark("The First Chicane", 1720, 1800),
                new Landmark("The Second Chicane", 2280, 2350),
                new Landmark("Terlamenbocht", 2380, 2640),
                new Landmark("The Hairpin", 3000, 3070),
                new Landmark("Ickxbocht", 3580, 3700)
            },
            ["roadamerica full"] = new[]
            {
                new Landmark("Turn 1", 568, 758),
                new Landmark("Turn 3", 1055, 1224),
                new Landmark("Turn 5", 2257, 2379),
                new Landmark("Turn 6", 2540, 2686),
                new Landmark("Turn 7", 2774, 2939),
                new Landmark("Turn 8", 3199, 3343),
                new Landmark("The Carousel", 3327, 3930),
                new Landmark("The Kink", 4163, 4411),
                new Landmark("Canada Corner", 5044, 5189),
                new Landmark("Bill Mitchell Bend", 5367, 5563),
                new Landmark("Turn 14", 5676, 5862)
            },
            ["oulton fosters"] = new[]
            {
                new Landmark("Old Hall", 130, 310),
                new Landmark("Dentons", 480, 575),
                new Landmark("Cascades", 615, 745),
                new Landmark("Fosters", 800, 860),
                new Landmark("Knickerbrook", 1020, 1150),
                new Landmark("Druids", 1650, 1870),
                new Landmark("Lodge", 2200, 2330),
                new Landmark("Deer Leap", 2400, 2525)
            },
            ["oulton international"] = new[]
            {
                new Landmark("Old Hall", 130, 310),
                new Landmark("Dentons", 480, 575),
                new Landmark("Cascades", 615, 845),
                new Landmark("Lake Side", 1240, 1490),
                new Landmark("Shell Oils Corner", 1525, 1720),
                new Landmark("Brittens", 1920, 2120),
                new Landmark("Knickerbrook", 2675, 2790),
                new Landmark("Druids", 3285, 3505),
                new Landmark("Lodge", 3870, 3985),
                new Landmark("Deer Leap", 4075, 4120)
            },
            ["oulton inthislop"] = new[]
            {
                new Landmark("Old Hall", 130, 310),
                new Landmark("Dentons", 480, 575),
                new Landmark("Cascades", 615, 845),
                new Landmark("Lake Side", 1240, 1490),
                new Landmark("Shell Oils Corner", 1525, 1720),
                new Landmark("Hislops", 2473, 2630),
                new Landmark("Knickerbrook", 2631, 2770),
                new Landmark("Druids", 3260, 3480),
                new Landmark("Lodge", 3845, 3960),
                new Landmark("Deer Leap", 4050, 4095)
            },
            ["oulton islandhistoric"] = new[]
            {
                new Landmark("Old Hall", 130, 310),
                new Landmark("Dentons", 480, 575),
                new Landmark("Cascades", 615, 845),
                new Landmark("Island Hairpin", 1209, 1320),
                new Landmark("Hislops", 1765, 1900),
                new Landmark("Knickerbrook", 1925, 2040),
                new Landmark("Druids", 2535, 2755),
                new Landmark("Lodge", 3120, 3235),
                new Landmark("Deer Leap", 3325, 3370)
            },
            ["limerock full"] = new[]
            {
                new Landmark("Big Bend", 190, 500),
                new Landmark("Lefthander", 550, 725),
                new Landmark("Righthander", 750, 870),
                new Landmark("Uphill", 1145, 1360),
                new Landmark("West Bend", 1510, 1700),
                new Landmark("Downhill", 1830, 2050)
            },
            ["limerock chicane"] = new[]
            {
                new Landmark("Big Bend", 190, 500),
                new Landmark("Lefthander", 550, 725),
                new Landmark("Righthander", 750, 870),
                new Landmark("Uphill Chicane", 1145, 1360),
                new Landmark("West Bend", 1480, 1670),
                new Landmark("Downhill", 1800, 2020)
            },
            ["sebring international"] = new[]
            {
                new Landmark("Turn 1", 320, 660),
                new Landmark("Kristensen", 855, 980),
                new Landmark("Turn 5", 1030, 1180),
                new Landmark("The Hairpin", 1925, 2050),
                new Landmark("Fangio Chicane", 2170, 2530),
                new Landmark("Cunningham", 2700, 2825),
                new Landmark("Collier", 2840, 3005),
                new Landmark("Tower", 3185, 3340),
                new Landmark("Bishop", 3615, 3880),
                new Landmark("Gendebien", 3990, 4100),
                new Landmark("Le Mans", 4150, 4385),
                new Landmark("Sunset", 5180, 5600)
            },
            ["sebring  club course"] = new[]
            {
                new Landmark("Cunningham", 290, 425),
                new Landmark("Collier", 440, 605),
                new Landmark("Tower", 800, 950),
                new Landmark("Rabbit", 1075, 1171),
                new Landmark("Turn 5", 1250, 1335),
                new Landmark("Gurney", 1350, 1460),
                new Landmark("The Hairpin", 1480, 1940)
            },
            ["montreal"] = new[]
            {
                new Landmark("Turn 1", 190, 280),
                new Landmark("Senna", 290, 420),
                new Landmark("Turn 3", 665, 750),
                new Landmark("Turn 4", 755, 820),
                new Landmark("Turn 5", 940, 1110),
                new Landmark("Turn 6", 1190, 1280),
                new Landmark("Turn 7", 1281, 1420),
                new Landmark("Turn 9", 2021, 2120),
                new Landmark("The Hairpin", 2600, 2765),
                new Landmark("Wall of Champions", 3840, 3960)
            }
        };

        /// <summary>
        /// Removes standalone 4-digit year tokens from an iRacing track name so laser-rescan variants
        /// match the base entry — e.g. "spa 2024 up" → "spa up". iRacing appends the rescan year into
        /// <c>WeekendInfo.TrackName</c> ("spa 2024 up" confirmed live at Spa on 2026-07-19), but
        /// CrewChief's landmark data keys predate the rescans; without this, every rescanned track
        /// silently falls back to sectors despite having full corner data.
        /// </summary>
        public static string StripYearTokens(string trackName)
        {
            if (string.IsNullOrWhiteSpace(trackName)) return "";
            var parts = trackName.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var kept = new List<string>(parts.Length);
            foreach (var p in parts)
            {
                bool isYear = p.Length == 4
                    && (p.StartsWith("19", StringComparison.Ordinal) || p.StartsWith("20", StringComparison.Ordinal))
                    && int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _);
                if (!isYear) kept.Add(p);
            }
            return string.Join(" ", kept);
        }

        /// <summary>
        /// Named-corner lookup only (no sector fallback). Returns null when the track isn't in the
        /// table, the distance is out of range, or <paramref name="trackLengthMeters"/>/<paramref name="lapDistPct"/> are invalid.
        /// Tries the exact name first, then a year-stripped variant (see <see cref="StripYearTokens"/>).
        /// </summary>
        public static string LookupLandmark(string weekendInfoTrackName, float lapDistPct, float trackLengthMeters)
        {
            if (string.IsNullOrWhiteSpace(weekendInfoTrackName) || trackLengthMeters <= 0)
                return null;
            if (!ByTrack.TryGetValue(weekendInfoTrackName.Trim(), out var landmarks))
            {
                string stripped = StripYearTokens(weekendInfoTrackName);
                if (stripped.Length == 0 || !ByTrack.TryGetValue(stripped, out landmarks))
                    return null;
            }

            float distanceM = lapDistPct * trackLengthMeters;

            foreach (var lm in landmarks)
                if (distanceM >= lm.StartM && distanceM <= lm.EndM)
                    return lm.Name;

            // CrewChief's own boundary-tolerance retry for near-misses.
            foreach (var lm in landmarks)
                if (distanceM >= lm.StartM - BoundaryToleranceM && distanceM <= lm.EndM + BoundaryToleranceM)
                    return lm.Name;

            return null;
        }

        /// <summary>Generic Sector 1/2/3 division by lap-distance thirds — this project's own fallback, not part of CrewChief's data.</summary>
        public static string SectorFallback(float lapDistPct)
        {
            if (lapDistPct < 0f || lapDistPct > 1f) return null;
            if (lapDistPct < (1f / 3f)) return "Sector 1";
            if (lapDistPct < (2f / 3f)) return "Sector 2";
            return "Sector 3";
        }

        /// <summary>Result of <see cref="Resolve"/> — the label plus which mechanism produced it, so callers never present a sector guess as a real corner name.</summary>
        public readonly struct Result
        {
            public Result(string label, string source)
            {
                Label = label;
                Source = source;
            }

            /// <summary>Human-readable location, or null if unresolvable (invalid lapDistPct).</summary>
            public string Label { get; }

            /// <summary>"landmark" (real named corner), "sector_fallback" (generic thirds), or "unknown".</summary>
            public string Source { get; }
        }

        public static Result Resolve(string weekendInfoTrackName, float lapDistPct, float trackLengthMeters)
        {
            string landmark = LookupLandmark(weekendInfoTrackName, lapDistPct, trackLengthMeters);
            if (landmark != null)
                return new Result(landmark, "landmark");

            string sector = SectorFallback(lapDistPct);
            return sector != null ? new Result(sector, "sector_fallback") : new Result(null, "unknown");
        }

        /// <summary>
        /// Parses iRacing's <c>WeekendInfo.TrackLength</c> string (e.g. "2.9455 km") into meters.
        /// Returns 0 if unparseable.
        /// </summary>
        public static float ParseTrackLengthMeters(string trackLengthRaw)
        {
            if (string.IsNullOrWhiteSpace(trackLengthRaw)) return 0f;
            var parts = trackLengthRaw.Trim().Split(' ');
            if (parts.Length == 0) return 0f;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                return 0f;
            bool isMiles = parts.Length > 1 && parts[1].Trim().Equals("mi", StringComparison.OrdinalIgnoreCase);
            return isMiles ? value * 1609.344f : value * 1000f;
        }
    }
}
