using System.Net.Http;
using Ariva.Api.Common.Hosting;
using Ariva.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Json;
using Serilog.Sinks.Grafana.Loki;
using Serilog.Sinks.Grafana.Loki.HttpClients;

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
/// start with it (logs go only to the documented destinations: the console, Loki and the OTLP collector). In k8s-prd the logs
/// also leave the pod (ARV-091a, ASVS 5.0 V16.4.3): a host ships them over TLS to Loki or to the OTLP collector, besides the
/// console, and hashes with the deployment's pseudonym key, or it does not start (<see cref="EnsureProductionCollector"/>,
/// <see cref="LogProtection.From"/>). The one-shot commands (the migration and topics Jobs, the operator commands) never build
/// this logger, so they ask for neither.
/// </summary>
public static class ArivaLogging
{
    public const string HostingCategory = "Microsoft.AspNetCore.Hosting";

    /// <summary>The JSON file sink's path, set only by the E2E run (vm-local).</summary>
    public const string FileSetting = "LogFile:Path";

    /// <summary>The configuration section of the levels, the only part of <c>Serilog</c> that is read.</summary>
    public const string LevelsSection = "Serilog:MinimumLevel";

    /// <summary>Turns the Loki sink on, with <see cref="LokiUriSetting"/>.</summary>
    public const string LokiEnabledSetting = "Loki:Enabled";

    /// <summary>Loki's address (the sink adds the push path); https in k8s-prd.</summary>
    public const string LokiUriSetting = "Loki:Uri";

    /// <summary>
    /// The user name Ariva sends to Loki with every batch (HTTP basic authentication, ARV-091a), for a Loki or a gateway in
    /// front of it that authenticates senders; set with <see cref="LokiPasswordSetting"/> or not at all.
    /// </summary>
    public const string LokiUsernameSetting = "Loki:Username";

    /// <summary>The password that goes with <see cref="LokiUsernameSetting"/>. A secret: from the environment outside vm-local, never logged.</summary>
    public const string LokiPasswordSetting = "Loki:Password";

    /// <summary>The standard OTLP endpoint variable, which turns the OTLP export on and which the OTLP sink reads last.</summary>
    private const string OtlpEndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>Why a k8s-prd host does not start without a collector (ARV-091a); it names the settings, never a value.</summary>
    public const string NoCollector =
        "No log collector in k8s-prd: a host ships its logs over TLS, besides the console, to Loki (Loki:Enabled true and an https Loki:Uri) or to the OTLP collector (OTEL_EXPORTER_OTLP_ENDPOINT, or Otlp:Enabled true with Otlp:Endpoint, at an https address). Set one of them.";

    /// <summary>The security events' category, held at Warning whatever the configuration says.</summary>
    public static readonly string SecurityEventCategory = typeof(SecurityEventLog).FullName;

