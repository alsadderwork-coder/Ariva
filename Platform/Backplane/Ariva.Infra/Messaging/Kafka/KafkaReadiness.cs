using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ariva.Infra.Messaging.Kafka;

/// <summary>
/// Readiness of the Kafka cluster as this host's producer sees it (ARV-165), registered as <c>kafka</c>, tagged
/// <c>ready</c>, failure status Degraded: a broker answers a metadata request through the producer's own handle within
/// <see cref="ProbeTimeout"/>, and the producer is started and not in the gap after a fatal client error.
/// <para>
/// MassTransit's bus check cannot be relied on for this: its consumer stays Ready while an offset commit is in flight,
/// and MassTransit 8.5.11 commits with the synchronous <c>Commit</c>, which waits for a coordinator a broker that is away
/// never gives (45 seconds in the ARV-156 diagnostic, for good in one loop run), so the bus reported Healthy through an
/// outage. This check asks the brokers directly, so every readiness request that starts after the brokers became
/// unreachable reports Degraded within the probe timeout, whatever MassTransit's consumers are doing.
/// </para>
/// <para>
/// Degraded, not Unhealthy (the owner's ARV-125 decision): a broker outage raises an alert and the readiness answer stays
/// 200, since every host keeps working without Kafka (the outbox keeps events, Ingest answers 503 itself) and taking pods
/// out of the service would do more harm than good. The result carries no detail (CWE-200).
/// </para>
/// </summary>
public sealed class KafkaReadiness(KafkaProduce produce) : IHealthCheck
{
    /// <summary>How long a broker has to answer the metadata request.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        bool answered;
        try
        {
            // The probe ends within its timeout; the wait has a second more, in case the client does not keep to it.
            answered = await produce.BrokersAnswerAsync(ProbeTimeout).WaitAsync(ProbeTimeout + TimeSpan.FromSeconds(1), cancellationToken);
        }
        catch (TimeoutException)
        {
            answered = false;
        }

        return answered ? HealthCheckResult.Healthy() : new HealthCheckResult(context.Registration.FailureStatus);
    }
}
