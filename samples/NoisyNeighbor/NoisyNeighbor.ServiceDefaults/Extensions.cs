using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

public static class Extensions
{
    public const string QuotaApiMeter = "NoisyNeighbor.QuotaApi";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder
            .Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);
        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http => http.AddServiceDiscovery());

        return builder;
    }

    private static void ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        var aspire = !string.IsNullOrWhiteSpace(
            builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]
        );
        var grafana = builder.Configuration["GRAFANA_OTLP_ENDPOINT"];

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        var otel = builder.Services.AddOpenTelemetry();
        if (aspire)
        {
            otel.WithLogging(logging => logging.AddOtlpExporter());
        }

        otel.WithMetrics(metrics =>
        {
            metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(QuotaApiMeter);

            if (aspire)
            {
                metrics.AddOtlpExporter();
            }

            if (!string.IsNullOrWhiteSpace(grafana))
            {
                metrics.AddOtlpExporter(
                    "grafana",
                    (exporter, reader) =>
                    {
                        exporter.Endpoint = new Uri(grafana);
                        exporter.Protocol = OtlpExportProtocol.Grpc;
                        exporter.Headers = string.Empty;
                        reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds =
                            5000;
                    }
                );
            }
        });
        otel.WithTracing(tracing =>
        {
            tracing
                .AddSource(builder.Environment.ApplicationName)
                .AddAspNetCoreInstrumentation(o =>
                    o.Filter = context => !context.Request.Path.StartsWithSegments("/health")
                )
                .AddHttpClientInstrumentation();

            if (aspire)
            {
                tracing.AddOtlpExporter();
            }
        });
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        app.MapHealthChecks("/health");
        app.MapHealthChecks("/alive", new() { Predicate = r => r.Tags.Contains("live") });
        return app;
    }
}
