using MassTransit;

namespace Ariva.Infra.Messaging.Kafka;

/// <summary>
/// The moment a receive endpoint begins to stop (ARV-166), for the loops that would otherwise retry for ever inside a
/// message the endpoint is handling: the dead-letter filters' (<see cref="DeadLetterFilter"/>, <see cref="UnreadableFilter"/>).
/// MassTransit waits for the messages in hand before an endpoint stops and does not cancel their tokens, so a dead letter
/// that can never be sent used to keep the endpoint, the bus, the host and the process from stopping.
/// <para>
/// The endpoint's own <see cref="IReceiveEndpointObserver.Stopping"/> is the signal, not the host's ApplicationStopping or
/// the bus's PreStop. <c>ReceiveEndpoint.Stop</c> raises it after the bus has chosen the endpoint to stop and just before
/// it stops the endpoint's transport, so the transport is stopping by the time the message ends. Signalled earlier, the
/// message could end before the bus chose its endpoints: a Kafka endpoint whose consumer had ended on a broker error then
/// reported Completed, MassTransit 8.5.11's <c>ReceiveEndpointCollection.StopEndpoints</c> skipped it (it stops only
/// started, ready or faulted endpoints), and its transport started a new consumer a second later, in a host already
/// stopped, which took the partition from the next host (found with DeadLetterShutdownTests, backlog/progress.md).
/// </para>
/// <para>
/// One per endpoint, connected by <see cref="ConsumePipeline.Configure"/>. An endpoint that is ready again after a stop
/// gets a token that is not cancelled. The sources hold no timer or wait handle, so a replaced one needs no disposal.
/// </para>
/// </summary>
public sealed class EndpointStopping : IReceiveEndpointObserver
{
    private CancellationTokenSource _stopping = new();

    /// <summary>Cancelled once the endpoint begins to stop.</summary>
    public CancellationToken Token => Volatile.Read(ref _stopping).Token;

    /// <summary>The endpoint is stopping: every loop waiting on <see cref="Token"/> ends.</summary>
    public void Signal()
    {
        try
        {
            Volatile.Read(ref _stopping).Cancel();
        }
        catch (AggregateException)
        {
            // A callback registered on the token failed (they are the loops' own linked tokens and delays); the token is
            // cancelled all the same, and the endpoint's stop must not fail on it.
        }
    }

    public Task Ready(ReceiveEndpointReady ready)
    {
        var current = Volatile.Read(ref _stopping);
        if (current.IsCancellationRequested)
            Interlocked.CompareExchange(ref _stopping, new CancellationTokenSource(), current);
        return Task.CompletedTask;
    }

    public Task Stopping(ReceiveEndpointStopping stopping)
    {
        Signal();
        return Task.CompletedTask;
    }

    public Task Completed(ReceiveEndpointCompleted completed) => Task.CompletedTask;

    public Task Faulted(ReceiveEndpointFaulted faulted) => Task.CompletedTask;
}
