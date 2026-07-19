using System.IO;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Shared temp-file + <see cref="File.Replace(string,string,string)"/> atomic-write helper.
    /// Extracted from the pattern originally inlined in <see cref="ReplayIncidentIndexOutputPaths"/>
    /// so token storage, the cloud outbox, and the replay index file all share one durable write path.
    /// </summary>
    public static class AtomicFile
    {
        /// <summary>
        /// Writes <paramref name="bytes"/> to <paramref name="path"/> atomically: writes a sibling
        /// <c>.tmp</c> file first, then replaces the destination (or moves into place when absent).
        /// Creates the parent directory if needed. A crash mid-write can only leave the stale
        /// destination or an orphaned <c>.tmp</c>, never a truncated destination.
        /// </summary>
        public static void WriteAllBytesAtomic(string path, byte[] bytes)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(path))
                File.Replace(temp, path, null);
            else
                File.Move(temp, path);
        }
    }
}
