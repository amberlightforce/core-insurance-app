using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics;
using CoreIns.Platform.Http;
using CoreIns.Platform.Telemetry;
using CoreIns.SharedKernel.Identifiers;
using Serilog;
using Serilog.Context;

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
                .AddMeter("System.Runtime", "Npgsql", CoreInsMetrics.MeterName));

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

    /// <summary>
    /// Correlation ids (NX-OT-06). Accepts a valid <c>X-Correlation-Id</c> (a W3C trace id: 32 hex digits, so it fits the audit
    /// <c>CorrelationId</c>); anything else is ignored and the request's trace id is used. The id is echoed in the response header
    /// (also on errors), pushed into the Serilog LogContext and the Activity, and read by the request context and Problem Details.
    /// Place first in the pipeline.
    /// </summary>
    public static IApplicationBuilder UseCoreInsCorrelation(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            var incoming = context.Request.Headers[PlatformHeaders.CorrelationId].ToString();
            var id = CorrelationId.TryParse(incoming, out var accepted) ? accepted : CorrelationFromActivity();
            context.Items[PlatformHeaders.CorrelationItemKey] = id;
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[PlatformHeaders.CorrelationId] = id.Value;
                return Task.CompletedTask;
            });

            Activity.Current?.SetTag("correlation.id", id.Value);
            using (LogContext.PushProperty("CorrelationId", id.Value))
            {
                await next(context).ConfigureAwait(false);
            }
        });
    }

    private static CorrelationId CorrelationFromActivity() =>
        Activity.Current is { } activity && CorrelationId.TryParse(activity.TraceId.ToHexString(), out var id) ? id : CorrelationId.New();
}
