using System.Text.Json;
using Ariva.Core.Messaging;
using Ariva.Infra.Messaging;
using Ariva.Infra.Messaging.Kafka;
using Confluent.Kafka;

namespace Ariva.Infra.Streaming;

/// <summary>One record for a stateful stream worker: the value already read from JSON, with where it came from.</summary>
public sealed record StreamRecord<TValue>(string Topic, int Partition, long Offset, string Key, TValue Value, DateTime Timestamp);

/// <summary>
/// What Ariva.Api.Stream implements per topic (ADR-0018). State is kept per partition: loaded when a partition is
/// assigned, made durable by <see cref="CheckpointAsync"/>, dropped when a partition is lost. Offsets are committed
/// only right after a checkpoint, so a committed offset never points past state that was not persisted; anything after
/// the last checkpoint is replayed, and <see cref="HandleAsync"/> must tolerate that (upserts by revision, event ids).
/// </summary>
public interface IPartitionHandler<TValue>
{
    Task OnAssignedAsync(string topic, IReadOnlyList<int> partitions, CancellationToken ct);

    /// <summary>Make the state of these partitions durable. Called periodically and before a clean revoke.</summary>
    Task CheckpointAsync(string topic, IReadOnlyList<int> partitions, CancellationToken ct);

    /// <summary>The partitions were revoked cleanly (after a checkpoint); release their state.</summary>
    Task OnRevokedAsync(string topic, IReadOnlyList<int> partitions, CancellationToken ct);

    /// <summary>The partitions were taken away without a clean revoke (session timeout) or after a failure; drop their state, do not persist it.</summary>
    Task OnLostAsync(string topic, IReadOnlyList<int> partitions, CancellationToken ct);

    Task HandleAsync(StreamRecord<TValue> record, CancellationToken ct);
}

