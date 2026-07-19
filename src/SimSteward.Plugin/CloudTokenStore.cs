using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Long-lived user token persisted to disk, DPAPI-encrypted with
    /// <see cref="DataProtectionScope.CurrentUser"/>. The short-lived access token is kept in
    /// memory only (never written to disk) and refreshed via <see cref="CloudAuthClient"/>.
    /// </summary>
    public sealed class CloudTokenRecord
    {
        [JsonProperty("userToken")]
        public string UserToken { get; set; }

        [JsonProperty("userId")]
        public string UserId { get; set; }

        [JsonProperty("issuedAtUtc")]
        public DateTime IssuedAtUtc { get; set; }

        /// <summary>App-token version at issuance; a server bump invalidates prior user tokens.</summary>
        [JsonProperty("appTokenVersion")]
        public int AppTokenVersion { get; set; }
    }

    /// <summary>
    /// DPAPI-backed storage for the cloud user token plus in-memory access-token bookkeeping.
    /// Disk path defaults to <c>%LocalAppData%\SimSteward\cloud-auth\user-token.bin</c>;
    /// a custom path may be injected for tests.
    /// </summary>
    public sealed class CloudTokenStore
    {
        public const string SubFolderName = "SimSteward\\cloud-auth";
        public const string FileName = "user-token.bin";

        private readonly string _filePath;
        private readonly object _gate = new object();

        // In-memory access-token bookkeeping. Written from the cloud-sync Task.Run cycle and read from the
        // Fleck dispatch thread, so both are guarded by _gate (finding #9).
        private string _accessToken;
        private DateTime? _accessTokenExpiresAtUtc;

        /// <summary>Short-lived access token, memory-only. Null until first exchange.</summary>
        public string AccessToken
        {
            get { lock (_gate) return _accessToken; }
            set { lock (_gate) _accessToken = value; }
        }

        /// <summary>UTC expiry of <see cref="AccessToken"/>. Null when unknown/unset.</summary>
        public DateTime? AccessTokenExpiresAtUtc
        {
            get { lock (_gate) return _accessTokenExpiresAtUtc; }
            set { lock (_gate) _accessTokenExpiresAtUtc = value; }
        }

        public CloudTokenStore(string filePath = null)
        {
            _filePath = filePath ?? DefaultFilePath();
        }

        /// <summary>Atomically sets the access token and its expiry (single lock acquisition).</summary>
        public void SetAccessToken(string accessToken, DateTime? expiresAtUtc)
        {
            lock (_gate)
            {
                _accessToken = accessToken;
                _accessTokenExpiresAtUtc = expiresAtUtc;
            }
        }

        public static string DefaultFilePath()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, SubFolderName, FileName);
        }

        /// <summary>
        /// True when the access token is null/empty, has no known expiry, or is within
        /// <paramref name="leadTime"/> of expiring — i.e. a proactive refresh is warranted.
        /// </summary>
        public bool NeedsRefresh(TimeSpan leadTime)
        {
            lock (_gate)
            {
                if (string.IsNullOrEmpty(_accessToken)) return true;
                if (_accessTokenExpiresAtUtc == null) return true;
                return DateTime.UtcNow >= _accessTokenExpiresAtUtc.Value - leadTime;
            }
        }

        /// <summary>Loads and DPAPI-decrypts the persisted user token. False on absent/corrupt file.</summary>
        public bool TryLoad(out CloudTokenRecord record)
        {
            record = null;
            lock (_gate)
            {
                try
                {
                    if (!File.Exists(_filePath)) return false;
                    byte[] encrypted = File.ReadAllBytes(_filePath);
                    if (encrypted.Length == 0) return false;
                    byte[] plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                    string json = Encoding.UTF8.GetString(plain);
                    record = JsonConvert.DeserializeObject<CloudTokenRecord>(json);
                    return record != null;
                }
                catch
                {
                    // Corrupt or undecryptable (e.g. different user profile) — treat as absent.
                    record = null;
                    return false;
                }
            }
        }

        /// <summary>DPAPI-encrypts and atomically persists the user token record.</summary>
        public void Save(CloudTokenRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            lock (_gate)
            {
                string json = JsonConvert.SerializeObject(record);
                byte[] plain = Encoding.UTF8.GetBytes(json);
                byte[] encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
                AtomicFile.WriteAllBytesAtomic(_filePath, encrypted);
            }
        }

        /// <summary>Deletes the persisted user token and clears the in-memory access token.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                try
                {
                    if (File.Exists(_filePath)) File.Delete(_filePath);
                }
                catch
                {
                    // Best-effort delete; a stale file will fail to decrypt or be overwritten on next Save.
                }
                _accessToken = null;
                _accessTokenExpiresAtUtc = null;
            }
        }
    }
}
