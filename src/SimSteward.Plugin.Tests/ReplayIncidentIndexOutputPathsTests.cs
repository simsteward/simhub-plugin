using System;
using System.IO;
using Newtonsoft.Json;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class ReplayIncidentIndexOutputPathsTests
    {
        [Fact]
        public void GetFilePathForSubSession_EndsWithJson()
        {
            string p = ReplayIncidentIndexOutputPaths.GetFilePathForSubSession(12345);
            Assert.EndsWith("12345.json", p, StringComparison.Ordinal);
        }

        [Fact]
        public void WriteJsonAtomic_CreatesUtf8File()
        {
            string dir = Path.Combine(Path.GetTempPath(), "simsteward-ri-test-" + Guid.NewGuid().ToString("n"));
            string path = Path.Combine(dir, "out.json");
            try
            {
                ReplayIncidentIndexOutputPaths.WriteJsonAtomic(path, "{\"a\":1}");
                Assert.True(File.Exists(path));
                string text = File.ReadAllText(path);
                Assert.Contains("a", text);
                Assert.Contains("1", text);
            }
            finally
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                    if (Directory.Exists(dir))
                        Directory.Delete(dir);
                }
                catch
                {
                    /* best effort */
                }
            }
        }

        [Fact]
        public void TryReadIndexFile_ReturnsFalseWhenMissing()
        {
            Assert.False(ReplayIncidentIndexOutputPaths.TryReadIndexFile(0, out _));
            Assert.False(ReplayIncidentIndexOutputPaths.TryReadIndexFile(-1, out _));
            Assert.False(ReplayIncidentIndexOutputPaths.TryReadIndexFile(999999001, out _));
        }

        [Fact]
        public void ReplayIncidentIndexFileRoot_JsonRoundTrip_MatchesM6Payload()
        {
            var root = new ReplayIncidentIndexFileRoot
            {
                SubSessionId = 7,
                IndexBuildTimeMs = 99,
                TotalRaceIncidents = 1,
                Incidents =
                {
                    new ReplayIncidentIndexIncidentRow
                    {
                        Fingerprint = "aa",
                        CarIdx = 2,
                        SessionTimeMs = 1000,
                        DetectionSource = "furled_flag",
                        IncidentPoints = 2
                    }
                }
            };
            string j = JsonConvert.SerializeObject(root);
            var back = JsonConvert.DeserializeObject<ReplayIncidentIndexFileRoot>(j);
            Assert.Equal(7, back.SubSessionId);
            Assert.Single(back.Incidents);
            Assert.Equal("furled_flag", back.Incidents[0].DetectionSource);
        }
    }
}
