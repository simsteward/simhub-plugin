#if SIMHUB_SDK
using System;
using System.IO;
using System.Text;
using IRSDKSharper;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Feature-flagged 60 Hz telemetry recorder for testing only.
    /// Writes one JSONL row per <see cref="RecordTick"/> call with selected
    /// telemetry variables from the iRacing SDK data catalog (Tier 1 + Tier 2).
    /// Gated by env var <c>SIMSTEWARD_60HZ_TEST_CAPTURE=1</c>.
    /// </summary>
    public sealed class HighRateTelemetryRecorder : IDisposable
    {
        private const int CarSlotCount = 64;

        private readonly StreamWriter _writer;
        private readonly string _filePath;
        private int _ticksRecorded;
        private readonly DateTime _startUtc;

        public HighRateTelemetryRecorder(string testRunId, string basePath)
        {
            _startUtc = DateTime.UtcNow;
            var dir = Path.Combine(basePath, "60hz");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, $"sdk_60hz_capture_{testRunId}.jsonl");
            _writer = new StreamWriter(_filePath, append: false, encoding: new UTF8Encoding(false), bufferSize: 65536);
        }

        public string FilePath => _filePath;
        public int TicksRecorded => _ticksRecorded;

        /// <summary>Records one tick of telemetry. Called from DataUpdate (~60 Hz).</summary>
        public void RecordTick(IRacingSdk irsdk)
        {
            if (irsdk?.Data == null) return;
            try
            {
                var sb = new StringBuilder(2048);
                sb.Append('{');

                AppendInt(sb, "frame", GetInt(irsdk, "ReplayFrameNum"));
                sb.Append(',');
                AppendDouble(sb, "sessionTime", GetDouble(irsdk, "SessionTime"));
                sb.Append(',');
                AppendInt(sb, "sessionState", GetInt(irsdk, "SessionState"));
                sb.Append(',');
                AppendInt(sb, "sessionFlags", GetInt(irsdk, "SessionFlags"));
                sb.Append(',');
                AppendInt(sb, "camCarIdx", GetInt(irsdk, "CamCarIdx"));

                // Per-car arrays (Tier 1)
                sb.Append(",\"carIdxTrackSurface\":");
                AppendIntArray(sb, irsdk, "CarIdxTrackSurface");
                sb.Append(",\"carIdxTrackSurfaceMaterial\":");
                AppendIntArray(sb, irsdk, "CarIdxTrackSurfaceMaterial");
                sb.Append(",\"carIdxPosition\":");
                AppendIntArray(sb, irsdk, "CarIdxPosition");
                sb.Append(",\"carIdxClassPosition\":");
                AppendIntArray(sb, irsdk, "CarIdxClassPosition");
                sb.Append(",\"carIdxLap\":");
                AppendIntArray(sb, irsdk, "CarIdxLap");
                sb.Append(",\"carIdxLapDistPct\":");
                AppendFloatArray(sb, irsdk, "CarIdxLapDistPct");
                sb.Append(",\"carIdxSessionFlags\":");
                AppendIntArray(sb, irsdk, "CarIdxSessionFlags");
                sb.Append(",\"carIdxOnPitRoad\":");
                AppendBoolArray(sb, irsdk, "CarIdxOnPitRoad");

                // Focused-car telemetry (Tier 2)
                sb.Append(',');
                AppendFloat(sb, "latAccel", GetFloat(irsdk, "LatAccel"));
                sb.Append(',');
                AppendFloat(sb, "lonAccel", GetFloat(irsdk, "LonAccel"));
                sb.Append(',');
                AppendFloat(sb, "yawRate", GetFloat(irsdk, "YawRate"));

                sb.Append('}');
                _writer.WriteLine(sb.ToString());
                _ticksRecorded++;
            }
            catch { /* never throw on telemetry tick path */ }
        }

        /// <summary>
        /// Flushes and closes the file. Returns summary stats for the log event.
        /// </summary>
        public (int ticksRecorded, long fileSizeBytes, double durationSec) Finish()
        {
            try { _writer.Flush(); _writer.Close(); }
            catch { /* best effort */ }

            long size = 0;
            try { size = new FileInfo(_filePath).Length; } catch { }
            double dur = (DateTime.UtcNow - _startUtc).TotalSeconds;
            return (_ticksRecorded, size, dur);
        }

        public void Dispose()
        {
            try { _writer?.Dispose(); } catch { }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static int GetInt(IRacingSdk sdk, string name)
        {
            try { return sdk.Data.GetInt(name); } catch { return 0; }
        }

        private static double GetDouble(IRacingSdk sdk, string name)
        {
            try { return sdk.Data.GetDouble(name); } catch { return 0; }
        }

        private static float GetFloat(IRacingSdk sdk, string name)
        {
            try { return sdk.Data.GetFloat(name); } catch { return 0f; }
        }

        private static void AppendInt(StringBuilder sb, string key, int val)
        {
            sb.Append('"').Append(key).Append("\":").Append(val);
        }

        private static void AppendDouble(StringBuilder sb, string key, double val)
        {
            sb.Append('"').Append(key).Append("\":").Append(val.ToString("F4"));
        }

        private static void AppendFloat(StringBuilder sb, string key, float val)
        {
            sb.Append('"').Append(key).Append("\":").Append(val.ToString("F4"));
        }

        private static void AppendIntArray(StringBuilder sb, IRacingSdk sdk, string name)
        {
            sb.Append('[');
            for (int i = 0; i < CarSlotCount; i++)
            {
                if (i > 0) sb.Append(',');
                try { sb.Append(sdk.Data.GetInt(name, i)); }
                catch { sb.Append('0'); }
            }
            sb.Append(']');
        }

        private static void AppendFloatArray(StringBuilder sb, IRacingSdk sdk, string name)
        {
            sb.Append('[');
            for (int i = 0; i < CarSlotCount; i++)
            {
                if (i > 0) sb.Append(',');
                try { sb.Append(sdk.Data.GetFloat(name, i).ToString("F4")); }
                catch { sb.Append("0.0"); }
            }
            sb.Append(']');
        }

        private static void AppendBoolArray(StringBuilder sb, IRacingSdk sdk, string name)
        {
            sb.Append('[');
            for (int i = 0; i < CarSlotCount; i++)
            {
                if (i > 0) sb.Append(',');
                try { sb.Append(sdk.Data.GetBool(name, i) ? "true" : "false"); }
                catch { sb.Append("false"); }
            }
            sb.Append(']');
        }
    }
}
#endif
