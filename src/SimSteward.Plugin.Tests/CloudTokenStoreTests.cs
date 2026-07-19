using System;
using System.IO;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    /// <summary>
    /// DPAPI round-trip runs under the current Windows user profile, which this repo already assumes.
    /// </summary>
    public class CloudTokenStoreTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _path;

        public CloudTokenStoreTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ss-token-" + Guid.NewGuid().ToString("N"));
            _path = Path.Combine(_dir, "user-token.bin");
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void Save_ThenTryLoad_RoundTripsRecord()
        {
            var store = new CloudTokenStore(_path);
            var record = new CloudTokenRecord
            {
                UserToken = "user-token-abc",
                UserId = "cust-42",
                IssuedAtUtc = new DateTime(2026, 7, 19, 12, 0, 0, DateTimeKind.Utc),
                AppTokenVersion = 3
            };
            store.Save(record);

            // A fresh instance (same path) must decrypt the persisted token.
            var reloaded = new CloudTokenStore(_path);
            Assert.True(reloaded.TryLoad(out var loaded));
            Assert.Equal("user-token-abc", loaded.UserToken);
            Assert.Equal("cust-42", loaded.UserId);
            Assert.Equal(3, loaded.AppTokenVersion);
        }

        [Fact]
        public void PersistedFile_IsEncrypted_NotPlaintext()
        {
            var store = new CloudTokenStore(_path);
            store.Save(new CloudTokenRecord { UserToken = "PLAINTEXT_SECRET", UserId = "u" });

            byte[] onDisk = File.ReadAllBytes(_path);
            string asText = System.Text.Encoding.UTF8.GetString(onDisk);
            Assert.DoesNotContain("PLAINTEXT_SECRET", asText);
        }

        [Fact]
        public void Clear_EmptiesStore_AndInMemoryAccessToken()
        {
            var store = new CloudTokenStore(_path);
            store.Save(new CloudTokenRecord { UserToken = "t", UserId = "u" });
            store.AccessToken = "access";
            store.AccessTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(5);

            store.Clear();

            Assert.False(store.TryLoad(out _));
            Assert.Null(store.AccessToken);
            Assert.Null(store.AccessTokenExpiresAtUtc);
        }

        [Fact]
        public void NeedsRefresh_TrueWhenNoAccessToken()
        {
            var store = new CloudTokenStore(_path);
            Assert.True(store.NeedsRefresh(TimeSpan.FromSeconds(60)));
        }

        [Fact]
        public void NeedsRefresh_TrueWhenExpiryUnknown()
        {
            var store = new CloudTokenStore(_path) { AccessToken = "a", AccessTokenExpiresAtUtc = null };
            Assert.True(store.NeedsRefresh(TimeSpan.FromSeconds(60)));
        }

        [Fact]
        public void NeedsRefresh_FalseWhenTokenFreshBeyondLeadTime()
        {
            var store = new CloudTokenStore(_path)
            {
                AccessToken = "a",
                AccessTokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(10)
            };
            Assert.False(store.NeedsRefresh(TimeSpan.FromSeconds(60)));
        }

        [Fact]
        public void NeedsRefresh_TrueWhenWithinLeadTimeOfExpiry()
        {
            var store = new CloudTokenStore(_path)
            {
                AccessToken = "a",
                AccessTokenExpiresAtUtc = DateTime.UtcNow.AddSeconds(30)
            };
            Assert.True(store.NeedsRefresh(TimeSpan.FromSeconds(60)));
        }
    }
}
