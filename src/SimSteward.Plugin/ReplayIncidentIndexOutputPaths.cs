using System;
using System.IO;

namespace SimSteward.Plugin
{
    /// <summary>TR-019 writable directory under LocalApplicationData.</summary>
    public static class ReplayIncidentIndexOutputPaths
    {
        public const string SubFolderName = "SimSteward\\replay-incident-index";

        public static string GetDirectory()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = Path.Combine(root, SubFolderName);
            return dir;
        }

        public static string GetFilePathForSubSession(int subSessionId)
        {
            return Path.Combine(GetDirectory(), subSessionId + ".json");
        }

        /// <summary>UTF-8 atomic write (temp + replace).</summary>
        public static void WriteJsonAtomic(string finalPath, string json)
        {
            string dir = Path.GetDirectoryName(finalPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string temp = finalPath + ".tmp";
            File.WriteAllText(temp, json, System.Text.Encoding.UTF8);
            if (File.Exists(finalPath))
                File.Replace(temp, finalPath, null);
            else
                File.Move(temp, finalPath);
        }
    }
}
