using System.Collections.Generic;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Sim-expert verified mapping of iRacing <c>SessionInfo.Sessions[].SessionType</c>
    /// to a coarse impact class used for penalty-points scaling.
    ///
    /// Authoritative table (do not infer from <c>SessionName</c> — series owners can override that):
    ///   "Practice" / "Open Practice" / "Lone Practice" / "Warmup" -> "free"
    ///   "Qualify"  / "Open Qualify"  / "Lone Qualify"             -> "partial"
    ///   "Race"                                                    -> "full"
    ///   unknown                                                   -> "free" (fail-safe)
    /// </summary>
    public static class SessionTypeImpactClass
    {
        public const string Free    = "free";
        public const string Partial = "partial";
        public const string Full    = "full";

        private static readonly Dictionary<string, string> Map =
            new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                { "Practice",       Free },
                { "Open Practice",  Free },
                { "Lone Practice",  Free },
                { "Warmup",         Free },
                { "Qualify",        Partial },
                { "Open Qualify",   Partial },
                { "Lone Qualify",   Partial },
                { "Race",           Full }
            };

        /// <summary>Returns Free | Partial | Full. Unknown / null / empty -> Free (fail-safe).</summary>
        public static string Classify(string sessionType)
        {
            if (string.IsNullOrEmpty(sessionType)) return Free;
            return Map.TryGetValue(sessionType, out string cls) ? cls : Free;
        }

        /// <summary>True iff <paramref name="sessionType"/> is one of the 8 known strings.</summary>
        public static bool IsKnown(string sessionType)
        {
            if (string.IsNullOrEmpty(sessionType)) return false;
            return Map.ContainsKey(sessionType);
        }
    }
}
