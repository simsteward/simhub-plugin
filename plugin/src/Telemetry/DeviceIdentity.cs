using System;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace SimStewardPlugin.Telemetry
{
    public static class DeviceIdentity
    {
        public static string ComputeDeviceIdHash(string installId)
        {
            string machineName = Environment.MachineName ?? string.Empty;
            string userSid = string.Empty;

            try
            {
                userSid = WindowsIdentity.GetCurrent()?.User?.Value ?? string.Empty;
            }
            catch
            {
                userSid = string.Empty;
            }

            // One-way fingerprint: never emit raw identifiers. This is NOT strong attestation.
            string raw = $"machine={machineName}|sid={userSid}|install={installId ?? string.Empty}";
            return Sha256Hex(raw);
        }

        public static string EnsureInstallId(string existing)
        {
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }

            return Guid.NewGuid().ToString("N");
        }

        private static string Sha256Hex(string input)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(input ?? string.Empty);
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                {
                    sb.Append(b.ToString("x2"));
                }

                return sb.ToString();
            }
        }
    }
}
