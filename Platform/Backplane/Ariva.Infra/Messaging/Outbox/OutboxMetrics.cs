using System.Diagnostics.Metrics;
using Ariva.Core.Messaging;

namespace Ariva.Infra.Messaging.Outbox;

/// <summary>What the outbox held unsent when it was measured.</summary>
/// <param name="Unsent">Rows not yet sent (events and kept dead letters).</param>
/// <param name="OldestUtc">When the oldest of them was written; null when there is none.</param>
public sealed record OutboxBacklog(long Unsent, DateTime? OldestUtc);

/// <summary>
/// The outbox's backlog (meter <c>Ariva.Outbox</c>, ARV-125a): <see cref="UnsentName"/>, the rows not yet sent, and
/// <see cref="OldestUnsentAgeName"/>, how old the oldest of them was when measured (0 when there is none). The table is
/// shared by every host, so every relay measures it (at most every <see cref="OutboxRelay.MeasureEvery"/>, outside its
/// pass and its leader lock) and they all report the same figures: a dashboard takes the largest, never the sum. A
/// measurement is reported for three intervals; a relay that cannot measure (the database away) reports nothing, which
/// the database readiness check already raises. No tags.
/// <para>
/// ARV-245: <see cref="BrokerStreaksName"/>, a counter of the rows whose sends have failed with the broker unavailable for
/// <c>Kafka:Outbox:BrokerStreakMinutes</c> without a break (each counted once by the relay that leads through its streak),
/// tagged with the row's <c>topic</c>: a <c>KafkaTopics</c> constant or the dead-letter topic of one, else
/// <see cref="UnknownTopic"/> (<see cref="TopicTag"/>), never another value from the row. Any increase is the signal: a
/// partition with no leader, which the brokers' readiness does not show.
/// </para>
/// <para>
/// ARV-264: <see cref="TopicHoldsName"/>, a counter of the topics the relay held back, tagged with the <c>topic</c> (as above)
/// and the hold's <c>kind</c>: <see cref="PartitionUnavailableHold"/> (an event's send timed out while the brokers answered),
/// <see cref="TopicRefusedHold"/> (the topic missing or refused, ARV-177) or <see cref="DeadLetterHold"/> (a dead letter's
/// failure that was not its own, ARV-175); any other kind is <see cref="OtherHold"/>.
/// </para>
/// </summary>
public sealed class OutboxMetrics : IDisposable
{
    public const string MeterName = "Ariva.Outbox";
    public const string UnsentName = "ariva.outbox.unsent";
    public const string OldestUnsentAgeName = "ariva.outbox.oldest_unsent_age";
    public const string BrokerStreaksName = "ariva.outbox.broker_streaks";
    public const string TopicHoldsName = "ariva.outbox.topic_holds";

    /// <summary>The <c>kind</c> of a topic hold (ARV-264).</summary>
    public const string PartitionUnavailableHold = "partition_unavailable";
    public const string TopicRefusedHold = "topic_refused";
    public const string DeadLetterHold = "dead_letter";
    public const string OtherHold = "other";

    /// <summary>The tag value of a topic that is not one of Ariva's.</summary>
    public const string UnknownTopic = "unknown";

    #region Fields

    private readonly Meter _meter;
    private readonly Counter<long> _brokerStreaks;
    private readonly Counter<long> _topicHolds;
    private readonly TimeProvider _time;
    private volatile Snapshot _last;

    #endregion

    private sealed record Snapshot(long Unsent, double OldestAgeSeconds, DateTimeOffset At, TimeSpan FreshFor);

    #region Constructors

    public OutboxMetrics(IMeterFactory meterFactory = null, TimeProvider timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        _meter.CreateObservableGauge(UnsentName, () => Fresh() is { } last ? [new Measurement<long>(last.Unsent)] : Array.Empty<Measurement<long>>(),
            unit: "{row}", description: "Outbox rows not yet sent to Kafka (events and kept dead letters)");
        _meter.CreateObservableGauge(OldestUnsentAgeName, () => Fresh() is { } last ? [new Measurement<double>(last.OldestAgeSeconds)] : Array.Empty<Measurement<double>>(),
            unit: "s", description: "Age of the oldest outbox row not yet sent when measured, 0 when there is none");
        _brokerStreaks = _meter.CreateCounter<long>(BrokerStreaksName, unit: "{row}",
            description: "Outbox rows whose sends have failed with the broker unavailable for Kafka:Outbox:BrokerStreakMinutes without a break, by topic");
        _topicHolds = _meter.CreateCounter<long>(TopicHoldsName, unit: "{hold}",
            description: "Topics the outbox relay held back while the others carried on, by topic and kind of hold");
    }

    #endregion

    #region Properties

    /// <summary>This instance's meter (tests listen to it alone).</summary>
    internal Meter Meter => _meter;

    #endregion

    #region Methods

    /// <summary>Reports a measurement taken now, valid for <paramref name="freshFor"/>.</summary>
    public void Report(OutboxBacklog backlog, TimeSpan freshFor)
    {
        ArgumentNullException.ThrowIfNull(backlog);
        var now = _time.GetUtcNow();
        var age = backlog.OldestUtc is { } oldest ? Math.Max(0, (now.UtcDateTime - DateTime.SpecifyKind(oldest, DateTimeKind.Utc)).TotalSeconds) : 0;
        _last = new Snapshot(Math.Max(0, backlog.Unsent), age, now, freshFor);
    }

    /// <summary>A row on <paramref name="topic"/> whose broker-unavailable streak passed the configured length (ARV-245).</summary>
    public void BrokerStreak(string topic) => _brokerStreaks.Add(1, new KeyValuePair<string, object>("topic", TopicTag(topic)));

    /// <summary>A hold of <paramref name="topic"/> of the given <paramref name="kind"/> (ARV-264).</summary>
    public void TopicHeld(string topic, string kind) =>
        _topicHolds.Add(1, new KeyValuePair<string, object>("topic", TopicTag(topic)),
            new KeyValuePair<string, object>("kind", kind is PartitionUnavailableHold or TopicRefusedHold or DeadLetterHold ? kind : OtherHold));

    /// <summary>A topic as a tag value: a <c>KafkaTopics</c> constant or the dead-letter topic of one, else <see cref="UnknownTopic"/>.</summary>
    public static string TopicTag(string topic) =>
        KafkaTopics.IsKnown(topic) || (topic is not null && KafkaTopics.All.Any(known => string.Equals(KafkaTopics.DeadLetter(known), topic, StringComparison.Ordinal)))
            ? topic
            : UnknownTopic;

    private Snapshot Fresh()
    {
        var last = _last;
        return last is not null && _time.GetUtcNow() - last.At <= last.FreshFor ? last : null;
    }

    public void Dispose() => _meter.Dispose();

    #endregion
}
