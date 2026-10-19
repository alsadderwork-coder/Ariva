using Microsoft.Extensions.Configuration;

namespace Ariva.ServiceDefaults;

/// <summary>
/// The OTLP target, shared by the trace, metric and log exporters and by Serilog's sink in the Backplane hosts. Enabled
/// by Otlp:Enabled or by the standard OTEL_EXPORTER_OTLP_ENDPOINT variable (set by the Helm chart and by the Aspire
/// AppHost); off when neither is set, so an unreachable collector never affects startup. Other standard variables
/// (OTEL_EXPORTER_OTLP_HEADERS, for the Aspire dashboard's key or a collector's sender credential) are read by the exporters
/// themselves; outside vm-local the headers come from the environment and go over https only (ARV-091a,
/// <see cref="EnsureHeadersAreProtected"/>). Over http/protobuf each signal goes to its own path (<see cref="EndpointFor"/>).
/// </summary>
public sealed record OtlpSettings(bool Enabled, Uri Endpoint, bool UseHttp)
{
    /// <summary>The traces' path after the base endpoint over http/protobuf (the OTLP specification).</summary>
    public const string TracesPath = "v1/traces";

    /// <summary>The metrics' path after the base endpoint over http/protobuf.</summary>
    public const string MetricsPath = "v1/metrics";

    /// <summary>The logs' path after the base endpoint over http/protobuf.</summary>
    public const string LogsPath = "v1/logs";

    /// <summary>The standard endpoint variable (set by the Helm chart and by the Aspire AppHost).</summary>
    public const string EndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>
    /// The OTLP header variables the exporters and Serilog's OTLP sink read: the shared one and one per signal. A site whose
    /// collector authenticates senders puts its credential there (ARV-091a), so they are a secret (<see cref="EnsureHeadersAreProtected"/>).
    /// </summary>
    public static readonly IReadOnlyList<string> HeaderVariables =
        ["OTEL_EXPORTER_OTLP_HEADERS", "OTEL_EXPORTER_OTLP_TRACES_HEADERS", "OTEL_EXPORTER_OTLP_METRICS_HEADERS", "OTEL_EXPORTER_OTLP_LOGS_HEADERS"];

    private const string DefaultEndpoint = "http://localhost:4317";

    private const string VmLocal = "vm-local";

