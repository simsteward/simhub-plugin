using System.Collections.Generic;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class ReplayControlActionsTests
    {
        // ── ParseSetSpeed ───────────────────────────────────────────────────
        [Theory]
        [InlineData("1", 1, false)]
        [InlineData("2", 2, false)]
        [InlineData("4", 4, false)]
        [InlineData("8", 8, false)]
        [InlineData("16", 16, false)]
        public void ParseSetSpeed_ValidMagnitudes(string arg, int expectedMag, bool expectedSlow)
        {
            var r = ReplayControlActions.ParseSetSpeed(arg);
            Assert.True(r.Ok);
            Assert.Equal(expectedMag, r.Magnitude);
            Assert.Equal(expectedSlow, r.SlowMotion);
            Assert.Null(r.Error);
        }

        [Fact]
        public void ParseSetSpeed_SlowMotion_HalfSpeed()
        {
            var r = ReplayControlActions.ParseSetSpeed("0.5");
            Assert.True(r.Ok);
            Assert.True(r.SlowMotion);
            Assert.Equal(2, r.Magnitude); // 1 / 0.5 = 2 (divisor)
        }

        [Theory]
        [InlineData("17")]
        [InlineData("32")]
        [InlineData("100")]
        public void ParseSetSpeed_AboveMax_ReturnsBadArg(string arg)
        {
            var r = ReplayControlActions.ParseSetSpeed(arg);
            Assert.False(r.Ok);
            Assert.Equal("bad_arg", r.Error);
        }

        [Theory]
        [InlineData("")]
        [InlineData("abc")]
        [InlineData("0")]
        [InlineData("-4")]
        public void ParseSetSpeed_InvalidInputs_ReturnBadArg(string arg)
        {
            var r = ReplayControlActions.ParseSetSpeed(arg);
            Assert.False(r.Ok);
            Assert.Equal("bad_arg", r.Error);
        }

        // ── ParseSetDirection ───────────────────────────────────────────────
        [Theory]
        [InlineData("forward", "forward")]
        [InlineData("reverse", "reverse")]
        [InlineData("FORWARD", "forward")]
        [InlineData("Reverse", "reverse")]
        public void ParseSetDirection_Valid(string arg, string expected)
        {
            var r = ReplayControlActions.ParseSetDirection(arg);
            Assert.True(r.Ok);
            Assert.Equal(expected, r.Direction);
        }

        [Theory]
        [InlineData("")]
        [InlineData("backward")]
        [InlineData("rewind")]
        public void ParseSetDirection_Invalid_ReturnsBadArg(string arg)
        {
            var r = ReplayControlActions.ParseSetDirection(arg);
            Assert.False(r.Ok);
            Assert.Equal("bad_arg", r.Error);
        }

        // ── ParseSeekFrame ──────────────────────────────────────────────────
        [Theory]
        [InlineData("0", 0)]
        [InlineData("1", 1)]
        [InlineData("43210", 43210)]
        [InlineData("999999", 999999)]
        public void ParseSeekFrame_Valid(string arg, int expected)
        {
            var r = ReplayControlActions.ParseSeekFrame(arg);
            Assert.True(r.Ok);
            Assert.Equal(expected, r.Frame);
        }

        [Theory]
        [InlineData("")]
        [InlineData("abc")]
        [InlineData("-1")]
        [InlineData("3.5")]
        public void ParseSeekFrame_Invalid_ReturnsBadArg(string arg)
        {
            var r = ReplayControlActions.ParseSeekFrame(arg);
            Assert.False(r.Ok);
            Assert.Equal("bad_arg", r.Error);
        }

        // ── Resolve next/prev ───────────────────────────────────────────────
        private static List<ReplayIncidentIndexIncidentRow> Idx(params (int frame, int sessTimeMs, string fp)[] rows)
        {
            var l = new List<ReplayIncidentIndexIncidentRow>();
            foreach (var (f, t, fp) in rows)
                l.Add(new ReplayIncidentIndexIncidentRow { ReplayFrame = f, SessionTimeMs = t, Fingerprint = fp });
            return l;
        }

        [Fact]
        public void ResolveNextIncident_Empty_IndexUnavailable()
        {
            var r = ReplayControlActions.ResolveNextIncident(new List<ReplayIncidentIndexIncidentRow>(), 100);
            Assert.False(r.Ok);
            Assert.Equal("index_unavailable", r.Error);
        }

        [Fact]
        public void ResolveNextIncident_AtEnd_ReturnsAtEnd()
        {
            var idx = Idx((100, 1000, "a"), (200, 2000, "b"));
            var r = ReplayControlActions.ResolveNextIncident(idx, 200);
            Assert.False(r.Ok);
            Assert.Equal("at_end", r.Error);
        }

        [Fact]
        public void ResolveNextIncident_PicksFirstAfterCurrent()
        {
            var idx = Idx((100, 1000, "a"), (200, 2000, "b"), (300, 3000, "c"));
            var r = ReplayControlActions.ResolveNextIncident(idx, 150);
            Assert.True(r.Ok);
            Assert.Equal(200, r.Row.ReplayFrame);
        }

        [Fact]
        public void ResolvePrevIncident_AtStart_ReturnsAtStart()
        {
            var idx = Idx((100, 1000, "a"), (200, 2000, "b"));
            var r = ReplayControlActions.ResolvePrevIncident(idx, 100);
            Assert.False(r.Ok);
            Assert.Equal("at_start", r.Error);
        }

        [Fact]
        public void ResolvePrevIncident_PicksLastBeforeCurrent()
        {
            var idx = Idx((100, 1000, "a"), (200, 2000, "b"), (300, 3000, "c"));
            var r = ReplayControlActions.ResolvePrevIncident(idx, 250);
            Assert.True(r.Ok);
            Assert.Equal(200, r.Row.ReplayFrame);
        }

        private static List<ReplayIncidentIndexIncidentRow> IdxCar(params (int frame, int carIdx, string fp)[] rows)
        {
            var l = new List<ReplayIncidentIndexIncidentRow>();
            foreach (var (f, c, fp) in rows)
                l.Add(new ReplayIncidentIndexIncidentRow { ReplayFrame = f, CarIdx = c, Fingerprint = fp });
            return l;
        }

        [Fact]
        public void ResolveNextIncidentForCar_Empty_IndexUnavailable()
        {
            var r = ReplayControlActions.ResolveNextIncidentForCar(new List<ReplayIncidentIndexIncidentRow>(), 100, 5);
            Assert.False(r.Ok);
            Assert.Equal("index_unavailable", r.Error);
        }

        [Fact]
        public void ResolveNextIncidentForCar_NegativeCarIdx_NoCarSelected()
        {
            var idx = IdxCar((100, 5, "a"));
            var r = ReplayControlActions.ResolveNextIncidentForCar(idx, 0, -1);
            Assert.False(r.Ok);
            Assert.Equal("no_car_selected", r.Error);
        }

        [Fact]
        public void ResolveNextIncidentForCar_CarHasNoIncidents_DistinctFromAtEnd()
        {
            var idx = IdxCar((100, 5, "a"), (200, 7, "b"));
            var r = ReplayControlActions.ResolveNextIncidentForCar(idx, 0, 12);
            Assert.False(r.Ok);
            Assert.Equal("no_incidents_for_car", r.Error);
        }

        [Fact]
        public void ResolveNextIncidentForCar_SkipsOtherCars_PicksThisCarsNext()
        {
            var idx = IdxCar((100, 5, "a"), (150, 7, "other"), (200, 5, "b"), (250, 7, "other2"), (300, 5, "c"));
            var r = ReplayControlActions.ResolveNextIncidentForCar(idx, 150, 5);
            Assert.True(r.Ok);
            Assert.Equal(200, r.Row.ReplayFrame);
            Assert.Equal(5, r.Row.CarIdx);
        }

        [Fact]
        public void ResolveNextIncidentForCar_AtEnd_ForThisCar()
        {
            var idx = IdxCar((100, 5, "a"), (200, 5, "b"), (500, 7, "other_car_later"));
            var r = ReplayControlActions.ResolveNextIncidentForCar(idx, 200, 5);
            Assert.False(r.Ok);
            Assert.Equal("at_end", r.Error);
        }

        [Fact]
        public void ResolvePrevIncidentForCar_SkipsOtherCars_PicksThisCarsPrev()
        {
            var idx = IdxCar((100, 5, "a"), (150, 7, "other"), (200, 5, "b"), (300, 5, "c"));
            var r = ReplayControlActions.ResolvePrevIncidentForCar(idx, 250, 5);
            Assert.True(r.Ok);
            Assert.Equal(200, r.Row.ReplayFrame);
        }

        [Fact]
        public void ResolvePrevIncidentForCar_CarHasNoIncidents_DistinctFromAtStart()
        {
            var idx = IdxCar((100, 5, "a"));
            var r = ReplayControlActions.ResolvePrevIncidentForCar(idx, 200, 12);
            Assert.False(r.Ok);
            Assert.Equal("no_incidents_for_car", r.Error);
        }
    }
}
