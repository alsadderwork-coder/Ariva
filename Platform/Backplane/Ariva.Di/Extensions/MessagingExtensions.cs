using System.Reflection;
using System.Text.RegularExpressions;
using Ariva.Core.Messaging;
using Ariva.Core.Services;
using Ariva.Infra.Messaging;
using Ariva.Infra.Messaging.Inbox;
using Ariva.Infra.Messaging.Kafka;
using Ariva.Infra.Messaging.Outbox;
using Ariva.Infra.Settings;
using Confluent.Kafka;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.Di.Extensions;

/// <summary>
/// Messaging (ARV-020, ADR-0018): Ariva's own Kafka producer behind <see cref="ISvcMessageBus"/> (ARV-167) and MassTransit 8
/// on an in-memory bus with the Kafka Rider for consuming. Always registered: the event catalog, the NHibernate outbox
/// (domain events become outbox rows in the entity's transaction) and the inbox. With <c>Kafka:Enabled</c>: the host's one
/// producer (<see cref="KafkaProduce"/>, built at start and flushed at stop), topic provisioning, the outbox relay, and a
/// topic endpoint per consumer with one consumer group per service and purpose and the Ariva consume pipe
/// (<see cref="ConsumePipeline"/>). The rider registers no producer: every send, dead letters included, goes through
/// <see cref="KafkaProduce"/>.
/// </summary>
public static partial class MessagingExtensions
{
    /// <summary>CWE-501: in production every event crosses the network authenticated and encrypted, or the host does not start.</summary>
    internal static void EnsureProductionTransport(KafkaSettings settings, IConfiguration configuration)
    {
        if (settings.Enabled && configuration["Application:Environment"] == "k8s-prd" &&
            !string.Equals(settings.SecurityProtocol, "SaslSsl", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Kafka in k8s-prd must use SecurityProtocol SaslSsl with a SASL mechanism and credentials.");
        }
    }

    /// <summary>
    /// ARV-097: MassTransit's usage tracker reads <c>MASSTRANSIT_USAGE_TELEMETRY</c> when it is built and lets "true" or
    /// "1" turn reporting back on despite <c>DisableUsageTelemetry</c>, so a host with that setting does not start.
    /// </summary>
    internal static void EnsureNoUsageTelemetry(IConfiguration configuration)
    {
        foreach (var value in new[] { configuration[UsageTelemetryVariable], Environment.GetEnvironmentVariable(UsageTelemetryVariable) })
        {
            var on = value?.Trim();
            if ((bool.TryParse(on, out var flag) && flag) || (int.TryParse(on, out var number) && number != 0))
                throw new InvalidOperationException($"{UsageTelemetryVariable} must not turn on MassTransit usage telemetry: Ariva makes no call outside the deployment.");
        }
    }

    internal const string UsageTelemetryVariable = "MASSTRANSIT_USAGE_TELEMETRY";

    /// <summary>
    /// The most a host's start waits for the bus (ARV-176): 15 seconds. The bus starts with no topic endpoint, so its start
    /// takes well under a second; the bound is counted in <see cref="HostingSettings.StartMarginSeconds"/>.
    /// </summary>
    internal static readonly TimeSpan BusStartTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// ARV-165: the host's stop budget must fit, or the host does not start. The bus's share (the shutdown timeout less
    /// <see cref="StopReserve"/>) must cover a stop that comes during a message's retries: the rest of the retry schedule
    /// (at most <c>RetryCount</c> times <c>RetryMaxMilliseconds</c>) and the dead letter's keep (10 s), the clean path of
    /// ARV-171; and the shutdown timeout must end <see cref="HostingSettings.ExitMarginSeconds"/> before the pod's grace
    /// period, so the dispose and the exit happen before the kubelet kills the process.
    /// </summary>
    internal static void EnsureStopBudget(HostingSettings hosting, KafkaSettings settings)
    {
        ArgumentNullException.ThrowIfNull(hosting);
        ArgumentNullException.ThrowIfNull(settings);
        var shutdown = TimeSpan.FromSeconds(hosting.ShutdownSeconds);
        var grace = TimeSpan.FromSeconds(hosting.GracePeriodSeconds);
        if (hosting.ShutdownSeconds <= 0 || shutdown > grace - TimeSpan.FromSeconds(HostingSettings.ExitMarginSeconds))
            throw new InvalidOperationException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Hosting:ShutdownSeconds ({hosting.ShutdownSeconds}) must be positive and at most Hosting:GracePeriodSeconds ({hosting.GracePeriodSeconds}) less {HostingSettings.ExitMarginSeconds}."));
        var cleanStop = CleanStop(settings.Consumers ?? new ConsumerSettings());
        if (settings.Enabled && BusStopTimeout(shutdown) < cleanStop)
            throw new InvalidOperationException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Hosting:ShutdownSeconds ({hosting.ShutdownSeconds}) leaves the message bus {BusStopTimeout(shutdown).TotalSeconds:F0} s to stop, less than the {cleanStop.TotalSeconds:F0} s a stop during a message's retries needs (the retry schedule and the dead letter's keep)."));
    }

