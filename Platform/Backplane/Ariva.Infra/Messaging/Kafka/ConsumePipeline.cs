using MassTransit;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.Infra.Messaging.Kafka;

/// <summary>
/// The consume pipe of every Ariva endpoint (ADR-0018), from the outside in: bounded exponential retry, dead letter
/// (acts on the final attempt only), inbox and unit of work (a fresh scope per attempt), consumer. The same method
/// configures rider topic endpoints and the in-memory endpoints of the tests, so the tests prove what the rider runs.
/// </summary>
public static class ConsumePipeline
{
    public static void Configure(IReceiveEndpointConfigurator endpoint, IRegistrationContext context, ConsumerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settings);

        var retries = Math.Max(0, settings.RetryCount);
        endpoint.UseFilter(new DeadLetterFilter(
            retries,
            context.GetRequiredService<IDeadLetterSink>(),
            context.GetRequiredService<TimeProvider>(),
            context.GetRequiredService<ILogger<DeadLetterFilter>>()));
        endpoint.UseMessageRetry(retry =>
        {
            retry.Exponential(
                retries,
                TimeSpan.FromMilliseconds(settings.RetryMinMilliseconds),
                TimeSpan.FromMilliseconds(settings.RetryMaxMilliseconds),
                TimeSpan.FromMilliseconds(Math.Max(1, settings.RetryMinMilliseconds)));
            // No Ignore<OperationCanceledException>: a consumer's own timeout is a failure like any other and must reach
            // the dead-letter topic; MassTransit stops retrying by itself when the endpoint is shutting down.
        });
        endpoint.UseConsumeFilter(typeof(InboxFilter<>), context);
    }
}
