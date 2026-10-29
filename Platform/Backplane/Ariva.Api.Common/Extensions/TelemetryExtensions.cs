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

    /// <summary>
    /// Every meter a Backplane host exports (ARV-125a). Every Ariva type that creates a meter names it in a <c>MeterName</c>
    /// constant and is listed here; MeterExportTests fails for one that is not, and for a meter created outside such a type.
    /// </summary>
    public static readonly IReadOnlyList<string> Meters =
    [
        MassTransit,
        // Ariva's own Kafka producer's fatal errors and recreations (ARV-167), the consumed topics (ARV-176), the consumers
        // that cannot connect and the dead letters (ARV-125a).
        Ariva.Infra.Messaging.Kafka.KafkaProducerMetrics.MeterName,
        // Device heartbeats and zone degradation (ARV-025), devices monitored (ARV-125a).
        Ariva.Infra.Sensing.DeviceHealthMetrics.MeterName,
        // Sensing batches, events and health reports the archive skipped (ARV-159).
        Ariva.Infra.Sensing.SensingArchiveMetrics.MeterName,
        // Immigration records by contract and outcome, unmapped codes (ARV-048); exported since ARV-125a.
        Ariva.Infra.Border.BorderMetrics.MeterName,
        // Flight feed items, stale feeds (ARV-041); exported since ARV-125a.
        Ariva.Infra.Flights.FlightMetrics.MeterName,
        // Integration token exchanges and lockouts (ARV-042); exported since ARV-125a.
        Ariva.Infra.Services.Integration.IntegrationMetrics.MeterName,
        // The readiness checks per check (ARV-125a).
        Ariva.Infra.Monitoring.HealthStatusMetrics.MeterName,
        // The expiry of the certificates the host loaded (ARV-125a).
        Ariva.Infra.Monitoring.CertificateMetrics.MeterName,
        // The outbox backlog (ARV-125a).
        Ariva.Infra.Messaging.Outbox.OutboxMetrics.MeterName,
        // The queue stream worker's consumer lag (ARV-125a).
        Ariva.Infra.Streaming.StreamMetrics.MeterName,
        // The malware scanner's signature age and whether it answers (ARV-092c), Ariva.Api.Main with a scanner only.
        Ariva.Infra.Storage.UploadScanMetrics.MeterName,
        // The relational retention run's outcome and TimescaleDB's failed policy jobs (ARV-259c), Ariva.Api.Cronz only.
        Ariva.Infra.Retention.RetentionMetrics.MeterName,
        // The enabled displays whose board would be stale, by site (ARV-259d), Ariva.Api.Main only.
        Ariva.Infra.Services.Displays.DisplayMetrics.MeterName,
        // Security events by kind and outcome, written or left out by the throttle (ARV-091).
        SecurityEventMetrics.MeterName
    ];

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
            foreach (var meter in Meters)
                options.Meters.Add(meter);
            options.ConfigureTracing = tracing => tracing.AddNpgsql();
        });
    }
}
