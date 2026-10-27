namespace Ariva.Infra.Messaging;

/// <summary>
/// The "Kafka" configuration section (ADR-0018). With <see cref="Enabled"/> false (unit tests, a laptop without a
/// broker) no bus starts; domain events still go to the outbox table and wait there for a relay.
/// </summary>
public sealed class KafkaSettings
{
    public const string SectionName = "Kafka";

    public bool Enabled { get; init; }

    /// <summary>Comma-separated host:port list.</summary>
    public string BootstrapServers { get; init; } = "localhost:9092";

    /// <summary>
    /// The service part of consumer group names, <c>ariva-&lt;service&gt;.&lt;purpose&gt;</c>, one group per service and
    /// endpoint so a deploy of one service never rebalances another. Each host sets it (main, stream, ingest, cronz,
    /// integration).
    /// </summary>
    public string ServiceName { get; init; } = "main";

    /// <summary>
    /// Plaintext in vm-local and the dev and demo clusters; production must use <c>SaslSsl</c> (appsettings.base.k8s-prd
    /// sets it, and a k8s-prd host with Kafka on refuses to start with anything else, CWE-501).
    /// </summary>
    public string SecurityProtocol { get; init; } = "Plaintext";

    /// <summary>PEM file of the CA that signed the brokers' certificates (a Strimzi cluster CA), mounted from a secret.</summary>
    public string SslCaLocation { get; init; }

    public string SaslMechanism { get; init; }
    public string SaslUsername { get; init; }

    /// <summary>From the Kubernetes secret (Kafka__SaslPassword), never from a committed file.</summary>
    public string SaslPassword { get; init; }

    /// <summary>Creates missing topics (and their dead-letter topics) at startup; existing topics are never changed.</summary>
    public bool ProvisionTopics { get; init; }

    public TopicDefaults Topics { get; init; } = new();

    public OutboxRelaySettings Outbox { get; init; } = new();

    public ConsumerSettings Consumers { get; init; } = new();

    public ProducerSettings Producer { get; init; } = new();

    /// <summary>Kafka's own limit is enforced on the producer too (ADR-0018): 1 MB.</summary>
    public const int MaxMessageBytes = 1_048_576;
}

/// <summary>Partitions, replication and retention for provisioned topics; per-topic overrides by name.</summary>
public sealed class TopicDefaults
{
    /// <summary>Zone-keyed topics need at least as many partitions as Stream replicas.</summary>
    public int Partitions { get; init; } = 6;

    /// <summary>Three brokers for a standard site; one for the small-site profile and vm-local.</summary>
    public short ReplicationFactor { get; init; } = 3;

    public int MinInSyncReplicas { get; init; } = 2;

    public Dictionary<string, TopicOverride> Overrides { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Also creates AMAN's feed topics (<c>aman.feed.*.v1</c>, ARV-048). AMAN owns them in a shared cluster; set this only
    /// where no AMAN creates them (vm-local, dev and demo, where the simulator plays AMAN).
    /// </summary>
    public bool ProvisionAmanFeed { get; init; }
}

public sealed class TopicOverride
{
    public int? Partitions { get; init; }
    public int? RetentionDays { get; init; }
}

/// <summary>The outbox relay (ADR-0018).</summary>
public sealed class OutboxRelaySettings
{
    /// <summary>Runs the relay in this host. Every host that writes domain events can; one leader relays at a time.</summary>
    public bool Enabled { get; init; } = true;

    public int BatchSize { get; init; } = 100;

    public int PollMilliseconds { get; init; } = 500;

    /// <summary>Sent rows are kept this long for tracing, then removed.</summary>
    public int KeepSentDays { get; init; } = 7;

    /// <summary>
    /// Processed-event rows (the inbox) are kept this long. Longer than the longest retention of a deleting topic (30
    /// days), so a replay from the earliest offset never applies an event twice; compacted topics carry state and their
    /// consumers apply it by version.
    /// </summary>
    public int KeepProcessedDays { get; init; } = 35;

    /// <summary>A row this many attempts old is logged as an error on every further failure, for the alert.</summary>
    public int StuckAfterAttempts { get; init; } = 10;

