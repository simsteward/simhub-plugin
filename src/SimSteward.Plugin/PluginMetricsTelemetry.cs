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
        private readonly KeyValuePair<string, object>[] _baseTags;

        private PluginMetricsTelemetry(
            MeterProvider meterProvider,
            Meter meter,
            Func<SystemMetricsSample> getSample,
            KeyValuePair<string, object>[] baseTags)
        {
            _meterProvider = meterProvider;
            _meter = meter;
            _getSample = getSample;
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
        }

        /// <summary>Returns null if OTLP is not configured (no endpoint env vars).</summary>
        public static PluginMetricsTelemetry TryCreate(
            PluginLogger logger,
            Func<SystemMetricsSample> getSample)
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

            var telemetry = new PluginMetricsTelemetry(provider, meter, getSample, baseTags);

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
