using Microsoft.Extensions.Configuration;

namespace Ariva.ServiceDefaults;

/// <summary>
/// The OTLP target, shared by the trace, metric and log exporters and by Serilog's sink in the Backplane hosts. Enabled
/// by Otlp:Enabled or by the standard OTEL_EXPORTER_OTLP_ENDPOINT variable (set by the Helm chart and by the Aspire
/// AppHost); off when neither is set, so an unreachable collector never affects startup. Other standard variables
/// (OTEL_EXPORTER_OTLP_HEADERS, for the Aspire dashboard's key) are read by the exporters themselves.
/// </summary>
public sealed record OtlpSettings(bool Enabled, Uri Endpoint, bool UseHttp)
{
    private const string DefaultEndpoint = "http://localhost:4317";

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
}
