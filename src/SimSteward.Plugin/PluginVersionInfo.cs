using System;
using System.IO;
using System.Reflection;

namespace SimSteward.Plugin
{
    /// <summary>Build identity for SimHub UI, WebSocket state, and deploy verification.</summary>
    public static class PluginVersionInfo
    {
        private static readonly string DisplayValue = ComputeDisplay();
        private static readonly string GitShaValue = ComputeGitSha(DisplayValue);
        private static readonly DateTime BuildTimestampUtcValue = ComputeBuildTimestampUtc();

        /// <summary>Informational version (semver+git) or assembly file version.</summary>
        public static string Display => DisplayValue;

        /// <summary>Full git SHA parsed from the informational version (part after '+'); empty if not present.</summary>
        public static string GitSha => GitShaValue;

        /// <summary>Last-write time (UTC) of the plugin assembly on disk — a proxy for build time; no build-system change required.</summary>
        public static DateTime BuildTimestampUtc => BuildTimestampUtcValue;

        private static string ComputeDisplay()
        {
            var asm = typeof(PluginVersionInfo).Assembly;
            var infoAttrs = asm.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
            if (infoAttrs != null && infoAttrs.Length > 0)
            {
                var v = ((AssemblyInformationalVersionAttribute)infoAttrs[0]).InformationalVersion;
                if (!string.IsNullOrWhiteSpace(v))
                    return v.Trim();
            }

            var ver = asm.GetName().Version;
            return ver != null ? ver.ToString() : "0.0.0.0";
        }

        private static string ComputeGitSha(string display)
        {
            if (string.IsNullOrEmpty(display)) return "";
            int plus = display.IndexOf('+');
            return plus >= 0 && plus + 1 < display.Length ? display.Substring(plus + 1) : "";
        }

        private static DateTime ComputeBuildTimestampUtc()
        {
            try
            {
                var location = typeof(PluginVersionInfo).Assembly.Location;
                return string.IsNullOrEmpty(location) ? DateTime.MinValue : File.GetLastWriteTimeUtc(location);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }
    }
}
