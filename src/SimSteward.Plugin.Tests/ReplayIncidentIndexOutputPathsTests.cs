using System;
using System.IO;
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
    }
}
