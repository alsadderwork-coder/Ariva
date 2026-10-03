using System.Reflection;
using System.Text.RegularExpressions;
using Ariva.Core.Messaging;
using Ariva.Core.Services;
using Ariva.Infra.Messaging;
using Ariva.Infra.Messaging.Inbox;
using Ariva.Infra.Messaging.Kafka;
using Ariva.Infra.Messaging.Outbox;
using Confluent.Kafka;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Ariva.Di.Extensions;

/// <summary>
/// Messaging (ARV-020, ADR-0018): MassTransit 8 on an in-memory bus with the Kafka Rider, behind
/// <see cref="ISvcMessageBus"/>. Always registered: the event catalog, the NHibernate outbox (domain events become
/// outbox rows in the entity's transaction) and the inbox. With <c>Kafka:Enabled</c>: topic provisioning, a keyed
/// producer per event topic and per dead-letter topic, the outbox relay, and a topic endpoint per consumer with one
/// consumer group per service and purpose and the Ariva consume pipe (<see cref="ConsumePipeline"/>).
/// </summary>
public static partial class MessagingExtensions
{
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

        // CWE-501: in production every event crosses the network authenticated and encrypted, or the host does not start.
        if (settings.Enabled && configuration["Application:Environment"] == "k8s-prd" &&
            !string.Equals(settings.SecurityProtocol, "SaslSsl", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Kafka in k8s-prd must use SecurityProtocol SaslSsl with a SASL mechanism and credentials.");
        }

        if (!settings.Enabled)
        {
            services.TryAddSingleton<ISvcMessageBus, DisabledMessageBus>();
            return services;
        }

        if (settings.ProvisionTopics)
            services.AddHostedService<TopicProvisioner>();

        services.AddSingleton<KafkaProduce>();
        services.AddSingleton<IOutboxTransport, MassTransitOutboxTransport>();
        services.AddSingleton<IDeadLetterSink, KafkaDeadLetterSink>();
        services.Replace(ServiceDescriptor.Singleton<ISvcMessageBus, KafkaMessageBus>());

        var producerConfig = KafkaClientConfig.Producer(settings);
        var deadLetterTopics = builder.Consumers.Select(c => c.Topic).Concat(builder.StreamTopics)
            .Select(KafkaTopics.DeadLetter).Distinct(StringComparer.Ordinal).ToList();

        services.AddHealthChecks();

        // Before the bus (hosted services start in order): the rider faults for good on a topic that does not exist yet.
        var consumedTopics = builder.Consumers.Select(c => c.Topic).Distinct(StringComparer.Ordinal).ToList();
        services.AddHostedService(provider => new KafkaTopicsReady(settings, consumedTopics,
            provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<KafkaTopicsReady>>()));
        services.AddMassTransit(bus =>
        {
            bus.UsingInMemory((context, memory) => memory.ConfigureEndpoints(context));
            bus.AddRider(rider =>
            {
                foreach (var type in catalog.EventTypes)
                    AddProducerOf(type, rider, catalog.TopicOf(type), producerConfig);
                foreach (var topic in deadLetterTopics)
                    AddProducer<DeadLetter>(rider, topic, producerConfig);
                foreach (var consumer in builder.Consumers)
                    consumer.Register(rider);

                rider.UsingKafka((context, kafka) =>
                {
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
                    foreach (var consumer in builder.Consumers)
                        consumer.Endpoint(context, kafka, settings);
                });
            });
        });

        if (settings.Outbox.Enabled)
            services.AddHostedService<OutboxRelay>();

        return services;
    }

    private static readonly MethodInfo AddProducerGeneric = typeof(MessagingExtensions).GetMethod(nameof(AddProducer), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void AddProducerOf(Type type, IRiderRegistrationConfigurator rider, string topic, ProducerConfig config) =>
        AddProducerGeneric.MakeGenericMethod(type).Invoke(null, [rider, topic, config]);

    private static void AddProducer<T>(IRiderRegistrationConfigurator rider, string topic, ProducerConfig config) where T : class =>
        rider.AddProducer<string, T>(topic, config, (_, producer) => producer.SetValueSerializer(new JsonValueSerializer<T>()));

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    internal static partial Regex PurposeRule();
}

/// <summary>What a host consumes. Each consumer gets its own group, <c>ariva-&lt;service&gt;.&lt;purpose&gt;</c>.</summary>
public sealed class ArivaMessagingBuilder
{
    internal List<Assembly> EventAssemblies { get; } = [];

    internal List<ConsumerRegistration> Consumers { get; } = [];

    internal List<string> StreamTopics { get; } = [];

    /// <summary>
    /// A topic this host reads with <c>PartitionedConsumer</c> (Ariva.Api.Stream): registers its dead-letter producer,
    /// which an unreadable record needs.
    /// </summary>
    public ArivaMessagingBuilder StreamFrom(string topic)
    {
        if (!KafkaTopics.IsKnown(topic))
            throw new ArgumentException($"Unknown topic '{topic}'. Use a KafkaTopics constant.", nameof(topic));
        StreamTopics.Add(topic);
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
            (context, kafka, settings) => kafka.TopicEndpoint<string, TMessage>(topic, $"ariva-{settings.ServiceName}.{purpose}", endpoint =>
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

internal sealed record ConsumerRegistration(
    string Topic,
    string Purpose,
    Action<IRiderRegistrationConfigurator> Register,
    Action<IRiderRegistrationContext, IKafkaFactoryConfigurator, KafkaSettings> Endpoint);
