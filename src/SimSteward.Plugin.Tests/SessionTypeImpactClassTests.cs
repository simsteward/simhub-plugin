using SimSteward.Plugin;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class SessionTypeImpactClassTests
    {
        [Theory]
        [InlineData("Practice")]
        [InlineData("Open Practice")]
        [InlineData("Lone Practice")]
        [InlineData("Warmup")]
        public void Classify_Practice_Family_Returns_Free(string sessionType)
        {
            Assert.Equal(SessionTypeImpactClass.Free, SessionTypeImpactClass.Classify(sessionType));
            Assert.True(SessionTypeImpactClass.IsKnown(sessionType));
        }

        [Theory]
        [InlineData("Qualify")]
        [InlineData("Open Qualify")]
        [InlineData("Lone Qualify")]
        public void Classify_Qualify_Family_Returns_Partial(string sessionType)
        {
            Assert.Equal(SessionTypeImpactClass.Partial, SessionTypeImpactClass.Classify(sessionType));
            Assert.True(SessionTypeImpactClass.IsKnown(sessionType));
        }

        [Fact]
        public void Classify_Race_Returns_Full()
        {
            Assert.Equal(SessionTypeImpactClass.Full, SessionTypeImpactClass.Classify("Race"));
            Assert.True(SessionTypeImpactClass.IsKnown("Race"));
        }

        [Theory]
        [InlineData("race", SessionTypeImpactClass.Full)]
        [InlineData("RACE", SessionTypeImpactClass.Full)]
        [InlineData("qualify", SessionTypeImpactClass.Partial)]
        [InlineData("warmup", SessionTypeImpactClass.Free)]
        [InlineData("open practice", SessionTypeImpactClass.Free)]
        public void Classify_Is_Case_Insensitive(string sessionType, string expected)
        {
            Assert.Equal(expected, SessionTypeImpactClass.Classify(sessionType));
            Assert.True(SessionTypeImpactClass.IsKnown(sessionType));
        }

        [Theory]
        [InlineData("Heat Race")]
        [InlineData("Lone Heat")]
        [InlineData("Practice ")]   // trailing space — not a recognized key
        [InlineData("xyz")]
        public void Classify_Unknown_Returns_Free_Fail_Safe(string sessionType)
        {
            Assert.Equal(SessionTypeImpactClass.Free, SessionTypeImpactClass.Classify(sessionType));
            Assert.False(SessionTypeImpactClass.IsKnown(sessionType));
        }

        [Fact]
        public void Classify_Null_Returns_Free()
        {
            Assert.Equal(SessionTypeImpactClass.Free, SessionTypeImpactClass.Classify(null));
            Assert.False(SessionTypeImpactClass.IsKnown(null));
        }

        [Fact]
        public void Classify_Empty_Returns_Free()
        {
            Assert.Equal(SessionTypeImpactClass.Free, SessionTypeImpactClass.Classify(""));
            Assert.False(SessionTypeImpactClass.IsKnown(""));
        }

        [Fact]
        public void Constants_Have_Expected_Values()
        {
            // These are wire-format values consumed downstream — lock them.
            Assert.Equal("free", SessionTypeImpactClass.Free);
            Assert.Equal("partial", SessionTypeImpactClass.Partial);
            Assert.Equal("full", SessionTypeImpactClass.Full);
        }
    }
}
