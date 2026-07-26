using System;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Gates repeated per-car SDK-read failure logging so a persistently-bad field/index doesn't
    /// flood the logger at tick rate, while still guaranteeing at least one WARN reaches Loki instead
    /// of being silently swallowed.
    /// </summary>
    public sealed class PerCarReadFailureThrottle
    {
        private readonly TimeSpan _minInterval;
        private DateTime _lastLoggedUtc = DateTime.MinValue;

        public PerCarReadFailureThrottle(TimeSpan minInterval)
        {
            _minInterval = minInterval;
        }

        public bool ShouldLog(DateTime nowUtc)
        {
            if (nowUtc - _lastLoggedUtc < _minInterval)
                return false;
            _lastLoggedUtc = nowUtc;
            return true;
        }
    }
}
