using System;
using System.IO;
using System.Linq;
using Xunit;

namespace SimSteward.Plugin.Tests
{
    public class CloudOutboxTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _path;

        public CloudOutboxTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ss-outbox-" + Guid.NewGuid().ToString("N"));
            _path = Path.Combine(_dir, "pending.ndjson");
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void Enqueue_ThenDequeueReady_ReturnsBothImmediately()
        {
            var outbox = new CloudOutbox(_path);
            outbox.Enqueue(CloudOutbox.KindLiveIncident, "{\"a\":1}");
            outbox.Enqueue(CloudOutbox.KindIndexFinalize, "{\"b\":2}");

            Assert.Equal(2, outbox.PendingCount);
            Assert.True(outbox.TryDequeueReady(DateTime.UtcNow, out var ready));
            Assert.Equal(2, ready.Count);
        }

        [Fact]
        public void Ack_RemovesEntry_AndPersists()
        {
            var outbox = new CloudOutbox(_path);
            string id = outbox.Enqueue(CloudOutbox.KindLiveIncident, "{}");
            outbox.Enqueue(CloudOutbox.KindLiveIncident, "{}");

            outbox.Ack(id);
            Assert.Equal(1, outbox.PendingCount);

            // Ack() only flags dirty in memory (no disk I/O on the caller's thread) — the drain persists it.
            outbox.PersistPendingIfDirty();

            // Persisted removal survives a reload.
            var reloaded = new CloudOutbox(_path);
            Assert.Equal(1, reloaded.PendingCount);
            Assert.DoesNotContain(id, ReadIds(reloaded));
        }

        [Fact]
        public void RecordFailure_AppliesBackoff_MakingEntryNotReadyUntilLater()
        {
            var outbox = new CloudOutbox(_path);
            string id = outbox.Enqueue(CloudOutbox.KindLiveIncident, "{}");

            outbox.RecordFailure(id);

            // 5s backoff on first failure: not ready now, ready 6s later.
            Assert.False(outbox.TryDequeueReady(DateTime.UtcNow, out _));
            Assert.True(outbox.TryDequeueReady(DateTime.UtcNow.AddSeconds(6), out var later));
            Assert.Single(later);
        }

        [Theory]
        [InlineData(1, 5)]
        [InlineData(2, 15)]
        [InlineData(3, 60)]
        [InlineData(4, 300)]
        [InlineData(10, 300)]
        public void BackoffForAttempt_FollowsCappedSchedule(int attempt, int expectedSeconds)
        {
            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), CloudOutbox.BackoffForAttempt(attempt));
        }

        [Fact]
        public void Entries_SurviveProcessRestart()
        {
            var first = new CloudOutbox(_path);
            string id1 = first.Enqueue(CloudOutbox.KindLiveIncident, "{\"x\":1}");
            string id2 = first.Enqueue(CloudOutbox.KindIndexFinalize, "{\"y\":2}");

            // Enqueue() only flags dirty in memory (no disk I/O on the caller's thread) — the drain persists it.
            first.PersistPendingIfDirty();

            // New instance pointed at the same path reloads pending entries.
            var second = new CloudOutbox(_path);
            Assert.Equal(2, second.PendingCount);
            var ids = ReadIds(second);
            Assert.Contains(id1, ids);
            Assert.Contains(id2, ids);
        }

        private static System.Collections.Generic.List<string> ReadIds(CloudOutbox outbox)
        {
            outbox.TryDequeueReady(DateTime.UtcNow.AddYears(1), out var all);
            return all.Select(e => e.Id).ToList();
        }
    }
}
