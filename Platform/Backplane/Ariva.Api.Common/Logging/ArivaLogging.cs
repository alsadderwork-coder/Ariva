using Ariva.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;
using Serilog.Sinks.Grafana.Loki;

namespace Ariva.Api.Common.Logging;

/// <summary>
/// The Serilog pipeline every host uses (ARV-007, AMAN parity): levels from the "Serilog" section, structured JSON on
/// the console, Loki and OTLP sinks when enabled in configuration, and the redaction enricher on every event.
/// Settings fixed in code because they protect secrets: the redaction enricher, and Microsoft.AspNetCore.Hosting at
/// Warning (its request-start log line carries the query string, where SignalR puts access_token; Microsoft's SignalR
/// security guidance).
/// </summary>
public static class ArivaLogging
{
    public const string HostingCategory = "Microsoft.AspNetCore.Hosting";

    public static LoggerConfiguration Configure(LoggerConfiguration logger, IConfiguration configuration, string environment)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(configuration);

        var application = ApplicationName(configuration);

        logger
            .ReadFrom.Configuration(configuration)
            .MinimumLevel.Override(HostingCategory, LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", application)
            .Enrich.WithProperty("Environment", environment ?? "unknown")
            .Enrich.With<RedactionEnricher>()
            .WriteTo.Async(sink => sink.Console(new JsonFormatter(renderMessage: true)));

        var file = configuration["LogFile:Path"];
        if (!string.IsNullOrWhiteSpace(file))
            logger.WriteTo.File(new JsonFormatter(renderMessage: true), file, shared: true);

        if (bool.TryParse(configuration["Loki:Enabled"], out var loki) && loki && Uri.TryCreate(configuration["Loki:Uri"], UriKind.Absolute, out var lokiUri))
        {
            logger.WriteTo.GrafanaLoki(
                lokiUri.ToString(),
                labels:
                [
                    new LokiLabel { Key = "app", Value = application },
                    new LokiLabel { Key = "environment", Value = environment ?? "unknown" },
                    new LokiLabel { Key = "log_type", Value = "application" }
                ],
                propertiesAsLabels: ["level"]);
        }

        var otlp = OtlpSettings.From(configuration);
        if (otlp.Enabled)
        {
            logger.WriteTo.OpenTelemetry(options =>
            {
                options.Endpoint = otlp.Endpoint.ToString();
                options.Protocol = otlp.UseHttp
                    ? Serilog.Sinks.OpenTelemetry.OtlpProtocol.HttpProtobuf
                    : Serilog.Sinks.OpenTelemetry.OtlpProtocol.Grpc;
                options.ResourceAttributes = new Dictionary<string, object>
                {
                    ["service.name"] = application,
                    ["service.namespace"] = "ariva",
                    ["deployment.environment"] = environment ?? "unknown"
                };
            });
        }

        return logger;
    }

    /// <summary>
    /// The service name, for example "api-main". Application:Name is "api-${Application:ShortName}" in the base file;
    /// that one placeholder is resolved here until AMAN's configuration substitution is ported.
    /// </summary>
    public static string ApplicationName(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var name = configuration["Application:Name"];
        if (string.IsNullOrWhiteSpace(name))
            return "ariva";
        return name.Replace("${Application:ShortName}", configuration["Application:ShortName"] ?? "base", StringComparison.Ordinal);
    }
}