    /// <summary>
    /// ARV-176: the host's start budget must fit, or the host does not start. Its start holds the web server until the
    /// consumers' wait for their topics (<c>Kafka:Consumers:StartWaitSeconds</c>) and their ready wait
    /// (<see cref="KafkaConsumerEndpoints.ReadyWaitSeconds"/>) have ended, so the two, with
    /// <see cref="HostingSettings.StartMarginSeconds"/> for the rest of the start, must fit in the pod's startup allowance
    /// (<see cref="HostingSettings.StartupAllowanceSeconds"/>); otherwise the kubelet restarts a host that is still waiting,
    /// again and again.
    /// </summary>
    internal static void EnsureStartBudget(HostingSettings hosting, KafkaSettings settings)
    {
        ArgumentNullException.ThrowIfNull(hosting);
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.Enabled)
            return;
        var wait = settings.Consumers?.StartWaitSeconds ?? 0;
        var most = hosting.StartupAllowanceSeconds - KafkaConsumerEndpoints.ReadyWaitSeconds - HostingSettings.StartMarginSeconds;
        if (wait < 0 || wait > most)
            throw new InvalidOperationException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Kafka:Consumers:StartWaitSeconds ({wait}) must be from 0 to Hosting:StartupAllowanceSeconds ({hosting.StartupAllowanceSeconds}) less the {KafkaConsumerEndpoints.ReadyWaitSeconds} s ready wait and the {HostingSettings.StartMarginSeconds} s margin ({Math.Max(0, most)})."));
    }

    /// <summary>The longest clean stop of an endpoint with a message in hand: the rest of its retry schedule and the dead letter's keep.</summary>
    internal static TimeSpan CleanStop(ConsumerSettings consumers) =>
        TimeSpan.FromMilliseconds((double)Math.Max(0, consumers.RetryCount) * Math.Max(0, consumers.RetryMaxMilliseconds)) + DeadLetterRetry.KeepTimeout;

    /// <summary>
    /// What the host's stop keeps for the services that stop after the bus (ARV-165): the endpoints the bus skipped
    /// (<see cref="KafkaEndpoints.StopTimeout"/>, 5 s), the producer's flush (<see cref="KafkaProduce.FlushTimeout"/>, 5 s)
    /// and 5 s for the rest.
    /// </summary>
    internal static readonly TimeSpan StopReserve = KafkaEndpoints.StopTimeout + KafkaProduce.FlushTimeout + TimeSpan.FromSeconds(5);

    /// <summary>The bus's stop wait for a host shutdown timeout: the timeout less <see cref="StopReserve"/>, at least 5 s.</summary>
    internal static TimeSpan BusStopTimeout(TimeSpan shutdownTimeout)
    {
        if (shutdownTimeout == Timeout.InfiniteTimeSpan)
            return Timeout.InfiniteTimeSpan;
        var left = shutdownTimeout - StopReserve;
        return left < TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : left;
    }

    /// <summary>
    /// ARV-167 and ARV-165: the producer's recreation interval and the relay's retry after a broker failure are bounded, or
    /// the host does not start.
    /// </summary>
    internal static void EnsureProducerSettings(KafkaSettings settings)
    {
        var retry = settings.Outbox?.BrokerRetrySeconds ?? 0;
        if (retry is < OutboxRelaySettings.MinBrokerRetrySeconds or > OutboxRelaySettings.MaxBrokerRetrySeconds)
            throw new InvalidOperationException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Kafka:Outbox:BrokerRetrySeconds must be from {OutboxRelaySettings.MinBrokerRetrySeconds} to {OutboxRelaySettings.MaxBrokerRetrySeconds}."));
        var watch = settings.Consumers?.TopicWatchSeconds ?? 0;
        if (watch is < 1 or > 300)
            throw new InvalidOperationException("Kafka:Consumers:TopicWatchSeconds must be from 1 to 300.");
        var handler = settings.Consumers?.HandlerTimeoutSeconds ?? 0;
        if (handler is < ConsumerSettings.MinHandlerTimeoutSeconds or > ConsumerSettings.MaxHandlerTimeoutSeconds)
            throw new InvalidOperationException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Kafka:Consumers:HandlerTimeoutSeconds must be from {ConsumerSettings.MinHandlerTimeoutSeconds} to {ConsumerSettings.MaxHandlerTimeoutSeconds} (ARV-096e)."));
        var seconds = settings.Producer?.RecreateSeconds ?? 0;
        if (seconds is < ProducerSettings.MinRecreateSeconds or > ProducerSettings.MaxRecreateSeconds)
            throw new InvalidOperationException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"Kafka:Producer:RecreateSeconds must be from {ProducerSettings.MinRecreateSeconds} to {ProducerSettings.MaxRecreateSeconds}."));
    }

    public static IServiceCollection AddArivaMessaging(this IServiceCollection services, IConfiguration configuration, Action<ArivaMessagingBuilder> configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var settings = configuration.GetSection(KafkaSettings.SectionName).Get<KafkaSettings>() ?? new KafkaSettings();
        var builder = new ArivaMessagingBuilder();
        configure?.Invoke(builder);

        var catalog = new EventCatalog([typeof(Ariva.Core._IAssemblyMark).Assembly, .. builder.EventAssemblies]);
        services.AddSingleton(settings);
        services.AddSingleton(catalog);
        services.TryAddSingleton(TimeProvider.System);
        services.Replace(ServiceDescriptor.Singleton<IDomainEventOutbox, NHibernateDomainEventOutbox>());
        services.TryAddScoped<IInbox, PostgresInbox>();

        EnsureProductionTransport(settings, configuration);
        EnsureNoUsageTelemetry(configuration);
        EnsureProducerSettings(settings);

        // ARV-165: the host's stop budget, checked against what a stop must fit (EnsureStopBudget) and set for every host.
        var hosting = configuration.GetSection(HostingSettings.SectionName).Get<HostingSettings>() ?? new HostingSettings();
        EnsureStopBudget(hosting, settings);
        EnsureStartBudget(hosting, settings);
        services.Configure<Microsoft.Extensions.Hosting.HostOptions>(host => host.ShutdownTimeout = TimeSpan.FromSeconds(hosting.ShutdownSeconds));

        if (!settings.Enabled)
        {
            services.TryAddSingleton<ISvcMessageBus, DisabledMessageBus>();
            return services;
        }

        // ARV-167: the host's one producer, built when the host starts. Its lifetime is the first messaging hosted service, so
        // it starts before the relay and the bus and stops (with its bounded flush) after them.
        services.TryAddSingleton<KafkaProducerMetrics>();
        services.AddSingleton(provider => new KafkaProduce(settings, provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<KafkaProduce>>(), provider.GetRequiredService<KafkaProducerMetrics>()));
        services.AddHostedService<KafkaProducerLifetime>();
        // ARV-165: after the bus (registered before it, so it stops after it), the endpoints the bus's stop skipped.
        services.TryAddSingleton<KafkaEndpoints>();
        services.AddHostedService<KafkaEndpointsLifetime>();
        // ARV-165: the bus's stop waits at most the host's shutdown timeout less what the services after it need, so the
        // host stops in time even while a consumer is held by an offset commit (the bus is still stopped; only the wait ends).
        services.AddOptions<MassTransitHostOptions>().Configure<Microsoft.Extensions.Options.IOptions<Microsoft.Extensions.Hosting.HostOptions>>(
            (bus, host) =>
            {
                bus.StopTimeout = BusStopTimeout(host.Value.ShutdownTimeout);
                // ARV-176: the bus has no topic endpoint to start (KafkaConsumerEndpoints connects them after it), so its
                // start is quick, and it is waited for: the consumers are connected to a started bus. Its bound is part of
                // the host's start budget (HostingSettings.StartMarginSeconds), so it holds even when the start times out.
                bus.WaitUntilStarted = true;
                bus.StartTimeout = BusStartTimeout;
            });

        if (settings.ProvisionTopics)
            services.AddHostedService<TopicProvisioner>();

        services.AddSingleton<IOutboxTransport, KafkaOutboxTransport>();
        services.AddSingleton<IDeadLetterSink, KafkaDeadLetterSink>();
        // ARV-171: a dead letter Kafka did not take before its endpoint stopped is kept as an outbox row.
        services.AddSingleton<IDeadLetterStore, OutboxDeadLetterStore>();
        services.Replace(ServiceDescriptor.Singleton<ISvcMessageBus, KafkaMessageBus>());

        // ARV-165: the brokers as the producer sees them, so an outage is reported even while MassTransit's consumer is held
        // by an offset commit (KafkaReadiness). Degraded, as the bus's own check, so readiness answers 200 and the alert fires.
        services.AddHealthChecks().AddCheck<KafkaReadiness>("kafka", Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded,
            [HealthExtensions.ReadyTag], KafkaReadiness.ProbeTimeout + TimeSpan.FromSeconds(2));

        services.AddMassTransit(bus =>
        {
            // ARV-097: MassTransit otherwise reports versions, the OS, the time zone and the topic names to an external endpoint at startup.
            bus.DisableUsageTelemetry();
            bus.UsingInMemory((context, memory) => memory.ConfigureEndpoints(context));
            bus.AddRider(rider =>
            {
                foreach (var consumer in builder.Consumers)
                    consumer.Register(rider);

                // ARV-177: the rider's clients on Ariva's base, so none of them asks a broker to create a topic either.
                rider.UsingKafka(KafkaClientConfig.Rider(), (context, kafka) =>
                {
                    // A ClientConfig in the container, which MassTransit takes as the base only when UsingKafka is given none,
                    // still gives the rider its librdkafka debug contexts (CommitRaceTests' diagnostics); nothing else of it.
                    if (context.GetService<ClientConfig>()?.Debug is { Length: > 0 } debug)
                        kafka.Debug = debug;
                    kafka.Host(settings.BootstrapServers, host =>
                    {
                        if (!string.IsNullOrEmpty(settings.SslCaLocation))
                            host.UseSsl(ssl => ssl.CaLocation = settings.SslCaLocation);
                        if (!string.IsNullOrEmpty(settings.SaslMechanism))
                        {
                            host.UseSasl(sasl =>
                            {
                                sasl.Mechanism = Enum.Parse<SaslMechanism>(settings.SaslMechanism, ignoreCase: true);
                                sasl.Username = settings.SaslUsername;
                                sasl.Password = settings.SaslPassword;
                                sasl.SecurityProtocol = Enum.Parse<SecurityProtocol>(settings.SecurityProtocol, ignoreCase: true);
                            });
                        }
                    });
                    kafka.ClientId = $"ariva-{settings.ServiceName}";
                    kafka.SecurityProtocol = Enum.Parse<SecurityProtocol>(settings.SecurityProtocol, ignoreCase: true);
                    kafka.Acks = Acks.All;
                    kafka.MessageMaxBytes = KafkaSettings.MaxMessageBytes;
                    // ARV-176: no topic endpoint here. MassTransit starts the endpoints configured with the bus as part of the
                    // bus's start, and one whose topic is missing faults that start and leaves the bus stopped for good, every
                    // consumer of the host with it. KafkaConsumerEndpoints connects each endpoint after the bus has started,
                    // once its topic exists.
                });
            });
        });

        // ARV-176: after the bus (hosted services start in order), each consumer's endpoint once its topic exists. The bus
        // starts with no topic endpoint, so its start is quick and is waited for.
        var consumed = builder.Consumers
            .Select(c => new ConsumedTopic(c.Topic, $"ariva-{settings.ServiceName}.{c.Purpose}", rider => c.Connect(rider, settings)))
            .ToList();
        services.AddHostedService(provider => new KafkaConsumerEndpoints(settings, consumed, provider.GetRequiredService<IKafkaRider>,
            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<KafkaConsumerEndpoints>>(),
            provider.GetRequiredService<KafkaProducerMetrics>()));

        if (settings.Outbox.Enabled)
        {
            // ARV-125a: the backlog every relay measures (meter Ariva.Outbox).
            services.TryAddSingleton<OutboxMetrics>();
            services.AddHostedService<OutboxRelay>();
        }

        return services;
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    internal static partial Regex PurposeRule();
}