    /// <summary>
    /// The longest a relay pass produces while it holds its transaction, its leader lock and its claimed rows (ARV-166):
    /// a send still waiting then fails as a broker failure and the pass ends and commits. Each produce ends within 45
    /// seconds anyway (<c>KafkaProduce.SendTimeout</c>); this also bounds a pass of many slow sends. From
    /// <see cref="MinPassSeconds"/> to <see cref="MaxPassSeconds"/> (ARV-264), a host with another value does not start; below
    /// <see cref="MinPassSecondsForTopicHolds"/> a topic with a partition without a leader is never held, and ends each pass.
    /// </summary>
    public int PassSeconds { get; init; } = 60;

    public const int MinPassSeconds = 10;
    public const int MaxPassSeconds = 120;

    /// <summary>
    /// The shortest pass that leaves room for the producer to report a send that waited for a partition without a leader
    /// (ARV-264): its 45 second bound (<c>KafkaProduce.SendTimeout</c>, which the client's 30 second message timeout reports
    /// within) and a second more. A shorter pass cuts such a send first, which holds nothing.
    /// </summary>
    public const int MinPassSecondsForTopicHolds = 46;

    public const int MinBrokerRetrySeconds = 1;
    public const int MaxBrokerRetrySeconds = 30;

    /// <summary>
    /// When a send fails because the broker is unavailable (its message timeout, all brokers down, a transport error, a
    /// full producer queue, the producer's own bound or the pass's, the producer in its fatal gap or stopping), the row is
    /// tried again after this many seconds and its attempts are not counted (ARV-165), so after an outage every key
    /// resumes within this wait of the broker's return instead of the row's grown backoff (up to five minutes). From
    /// <see cref="MinBrokerRetrySeconds"/> to <see cref="MaxBrokerRetrySeconds"/>; a host with another value does not
    /// start. Any other failure keeps the exponential backoff.
    /// </summary>
    public int BrokerRetrySeconds { get; init; } = 5;

    public const int MinBrokerStreakMinutes = 1;
    public const int MaxBrokerStreakMinutes = 1440;

    /// <summary>
    /// A row whose sends have failed with the broker unavailable for this many minutes without a break is logged once as an
    /// Error and counted (<c>ariva.outbox.broker_streaks</c>, ARV-245): such failures count no attempt, so the "stuck after N
    /// attempts" Error never fires for them, and a partition without a leader would otherwise only warn for ever. From
    /// <see cref="MinBrokerStreakMinutes"/> to <see cref="MaxBrokerStreakMinutes"/>; a host with another value does not start.
    /// </summary>
    public int BrokerStreakMinutes { get; init; } = 15;
}

/// <summary>The host's own producer (ARV-167).</summary>
public sealed class ProducerSettings
{
    public const int MinRecreateSeconds = 5;
    public const int MaxRecreateSeconds = 3600;

    /// <summary>
    /// After a fatal client error the producer is recreated in place, at most once in this many seconds (counted from the
    /// previous client's creation); sends in between fail at once (ARV-167). From <see cref="MinRecreateSeconds"/> to
    /// <see cref="MaxRecreateSeconds"/>; a host with another value does not start.
    /// </summary>
    public int RecreateSeconds { get; init; } = 30;
}

/// <summary>Endpoint defaults (ADR-0018).</summary>
public sealed class ConsumerSettings
{
    /// <summary>
    /// The most retries of a message (ARV-254): each attempt may take <see cref="HandlerTimeoutSeconds"/>, and one consumer
    /// handles one record at a time (ARV-253), so the retries set how long one message can hold every partition its consumer
    /// reads. <see cref="RetryCount"/> from 0 to this, or the host does not start.
    /// </summary>
    public const int MaxRetryCount = 10;

    public int RetryCount { get; init; } = 3;

    /// <summary>The largest <see cref="RetryMinMilliseconds"/> and <see cref="RetryMaxMilliseconds"/> (ARV-266): a minute.</summary>
    public const int MaxRetryMilliseconds = 60_000;

    /// <summary>
    /// The most a retry wait can be, as a multiple of <see cref="RetryMaxMilliseconds"/> (ARV-268): MassTransit 8.5.11 multiplies
    /// each capped wait by 0.75 and a random part below 0.5 (<c>ExponentialRetryPolicy.GetRetryInterval</c>), so a wait stays
    /// under 1.25 times the cap. <see cref="LongestHold"/> and the stop budget count every wait at this much.
    /// </summary>
    public const double RetryJitter = 1.25;

