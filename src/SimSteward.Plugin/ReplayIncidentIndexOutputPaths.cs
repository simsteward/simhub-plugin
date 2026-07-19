using System;
using System.IO;
using Newtonsoft.Json;

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

        public static string GetRecordSamplesDirectory()
        {
            return Path.Combine(GetDirectory(), "record-samples");
        }

        public static string GetFilePathForSubSession(int subSessionId)
        {
            return Path.Combine(GetDirectory(), subSessionId + ".json");
        }

        /// <summary>Read TR-019 JSON from disk if present (M6 dashboard refresh).</summary>
        public static bool TryReadIndexFile(int subSessionId, out ReplayIncidentIndexFileRoot root)
        {
            root = null;
            if (subSessionId <= 0) return false;
            string path = GetFilePathForSubSession(subSessionId);
            if (!File.Exists(path)) return false;
            try
            {
                string json = File.ReadAllText(path, System.Text.Encoding.UTF8);
                root = JsonConvert.DeserializeObject<ReplayIncidentIndexFileRoot>(json);
                return root != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>UTF-8 atomic write (temp + replace). Emits a UTF-8 BOM to match prior behavior.</summary>
        public static void WriteJsonAtomic(string finalPath, string json)
        {
            // Preserve the exact bytes File.WriteAllText(path, json, Encoding.UTF8) produced:
            // Encoding.UTF8 emits a BOM preamble, so prepend it before the encoded body.
            var enc = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            byte[] preamble = enc.GetPreamble();
            byte[] body = enc.GetBytes(json ?? "");
            byte[] bytes = new byte[preamble.Length + body.Length];
            System.Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
            System.Buffer.BlockCopy(body, 0, bytes, preamble.Length, body.Length);
            AtomicFile.WriteAllBytesAtomic(finalPath, bytes);
        }
    }
}
