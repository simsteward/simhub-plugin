using System.Reflection;

namespace SimSteward.Plugin
{
    /// <summary>Build identity for SimHub UI, WebSocket state, and deploy verification.</summary>
    public static class PluginVersionInfo
    {
        private static readonly string DisplayValue = ComputeDisplay();

        /// <summary>Informational version (semver+git) or assembly file version.</summary>
        public static string Display => DisplayValue;

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
    }
}
