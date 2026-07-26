using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Reflection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace SimSteward.Plugin
{
    /// <summary>
    /// Optional OTLP metrics export to a local OpenTelemetry Collector (→ Prometheus).
    /// Enabled when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> or <c>SIMSTEWARD_OTLP_ENDPOINT</c> is set (see docs/observability-local.md).
    /// Low-cardinality tags only; host/process samples mirror <see cref="SystemMetricsSampler"/> at the same cadence as structured logs.
    /// </summary>
    public sealed class PluginMetricsTelemetry : IDisposable
    {
        private readonly MeterProvider _meterProvider;
        private readonly Meter _meter;
        private readonly Func<SystemMetricsSample> _getSample;
        private readonly Func<double> _getDataUpdateHz;
        private readonly Func<int> _getClientCount;
        private readonly Func<double> _getReplayBuildProgressPct;
        private readonly KeyValuePair<string, object>[] _baseTags;

        private readonly Histogram<double> _dataUpdateTickIntervalMs;
        private readonly Counter<long> _broadcastsSent;
        private readonly Counter<long> _broadcastsSkippedThrottle;
        private readonly Counter<long> _broadcastsSkippedNoClients;
        private readonly Histogram<double> _broadcastDurationMs;
        private readonly Counter<long> _sendErrors;
        private readonly Histogram<double> _incidentDetectionDurationMs;
        private readonly Histogram<double> _actionDurationMs;
        private readonly Histogram<double> _replayIndexBuildDurationMs;
        private readonly Counter<long> _replayIndexBuildsTotal;

        private PluginMetricsTelemetry(
            MeterProvider meterProvider,
            Meter meter,
            Func<SystemMetricsSample> getSample,
            Func<double> getDataUpdateHz,
            Func<int> getClientCount,
            Func<double> getReplayBuildProgressPct,
            KeyValuePair<string, object>[] baseTags)
        {
            _meterProvider = meterProvider;
            _meter = meter;
            _getSample = getSample;
            _getDataUpdateHz = getDataUpdateHz;
            _getClientCount = getClientCount;
            _getReplayBuildProgressPct = getReplayBuildProgressPct;
            _baseTags = baseTags;

            _meter.CreateObservableGauge(
                "simsteward.plugin.ready",
                ObserveReady,
                unit: "1",
                description: "1 while the SimSteward plugin is loaded.");
            _meter.CreateObservableGauge(
                "simsteward.process.cpu.percent",
                ObserveCpu,
                unit: "%",
                description: "SimHub process CPU over the last resource sample interval.");
            _meter.CreateObservableGauge(
                "simsteward.process.working_set_mb",
                ObserveWs,
                unit: "MiBy",
                description: "SimHub process working set.");

            // --- DataUpdate loop health (real achieved tick rate vs. the ~60Hz game-tick target) ---
            _meter.CreateObservableGauge(
                "simsteward.dataupdate.hz",
                ObserveDataUpdateHz,
                unit: "Hz",
                description: "Achieved DataUpdate() tick rate (EMA), vs. the ~60Hz game-tick target.");
            _dataUpdateTickIntervalMs = _meter.CreateHistogram<double>(
                "simsteward.dataupdate.tick_interval_ms",
                unit: "ms",
                description: "Distribution of inter-tick intervals for DataUpdate() — jitter/stall detection.");

            // --- WebSocket dashboard broadcast path ---
            _meter.CreateObservableGauge(
                "simsteward.ws.clients",
                ObserveClientCount,
                unit: "1",
                description: "Connected dashboard WebSocket clients.");
            _broadcastsSent = _meter.CreateCounter<long>(
                "simsteward.ws.broadcasts_sent",
                unit: "1",
                description: "State broadcasts successfully sent to dashboard clients.");
            _broadcastsSkippedThrottle = _meter.CreateCounter<long>(
                "simsteward.ws.broadcasts_skipped_throttle",
                unit: "1",
                description: "DataUpdate ticks where a broadcast was skipped due to the 200ms/5Hz throttle.");
            _broadcastsSkippedNoClients = _meter.CreateCounter<long>(
                "simsteward.ws.broadcasts_skipped_no_clients",
                unit: "1",
                description: "Broadcasts skipped because no dashboard client was connected.");
            _broadcastDurationMs = _meter.CreateHistogram<double>(
                "simsteward.ws.broadcast_duration_ms",
                unit: "ms",
                description: "Wall-clock time to build the state snapshot and send it to all connected clients.");
            _sendErrors = _meter.CreateCounter<long>(
                "simsteward.ws.send_errors",
                unit: "1",
                description: "Per-client WebSocket send failures.");

            // --- Incident detection ---
            _incidentDetectionDurationMs = _meter.CreateHistogram<double>(
                "simsteward.incident_detection.duration_ms",
                unit: "ms",
                description: "Duration of a single incident-detection pass, tagged by path (live | replay_sweep).");

            // --- Dashboard action dispatch ---
            _actionDurationMs = _meter.CreateHistogram<double>(
                "simsteward.action.duration_ms",
                unit: "ms",
                description: "Duration of a dispatched dashboard action, tagged by action name.");

            // --- Replay incident index build (the >2s long-running operation) ---
            _meter.CreateObservableGauge(
                "simsteward.replay_index_build.progress_pct",
                ObserveReplayBuildProgress,
                unit: "%",
                description: "Current replay-index fast-forward sweep completion percentage (0 when idle).");
            _replayIndexBuildDurationMs = _meter.CreateHistogram<double>(
                "simsteward.replay_index_build.duration_ms",
                unit: "ms",
                description: "Total wall-clock duration of a replay incident index build, tagged by completion_reason.");
            _replayIndexBuildsTotal = _meter.CreateCounter<long>(
                "simsteward.replay_index_build.builds_total",
                unit: "1",
                description: "Replay incident index builds completed, tagged by completion_reason.");
        }

        /// <summary>Returns null if OTLP is not configured (no endpoint env vars).</summary>
        public static PluginMetricsTelemetry TryCreate(
            PluginLogger logger,
            Func<SystemMetricsSample> getSample,
            Func<double> getDataUpdateHz = null,
            Func<int> getClientCount = null,
            Func<double> getReplayBuildProgressPct = null)
        {
            var endpoint = FirstNonEmpty(
                Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"),
                Environment.GetEnvironmentVariable("SIMSTEWARD_OTLP_ENDPOINT"));
            if (string.IsNullOrWhiteSpace(endpoint))
                return null;

            var trimmed = endpoint.Trim();
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                logger?.Structured("WARN", "simhub-plugin", "otel_metrics_bad_endpoint",
                    "OTLP endpoint is not a valid URI; OTLP metrics disabled.",
                    new Dictionary<string, object> { ["endpoint"] = trimmed }, "lifecycle", null);
                return null;
            }

            var env = Environment.GetEnvironmentVariable("SIMSTEWARD_LOG_ENV");
            if (string.IsNullOrWhiteSpace(env))
                env = "unknown";

            var ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
            var baseTags = new[]
            {
                new KeyValuePair<string, object>("deployment.environment", env),
            };

            var resource = ResourceBuilder.CreateDefault()
                .AddService("sim-steward-plugin", serviceVersion: ver)
                .AddAttributes(new Dictionary<string, object>
                {
                    ["deployment.environment"] = env,
                });

            var meter = new Meter("SimSteward.Plugin", ver);

            var provider = Sdk.CreateMeterProviderBuilder()
                .SetResourceBuilder(resource)
                .AddMeter("SimSteward.Plugin")
                .AddOtlpExporter(o =>
                {
                    o.Endpoint = uri;
                })
                .Build();

            var telemetry = new PluginMetricsTelemetry(
                provider, meter, getSample,
                getDataUpdateHz ?? (() => 0),
                getClientCount ?? (() => 0),
                getReplayBuildProgressPct ?? (() => 0),
                baseTags);

            logger?.Structured("INFO", "simhub-plugin", "otel_metrics_started",
                "OTLP metrics export enabled (OpenTelemetry → collector).",
                new Dictionary<string, object>
                {
                    ["endpoint"] = uri.GetLeftPart(UriPartial.Authority),
                    ["deployment_environment"] = env,
                }, "lifecycle", null);

            return telemetry;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var v in values)
            {
                if (!string.IsNullOrWhiteSpace(v))
                    return v;
            }
            return null;
        }

        private IEnumerable<Measurement<int>> ObserveReady()
        {
            yield return new Measurement<int>(1, _baseTags);
        }

        private IEnumerable<Measurement<double>> ObserveCpu()
        {
            var s = _getSample();
            var v = s?.ProcessCpuPct ?? 0;
            yield return new Measurement<double>(v, _baseTags);
        }

        private IEnumerable<Measurement<double>> ObserveWs()
        {
            var s = _getSample();
            var v = s?.ProcessWorkingSetMb ?? 0;
            yield return new Measurement<double>(v, _baseTags);
        }

        private IEnumerable<Measurement<double>> ObserveDataUpdateHz()
        {
            yield return new Measurement<double>(_getDataUpdateHz(), _baseTags);
        }

        private IEnumerable<Measurement<int>> ObserveClientCount()
        {
            yield return new Measurement<int>(_getClientCount(), _baseTags);
        }

        private IEnumerable<Measurement<double>> ObserveReplayBuildProgress()
        {
            yield return new Measurement<double>(_getReplayBuildProgressPct(), _baseTags);
        }

        // --- Imperative recording (histograms/counters, called from the hot paths that measure them) ---

        public void RecordDataUpdateTickIntervalMs(double ms) =>
            _dataUpdateTickIntervalMs.Record(ms, _baseTags);

        public void RecordBroadcastSent(double durationMs)
        {
            _broadcastsSent.Add(1, _baseTags);
            _broadcastDurationMs.Record(durationMs, _baseTags);
        }

        public void RecordBroadcastSkippedThrottle() =>
            _broadcastsSkippedThrottle.Add(1, _baseTags);

        public void RecordBroadcastSkippedNoClients() =>
            _broadcastsSkippedNoClients.Add(1, _baseTags);

        public void RecordSendError() =>
            _sendErrors.Add(1, _baseTags);

        /// <param name="path">Bounded label: "live" or "replay_sweep".</param>
        public void RecordIncidentDetectionDuration(string path, double ms)
        {
            var tags = new[]
            {
                _baseTags[0],
                new KeyValuePair<string, object>("path", path ?? "unknown"),
            };
            _incidentDetectionDurationMs.Record(ms, tags);
        }

        /// <param name="action">Bounded label: dashboard action name (~24 distinct values).</param>
        public void RecordActionDuration(string action, double ms)
        {
            var tags = new[]
            {
                _baseTags[0],
                new KeyValuePair<string, object>("action", action ?? "unknown"),
            };
            _actionDurationMs.Record(ms, tags);
        }

        /// <param name="completionReason">Bounded label: "success" | "cancelled" | "error" etc.</param>
        public void RecordReplayIndexBuildCompleted(string completionReason, double durationMs)
        {
            var tags = new[]
            {
                _baseTags[0],
                new KeyValuePair<string, object>("completion_reason", completionReason ?? "unknown"),
            };
            _replayIndexBuildDurationMs.Record(durationMs, tags);
            _replayIndexBuildsTotal.Add(1, tags);
        }

        public void Dispose()
        {
            try
            {
                _meterProvider?.Dispose();
            }
            catch
            {
                // ignore
            }
        }
    }
}
