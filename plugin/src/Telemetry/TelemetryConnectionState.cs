using System;

namespace SimStewardPlugin.Telemetry
{
    public enum TelemetryConnectionState
    {
        Disconnected,
        NotConfigured,
        Connecting,
        Connected,
        Error
    }

    public sealed class TelemetryStatusSnapshot
    {
        public TelemetryConnectionState State { get; set; } = TelemetryConnectionState.Disconnected;
        public DateTime LastAttemptUtc { get; set; } = DateTime.MinValue;
        public DateTime LastSuccessUtc { get; set; } = DateTime.MinValue;
        public string LastError { get; set; } = string.Empty;
        public long SentLinesTotal { get; set; }
        public long SentBatchesTotal { get; set; }
        public long SentExceptionLinesTotal { get; set; }
        public long DroppedLinesTotal { get; set; }
        public long LogLinesWrittenTotal { get; set; }
        public long SentBytesTotal { get; set; }

        public string StateText
        {
            get { return State.ToString(); }
        }
    }
}
