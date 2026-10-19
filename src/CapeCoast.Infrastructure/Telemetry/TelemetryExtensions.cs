using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace CapeCoast.Infrastructure.Telemetry;

public static class TelemetryExtensions
{
    // the event stream stays open for as long as a browser does, which would wreck latency percentiles
    private static readonly string[] LongLivedRoutes = { "/api/stream" };

    /// <summary>
    /// Metrics go to the metric_points table. Traces go to any OTLP endpoint (Seq accepts them), if one is configured.
    /// Telemetry:Enabled=false switches the whole thing off.
    /// </summary>
    public static IServiceCollection AddCapeCoastTelemetry(this IServiceCollection services, IConfiguration config,
        string serviceName, bool instrumentIncomingRequests)
    {
        if (!config.GetValue("Telemetry:Enabled", true)) return services;

        var otlp = config["Telemetry:OtlpEndpoint"];
        var interval = TimeSpan.FromSeconds(config.GetValue("Telemetry:MetricsIntervalSeconds", 30));

        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName, serviceVersion: typeof(Telemetry).Assembly.GetName().Version?.ToString()))
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(Telemetry.Name);
                if (instrumentIncomingRequests) metrics.AddAspNetCoreInstrumentation();

                metrics.AddReader(sp =>
                {
                    var exporter = new PostgresMetricExporter(
                        sp.GetRequiredService<NpgsqlDataSource>(), serviceName, LongLivedRoutes,
                        sp.GetService<ILoggerFactory>()?.CreateLogger<PostgresMetricExporter>());

                    // delta, so each row says what happened in that interval and percentiles describe it, not all time
                    return new PeriodicExportingMetricReader(exporter, (int)interval.TotalMilliseconds)
                    {
                        TemporalityPreference = MetricReaderTemporalityPreference.Delta,
                    };
                });
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(Telemetry.Name).AddHttpClientInstrumentation();

                if (instrumentIncomingRequests)
                {
                    tracing.AddAspNetCoreInstrumentation(o => o.Filter = ctx => !IsNoise(ctx.Request.Path));
                }

                if (!string.IsNullOrWhiteSpace(otlp))
                {
                    tracing.AddOtlpExporter(o =>
                    {
                        o.Endpoint = new Uri(otlp);
                        o.Protocol = OtlpExportProtocol.HttpProtobuf;
                    });
                }
            });

        return services;
    }

    private static bool IsNoise(PathString path) =>
        path.StartsWithSegments("/api/stream") || path.StartsWithSegments("/swagger")
        || path.StartsWithSegments("/js") || path.StartsWithSegments("/css") || path.StartsWithSegments("/vendor")
        || path.Value is "/" or "/favicon.ico";
}