/// <summary>What a host consumes. Each consumer gets its own group, <c>ariva-&lt;service&gt;.&lt;purpose&gt;</c>.</summary>
public sealed class ArivaMessagingBuilder
{
    internal List<Assembly> EventAssemblies { get; } = [];

    internal List<ConsumerRegistration> Consumers { get; } = [];

    /// <summary>
    /// A topic this host reads with <c>PartitionedConsumer</c> (Ariva.Api.Stream); it must be a <see cref="KafkaTopics"/>
    /// constant. Its unreadable records go to its dead-letter topic through the host's one producer, which needs no
    /// registration per topic (ARV-167).
    /// </summary>
    public ArivaMessagingBuilder StreamFrom(string topic)
    {
        if (!KafkaTopics.IsKnown(topic))
            throw new ArgumentException($"Unknown topic '{topic}'. Use a KafkaTopics constant.", nameof(topic));
        return this;
    }

    /// <summary>Assemblies with more <see cref="KafkaTopicAttribute"/> events than Ariva.Core (contracts, tests).</summary>
    public ArivaMessagingBuilder AddEventsFrom(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        EventAssemblies.Add(assembly);
        return this;
    }

    /// <summary>Consumes <paramref name="topic"/> with <typeparamref name="TConsumer"/> through the Ariva consume pipe.</summary>
    public ArivaMessagingBuilder Consume<TMessage, TConsumer>(string topic, string purpose)
        where TMessage : class
        where TConsumer : class, IConsumer<TMessage> =>
        Consume<TMessage, TConsumer>(topic, purpose, new JsonValueSerializer<TMessage>());

