using System;
using System.Security.Cryptography;
using System.Text;

namespace SimStewardPlugin.Telemetry
{
    public static class SecretStore
    {
        // Static entropy to make blobs app-specific (still DPAPI-backed).
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SimStewardPlugin.Telemetry.SecretStore.v1");

        public static string ProtectToBase64(string plaintext)
        {
            if (string.IsNullOrEmpty(plaintext))
            {
                return string.Empty;
            }

            byte[] input = Encoding.UTF8.GetBytes(plaintext);
            byte[] protectedBytes = ProtectedData.Protect(input, Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        public static string UnprotectFromBase64(string protectedBase64)
        {
            if (string.IsNullOrWhiteSpace(protectedBase64))
            {
                return string.Empty;
            }

            try
            {
                byte[] protectedBytes = Convert.FromBase64String(protectedBase64);
                byte[] bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        public static bool LooksLikeProtectedBlob(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            // DPAPI blobs are binary -> base64; quick heuristic: can parse base64 and has a minimum size.
            try
            {
                byte[] bytes = Convert.FromBase64String(value);
                return bytes != null && bytes.Length >= 16;
            }
            catch
            {
                return false;
            }
        }
    }
}
