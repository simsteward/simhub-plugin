using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Samples the current SimHub process and the drive hosting plugin data (wall-clock deltas for CPU).
    /// Not thread-safe; call from <see cref="SimStewardPlugin.DataUpdate"/> only.
    /// </summary>
    public sealed class SystemMetricsSampler
    {
        private readonly Process _proc = Process.GetCurrentProcess();
        private TimeSpan _prevCpu;
        private DateTime _prevWallUtc;

        public SystemMetricsSampler()
        {
            Prime();
        }

        /// <summary>Reset CPU baseline (e.g. after plugin init).</summary>
        public void Prime()
        {
            try
            {
                _proc.Refresh();
                _prevCpu = _proc.TotalProcessorTime;
                _prevWallUtc = DateTime.UtcNow;
            }
            catch
            {
                _prevCpu = TimeSpan.Zero;
                _prevWallUtc = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// One observation over the elapsed time since the previous sample. Returns null if the wall delta is too small.
        /// </summary>
        public SystemMetricsSample TrySample(string pluginDataPath, int wsClients, int intervalSec)
        {
            try
            {
                _proc.Refresh();
            }
            catch
            {
                return null;
            }

            var wallNow = DateTime.UtcNow;
            var wallDeltaSec = (wallNow - _prevWallUtc).TotalSeconds;
            if (wallDeltaSec < 0.25)
                return null;

            double cpuPct = 0;
            int procCount = Environment.ProcessorCount;
            if (procCount > 0 && wallDeltaSec >= 0.5)
            {
                var cpuDeltaSec = (_proc.TotalProcessorTime - _prevCpu).TotalSeconds;
                cpuPct = 100.0 * cpuDeltaSec / (procCount * wallDeltaSec);
                if (cpuPct < 0) cpuPct = 0;
                if (cpuPct > 100) cpuPct = 100;
            }

            _prevCpu = _proc.TotalProcessorTime;
            _prevWallUtc = wallNow;

            double wsMb = _proc.WorkingSet64 / (1024.0 * 1024.0);
            double privMb = _proc.PrivateMemorySize64 / (1024.0 * 1024.0);
            double gcHeapMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
            int threads = 0;
            try
            {
                threads = _proc.Threads.Count;
            }
            catch
            {
                threads = 0;
            }

            string diskRoot = null;
            double diskTotalGb = 0;
            double diskFreeGb = 0;
            double diskUsedPct = 0;
            try
            {
                if (!string.IsNullOrEmpty(pluginDataPath))
                {
                    var full = Path.GetFullPath(pluginDataPath);
                    var root = Path.GetPathRoot(full);
                    if (!string.IsNullOrEmpty(root))
                    {
                        var di = new DriveInfo(root);
                        if (di.IsReady)
                        {
                            long total = di.TotalSize;
                            long free = di.AvailableFreeSpace;
                            diskRoot = root;
                            diskTotalGb = total / (1024.0 * 1024.0 * 1024.0);
                            diskFreeGb = free / (1024.0 * 1024.0 * 1024.0);
                            diskUsedPct = total > 0 ? 100.0 * (total - free) / (double)total : 0;
                        }
                    }
                }
            }
            catch
            {
                diskRoot = null;
            }

            return new SystemMetricsSample
            {
                ProcessCpuPct = Round2(cpuPct),
                ProcessWorkingSetMb = Round2(wsMb),
                ProcessPrivateMb = Round2(privMb),
                GcHeapMb = Round2(gcHeapMb),
                ProcessThreads = threads,
                DiskRoot = diskRoot,
                DiskTotalGb = Round2(diskTotalGb),
                DiskFreeGb = Round2(diskFreeGb),
                DiskUsedPct = Round2(diskUsedPct),
                WsClients = wsClients,
                SampleIntervalSec = intervalSec,
                TimestampUtc = wallNow
            };
        }

        private static double Round2(double v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

        public static Dictionary<string, object> ToLogFields(SystemMetricsSample s)
        {
            if (s == null) return null;
            var d = new Dictionary<string, object>
            {
                ["process_cpu_pct"] = s.ProcessCpuPct,
                ["process_working_set_mb"] = s.ProcessWorkingSetMb,
                ["process_private_mb"] = s.ProcessPrivateMb,
                ["gc_heap_mb"] = s.GcHeapMb,
                ["process_threads"] = s.ProcessThreads,
                ["ws_clients"] = s.WsClients,
                ["sample_interval_sec"] = s.SampleIntervalSec
            };
            if (!string.IsNullOrEmpty(s.DiskRoot))
            {
                d["disk_root"] = s.DiskRoot;
                d["disk_total_gb"] = s.DiskTotalGb;
                d["disk_free_gb"] = s.DiskFreeGb;
                d["disk_used_pct"] = s.DiskUsedPct;
            }

            return d;
        }
    }

    public sealed class SystemMetricsSample
    {
        public double ProcessCpuPct { get; set; }
        public double ProcessWorkingSetMb { get; set; }
        public double ProcessPrivateMb { get; set; }
        public double GcHeapMb { get; set; }
        public int ProcessThreads { get; set; }
        public string DiskRoot { get; set; }
        public double DiskTotalGb { get; set; }
        public double DiskFreeGb { get; set; }
        public double DiskUsedPct { get; set; }
        public int WsClients { get; set; }
        public int SampleIntervalSec { get; set; }
        public DateTime TimestampUtc { get; set; }
    }
}