    public static OtlpSettings From(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var fromEnvironment = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var enabled = (bool.TryParse(configuration["Otlp:Enabled"], out var flag) && flag) || !string.IsNullOrWhiteSpace(fromEnvironment);
        var endpointText = configuration["Otlp:Endpoint"];
        if (string.IsNullOrWhiteSpace(endpointText))
            endpointText = string.IsNullOrWhiteSpace(fromEnvironment) ? DefaultEndpoint : fromEnvironment;
        var protocol = configuration["Otlp:Protocol"] ?? configuration["OTEL_EXPORTER_OTLP_PROTOCOL"] ?? "grpc";

        return new OtlpSettings(
            enabled && Uri.TryCreate(endpointText, UriKind.Absolute, out _),
            Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) ? endpoint : new Uri(DefaultEndpoint),
            protocol.StartsWith("http", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Where an exporter sends one signal (ARV-091a). Over http/protobuf <see cref="Endpoint"/> is the base address and each
    /// signal has its own path after it (<see cref="TracesPath"/>, <see cref="MetricsPath"/>, <see cref="LogsPath"/>), as the
    /// OTLP specification has it for OTEL_EXPORTER_OTLP_ENDPOINT. The OpenTelemetry exporter appends that path only to an
    /// endpoint it read from the variable itself, never to one set in code, so Ariva appends it. A base with a path keeps it
    /// (<c>https://collector/otlp</c> gives <c>https://collector/otlp/v1/traces</c>). Over gRPC the endpoint is used as given:
    /// the gRPC method names the signal.
    /// </summary>
    /// <param name="signalPath">One of <see cref="TracesPath"/>, <see cref="MetricsPath"/> or <see cref="LogsPath"/>.</param>
    /// <returns>The address the signal's exporter uses.</returns>
    public Uri EndpointFor(string signalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signalPath);
        if (!UseHttp)
            return Endpoint;
        return new UriBuilder(Endpoint) { Path = $"{Endpoint.AbsolutePath.TrimEnd('/')}/{signalPath}" }.Uri;
    }

    /// <summary>Why this export is not fit for k8s-prd, or null (ARV-082a, CWE-319): with OTLP on, the collector is reached over https.</summary>
    public string ProductionTransportProblem() =>
        Enabled && Endpoint.Scheme != Uri.UriSchemeHttps
            ? "The OTLP endpoint must be https in k8s-prd (Otlp:Endpoint or OTEL_EXPORTER_OTLP_ENDPOINT): traces, metrics and logs cross the network encrypted."
            : null;

    /// <summary>
    /// In production telemetry crosses the network encrypted, or the host does not start (as Kafka, PostgreSQL and Redis): the
    /// endpoint the exporters use, and the OTEL_EXPORTER_OTLP_ENDPOINT variable too when it is set, since some exporters (the
    /// Serilog OTLP sink) read it after Ariva's own setting.
    /// </summary>
    public static void EnsureProductionTransport(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration["Application:Environment"] != "k8s-prd")
            return;
        if (From(configuration).ProductionTransportProblem() is { } problem)
            throw new InvalidOperationException(problem);
        var variable = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (!string.IsNullOrWhiteSpace(variable) && (!Uri.TryCreate(variable, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("OTEL_EXPORTER_OTLP_ENDPOINT must be https in k8s-prd: traces, metrics and logs cross the network encrypted.");
    }

    /// <summary>
    /// ARV-091a (CWE-319, CWE-522): the OTLP headers (<see cref="HeaderVariables"/>) carry a collector's sender credential, as
    /// <c>Loki:Username</c> and <c>Loki:Password</c> do for Loki. Outside vm-local, while OTLP is on, they come from the
    /// environment (a Kubernetes Secret), never from a configuration file, and go over https only: Ariva's endpoint, and
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> too when it is set, since some exporters read it after Ariva's setting. No message
    /// names a value. vm-local is exempt: the Aspire AppHost gives its dashboard's key this way, over http on the machine.
    /// </summary>
    /// <param name="configuration">The host's configuration.</param>
    /// <param name="environment">The host's environment; null takes the rules of every environment but vm-local.</param>
    /// <exception cref="InvalidOperationException">A header variable comes from a file, or would go over http.</exception>
    public static void EnsureHeadersAreProtected(IConfiguration configuration, string environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.Equals(environment, VmLocal, StringComparison.Ordinal) || !From(configuration).Enabled)
            return;
        var set = HeaderVariables.Where(name => !string.IsNullOrWhiteSpace(configuration[name])).ToList();
        if (set.Count == 0)
            return;

        var fromAFile = set.Where(name => ConfigurationOrigin.IsFromAFile(configuration, name)).ToList();
        if (fromAFile.Count > 0)
            throw new InvalidOperationException(
                $"{string.Join(", ", fromAFile)} comes from a configuration file; outside vm-local the OTLP headers carry a collector's credential, set from a Kubernetes Secret in the environment.");
        if (!OverHttps(configuration))
            throw new InvalidOperationException(
                $"{string.Join(", ", set)} is sent over https only: outside vm-local, the OTLP endpoint (Otlp:Endpoint or OTEL_EXPORTER_OTLP_ENDPOINT) must be https when the OTLP headers are set.");
    }

    /// <summary>Whether both addresses an exporter may use are https: Ariva's endpoint, and the variable when it is set.</summary>
    private static bool OverHttps(IConfiguration configuration)
    {
        var variable = configuration[EndpointVariable];
        return From(configuration).Endpoint.Scheme == Uri.UriSchemeHttps &&
               (string.IsNullOrWhiteSpace(variable) || (Uri.TryCreate(variable, UriKind.Absolute, out var endpoint) && endpoint.Scheme == Uri.UriSchemeHttps));
    }
}
