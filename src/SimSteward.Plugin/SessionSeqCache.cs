using System;
using System.Text;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Caches the session-sequence string (sanitized track name + UTC date) so it is rebuilt only
    /// when the track or calendar day actually changes, instead of on every ~60Hz DataUpdate tick.
    /// </summary>
    public sealed class SessionSeqCache
    {
        private string _lastTrackName;
        private string _lastDateStamp;
        private string _cached = "";

        public string Resolve(string trackName, DateTime utcNow)
        {
            if (string.IsNullOrEmpty(trackName))
            {
                _lastTrackName = null;
                _lastDateStamp = null;
                _cached = "";
                return _cached;
            }

            string dateStamp = utcNow.ToString("yyyyMMdd");
            if (trackName == _lastTrackName && dateStamp == _lastDateStamp)
                return _cached;

            _lastTrackName = trackName;
            _lastDateStamp = dateStamp;
            _cached = Build(trackName, dateStamp);
            return _cached;
        }

        private static string Build(string trackName, string dateStamp)
        {
            var safe = new StringBuilder();
            foreach (var c in trackName)
                safe.Append(char.IsLetterOrDigit(c) ? c : '_');
            return $"{safe}_{dateStamp}";
        }
    }
}
