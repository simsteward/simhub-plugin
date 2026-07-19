using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class IncidentCauseMappingTests
    {
        // ── Source-driven fallback (points unknown) ────────────────────────────
        [Theory]
        [InlineData(ReplayIncidentIndexDetection.SourceTrackSurface, "off-track")]
        [InlineData(ReplayIncidentIndexDetection.SourceFastRepair, "contact")]
        [InlineData(ReplayIncidentIndexDetection.SourceRepairFlag, "contact")]
        [InlineData(ReplayIncidentIndexDetection.SourceFurledFlag, "flagged")]
        [InlineData(ReplayIncidentIndexDetection.SourceBlackFlag, "flagged")]
        [InlineData(ReplayIncidentIndexDetection.SourceDisqualify, "flagged")]
        [InlineData(ReplayIncidentIndexDetection.SourcePlayerIncidentCount, "unknown")]
        [InlineData(ReplayIncidentIndexDetection.SourceYamlIncidentDelta, "unknown")]
        [InlineData("some_unrecognized_source", "unknown")]
        [InlineData("", "unknown")]
        [InlineData(null, "unknown")]
        public void Resolve_PointsUnknown_UsesSourceFallback(string source, string expectedCause)
        {
            Assert.Equal(expectedCause, IncidentCauseMapping.Resolve(source, null));
        }

        // ── Points-value override wins regardless of source ────────────────────
        [Theory]
        [InlineData(1, "off-track")]
        [InlineData(2, "spin")]
        [InlineData(4, "contact")]
        public void Resolve_PointsKnown_OverridesSourceGuess(int points, string expectedCause)
        {
            // repair_flag's source-native guess is "contact" — points, when known, must win anyway.
            Assert.Equal(expectedCause, IncidentCauseMapping.Resolve(ReplayIncidentIndexDetection.SourceRepairFlag, points));
        }

        [Fact]
        public void Resolve_PointsKnown_NonStandardValue_FallsBackToSource()
        {
            // A points value outside {1,2,4} (e.g. an uncapped aggregate) isn't in the override table —
            // falls back to source-driven classification rather than throwing or defaulting blindly.
            Assert.Equal("off-track", IncidentCauseMapping.Resolve(ReplayIncidentIndexDetection.SourceTrackSurface, 3));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(4)]
        public void Resolve_PointsKnown_TrackSurfaceSource_StillUsesOverride(int points)
        {
            // Even track_surface's own strong source-native guess ("off-track") is overridden when a
            // higher-confidence points value says otherwise (e.g. an escalated contact).
            var expected = points == 1 ? "off-track" : points == 2 ? "spin" : "contact";
            Assert.Equal(expected, IncidentCauseMapping.Resolve(ReplayIncidentIndexDetection.SourceTrackSurface, points));
        }
    }
}
