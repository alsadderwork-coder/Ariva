using Ariva.Api.Common.Logging;
using Ariva.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Npgsql;

namespace Ariva.Api.Common.Extensions;

/// <summary>
/// The service defaults of a Backplane host (ARV-007, ARV-066): <see cref="ServiceDefaultsExtensions.AddArivaServiceDefaults{TBuilder}"/>
/// with Ariva's own sources and meters. Traces and metrics go over OTLP when Otlp:Enabled or OTEL_EXPORTER_OTLP_ENDPOINT is
/// set; logs travel through Serilog's OTLP sink instead, after redaction. The ASP.NET Core and HttpClient
/// instrumentations redact query string values by default (OpenTelemetry .NET 1.9 and later), so access_token never
/// reaches a span; do not set OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION.
/// </summary>
public static class TelemetryExtensions
{
    // MassTransit's Kafka consume spans and its meter share this name (ADR-0018); since ARV-167 Ariva's producer has its own source.
    private const string MassTransit = "MassTransit";

    public static WebApplicationBuilder AddAppServiceDefaults(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddArivaServiceDefaults(options =>
        {
            options.ServiceName = ArivaLogging.ApplicationName(builder.Configuration);
            options.Environment = builder.Configuration["Application:Environment"] ?? "unknown";
            options.ActivitySources.Add(MassTransit);
            // Ariva's own Kafka producer's send spans (ARV-167); MassTransit's consume spans take them as parents.
            options.ActivitySources.Add(Ariva.Infra.Messaging.Kafka.KafkaProduce.ActivitySourceName);
            options.Meters.Add(MassTransit);
            // Ariva's own Kafka producer's fatal errors and recreations (ARV-167).
            options.Meters.Add(Ariva.Infra.Messaging.Kafka.KafkaProducerMetrics.MeterName);
            // Device heartbeats and zone degradation (ARV-025).
            options.Meters.Add(Ariva.Infra.Sensing.DeviceHealthMetrics.MeterName);
            // Sensing batches, events and health reports the archive skipped (ARV-159).
            options.Meters.Add(Ariva.Infra.Sensing.SensingArchiveMetrics.MeterName);
            options.ConfigureTracing = tracing => tracing.AddNpgsql();
        });
    }
}
