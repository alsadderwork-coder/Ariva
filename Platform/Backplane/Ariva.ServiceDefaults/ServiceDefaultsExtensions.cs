using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Ariva.ServiceDefaults;

/// <summary>What a host adds to the shared defaults: its name, its own activity sources and meters.</summary>
public sealed class ArivaServiceDefaultsOptions
{
    /// <summary>service.name of every signal; the host's application name when not set.</summary>
    public string ServiceName { get; set; }

    /// <summary>deployment.environment; Application:Environment when not set.</summary>
    public string Environment { get; set; }

    public ICollection<string> ActivitySources { get; } = new List<string>();

    public ICollection<string> Meters { get; } = new List<string>();

    /// <summary>More tracing setup (for example Npgsql's instrumentation in the Backplane hosts).</summary>
    public Action<TracerProviderBuilder> ConfigureTracing { get; set; }

    /// <summary>
    /// Export logs through OpenTelemetry's logging provider. Off for the Backplane hosts, whose logs go through Serilog's
    /// OTLP sink after redaction; on for a host without Serilog (the simulator).
    /// </summary>
    public bool ExportLogs { get; set; }
}

/// <summary>
/// The Aspire-style service defaults of an Ariva host (ARV-066), applied the same way in Kubernetes, under the Aspire
/// AppHost and in tests: traces and metrics over OTLP when an endpoint is configured, the health check service (the
/// probes themselves are mapped by the host, never here), and resilience for HttpClientFactory clients. Nothing here
/// maps an endpoint or adds middleware, so default deny, security headers, limits and rate limits stay as the host set them.
/// </summary>
public static class ServiceDefaultsExtensions
{
    public static TBuilder AddArivaServiceDefaults<TBuilder>(this TBuilder builder, Action<ArivaServiceDefaultsOptions> configure = null)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new ArivaServiceDefaultsOptions();
        configure?.Invoke(options);
        options.ServiceName ??= builder.Environment.ApplicationName;
        options.Environment ??= builder.Configuration["Application:Environment"] ?? builder.Environment.EnvironmentName;

        builder.Services.AddArivaTelemetry(builder.Configuration, options);
        if (options.ExportLogs)
            builder.Logging.AddArivaOpenTelemetryLogs(builder.Configuration, options);
        builder.Services.AddHealthChecks();
        builder.Services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler(ConfigureResilience));
        return builder;
    }

    /// <summary>Traces and metrics over OTLP; nothing when OTLP is off (<see cref="OtlpSettings"/>).</summary>
    public static IServiceCollection AddArivaTelemetry(this IServiceCollection services, IConfiguration configuration, ArivaServiceDefaultsOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);

        var otlp = OtlpSettings.From(configuration);
        if (!otlp.Enabled)
            return services;

        services
            .AddOpenTelemetry()
            .ConfigureResource(resource => Describe(resource, options))
            .WithTracing(tracing =>
            {
                tracing
                    // Kubernetes and the AppHost poll the probes constantly; they would dominate the trace volume.
                    .AddAspNetCoreInstrumentation(instrumentation => instrumentation.Filter = context => !IsProbe(context.Request.Path))
                    .AddHttpClientInstrumentation();
                foreach (var source in options.ActivitySources)
                    tracing.AddSource(source);
                options.ConfigureTracing?.Invoke(tracing);
                tracing.AddOtlpExporter(exporter => Target(exporter, otlp));
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
                foreach (var meter in options.Meters)
                    metrics.AddMeter(meter);
                metrics.AddOtlpExporter(exporter => Target(exporter, otlp));
            });

        return services;
    }

    /// <summary>
    /// Resilience for HttpClientFactory clients: the standard pipeline (rate limiter, total timeout, retry, circuit breaker,
    /// attempt timeout) with retries for safe methods only, so a POST (a sensor event, a feed message) is never sent twice,
    /// and timeouts wide enough for the longest configured client timeout (60 seconds). The integration outbound clients
    /// build their own chain per endpoint (OutboundClients) and are not factory clients.
    /// </summary>
    public static void ConfigureResilience(HttpStandardResilienceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Retry.DisableForUnsafeHttpMethods();
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(120);
        // At least twice the attempt timeout, as the pipeline's validation requires.
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(120);
    }

    private static void AddArivaOpenTelemetryLogs(this ILoggingBuilder logging, IConfiguration configuration, ArivaServiceDefaultsOptions options)
    {
        var otlp = OtlpSettings.From(configuration);
        if (!otlp.Enabled)
            return;
        logging.AddOpenTelemetry(log =>
        {
            log.IncludeFormattedMessage = true;
            log.SetResourceBuilder(Describe(ResourceBuilder.CreateDefault(), options));
            log.AddOtlpExporter(exporter => Target(exporter, otlp));
        });
    }

    // No generated service.instance.id: the one in OTEL_RESOURCE_ATTRIBUTES (set by the AppHost or the chart) is the one
    // Serilog's sink uses too, so a host's traces, metrics and logs land under one instance.
    private static ResourceBuilder Describe(ResourceBuilder resource, ArivaServiceDefaultsOptions options) =>
        resource
            .AddService(options.ServiceName, serviceNamespace: "ariva", serviceVersion: Assembly.GetEntryAssembly()?.GetName().Version?.ToString(),
                autoGenerateServiceInstanceId: false)
            .AddAttributes([new KeyValuePair<string, object>("deployment.environment", options.Environment ?? "unknown")]);

    private static void Target(OtlpExporterOptions exporter, OtlpSettings otlp)
    {
        exporter.Endpoint = otlp.Endpoint;
        exporter.Protocol = otlp.UseHttp ? OtlpExportProtocol.HttpProtobuf : OtlpExportProtocol.Grpc;
    }

    private static bool IsProbe(PathString path) => path.StartsWithSegments("/health");
}
