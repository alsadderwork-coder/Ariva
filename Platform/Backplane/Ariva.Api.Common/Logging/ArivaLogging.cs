using Ariva.Api.Common.Hosting;
using Ariva.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;
using Serilog.Sinks.Grafana.Loki;

namespace Ariva.Api.Common.Logging;

/// <summary>
/// The Serilog pipeline every host uses (ARV-007, AMAN parity): levels from <c>Serilog:MinimumLevel</c>, structured JSON on
/// the console, Loki and OTLP sinks when enabled in configuration, and the redaction enricher on every event.
/// Everything else is fixed in code (ARV-091, CWE-15, ASVS 5.0 V16.2.3, V16.4.3): only <c>Serilog:MinimumLevel</c> is read,
/// so no setting can add a sink (a file, or Loki over plain HTTP past <see cref="EnsureProductionTransport"/>), an enricher, a
/// filter or a type to load, and a host refuses to start when the <c>Serilog</c> section holds anything else
/// (<see cref="EnsureOnlyLevelsAreConfigured"/>), so a setting is never silently ignored either. Settings fixed in code because
/// they protect secrets or the security record: the redaction enricher (with the log protection levels and the pseudonym key
/// of <see cref="LogProtection"/>, ARV-091); Microsoft.AspNetCore.Hosting at Warning (its request-start log line carries the
/// query string, where SignalR puts access_token; Microsoft's SignalR security guidance); the security events' category at
/// Warning (<see cref="SecurityEventCategory"/>), so no level setting silences them. The request enricher
/// (<see cref="RequestLogContext"/>) reaches the events through <c>Enrich.FromLogContext</c>. The JSON file
/// (<see cref="FileSetting"/>) is for vm-local only, where the E2E run reads every host's lines; elsewhere a host refuses to
/// start with it (logs go only to the documented destinations: the console, Loki and the OTLP collector).
/// </summary>
public static class ArivaLogging
{
    public const string HostingCategory = "Microsoft.AspNetCore.Hosting";

    /// <summary>The JSON file sink's path, set only by the E2E run (vm-local).</summary>
    public const string FileSetting = "LogFile:Path";

    /// <summary>The configuration section of the levels, the only part of <c>Serilog</c> that is read.</summary>
    public const string LevelsSection = "Serilog:MinimumLevel";

    /// <summary>The security events' category, held at Warning whatever the configuration says.</summary>
    public static readonly string SecurityEventCategory = typeof(SecurityEventLog).FullName;