    /// <summary>Builds the host's pipeline.</summary>
    /// <param name="logger">The configuration to fill.</param>
    /// <param name="configuration">The host's configuration.</param>
    /// <param name="environment">The host's own environment name.</param>
    /// <param name="metrics">The security metrics, where the redaction enricher counts a property it had to replace; none in tests.</param>
    /// <param name="services">
    /// The host's services, for the collector clients <see cref="AddCollectorClients"/> registered (ARV-200); needed only when a
    /// collector's CA file is set or a collector is reached over https (ARV-226).
    /// </param>
    /// <returns>The same configuration.</returns>
    public static LoggerConfiguration Configure(LoggerConfiguration logger, IConfiguration configuration, string environment, SecurityEventMetrics metrics = null,
        IServiceProvider services = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(configuration);

        var application = ApplicationName(configuration);
        // Every refusal comes before any sink exists, so a host that does not start has opened no file and no connection.
        EnsureOnlyLevelsAreConfigured(configuration);
        EnsureFileSinkIsLocal(configuration, environment);
        // ARV-082a: in production the logs leave a host over TLS only, to Loki as to the OTLP collector; ARV-091a: and they
        // leave it, to one of them at least.
        EnsureProductionTransport(configuration);
        EnsureProductionCollector(configuration, environment);
        // ARV-091a: the OTLP headers (a collector's sender credential) come from the environment and go over https only,
        // outside vm-local, as the Loki credentials below.
        OtlpSettings.EnsureHeadersAreProtected(configuration, environment);
        // ARV-091a: in production with the deployment's key, so a day's pseudonyms agree across the pods.
        var protection = LogProtection.From(configuration, environment);
        var lokiUri = LokiUri(configuration);
        var lokiCredentials = lokiUri is null ? null : LokiCredentialsFrom(configuration, environment, lokiUri);
        // ARV-200: a collector whose certificate comes from the site PKI is reached through its pinned client; ARV-226: one reached
        // over https without it, through a client pinned to the image's public CAs; either downloads no certificate.
        var lokiClient = lokiUri is not null && (CollectorTrust.RootsFrom(configuration, CollectorTrust.LokiSetting) is not null || LokiOverHttps(configuration))
            ? PinnedLokiClient(lokiUri, configuration, services)
            : null;
        var otlp = OtlpSettings.From(configuration);
        var otlpHandler = otlp.Enabled && (CollectorTrust.RootsFrom(configuration, CollectorTrust.OtlpSetting) is not null || OtlpOverHttps(configuration))
            ? PinnedOtlpHandler(otlp, configuration, services)
            : null;

        ReadLevels(logger, configuration);
        logger
            .MinimumLevel.Override(HostingCategory, LogEventLevel.Warning)
            .MinimumLevel.Override(SecurityEventCategory, LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", application)
            .Enrich.WithProperty("Environment", environment ?? "unknown")
            .Enrich.With(new RedactionEnricher(protection, metrics))
            // Always on, beside any collector: the kubelet keeps the pod's lines (log inventory, section 2.2).
            .WriteTo.Async(sink => sink.Console(new JsonFormatter(renderMessage: true)));

        var file = configuration[FileSetting];
        if (!string.IsNullOrWhiteSpace(file))
            logger.WriteTo.File(new JsonFormatter(renderMessage: true), file, shared: true);

        if (lokiUri is not null)
        {
            logger.WriteTo.GrafanaLoki(
                lokiUri.ToString(),
                labels:
                [
                    new LokiLabel { Key = "app", Value = application },
                    new LokiLabel { Key = "environment", Value = environment ?? "unknown" },
                    new LokiLabel { Key = "log_type", Value = "application" }
                ],
                propertiesAsLabels: ["level"],
                credentials: lokiCredentials,
                httpClient: lokiClient);
        }

        if (otlp.Enabled)
        {
            logger.WriteTo.OpenTelemetry(
                options =>
                {
                    options.Endpoint = otlp.Endpoint.ToString();
                    if (otlpHandler is not null)
                        options.HttpMessageHandler = otlpHandler;
                    options.Protocol = otlp.UseHttp
                        ? Serilog.Sinks.OpenTelemetry.OtlpProtocol.HttpProtobuf
                        : Serilog.Sinks.OpenTelemetry.OtlpProtocol.Grpc;
                    options.ResourceAttributes = new Dictionary<string, object>
                    {
                        ["service.name"] = application,
                        ["service.namespace"] = "ariva",
                        ["deployment.environment"] = environment ?? "unknown"
                    };
                },
                // ARV-091a: the sink then applies the standard variables (OTEL_EXPORTER_OTLP_ENDPOINT, _PROTOCOL, _HEADERS,
                // OTEL_RESOURCE_ATTRIBUTES, OTEL_SERVICE_NAME). It reads them from the host's configuration, which holds the
                // environment, rather than from the process environment, so it ships to exactly the endpoint the guards above
                // judged, even when a command line setting replaced the environment's value.
                getConfigurationVariable: name => configuration[name]);
        }

        return logger;
    }

    /// <summary>
    /// ARV-091a (ASVS 5.0 V16.4.3, CWE-778): in k8s-prd a host's logs leave its pod for a logically separate store, over TLS:
    /// Loki (<see cref="LokiEnabledSetting"/> true with an https <see cref="LokiUriSetting"/>) or the OTLP collector (the export
    /// on, by <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> or by <c>Otlp:Enabled</c>, at an https endpoint, the variable too when it is
    /// set, since the sink reads it last). The console stays on beside it. A host with neither does not start: on the console
    /// alone its lines would last only as long as the pod and the node's rotation. Neither <c>Otlp:Endpoint</c> alone nor
    /// <c>OTEL_EXPORTER_OTLP_LOGS_ENDPOINT</c> turns an export on (the sink does not read the latter). Elsewhere a collector is
    /// the environment's choice. The one-shot commands never build the host's logger, so this never applies to them.
    /// </summary>
    /// <param name="configuration">The host's configuration.</param>
    /// <param name="environment">The host's own environment name.</param>
    /// <exception cref="InvalidOperationException">k8s-prd ships the logs nowhere; the message (<see cref="NoCollector"/>) names the settings.</exception>
    public static void EnsureProductionCollector(IConfiguration configuration, string environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (IsProduction(configuration, environment) && !ShipsToLokiOverTls(configuration) && !ShipsToOtlpOverTls(configuration))
            throw new InvalidOperationException(NoCollector);
    }

    /// <summary>
    /// Whether these settings are production's: the host's own environment, or <c>Application:Environment</c>, names k8s-prd
    /// (the host checks at start that both agree, ARV-098; either one is enough here, so a caller that passes no environment
    /// still gets production's rules).
    /// </summary>
    internal static bool IsProduction(IConfiguration configuration, string environment) =>
        string.Equals(environment, ArivaEnvironment.K8sPrd, StringComparison.Ordinal) ||
        string.Equals(configuration["Application:Environment"], ArivaEnvironment.K8sPrd, StringComparison.Ordinal);

    /// <summary>
    /// ARV-200: the log sinks' pinned clients for a collector whose CA file is set (<see cref="CollectorClients"/>), and ARV-226: for
    /// one reached over https without it, registered for the logger and disposed by the host after it. A CA file that cannot serve stops the host here, before any sink exists.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="configuration">The host's configuration.</param>
    public static void AddCollectorClients(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        // Through a factory, so the host owns it. The logger resolves it while it is built, so it is made before the logger and,
        // as the host disposes in the reverse order of creation, disposed after it: the logger's last batch still goes out.
        if (CollectorClients.From(configuration) is { } clients)
            services.AddSingleton(_ => clients);
    }

    /// <summary>The Loki sink's pinned client (ARV-200, ARV-226): with a CA file, over https only.</summary>
    private static LokiHttpClient PinnedLokiClient(Uri lokiUri, IConfiguration configuration, IServiceProvider services)
    {
        if (CollectorTrust.RootsFrom(configuration, CollectorTrust.LokiSetting) is not null)
            CollectorTrust.EnsureHttps(lokiUri);
        return new LokiHttpClient(PinnedClients(services, CollectorTrust.LokiClient, CollectorTrust.LokiSetting).CreateClient(CollectorTrust.LokiClient));
    }

    /// <summary>The OTLP sink's pinned handler (ARV-200, ARV-226): with a CA file, over https only.</summary>
    private static HttpMessageHandler PinnedOtlpHandler(OtlpSettings otlp, IConfiguration configuration, IServiceProvider services)
    {
        if (CollectorTrust.RootsFrom(configuration, CollectorTrust.OtlpSetting) is not null)
            CollectorTrust.EnsureHttps(otlp, configuration);
        return PinnedClients(services, CollectorTrust.OtlpLogsClient, CollectorTrust.OtlpSetting).CreateHandler(CollectorTrust.OtlpLogsClient);
    }

    /// <summary>
    /// True when the Loki sink is on and reaches Loki over https: its client is then pinned, to the site CA or to the image's
    /// public CAs (ARV-226).
    /// </summary>
    internal static bool LokiOverHttps(IConfiguration configuration) => LokiUri(configuration) is { } uri && CollectorTrust.IsHttps(uri.AbsoluteUri);

    /// <summary>
    /// True when the OTLP export is on and may reach its collector over https (its endpoint, or the variable the sink reads last):
    /// the sink's handler is then pinned, to the site CA or to the image's public CAs (ARV-226).
    /// </summary>
    internal static bool OtlpOverHttps(IConfiguration configuration) =>
        OtlpSettings.From(configuration) is { Enabled: true } otlp &&
        (CollectorTrust.IsHttps(otlp.Endpoint.AbsoluteUri) || CollectorTrust.IsHttps(configuration[OtlpSettings.EndpointVariable]));

    /// <summary>
    /// The host's pinned collector clients, which must hold <paramref name="client"/>: a client of any other origin would trust any
    /// public CA, or download certificates while it builds the collector's chain.
    /// </summary>
    private static CollectorClients PinnedClients(IServiceProvider services, string client, string setting) =>
        services?.GetService<CollectorClients>() is { } clients && clients.IsPinned(client)
            ? clients
            : throw new InvalidOperationException(
                $"{setting} is set or the collector is reached over https, but the logger was built without the host's pinned collector clients (AddCollectorClients).");

    /// <summary>Loki's address when the Loki sink is on, otherwise null.</summary>
    private static Uri LokiUri(IConfiguration configuration) =>
        bool.TryParse(configuration[LokiEnabledSetting], out var enabled) && enabled && Uri.TryCreate(configuration[LokiUriSetting], UriKind.Absolute, out var uri)
            ? uri
            : null;

    private static bool ShipsToLokiOverTls(IConfiguration configuration) => LokiUri(configuration) is { Scheme: "https" };

    /// <summary>The OTLP sink is on and both addresses it may use are https: Ariva's endpoint, and the variable the sink reads last.</summary>
    private static bool ShipsToOtlpOverTls(IConfiguration configuration)
    {
        var otlp = OtlpSettings.From(configuration);
        var variable = configuration[OtlpEndpointVariable];
        return otlp.Enabled && otlp.Endpoint.Scheme == Uri.UriSchemeHttps &&
               (string.IsNullOrWhiteSpace(variable) || (Uri.TryCreate(variable, UriKind.Absolute, out var endpoint) && endpoint.Scheme == Uri.UriSchemeHttps));
    }

    /// <summary>
    /// ARV-091a: the Loki sender credentials, for a site whose Loki (or the gateway in front of it) authenticates senders: the
    /// sink sends them with every batch as HTTP basic authentication. Both settings or neither; a user name without a colon
    /// (RFC 7617). Outside vm-local they are a secret: from the environment (a Kubernetes Secret), never from a configuration
    /// file, and sent over https only, so they never cross the network in clear. No message names a value, and the redaction
    /// enricher omits a property named Password in any case.
    /// </summary>
    /// <returns>The credentials, or null when neither setting is set.</returns>
    /// <exception cref="InvalidOperationException">Only one is set, the user name holds a colon, or outside vm-local they come
    /// from a file or would go over http.</exception>
    private static LokiCredentials LokiCredentialsFrom(IConfiguration configuration, string environment, Uri lokiUri)
    {
        var username = configuration[LokiUsernameSetting];
        var password = configuration[LokiPasswordSetting];
        var hasUsername = !string.IsNullOrWhiteSpace(username);
        var hasPassword = !string.IsNullOrWhiteSpace(password);
        if (!hasUsername && !hasPassword)
            return null;

        if (!hasUsername || !hasPassword)
            throw new InvalidOperationException($"{LokiUsernameSetting} and {LokiPasswordSetting} are set together, or neither is set.");
        if (username.Contains(':', StringComparison.Ordinal))
            throw new InvalidOperationException($"{LokiUsernameSetting} must not contain a colon (HTTP basic authentication).");
        if (environment != ArivaEnvironment.VmLocal)
        {
            if (LogProtection.ComesFromAFile(configuration, LokiUsernameSetting) || LogProtection.ComesFromAFile(configuration, LokiPasswordSetting))
                throw new InvalidOperationException(
                    $"{LokiUsernameSetting} and {LokiPasswordSetting} come from a configuration file; outside vm-local they are a secret, set from a Kubernetes Secret in the environment.");
            if (lokiUri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException(
                    $"{LokiUsernameSetting} and {LokiPasswordSetting} are sent over https only: outside vm-local, {LokiUriSetting} must be https when they are set.");
        }

        return new LokiCredentials { Login = username, Password = password };
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
        if (configuration["Application:Environment"] == "k8s-prd" && bool.TryParse(configuration[LokiEnabledSetting], out var loki) && loki &&
            (!Uri.TryCreate(configuration[LokiUriSetting], UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
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
