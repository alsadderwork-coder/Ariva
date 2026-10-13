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
        // ARV-166: a dead letter that cannot be sent is retried only until this endpoint begins to stop, so it never holds
        // a host open. The endpoint's own Stopping event, not the host's: EndpointStopping explains why.
        var stopping = new EndpointStopping();
        endpoint.ConnectReceiveEndpointObserver(stopping);
        // ARV-171: a dead letter Kafka did not take before the stop is kept in the outbox, so the record can complete.
        var store = context.GetService<IDeadLetterStore>();
        endpoint.UseFilter(new DeadLetterFilter(
            retries,
            context.GetRequiredService<IDeadLetterSink>(),
            context.GetRequiredService<TimeProvider>(),
            context.GetRequiredService<ILogger<DeadLetterFilter>>(),
            stopping,
            store));
        endpoint.UseMessageRetry(retry =>
        {
            retry.Exponential(
                retries,
                TimeSpan.FromMilliseconds(settings.RetryMinMilliseconds),
                TimeSpan.FromMilliseconds(settings.RetryMaxMilliseconds),
                TimeSpan.FromMilliseconds(Math.Max(1, settings.RetryMinMilliseconds)));
            // No Ignore<OperationCanceledException>: a consumer's own timeout is a failure like any other and must reach
            // the dead-letter topic. A stop does not cancel this retry (MassTransit builds it with CancellationToken.None
            // on an endpoint): the stop waits out the rest of the schedule, and the final attempt dead-letters the message
            // or keeps its letter (ARV-171), so a retry wait never leaves a faulted record behind.
        });
        endpoint.UseConsumeFilter(typeof(InboxFilter<>), context);
        endpoint.ConfigureDeadLetter(skipped => skipped.UseFilter(new UnreadableFilter(
            context.GetRequiredService<IDeadLetterSink>(),
            context.GetRequiredService<TimeProvider>(),
            context.GetRequiredService<ILogger<UnreadableFilter>>(),
            stopping,
            store)));
    }
}