    /// <summary>The same with a value reader of its own (a partner's topic read strictly, ARV-048).</summary>
    public ArivaMessagingBuilder Consume<TMessage, TConsumer>(string topic, string purpose, IDeserializer<TMessage> deserializer)
        where TMessage : class
        where TConsumer : class, IConsumer<TMessage>
    {
        ArgumentNullException.ThrowIfNull(deserializer);
        if (!KafkaTopics.IsKnown(topic))
            throw new ArgumentException($"Unknown topic '{topic}'. Use a KafkaTopics constant.", nameof(topic));
        if (purpose is null || !MessagingExtensions.PurposeRule().IsMatch(purpose))
            throw new ArgumentException("A purpose is lowercase kebab case, for example 'topology-cache'.", nameof(purpose));
        if (Consumers.Any(c => c.Purpose == purpose))
            throw new ArgumentException($"Purpose '{purpose}' is used twice; each endpoint has its own consumer group.", nameof(purpose));

        Consumers.Add(new ConsumerRegistration(
            topic,
            purpose,
            rider => rider.AddConsumer<TConsumer>(),
            (kafkaRider, settings) => kafkaRider.ConnectTopicEndpoint<string, TMessage>(topic, $"ariva-{settings.ServiceName}.{purpose}", (context, endpoint) =>
            {
                endpoint.AutoOffsetReset = AutoOffsetReset.Earliest;
                endpoint.ConcurrentDeliveryLimit = 1;
                endpoint.ConcurrentMessageLimit = settings.Consumers.ConcurrentMessageLimit;
                endpoint.CheckpointInterval = TimeSpan.FromSeconds(settings.Consumers.CheckpointSeconds);
                endpoint.CheckpointMessageCount = settings.Consumers.CheckpointMessageCount;
                endpoint.SetValueDeserializer(deserializer);
                ConsumePipeline.Configure(endpoint, context, settings.Consumers);
                endpoint.ConfigureConsumer<TConsumer>(context);
            })));
        return this;
    }
}

/// <summary>
/// One consumer: its topic and purpose, its registration with the rider, and how to connect its topic endpoint at run time
/// (ARV-176, <see cref="KafkaConsumerEndpoints"/>).
/// </summary>
internal sealed record ConsumerRegistration(
    string Topic,
    string Purpose,
    Action<IRiderRegistrationConfigurator> Register,
    Func<IKafkaRider, KafkaSettings, HostReceiveEndpointHandle> Connect);