/// <summary>
/// The raw Confluent consumer for stateful stream processing (ADR-0018), which the v8 rider cannot serve because it
/// has no partition callbacks. Cooperative sticky assignment, one consumer group per service and purpose, manual
/// commits: a record's offset is stored after <see cref="IPartitionHandler{TValue}.HandleAsync"/> returns and committed
/// only after a checkpoint (every <c>Kafka:Consumers:CheckpointSeconds</c> and before a clean revoke). A record whose
/// value cannot be read as <typeparamref name="TValue"/> goes to the dead-letter topic (register the topic with
/// <c>StreamFrom</c> so its dead-letter producer exists) and is skipped. A handler failure stops the loop without
/// checkpointing: the partitions' state is dropped and the records since the last checkpoint are replayed after
/// restart, because skipping a record would corrupt the partition's state.
/// </summary>
public sealed class PartitionedConsumer<TValue>(KafkaSettings settings, IDeadLetterSink deadLetters, TimeProvider timeProvider, ILogger<PartitionedConsumer<TValue>> logger)
    where TValue : class
{
    public static string GroupId(KafkaSettings settings, string purpose)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        return $"ariva-{settings.ServiceName}.{purpose}";
    }

    /// <summary>Consumes <paramref name="topic"/> until <paramref name="ct"/> is cancelled, on a dedicated thread.</summary>
    public Task RunAsync(string topic, string purpose, IPartitionHandler<TValue> handler, CancellationToken ct)
    {
        if (!KafkaTopics.IsKnown(topic))
            throw new ArgumentException($"Unknown topic '{topic}'.", nameof(topic));
        ArgumentNullException.ThrowIfNull(handler);
        return Task.Factory.StartNew(() => Loop(topic, GroupId(settings, purpose), handler, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void Loop(string topic, string groupId, IPartitionHandler<TValue> handler, CancellationToken ct)
    {
        var failed = false;
        var assigned = new HashSet<int>();
        using var consumer = new ConsumerBuilder<string, byte[]>(KafkaClientConfig.Consumer(settings, groupId))
            .SetPartitionsAssignedHandler((_, partitions) =>
            {
                var list = Partitions(partitions);
                assigned.UnionWith(list);
                handler.OnAssignedAsync(topic, list, ct).GetAwaiter().GetResult();
            })
            .SetPartitionsRevokedHandler((c, offsets) =>
            {
                var list = Partitions(offsets.Select(o => o.TopicPartition));
                assigned.ExceptWith(list);
                if (failed)
                {
                    // After a handler failure the state may hold a half-applied record: drop it, commit nothing.
                    handler.OnLostAsync(topic, list, CancellationToken.None).GetAwaiter().GetResult();
                    return;
                }

                handler.CheckpointAsync(topic, list, CancellationToken.None).GetAwaiter().GetResult();
                Commit(c);
                handler.OnRevokedAsync(topic, list, CancellationToken.None).GetAwaiter().GetResult();
            })
            .SetPartitionsLostHandler((_, offsets) =>
            {
                var list = Partitions(offsets.Select(o => o.TopicPartition));
                assigned.ExceptWith(list);
                handler.OnLostAsync(topic, list, CancellationToken.None).GetAwaiter().GetResult();
            })
            .Build();

        consumer.Subscribe(topic);
        logger.LogInformation("Stream consumer {GroupId} subscribed to {Topic}", groupId, topic);
        var every = TimeSpan.FromSeconds(Math.Max(1, settings.Consumers.CheckpointSeconds));
        var nextCheckpoint = timeProvider.GetUtcNow() + every;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = consumer.Consume(TimeSpan.FromMilliseconds(500));
                if (result?.Message is not null)
                {
                    if (TryRead(result, groupId, out var value) && value is not null)
                    {
                        var record = new StreamRecord<TValue>(result.Topic, result.Partition.Value, result.Offset.Value, result.Message.Key, value, result.Message.Timestamp.UtcDateTime);
                        handler.HandleAsync(record, ct).GetAwaiter().GetResult();
                    }

                    consumer.StoreOffset(result);
                }

                if (timeProvider.GetUtcNow() >= nextCheckpoint && assigned.Count > 0)
                {
                    handler.CheckpointAsync(topic, [.. assigned], ct).GetAwaiter().GetResult();
                    Commit(consumer);
                    nextCheckpoint = timeProvider.GetUtcNow() + every;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown: Close below revokes cleanly, which checkpoints and commits.
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            consumer.Close();
        }
    }

    /// <summary>The value, or false after dead-lettering a record that cannot be read.</summary>
    private bool TryRead(ConsumeResult<string, byte[]> result, string groupId, out TValue value)
    {
        try
        {
            value = result.Message.Value is null ? null : JsonSerializer.Deserialize<TValue>(result.Message.Value, EventCatalog.Json);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // JsonException for bad JSON, ArgumentException and others from a validating contract constructor.
            value = null;
            var headers = (result.Message.Headers ?? [])
                .Select(h => new KeyValuePair<string, string>(h.Key, h.GetValueBytes() is { } bytes ? System.Text.Encoding.UTF8.GetString(bytes) : null));
            var letter = DeadLetters.Create(result.Topic, result.Partition.Value, result.Offset.Value, result.Message.Key, result.Message.Value, headers, groupId, e,
                timeProvider.GetUtcNow().UtcDateTime);
            logger.LogError(e, "Unreadable record at {Topic} partition {Partition} offset {Offset}; sent to the dead-letter topic", result.Topic, result.Partition.Value, result.Offset.Value);
            deadLetters.SendAsync(letter, CancellationToken.None).GetAwaiter().GetResult();
            return false;
        }
    }

    private void Commit(IConsumer<string, byte[]> consumer)
    {
        try
        {
            consumer.Commit();
        }
        catch (KafkaException e) when (e.Error.Code is ErrorCode.Local_NoOffset)
        {
            // Nothing stored since the last commit.
        }
        catch (KafkaException e)
        {
            logger.LogWarning(e, "Committing offsets failed; the records since the last commit will be replayed");
        }
    }

    private static List<int> Partitions(IEnumerable<TopicPartition> partitions) => [.. partitions.Select(p => p.Partition.Value)];
}
