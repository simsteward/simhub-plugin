using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace SimSteward.Plugin
{
    /// <summary>One durable outbox record. Serialized one-per-line as NDJSON.</summary>
    public sealed class CloudOutboxEntry
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary><c>live_incident</c> or <c>index_finalize</c>.</summary>
        [JsonProperty("kind")]
        public string Kind { get; set; }

        [JsonProperty("payloadJson")]
        public string PayloadJson { get; set; }

        [JsonProperty("enqueuedAtUtc")]
        public DateTime EnqueuedAtUtc { get; set; }

        [JsonProperty("attemptCount")]
        public int AttemptCount { get; set; }

        [JsonProperty("nextAttemptAtUtc")]
        public DateTime NextAttemptAtUtc { get; set; }
    }

    /// <summary>Result of <see cref="CloudOutbox.RecordFailure"/>, so the caller can log the transition.</summary>
    public enum OutboxFailureOutcome
    {
        /// <summary>No entry with that id (already acked/dead-lettered) — nothing to do.</summary>
        NotFound,
        /// <summary>Rescheduled with backoff; will be retried.</summary>
        Retrying,
        /// <summary>Exhausted retries (or a permanent failure) — moved to the dead-letter file and dropped.</summary>
        DeadLettered
    }

    /// <summary>
    /// Durable NDJSON outbox for cloud sync.
    /// <para>
    /// <b>Persistence model (see finding #4).</b> <see cref="Enqueue"/> only mutates the in-memory list and
    /// flags the outbox dirty — it does NO disk I/O, because it is called from the 60 Hz DataUpdate tick
    /// (via replay-index finalize / live detection) and a synchronous <c>Flush(true)</c> there stalls the
    /// game loop. All disk writes happen in <see cref="PersistPendingIfDirty"/>, which the drain calls off
    /// the game thread. To keep write-ahead semantics the drain persists BEFORE it pushes, so the only lost
    /// window is a crash between an <see cref="Enqueue"/> and the next drain tick (~2 s). That is acceptable
    /// because delivery is at-least-once and the Worker upsert is idempotent on the fingerprint id.
    /// </para>
    /// Pending entries are reloaded from disk on construction so they survive a process restart.
    /// </summary>
    public sealed class CloudOutbox
    {
        public const string KindLiveIncident = "live_incident";
        public const string KindIndexFinalize = "index_finalize";

        public const string SubFolderName = "SimSteward\\cloud-outbox";
        public const string FileName = "pending.ndjson";
        public const string DeadLetterFileName = "dead-letter.ndjson";

        /// <summary>Attempts after which a still-failing entry is dead-lettered rather than retried forever.</summary>
        public const int MaxAttempts = 10;

        // Capped exponential backoff schedule keyed on post-increment attemptCount.
        private static readonly TimeSpan[] BackoffByAttempt =
        {
            TimeSpan.FromSeconds(5),   // attempt 1
            TimeSpan.FromSeconds(15),  // attempt 2
            TimeSpan.FromSeconds(60),  // attempt 3
        };
        private static readonly TimeSpan BackoffCeiling = TimeSpan.FromMinutes(5);

        private readonly string _filePath;
        private readonly string _deadLetterPath;
        private readonly object _gate = new object();
        private readonly List<CloudOutboxEntry> _entries = new List<CloudOutboxEntry>();
        private bool _dirty;

        public CloudOutbox(string filePath = null)
        {
            _filePath = filePath ?? DefaultFilePath();
            string dir = Path.GetDirectoryName(_filePath);
            _deadLetterPath = string.IsNullOrEmpty(dir)
                ? DeadLetterFileName
                : Path.Combine(dir, DeadLetterFileName);
            LoadFromDisk();
        }

        public static string DefaultFilePath()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, SubFolderName, FileName);
        }

        /// <summary>Count of pending entries currently held (in memory / on disk).</summary>
        public int PendingCount
        {
            get { lock (_gate) return _entries.Count; }
        }

        /// <summary>True when in-memory state has un-persisted changes awaiting the next drain flush.</summary>
        public bool IsDirty
        {
            get { lock (_gate) return _dirty; }
        }

        /// <summary>Entry count reloaded from disk at construction (for a startup log).</summary>
        public int LastLoadEntryCount { get; private set; }

        /// <summary>Corrupt/undeserializable NDJSON lines skipped at construction (for a WARN log). See finding #10.</summary>
        public int LastLoadCorruptLineCount { get; private set; }

        /// <summary>
        /// Appends a new entry in memory only and flags the outbox dirty. NO disk I/O — persistence is the
        /// drain's job (<see cref="PersistPendingIfDirty"/>), see the class remarks. The entry is immediately
        /// ready (<c>nextAttemptAtUtc = now</c>). Returns the new entry id.
        /// </summary>
        public string Enqueue(string kind, string payloadJson)
        {
            var entry = new CloudOutboxEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = kind,
                PayloadJson = payloadJson ?? "",
                EnqueuedAtUtc = DateTime.UtcNow,
                AttemptCount = 0,
                NextAttemptAtUtc = DateTime.UtcNow
            };

            lock (_gate)
            {
                _entries.Add(entry);
                _dirty = true;
            }
            return entry.Id;
        }

        /// <summary>
        /// Persists the full pending set to disk when dirty. Called by the drain OFF the game thread, both
        /// before pushing (write-ahead) and after acks/backoff bumps. Snapshots under the lock and writes
        /// outside it so a slow disk never blocks a concurrent <see cref="Enqueue"/> on the game thread.
        /// </summary>
        public void PersistPendingIfDirty()
        {
            List<CloudOutboxEntry> snapshot;
            lock (_gate)
            {
                if (!_dirty) return;
                snapshot = new List<CloudOutboxEntry>(_entries);
                _dirty = false;
            }

            try
            {
                RewriteAtomic(snapshot);
            }
            catch
            {
                // Write failed — re-flag so the next drain cycle retries the flush rather than silently
                // dropping the change. (The in-memory list remains the source of truth meanwhile.)
                lock (_gate) { _dirty = true; }
                throw;
            }
        }

        /// <summary>
        /// Returns entries whose <c>nextAttemptAtUtc &lt;= nowUtc</c>, oldest-enqueued first. Snapshot copy —
        /// safe to iterate while calling <see cref="Ack"/>/<see cref="RecordFailure"/>.
        /// </summary>
        public bool TryDequeueReady(DateTime nowUtc, out IReadOnlyList<CloudOutboxEntry> entries)
        {
            lock (_gate)
            {
                var ready = _entries
                    .Where(e => e.NextAttemptAtUtc <= nowUtc)
                    .OrderBy(e => e.EnqueuedAtUtc)
                    .ToList();
                entries = ready;
                return ready.Count > 0;
            }
        }

        /// <summary>Removes the entry with <paramref name="id"/> in memory and flags dirty (drain persists it).</summary>
        public void Ack(string id)
        {
            lock (_gate)
            {
                int removed = _entries.RemoveAll(e => e.Id == id);
                if (removed > 0) _dirty = true;
            }
        }

        /// <summary>
        /// Records a push failure. A <paramref name="permanent"/> failure (server rejected the payload) or an
        /// entry that has now hit <see cref="MaxAttempts"/> is moved to the dead-letter file and dropped;
        /// otherwise <c>attemptCount</c> is bumped and <c>nextAttemptAtUtc</c> rescheduled via capped
        /// exponential backoff (5s → 15s → 60s → flat 5-minute ceiling). Flags dirty for the drain to persist.
        /// </summary>
        public OutboxFailureOutcome RecordFailure(string id, bool permanent = false)
        {
            CloudOutboxEntry dead = null;
            lock (_gate)
            {
                var entry = _entries.FirstOrDefault(e => e.Id == id);
                if (entry == null) return OutboxFailureOutcome.NotFound;

                entry.AttemptCount++;
                if (permanent || entry.AttemptCount >= MaxAttempts)
                {
                    _entries.Remove(entry);
                    dead = entry;
                    _dirty = true;
                }
                else
                {
                    entry.NextAttemptAtUtc = DateTime.UtcNow + BackoffForAttempt(entry.AttemptCount);
                    _dirty = true;
                    return OutboxFailureOutcome.Retrying;
                }
            }

            // Append outside the lock; a failed dead-letter write must not resurrect the entry into pending.
            AppendDeadLetter(dead);
            return OutboxFailureOutcome.DeadLettered;
        }

        internal static TimeSpan BackoffForAttempt(int attemptCount)
        {
            if (attemptCount <= 0) return TimeSpan.Zero;
            int idx = attemptCount - 1;
            return idx < BackoffByAttempt.Length ? BackoffByAttempt[idx] : BackoffCeiling;
        }

        private void LoadFromDisk()
        {
            lock (_gate)
            {
                _entries.Clear();
                LastLoadEntryCount = 0;
                LastLoadCorruptLineCount = 0;
                try
                {
                    if (!File.Exists(_filePath)) return;
                    foreach (var line in File.ReadAllLines(_filePath, Encoding.UTF8))
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        CloudOutboxEntry entry = null;
                        try { entry = JsonConvert.DeserializeObject<CloudOutboxEntry>(line); }
                        catch { entry = null; }

                        if (entry != null && !string.IsNullOrEmpty(entry.Id))
                            _entries.Add(entry);
                        else
                            LastLoadCorruptLineCount++; // count, don't silently discard — surfaced by a WARN log
                    }
                    LastLoadEntryCount = _entries.Count;
                }
                catch
                {
                    // Unreadable file — start empty rather than throwing during construction.
                    _entries.Clear();
                }
            }
        }

        private void AppendDeadLetter(CloudOutboxEntry entry)
        {
            if (entry == null) return;
            try
            {
                string dir = Path.GetDirectoryName(_deadLetterPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                string line = JsonConvert.SerializeObject(entry) + "\n";
                byte[] bytes = Encoding.UTF8.GetBytes(line);
                using (var fs = new FileStream(_deadLetterPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true); // rare path — a durable append keeps the poison record for later inspection
                }
            }
            catch
            {
                // Best-effort: losing a dead-letter record must not throw into the drain.
            }
        }

        private void RewriteAtomic(List<CloudOutboxEntry> entries)
        {
            var sb = new StringBuilder();
            foreach (var e in entries)
                sb.Append(JsonConvert.SerializeObject(e)).Append('\n');
            AtomicFile.WriteAllBytesAtomic(_filePath, Encoding.UTF8.GetBytes(sb.ToString()));
        }
    }
}
