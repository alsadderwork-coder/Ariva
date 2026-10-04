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
    // MassTransit's Kafka produce and consume spans and its meter share this name (ADR-0018).
    private const string MassTransit = "MassTransit";

    public static WebApplicationBuilder AddAppServiceDefaults(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddArivaServiceDefaults(options =>
        {
            options.ServiceName = ArivaLogging.ApplicationName(builder.Configuration);
            options.Environment = builder.Configuration["Application:Environment"] ?? "unknown";
            options.ActivitySources.Add(MassTransit);
            options.Meters.Add(MassTransit);
            // Device heartbeats and zone degradation (ARV-025).
            options.Meters.Add(Ariva.Infra.Sensing.DeviceHealthMetrics.MeterName);
            options.ConfigureTracing = tracing => tracing.AddNpgsql();
        });
    }
}
