using Newtonsoft.Json.Linq;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class SessionHelloTests
    {
        [Fact]
        public void BuildJson_WithSession_EmitsNumericSubId()
        {
            var jo = JObject.Parse(SessionHello.BuildJson(12345678, "replay", "Replay"));
            Assert.Equal("session_hello", (string)jo["type"]);
            Assert.Equal(12345678, (int)jo["sub_session_id"]);
            Assert.Equal("replay", (string)jo["sim_mode"]);
            Assert.Equal("Replay", (string)jo["plugin_mode"]);
        }

        [Fact]
        public void BuildJson_ZeroSubId_EmitsNullSubIdAndNullSimMode()
        {
            var jo = JObject.Parse(SessionHello.BuildJson(0, "", ""));
            Assert.Equal(JTokenType.Null, jo["sub_session_id"].Type);
            Assert.Equal(JTokenType.Null, jo["sim_mode"].Type);
            Assert.Equal("Unknown", (string)jo["plugin_mode"]);
        }

        [Fact]
        public void BuildJson_NullSubId_EmitsNull()
        {
            var jo = JObject.Parse(SessionHello.BuildJson(null, "replay", "Replay"));
            Assert.Equal(JTokenType.Null, jo["sub_session_id"].Type);
        }
    }
}
