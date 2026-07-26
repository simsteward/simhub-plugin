using System;
using System.Collections.Generic;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Gates repeated per-car SDK-read failure logging so a persistently-bad field/index doesn't
    /// flood the logger at tick rate, while still guaranteeing at least one WARN reaches Loki instead
    /// of being silently swallowed. Tracked independently per key (e.g. SDK field name) so one
    /// consistently-failing field cannot starve the WARN budget of a different field sharing the
    /// same throttle instance.
    /// Safe to call concurrently from multiple threads: instances are shared between the native SDK
    /// telemetry thread and the main DataUpdate thread, and <see cref="ShouldLog"/> serializes its
    /// check-then-update on the underlying dictionary via an internal lock.
    /// </summary>
    public sealed class PerCarReadFailureThrottle
    {
        private readonly TimeSpan _minInterval;
        private readonly Dictionary<string, DateTime> _lastLoggedUtcByKey = new Dictionary<string, DateTime>();
        private readonly object _lock = new object();

        public PerCarReadFailureThrottle(TimeSpan minInterval)
        {
            _minInterval = minInterval;
        }

        public bool ShouldLog(string key, DateTime nowUtc)
        {
            lock (_lock)
            {
                DateTime lastLoggedUtc;
                if (_lastLoggedUtcByKey.TryGetValue(key, out lastLoggedUtc) && nowUtc - lastLoggedUtc < _minInterval)
                    return false;
                _lastLoggedUtcByKey[key] = nowUtc;
                return true;
            }
        }
    }
}
