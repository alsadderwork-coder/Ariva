using System.Collections.Concurrent;
using MassTransit;
using Microsoft.Extensions.Hosting;

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
/// <para>
/// ARV-165: it also remembers its endpoint (from any of its events) for <see cref="KafkaEndpoints"/>, which stops, after
/// the bus has stopped, an endpoint whose Stopping event never came because the bus skipped it.
/// </para>
/// </summary>
public sealed class EndpointStopping : IReceiveEndpointObserver
{
    private CancellationTokenSource _stopping = new();
    private IReceiveEndpoint _endpoint;

    /// <summary>The endpoint this observes, once it has raised an event.</summary>
    public IReceiveEndpoint Endpoint => Volatile.Read(ref _endpoint);

    /// <summary>Whether the endpoint has begun to stop since it was last ready.</summary>
    public bool IsStopping => Volatile.Read(ref _stopping).IsCancellationRequested;

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
        Remember(ready);
        var current = Volatile.Read(ref _stopping);
        if (current.IsCancellationRequested)
            Interlocked.CompareExchange(ref _stopping, new CancellationTokenSource(), current);
        return Task.CompletedTask;
    }

    public Task Stopping(ReceiveEndpointStopping stopping)
    {
        Remember(stopping);
        Signal();
        return Task.CompletedTask;
    }

    public Task Completed(ReceiveEndpointCompleted completed)
    {
        Remember(completed);
        return Task.CompletedTask;
    }

    public Task Faulted(ReceiveEndpointFaulted faulted)
    {
        Remember(faulted);
        return Task.CompletedTask;
    }

    private void Remember(ReceiveEndpointEvent endpointEvent)
    {
        if (endpointEvent?.ReceiveEndpoint is { } endpoint)
            Volatile.Write(ref _endpoint, endpoint);
    }
}

/// <summary>
/// The host's Kafka receive endpoints (ARV-165), each through its <see cref="EndpointStopping"/>, so that the endpoints the
/// bus's stop skipped can be stopped after it. MassTransit 8.5.11's <c>ReceiveEndpointCollection.StopEndpoints</c> stops only
/// endpoints that are started, ready or faulted; one whose consumer had just ended on a broker error is Completed, between
/// restarts, and is skipped, and its transport then starts a new consumer in a stopped host, which consumes with a disposed
/// service provider and holds its partition from the next host (proven by the ARV-165 baseline, backlog/progress.md).
/// <see cref="IReceiveEndpoint.Stop"/> on such an endpoint stops its transport, and with it the restarts, and raises its
/// Stopping event, so its dead-letter loops end.
/// </summary>
public sealed class KafkaEndpoints
{
    /// <summary>The bound on stopping the skipped endpoints, inside the host's shutdown timeout.</summary>
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentQueue<EndpointStopping> _endpoints = new();

    public void Add(EndpointStopping endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        _endpoints.Enqueue(endpoint);
    }

    /// <summary>Whether every endpoint added here has begun to stop: what a test waits on instead of a fixed delay (ARV-255).</summary>
    internal bool AllStopping => !_endpoints.IsEmpty && _endpoints.All(e => e.IsStopping);

    /// <summary>
    /// Stops every endpoint that has not begun to stop, within <see cref="StopTimeout"/> and <paramref name="ct"/>;
    /// returns how many it stopped and whether all of them ended in time.
    /// </summary>
    public async Task<(int Stopped, bool InTime)> StopSkippedAsync(ILogger logger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var skipped = _endpoints.Where(e => !e.IsStopping && e.Endpoint is not null).Select(e => e.Endpoint).Distinct().ToList();
        if (skipped.Count == 0)
            return (0, true);

        using var bound = new CancellationTokenSource(StopTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(bound.Token, ct);
        foreach (var endpoint in skipped)
            logger.LogWarning("Kafka endpoint {InputAddress} was not stopped with the bus (its consumer was between restarts); stopping it now", endpoint.InputAddress);
        var stops = skipped.Select(endpoint => endpoint.Stop(linked.Token)).ToList();
        try
        {
            await Task.WhenAll(stops).WaitAsync(StopTimeout + TimeSpan.FromSeconds(1), CancellationToken.None);
            return (skipped.Count, true);
        }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException)
        {
            // Its transport is stopping, so it starts no new consumer; a consumer still held (an offset commit in flight)
            // ends when the process does.
            logger.LogWarning("Kafka endpoints not stopped with the bus did not finish stopping within {Seconds:F0} s", StopTimeout.TotalSeconds);
            foreach (var stop in stops)
                _ = stop.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return (skipped.Count, false);
        }
    }
}

/// <summary>
/// Stops the Kafka endpoints the bus's stop skipped (ARV-165, <see cref="KafkaEndpoints"/>). Registered before the bus, so it
/// stops after it, and before the producer's lifetime, so a dead letter of such an endpoint can still be sent or kept.
/// </summary>
internal sealed class KafkaEndpointsLifetime(KafkaEndpoints endpoints, ILogger<KafkaEndpointsLifetime> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => endpoints.StopSkippedAsync(logger, cancellationToken);
}
