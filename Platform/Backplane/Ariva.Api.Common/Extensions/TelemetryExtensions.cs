using System.Reflection;
using Ariva.Api.Common.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Ariva.Api.Common.Extensions;

/// <summary>
/// Traces and metrics over OTLP, ported from AMAN's AddAppTelemetry (ARV-007). Off unless Otlp:Enabled or
/// OTEL_EXPORTER_OTLP_ENDPOINT is set. Logs travel through Serilog's OTLP sink instead, after redaction. The ASP.NET
/// Core and HttpClient instrumentations redact query string values by default (OpenTelemetry .NET 1.9 and later), so
/// access_token never reaches a span; do not set OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION.
/// </summary>
public static class TelemetryExtensions
{
    // MassTransit's Kafka produce and consume spans and its meter share this name (ADR-0018).
    private const string MassTransitActivitySource = "MassTransit";

    public static IServiceCollection AddAppTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var otlp = OtlpSettings.From(configuration);
        if (!otlp.Enabled)
            return services;

        var protocol = otlp.UseHttp ? OtlpExportProtocol.HttpProtobuf : OtlpExportProtocol.Grpc;
        services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    ArivaLogging.ApplicationName(configuration),
                    serviceNamespace: "ariva",
                    serviceVersion: Assembly.GetEntryAssembly()?.GetName().Version?.ToString())
                .AddAttributes([new KeyValuePair<string, object>("deployment.environment", configuration["Application:Environment"] ?? "unknown")]))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options => options.Filter = context => !IsNoise(context.Request.Path))
                .AddHttpClientInstrumentation()
                .AddNpgsql()
                .AddSource(MassTransitActivitySource)
                .AddOtlpExporter(options => Configure(options, otlp.Endpoint, protocol)))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                // MassTransit's consume, produce and fault counters and durations (ADR-0018).
                .AddMeter(MassTransitActivitySource)
                .AddOtlpExporter(options => Configure(options, otlp.Endpoint, protocol)));

        return services;
    }

    private static void Configure(OtlpExporterOptions options, Uri endpoint, OtlpExportProtocol protocol)
    {
        options.Endpoint = endpoint;
        options.Protocol = protocol;
    }

    // Kubernetes probes poll constantly and would dominate the trace volume.
    private static bool IsNoise(PathString path) => path.StartsWithSegments("/health");
}
