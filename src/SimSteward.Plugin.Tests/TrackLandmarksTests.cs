using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class TrackLandmarksTests
    {
        [Fact]
        public void LookupLandmark_KnownTrackInRange_ReturnsName()
        {
            // Hell Corner: 210-310m. Bathurst track length ~6213m -> lapDistPct ~0.041 lands inside.
            float lapDistPct = 260f / 6213f;
            string name = TrackLandmarks.LookupLandmark("bathurst", lapDistPct, 6213f);
            Assert.Equal("Hell Corner", name);
        }

        [Fact]
        public void LookupLandmark_UnknownTrack_ReturnsNull()
        {
            Assert.Null(TrackLandmarks.LookupLandmark("winton national", 0.1f, 2945.5f));
        }

        [Fact]
        public void LookupLandmark_CaseInsensitiveTrackName()
        {
            float lapDistPct = 260f / 6213f;
            Assert.Equal("Hell Corner", TrackLandmarks.LookupLandmark("BATHURST", lapDistPct, 6213f));
            Assert.Equal("Hell Corner", TrackLandmarks.LookupLandmark(" Bathurst ", lapDistPct, 6213f));
        }

        [Fact]
        public void LookupLandmark_BetweenLandmarks_ReturnsNull()
        {
            // Between Hell Corner (210-310) and Griffins Bend (1300-1490), no tolerance-adjusted overlap.
            float lapDistPct = 700f / 6213f;
            Assert.Null(TrackLandmarks.LookupLandmark("bathurst", lapDistPct, 6213f));
        }

        [Fact]
        public void LookupLandmark_WithinBoundaryTolerance_ReturnsName()
        {
            // 40m short of Hell Corner's start (210) — within the 70m tolerance.
            float distanceM = 210f - 40f;
            string name = TrackLandmarks.LookupLandmark("bathurst", distanceM / 6213f, 6213f);
            Assert.Equal("Hell Corner", name);
        }

        [Fact]
        public void LookupLandmark_InvalidInputs_ReturnsNull()
        {
            Assert.Null(TrackLandmarks.LookupLandmark("", 0.5f, 6213f));
            Assert.Null(TrackLandmarks.LookupLandmark(null, 0.5f, 6213f));
            Assert.Null(TrackLandmarks.LookupLandmark("bathurst", 0.5f, 0f));
            Assert.Null(TrackLandmarks.LookupLandmark("bathurst", 0.5f, -1f));
        }

        // ── Year-stripped rescan matching (root cause found live at Spa 2026-07-19) ──
        [Theory]
        [InlineData("spa 2024 up", "spa up")]
        [InlineData("monza 2023 full", "monza full")]
        [InlineData("bathurst 2020", "bathurst")]
        [InlineData("spa up", "spa up")]           // no year token — unchanged
        [InlineData("limerock 1966 chicane", "limerock chicane")]
        [InlineData("2024", "")]                   // nothing but a year
        [InlineData("", "")]
        [InlineData(null, "")]
        public void StripYearTokens_RemovesStandaloneYears(string input, string expected)
        {
            Assert.Equal(expected, TrackLandmarks.StripYearTokens(input));
        }

        [Fact]
        public void StripYearTokens_LeavesNonYearNumbersAlone()
        {
            // 4-digit tokens not starting 19/20, and non-numeric tokens, must survive.
            Assert.Equal("track 8500 gp", TrackLandmarks.StripYearTokens("track 8500 gp"));
            Assert.Equal("oulton inthislop", TrackLandmarks.StripYearTokens("oulton inthislop"));
        }

        [Fact]
        public void LookupLandmark_RescanTrackName_MatchesBaseEntry()
        {
            // The exact live-observed case: iRacing reports "spa 2024 up" (2024 laser rescan) but the
            // CrewChief data is keyed "spa up". Stavelot spans 4850-5240m; the observed off-track at
            // lapDistPct 0.7083 on the 6929.3m rescan gives 4908m -> inside Stavelot.
            string name = TrackLandmarks.LookupLandmark("spa 2024 up", 0.7083f, 6929.3f);
            Assert.Equal("Stavelot", name);
        }

        [Fact]
        public void Resolve_RescanTrackName_ReportsLandmarkSource()
        {
            var r = TrackLandmarks.Resolve("spa 2024 up", 0.7083f, 6929.3f);
            Assert.Equal("Stavelot", r.Label);
            Assert.Equal("landmark", r.Source);
        }

        [Fact]
        public void LookupLandmark_ExactMatchStillPreferred()
        {
            // Exact keys keep working unchanged after the year-strip fallback was added.
            float lapDistPct = 260f / 6213f;
            Assert.Equal("Hell Corner", TrackLandmarks.LookupLandmark("bathurst", lapDistPct, 6213f));
        }

        [Theory]
        [InlineData(0.0f, "Sector 1")]
        [InlineData(0.3f, "Sector 1")]
        [InlineData(0.34f, "Sector 2")]
        [InlineData(0.6f, "Sector 2")]
        [InlineData(0.67f, "Sector 3")]
        [InlineData(0.99f, "Sector 3")]
        public void SectorFallback_DividesIntoThirds(float lapDistPct, string expected)
        {
            Assert.Equal(expected, TrackLandmarks.SectorFallback(lapDistPct));
        }

        [Fact]
        public void SectorFallback_OutOfRange_ReturnsNull()
        {
            Assert.Null(TrackLandmarks.SectorFallback(-0.1f));
            Assert.Null(TrackLandmarks.SectorFallback(1.1f));
        }

        [Fact]
        public void Resolve_KnownTrack_PrefersLandmarkOverSector()
        {
            float lapDistPct = 260f / 6213f;
            var result = TrackLandmarks.Resolve("bathurst", lapDistPct, 6213f);
            Assert.Equal("Hell Corner", result.Label);
            Assert.Equal("landmark", result.Source);
        }

        [Fact]
        public void Resolve_UnknownTrack_FallsBackToSector()
        {
            var result = TrackLandmarks.Resolve("winton national", 0.1f, 2945.5f);
            Assert.Equal("Sector 1", result.Label);
            Assert.Equal("sector_fallback", result.Source);
        }

        [Fact]
        public void Resolve_InvalidLapDistPct_ReturnsUnknown()
        {
            var result = TrackLandmarks.Resolve("winton national", -1f, 2945.5f);
            Assert.Null(result.Label);
            Assert.Equal("unknown", result.Source);
        }

        [Fact]
        public void ParseTrackLengthMeters_Kilometers()
        {
            Assert.Equal(2945.5f, TrackLandmarks.ParseTrackLengthMeters("2.9455 km"), 1f);
        }

        [Fact]
        public void ParseTrackLengthMeters_Miles()
        {
            Assert.Equal(6213f, TrackLandmarks.ParseTrackLengthMeters("3.86 mi"), 5f);
        }

        [Fact]
        public void ParseTrackLengthMeters_Invalid_ReturnsZero()
        {
            Assert.Equal(0f, TrackLandmarks.ParseTrackLengthMeters(""));
            Assert.Equal(0f, TrackLandmarks.ParseTrackLengthMeters(null));
            Assert.Equal(0f, TrackLandmarks.ParseTrackLengthMeters("not a number"));
        }
    }
}
