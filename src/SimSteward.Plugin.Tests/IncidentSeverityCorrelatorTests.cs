using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class IncidentSeverityCorrelatorTests
    {
        private static IncidentSample Sample(int carIdx, string source, int? points, double sessionTimeSec = 0, bool aggregate = false)
        {
            return new IncidentSample(
                carIdx,
                ReplayIncidentIndexDetection.ToSessionTimeMs(sessionTimeSec),
                source,
                points,
                replayFrame: 0,
                isAggregateDelta: aggregate);
        }

        [Fact]
        public void Correlate_NewIncident_NoPriorPendingState_Passthrough()
        {
            var c = new IncidentSeverityCorrelator();
            var s = Sample(5, ReplayIncidentIndexDetection.SourceTrackSurface, null, 10.0);

            var r = c.Correlate(s, 10.0, isDirtSurface: false);

            Assert.True(r.IsNewIncident);
            Assert.False(r.IsEscalation);
            Assert.Null(r.Merged.IncidentPoints);
            Assert.Equal("off-track", r.Cause);
        }

        [Fact]
        public void Correlate_OffTrack_ThenLossOfControl_ThenContact_EscalatesToMaxNotSum()
        {
            // The exact scenario from the plan: a car goes off-track, then loses control (proxy: a
            // second surface flip with no resolvable points), then makes contact (points=4 resolves,
            // e.g. via the player-count delta or a live YAML delta). Final result must be points=4,
            // cause="contact" — never a summed 1+null+4.
            var c = new IncidentSeverityCorrelator();

            var s1 = Sample(5, ReplayIncidentIndexDetection.SourceTrackSurface, null, 100.0);
            var r1 = c.Correlate(s1, 100.0, isDirtSurface: false);
            Assert.True(r1.IsNewIncident);
            Assert.Null(r1.Merged.IncidentPoints);
            Assert.Equal("off-track", r1.Cause);

            var s2 = Sample(5, ReplayIncidentIndexDetection.SourceTrackSurface, null, 101.5);
            var r2 = c.Correlate(s2, 101.5, isDirtSurface: false);
            Assert.False(r2.IsNewIncident);
            Assert.False(r2.IsEscalation); // no numeric change — same cause/points as before, no-op
            Assert.Null(r2.Merged.IncidentPoints);

            var s3 = Sample(5, ReplayIncidentIndexDetection.SourcePlayerIncidentCount, 4, 102.8);
            var r3 = c.Correlate(s3, 102.8, isDirtSurface: false);
            Assert.False(r3.IsNewIncident);
            Assert.True(r3.IsEscalation);
            Assert.Equal(4, r3.Merged.IncidentPoints);
            Assert.Equal("contact", r3.Cause);
        }

        [Fact]
        public void Correlate_PointsArriveBeforeLaterNullPointsSample_MaxHolds_NoDowngrade()
        {
            var c = new IncidentSeverityCorrelator();

            var s1 = Sample(5, ReplayIncidentIndexDetection.SourceYamlIncidentDelta, 4, 50.0);
            var r1 = c.Correlate(s1, 50.0, isDirtSurface: false);
            Assert.True(r1.IsNewIncident);
            Assert.Equal(4, r1.Merged.IncidentPoints);
            Assert.Equal("contact", r1.Cause);

            var s2 = Sample(5, ReplayIncidentIndexDetection.SourceTrackSurface, null, 51.0);
            var r2 = c.Correlate(s2, 51.0, isDirtSurface: false);
            Assert.False(r2.IsNewIncident);
            Assert.False(r2.IsEscalation);
            Assert.Equal(4, r2.Merged.IncidentPoints); // still 4, not downgraded/nulled by the later null-points sample
            Assert.Equal("contact", r2.Cause);
        }

        [Fact]
        public void Correlate_TwoIndependentEvents_OutsideWindow_NotMerged()
        {
            var c = new IncidentSeverityCorrelator();

            var s1 = Sample(5, ReplayIncidentIndexDetection.SourceTrackSurface, null, 0.0);
            var r1 = c.Correlate(s1, 0.0, isDirtSurface: false, windowSec: 6.0);
            Assert.True(r1.IsNewIncident);

            var s2 = Sample(5, ReplayIncidentIndexDetection.SourceYamlIncidentDelta, 4, 20.0);
            var r2 = c.Correlate(s2, 20.0, isDirtSurface: false, windowSec: 6.0);
            Assert.True(r2.IsNewIncident); // well outside the window — a fresh incident, not a merge
        }

        [Fact]
        public void Correlate_WindowBoundary_ExactlyAtWindow_Merges_JustPast_DoesNot()
        {
            var c1 = new IncidentSeverityCorrelator();
            var a1 = c1.Correlate(Sample(5, ReplayIncidentIndexDetection.SourceTrackSurface, null, 0.0), 0.0, false, windowSec: 6.0);
            Assert.True(a1.IsNewIncident);
            var a2 = c1.Correlate(Sample(5, ReplayIncidentIndexDetection.SourceYamlIncidentDelta, 4, 6.0), 6.0, false, windowSec: 6.0);
            Assert.False(a2.IsNewIncident); // exactly at the boundary — still merges

            var c2 = new IncidentSeverityCorrelator();
            var b1 = c2.Correlate(Sample(5, ReplayIncidentIndexDetection.SourceTrackSurface, null, 0.0), 0.0, false, windowSec: 6.0);
            Assert.True(b1.IsNewIncident);
            var b2 = c2.Correlate(Sample(5, ReplayIncidentIndexDetection.SourceYamlIncidentDelta, 4, 6.001), 6.001, false, windowSec: 6.0);
            Assert.True(b2.IsNewIncident); // just past the boundary — fresh incident
        }

        [Fact]
        public void Correlate_MultipleCarsConcurrently_NoCrossContamination()
        {
            var c = new IncidentSeverityCorrelator();

            var carA1 = c.Correlate(Sample(3, ReplayIncidentIndexDetection.SourceTrackSurface, null, 10.0), 10.0, false);
            Assert.True(carA1.IsNewIncident);

            var carB1 = c.Correlate(Sample(7, ReplayIncidentIndexDetection.SourceYamlIncidentDelta, 1, 10.2), 10.2, false);
            Assert.True(carB1.IsNewIncident); // different car — independent pending state

            var carA2 = c.Correlate(Sample(3, ReplayIncidentIndexDetection.SourceYamlIncidentDelta, 4, 11.0), 11.0, false);
            Assert.False(carA2.IsNewIncident);
            Assert.Equal(4, carA2.Merged.IncidentPoints);

            var carB2 = c.Correlate(Sample(7, ReplayIncidentIndexDetection.SourceTrackSurface, null, 11.2), 11.2, false);
            Assert.False(carB2.IsNewIncident);
            Assert.Equal(1, carB2.Merged.IncidentPoints); // car 7's own state untouched by car 3's escalation
        }

        [Theory]
        [InlineData(4, true, 2)]   // heavy contact on dirt caps at 2
        [InlineData(2, true, 2)]   // spin on dirt — unaffected, already below cap
        [InlineData(1, true, 1)]   // off-track on dirt — unaffected
        [InlineData(4, false, 4)]  // heavy contact on asphalt — unaffected, stays 4
        public void Correlate_DirtCap_AppliesOnlyToHeavyContact(int rawPoints, bool isDirt, int expectedPoints)
        {
            var c = new IncidentSeverityCorrelator();
            var r = c.Correlate(Sample(5, ReplayIncidentIndexDetection.SourceYamlIncidentDelta, rawPoints, 0.0), 0.0, isDirt);
            Assert.Equal(expectedPoints, r.Merged.IncidentPoints);
        }

        [Fact]
        public void Correlate_DirtCap_InteractsCorrectlyWithEscalation()
        {
            // Off-track (null points) escalates toward what would be a 4x contact — on a dirt session the
            // final merged max must be the capped 2, not 4, even though the cap is applied per-sample
            // before the max-taking, not as an afterthought on the running total.
            var c = new IncidentSeverityCorrelator();

            var r1 = c.Correlate(Sample(5, ReplayIncidentIndexDetection.SourceTrackSurface, null, 0.0), 0.0, isDirtSurface: true);
            Assert.True(r1.IsNewIncident);
            Assert.Null(r1.Merged.IncidentPoints);

            var r2 = c.Correlate(Sample(5, ReplayIncidentIndexDetection.SourceYamlIncidentDelta, 4, 1.0), 1.0, isDirtSurface: true);
            Assert.False(r2.IsNewIncident);
            Assert.True(r2.IsEscalation);
            Assert.Equal(2, r2.Merged.IncidentPoints); // capped, never 4
            Assert.Equal("spin", r2.Cause); // points=2 resolves via IncidentCauseMapping to "spin"
        }

        [Fact]
        public void Reset_ClearsAllPendingState()
        {
            var c = new IncidentSeverityCorrelator();
            c.Correlate(Sample(5, ReplayIncidentIndexDetection.SourceYamlIncidentDelta, 4, 0.0), 0.0, false);

            c.Reset();

            // After reset, the same car at a time well within what would have been the old window
            // must be treated as brand new, not merged with pre-reset state.
            var r = c.Correlate(Sample(5, ReplayIncidentIndexDetection.SourceTrackSurface, null, 1.0), 1.0, false);
            Assert.True(r.IsNewIncident);
            Assert.Null(r.Merged.IncidentPoints);
        }
    }
}