    /// <summary>
    /// The base of the waits between two attempts of a message (MassTransit 8.5.11's exponential retry): the n-th wait, counting
    /// from 0, is this and 2^n times a random 0.8 to 1.2 times it, capped at <see cref="RetryMaxMilliseconds"/>, then multiplied by a random
    /// 0.75 to 1.25. So the first wait is about 270 to 550 ms at the defaults, a wait can reach about 1.25 times the cap (just
    /// under 75 s at the bound), and a base of 0 or 1 means no backoff at all (every wait about 0 ms, whatever the cap): a failing
    /// message is then dead-lettered within its attempts, each up to its handler timeout, during a database outage too. Both settings from 0 to
    /// <see cref="MaxRetryMilliseconds"/>, this one no more than the cap, or the host does not start (ARV-266: MassTransit took
    /// them as they were, and a negative value made it fail its first retry or wait for ever). The waits count in how long a
    /// message holds its consumer (<see cref="LongestHold"/>, which counts each at the cap and the jitter: ARV-268).
    /// </summary>
    public int RetryMinMilliseconds { get; init; } = 200;

    /// <summary>The cap on each wait between two attempts, before MassTransit's jitter; see <see cref="RetryMinMilliseconds"/>.</summary>
    public int RetryMaxMilliseconds { get; init; } = 5_000;
    public int CheckpointSeconds { get; init; } = 5;
    public ushort CheckpointMessageCount { get; init; } = 500;

    /// <summary>
    /// Lanes per Kafka consumer: 1, and only 1 (ARV-253). With more, MassTransit 8.5.11 handles a consumer's records in lanes
    /// by key, and its checkpointer, which passes a record a stop cancels (one in hand when the bus's stop wait ends, or one
    /// waiting in a lane when the endpoint begins to stop) as it passes a faulted one, commits past it once a later record of
    /// its partition has completed in another lane: a valid event lost without a trace. With one lane a consumer's records
    /// complete in the order they were fetched, so the completed records of a partition are always its first ones and a stop
    /// passes nothing. Any other value is refused at start; <see cref="ConsumersPerEndpoint"/> gives parallelism instead.
    /// </summary>
    public int ConcurrentMessageLimit { get; init; } = 1;

    /// <summary>The most Kafka consumers per endpoint: the topics' default partition count (a consumer beyond it has none).</summary>
    public const int MaxConsumersPerEndpoint = 6;

    /// <summary>
    /// Kafka consumers per endpoint (MassTransit's <c>ConcurrentConsumerLimit</c>, ARV-253): each joins the endpoint's group with
    /// one lane and its own share of the topic's partitions, so the endpoint handles up to this many records at once, in order
    /// within each partition. From 1 to <see cref="MaxConsumersPerEndpoint"/>.
    /// </summary>
    public int ConsumersPerEndpoint { get; init; } = 1;

    /// <summary>
    /// librdkafka's <c>queued.max.messages.kbytes</c> for an endpoint's consumers together (ARV-253): 64 MiB, librdkafka's
    /// default for one consumer, shared out over <see cref="ConsumersPerEndpoint"/>, so more consumers queue no more ahead in
    /// librdkafka. MassTransit's prefetch is each consumer's own (1,000 messages taken from that queue and not yet handled),
    /// so it grows with the consumers (wiki 04).
    /// </summary>
    public const int FetchQueueKbytesPerEndpoint = 64 * 1024;

    public const int MinHandlerTimeoutSeconds = 65;
    public const int MaxHandlerTimeoutSeconds = 120;

    /// <summary>
    /// ARV-096e: the longest one attempt of a message may take (inbox, unit of work and consumer) before it is cancelled, retried
    /// and at last dead-lettered (<see cref="Kafka.HandlerTimeoutFilter"/>). 70 seconds. The minimum, 65, keeps it above the archive
    /// consumers' wait for a database that refuses connections (about a minute of waits between attempts), so that wait is not cut
    /// short, and well above a stop's window; from <see cref="MinHandlerTimeoutSeconds"/> to <see cref="MaxHandlerTimeoutSeconds"/>,
    /// or the host does not start.
    /// </summary>
    public int HandlerTimeoutSeconds { get; init; } = 70;

