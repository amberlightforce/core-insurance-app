using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

namespace CoreIns.Host.Hosting;

/// <summary>Serilog + OpenTelemetry wiring. Every setting comes from configuration (INFRASTRUCTURE §5).</summary>
internal static class ObservabilityExtensions
{
    private const string AppInsightsKey = "APPLICATIONINSIGHTS_CONNECTION_STRING";
    private const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>
    /// Serilog writes structured logs (configured by the <c>Serilog</c> section) and forwards them to the
    /// OpenTelemetry logger provider. Traces and metrics go to Application Insights when
    /// <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> is set, otherwise to OTLP when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set.
    /// </summary>
    public static WebApplicationBuilder AddCoreInsObservability(this WebApplicationBuilder builder, AppRole role)
    {
        var serviceName = $"coreins-{role.ToConfigValue()}";

        builder.Logging.ClearProviders();
        builder.Logging.AddOpenTelemetry(options =>
        {
            options.IncludeFormattedMessage = true;
            options.IncludeScopes = true;
        });

        builder.Services.AddSerilog(
            (services, logger) => logger
                .ReadFrom.Configuration(builder.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("AppRole", role.ToConfigValue()),
            writeToProviders: true);

        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options =>
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal))
                .AddHttpClientInstrumentation()
                .AddSource("Npgsql"))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter("System.Runtime", "Npgsql"));

        if (!string.IsNullOrWhiteSpace(builder.Configuration[AppInsightsKey]))
        {
            telemetry.UseAzureMonitor();
        }
        else if (!string.IsNullOrWhiteSpace(builder.Configuration[OtlpEndpointKey]))
        {
            telemetry.UseOtlpExporter();
        }

        return builder;
    }
}