    /// <summary>Builds the host's pipeline.</summary>
    /// <param name="logger">The configuration to fill.</param>
    /// <param name="configuration">The host's configuration.</param>
    /// <param name="environment">The host's own environment name.</param>
    /// <param name="metrics">The security metrics, where the redaction enricher counts a property it had to replace; none in tests.</param>
    /// <returns>The same configuration.</returns>
    public static LoggerConfiguration Configure(LoggerConfiguration logger, IConfiguration configuration, string environment, SecurityEventMetrics metrics = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(configuration);

        var application = ApplicationName(configuration);
        EnsureOnlyLevelsAreConfigured(configuration);
        EnsureFileSinkIsLocal(configuration, environment);
        var protection = LogProtection.From(configuration, environment);

        ReadLevels(logger, configuration);
        logger
            .MinimumLevel.Override(HostingCategory, LogEventLevel.Warning)
            .MinimumLevel.Override(SecurityEventCategory, LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", application)
            .Enrich.WithProperty("Environment", environment ?? "unknown")
            .Enrich.With(new RedactionEnricher(protection, metrics))
            .WriteTo.Async(sink => sink.Console(new JsonFormatter(renderMessage: true)));

        var file = configuration[FileSetting];
        if (!string.IsNullOrWhiteSpace(file))
            logger.WriteTo.File(new JsonFormatter(renderMessage: true), file, shared: true);

        // ARV-082a: in production the logs leave a host over TLS only, to Loki as to the OTLP collector.
        EnsureProductionTransport(configuration);
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
    /// ARV-091 (CWE-15, ASVS 5.0 V16.2.3, V16.4.3): the <c>Serilog</c> section may hold <c>MinimumLevel</c> only. Sinks
    /// (<c>WriteTo</c>, <c>AuditTo</c>), assemblies (<c>Using</c>), enrichers, filters, destructuring and level switches are
    /// Ariva's code, never configuration: a setting could otherwise add a file in a pod, send logs to Loki over plain HTTP in
    /// production, drop the security events or load a type. A host refuses to start when any other key is set.
    /// </summary>
    /// <param name="configuration">The host's configuration.</param>
    /// <exception cref="InvalidOperationException">The section holds another key; the message names the key, never its value.</exception>
    public static void EnsureOnlyLevelsAreConfigured(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        foreach (var child in configuration.GetSection("Serilog").GetChildren())
        {
            if (!string.Equals(child.Key, "MinimumLevel", StringComparison.OrdinalIgnoreCase) && HasValue(child))
                throw new InvalidOperationException(
                    $"Serilog:{child.Key} is not read: Ariva's log sinks, enrichers and filters are fixed in code (the console, Loki and Otlp sections, and LogFile:Path in vm-local). Only {LevelsSection} may be set. Remove the setting.");
        }

        foreach (var child in configuration.GetSection(LevelsSection).GetChildren())
        {
            if (!string.Equals(child.Key, "Default", StringComparison.OrdinalIgnoreCase) && !string.Equals(child.Key, "Override", StringComparison.OrdinalIgnoreCase) &&
                HasValue(child))
                throw new InvalidOperationException($"{LevelsSection}:{child.Key} is not read: set {LevelsSection}:Default and {LevelsSection}:Override only.");
        }
    }

    /// <summary>
    /// The levels of <see cref="LevelsSection"/>: a level name (<c>"Warning"</c>), or <c>Default</c> and <c>Override</c> with a
    /// level per category, as Serilog's own settings reader takes them.
    /// </summary>
    /// <exception cref="InvalidOperationException">A value is not a Serilog level name.</exception>
    private static void ReadLevels(LoggerConfiguration logger, IConfiguration configuration)
    {
        var section = configuration.GetSection(LevelsSection);
        if (!string.IsNullOrWhiteSpace(section.Value))
        {
            logger.MinimumLevel.Is(LevelOf(section.Value, LevelsSection));
            return;
        }

        if (!string.IsNullOrWhiteSpace(section["Default"]))
            logger.MinimumLevel.Is(LevelOf(section["Default"], $"{LevelsSection}:Default"));
        foreach (var category in section.GetSection("Override").GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(category.Value))
                logger.MinimumLevel.Override(category.Key, LevelOf(category.Value, $"{LevelsSection}:Override:{category.Key}"));
        }
    }

    private static LogEventLevel LevelOf(string value, string key) =>
        value.Trim().All(char.IsAsciiLetter) && Enum.TryParse<LogEventLevel>(value.Trim(), ignoreCase: true, out var level) && Enum.IsDefined(level)
            ? level
            : throw new InvalidOperationException($"{key} must be one of Verbose, Debug, Information, Warning, Error or Fatal.");

    private static bool HasValue(IConfigurationSection section) =>
        !string.IsNullOrEmpty(section.Value) || section.GetChildren().Any(HasValue);

    /// <summary>
    /// ARV-091 (ASVS 5.0 V16.2.3): the JSON file sink is for vm-local only, where the E2E run reads the hosts' lines. In a pod
    /// a file would be a log destination outside the inventory, with no access control or retention of its own, so a host
    /// in any other environment refuses to start when <see cref="FileSetting"/> is set.
    /// </summary>
    /// <param name="configuration">The host's configuration.</param>
    /// <param name="environment">The host's own environment name.</param>
    /// <exception cref="InvalidOperationException">The path is set outside vm-local.</exception>
    public static void EnsureFileSinkIsLocal(IConfiguration configuration, string environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!string.IsNullOrWhiteSpace(configuration[FileSetting]) && environment != ArivaEnvironment.VmLocal)
            throw new InvalidOperationException(
                $"{FileSetting} is for vm-local only (the E2E run); a host in {environment ?? "this environment"} logs to the console, Loki or the OTLP collector. Remove the setting.");
    }

    /// <summary>
    /// ARV-082a (CWE-319): in k8s-prd a host does not start when its logs would cross the network in clear: an enabled Loki
    /// whose address is not https, or an OTLP endpoint (Otlp:Endpoint or OTEL_EXPORTER_OTLP_ENDPOINT, which the OTLP sink
    /// reads last) that is not https.
    /// </summary>
    public static void EnsureProductionTransport(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        OtlpSettings.EnsureProductionTransport(configuration);
        if (configuration["Application:Environment"] == "k8s-prd" && bool.TryParse(configuration["Loki:Enabled"], out var loki) && loki &&
            (!Uri.TryCreate(configuration["Loki:Uri"], UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Loki:Uri must be https in k8s-prd: the logs cross the network encrypted.");
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