    /// <summary>
    /// The longest one record can hold its consumer (ARV-254): every attempt at its handler timeout, the retry waits at their
    /// longest (each <see cref="RetryJitter"/> times the cap since ARV-268: MassTransit's jitter), then its dead letter: refused
    /// sends for <see cref="Kafka.DeadLetterRetry.SendWindow"/> and one more send that started by its end
    /// (<see cref="Kafka.KafkaProduce.SendTimeout"/>), then the outbox write (<see cref="Kafka.DeadLetterRetry.KeepTimeout"/>).
    /// 413.75 seconds (about 6 minutes 54 seconds) at the defaults. Longer in three cases: the
    /// broker refuses the dead letter and the database the outbox write too (the letter is tried again until one takes it); a
    /// handler ignores its cancellation (every Ariva consumer passes its token on; ARV-096e); and the inbox's rollback after
    /// an attempt is cut, which runs without the attempt's token and is bounded by the database client's own timeouts (the
    /// minute <see cref="MaxPollInterval"/> adds takes it at the defaults).
    /// </summary>
    public TimeSpan LongestHold =>
        TimeSpan.FromSeconds((double)(Math.Max(0, RetryCount) + 1) * HandlerTimeoutSeconds)
        + RetryWaits(RetryCount, RetryMaxMilliseconds)
        + Kafka.DeadLetterRetry.SendWindow + Kafka.KafkaProduce.SendTimeout + Kafka.DeadLetterRetry.KeepTimeout;

    /// <summary>
    /// The longest a message's retry waits can take together: each at <see cref="RetryJitter"/> times the cap (ARV-268). For
    /// <see cref="LongestHold"/> and the stop budget.
    /// </summary>
    /// <param name="retries">The retries (<see cref="RetryCount"/>).</param>
    /// <param name="capMilliseconds">The cap (<see cref="RetryMaxMilliseconds"/>).</param>
    public static TimeSpan RetryWaits(int retries, int capMilliseconds) =>
        TimeSpan.FromMilliseconds((double)Math.Max(0, retries) * Math.Max(0, capMilliseconds) * RetryJitter);

    /// <summary>
    /// librdkafka's <c>max.poll.interval.ms</c> for every rider consumer (ARV-254): <see cref="LongestHold"/> and a minute.
    /// MassTransit 8.5.11's consume loop (<c>KafkaMessageConsumer.Consume</c>) takes a slot of its prefetch (1,000 records)
    /// before it polls again, so once the records fetched behind a held one fill it, the loop stops polling until the hold
    /// ends; with librdkafka's default of 300 seconds a hold longer than that took the consumer out of its group, and after
    /// the rebalance the records it had not committed were handled again (duplicates the inbox drops). Above the longest
    /// hold, a consumer leaves its group only when it is truly stuck. The cost: librdkafka also sends it as the group's rebalance
    /// timeout, so in a rebalance (a rolling update, a scale out, a pod lost) a member held with its prefetch full delays the
    /// whole group for up to this long (473.75 seconds at the defaults, 300 before). At most <see cref="MaxPollIntervalLimit"/>,
    /// or the host does not start.
    /// </summary>
    public TimeSpan MaxPollInterval => LongestHold + TimeSpan.FromMinutes(1);

    /// <summary>The largest <c>max.poll.interval.ms</c> librdkafka takes: a day (ARV-254).</summary>
    public static readonly TimeSpan MaxPollIntervalLimit = TimeSpan.FromMilliseconds(86_400_000);

    /// <summary>
    /// How long a host waits at start for the Ariva topics it consumes to exist before it connects the consumers whose
    /// topics do (ARV-176: AMAN's feed topics are never waited for, and a topic that is still missing is connected when it
    /// appears). 60 seconds; the connected consumers then get at most 30 seconds more to be ready. The host's start holds
    /// its web server, so the two waits must fit in the pod's startup allowance with a margin
    /// (<c>Hosting:StartupAllowanceSeconds</c>, checked at start).
    /// </summary>
    public int StartWaitSeconds { get; init; } = 60;

    /// <summary>
    /// How often a host looks for a consumed topic that did not exist, to start its consumer when it appears without a
    /// restart (ARV-176). From 1 to 300.
    /// </summary>
    public int TopicWatchSeconds { get; init; } = 10;
}
