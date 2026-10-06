using Ariva.Core.Domain.Components;
using Ariva.Core.Messaging;
using Ariva.Infra.Messaging;
using Ariva.Infra.Messaging.Kafka;
using Ariva.Infra.Messaging.Outbox;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.UnitTests.Messaging;

[KafkaTopic(KafkaTopics.FlowNowcast)]
public sealed class CatalogProbe : EventBase
{
    public string ZoneId { get; set; }

    public override string GetPartitionKey() => ZoneId;
}

/// <summary>ADR-0018 and ADR-0019: topic names, dead-letter names, provisioning defaults and the event catalog.</summary>
public sealed class TopicTests
{
    [Fact]
    public void Owned_Should_FollowTheNamingRule_When_Listed()
    {
        KafkaTopics.Owned.Should().NotBeEmpty();
        KafkaTopics.Owned.Should().OnlyContain(t => KafkaTopics.FollowsNamingRule(t), "ariva.<context>.<event>.v1, lowercase kebab case");
        KafkaTopics.All.Except(KafkaTopics.Owned).Should().OnlyContain(t => t.StartsWith("aman.feed.", StringComparison.Ordinal));
        KafkaTopics.All.Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData("ariva.flow.nowcast.v1", true)]
    [InlineData("ariva.flow.queue-interval.v12", true)]
    [InlineData("ariva.flows.nowcast.v1", false)]
    [InlineData("ariva.flow.Nowcast.v1", false)]
    [InlineData("ariva.flow.now_cast.v1", false)]
    [InlineData("ariva.flow.nowcast", false)]
    [InlineData("tracks.samples.v1", false)]
    public void FollowsNamingRule_Should_MatchAdr0019_When_Checked(string topic, bool follows)
    {
        KafkaTopics.FollowsNamingRule(topic).Should().Be(follows);
    }

    [Fact]
    public void DeadLetter_Should_StayUnderTheArivaPrefix_When_TheSourceIsAman()
    {
        KafkaTopics.DeadLetter(KafkaTopics.FlowNowcast).Should().Be("ariva.flow.nowcast.v1.dlq.v1");
        KafkaTopics.DeadLetter(KafkaTopics.AmanDeskSessionChanged).Should().Be("ariva.aman.feed.desk-session-changed.v1.dlq.v1");
        FluentActions.Invoking(() => KafkaTopics.DeadLetter("ariva.made.up.v1")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TopicCatalog_Should_CoverEveryOwnedTopicAndEveryDeadLetter_When_Provisioning()
    {
        KafkaTopics.Owned.Should().OnlyContain(t => TopicCatalog.Covers(t), "every topic Ariva owns has a retention class");

        var specs = TopicCatalog.ForProvisioning(new TopicDefaults
        {
            Partitions = 6,
            ReplicationFactor = 3,
            MinInSyncReplicas = 2,
            Overrides = { [KafkaTopics.DeviceTrackSample] = new TopicOverride { Partitions = 24, RetentionDays = 1 } }
        });

        specs.Select(s => s.Name).Should().OnlyHaveUniqueItems();
        specs.Should().HaveCount(KafkaTopics.Owned.Count() + KafkaTopics.All.Count);
        specs.Should().NotContain(s => s.Name.StartsWith("aman.", StringComparison.Ordinal), "AMAN's topics are AMAN's to create");
        specs.Single(s => s.Name == KafkaTopics.FlowNowcast).Should().Be(new TopicSpec(KafkaTopics.FlowNowcast, 6, 3, 2, true, null));
        specs.Single(s => s.Name == KafkaTopics.FlowQueueInterval).RetentionDays.Should().Be(30);
        specs.Single(s => s.Name == KafkaTopics.DeviceTrackSample).Should().Be(new TopicSpec(KafkaTopics.DeviceTrackSample, 24, 3, 2, false, 1));
        specs.Single(s => s.Name == KafkaTopics.DeadLetter(KafkaTopics.AmanEgateIntervalStats)).RetentionDays.Should().Be(30);
    }

    [Fact]
    public void TopicSpecification_Should_CarryCleanupRetentionAndSizeLimit_When_Built()
    {
        var compacted = TopicProvisioner.Specification(new TopicSpec(KafkaTopics.DeskStateChanged, 6, 3, 2, true, null));
        var deleting = TopicProvisioner.Specification(new TopicSpec(KafkaTopics.DeskSignal, 12, 1, 1, false, 3));

        compacted.Configs["cleanup.policy"].Should().Be("compact");
        compacted.Configs.Should().NotContainKey("retention.ms");
        compacted.Configs["min.insync.replicas"].Should().Be("2");
        deleting.Configs["cleanup.policy"].Should().Be("delete");
        deleting.Configs["retention.ms"].Should().Be("259200000");
        deleting.Configs["max.message.bytes"].Should().Be("1048576");
        (deleting.NumPartitions, deleting.ReplicationFactor).Should().Be((12, (short)1));
    }

    [Fact]
    public void EventCatalog_Should_ResolveByTypeAndName_When_TheEventHasATopic()
    {
        var catalog = new EventCatalog([typeof(CatalogProbe).Assembly]);

        catalog.TopicOf(typeof(CatalogProbe)).Should().Be(KafkaTopics.FlowNowcast);
        catalog.TryResolve(nameof(CatalogProbe), out var type).Should().BeTrue();
        type.Should().Be<CatalogProbe>();
        catalog.TryResolve("Missing", out _).Should().BeFalse();
        FluentActions.Invoking(() => catalog.TopicOf(typeof(string))).Should().Throw<InvalidOperationException>().WithMessage("*no [KafkaTopic]*");
    }

    [Fact]
    public void EventCatalog_Should_Refuse_When_ATopicIsUnknownOrShared()
    {
        var catalog = new EventCatalog([]);

        FluentActions.Invoking(() => catalog.Register(typeof(CatalogProbe), "ariva.flow.made-up.v1")).Should().Throw<InvalidOperationException>();
        catalog.Register(typeof(CatalogProbe), KafkaTopics.FlowNowcast);
        FluentActions.Invoking(() => catalog.Register(typeof(TopicTests), KafkaTopics.FlowNowcast))
            .Should().Throw<InvalidOperationException>().WithMessage("*already carries*");
    }

    [Fact]
    public void CoreEvents_Should_AllNameAKnownTopic_When_Scanned()
    {
        // Building the catalog over Ariva.Core throws for an unknown topic, a shared topic or a duplicate name.
        FluentActions.Invoking(() => new EventCatalog([typeof(Ariva.Core._IAssemblyMark).Assembly])).Should().NotThrow();
    }

    [Fact]
    public void OutboxHeaders_Should_CarryTypeVersionAndTracing_When_Built()
    {
        var probe = new CatalogProbe { ZoneId = "z", CorrelationId = "c-1", CausationId = "" };

        var headers = OutboxHeaders.For(probe);

        headers.Should().Contain(OutboxHeaders.EventType, nameof(CatalogProbe));
        headers.Should().Contain(OutboxHeaders.EventVersion, "1");
        headers.Should().Contain(OutboxHeaders.CorrelationId, "c-1");
        headers.Should().NotContainKey(OutboxHeaders.CausationId);
    }

    [Fact]
    public void JsonValueSerializer_Should_WritePlainCamelCaseJson_When_Serializing()
    {
        var serializer = new JsonValueSerializer<CatalogProbe>();
        var probe = new CatalogProbe { ZoneId = "zone-7" };

        var bytes = serializer.SerializeAsync(probe, default).GetAwaiter().GetResult();
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        var back = serializer.Deserialize(bytes, false, default);

        text.Should().Contain("\"zoneId\":\"zone-7\"").And.Contain("\"eventType\":\"CatalogProbe\"").And.NotContain("\"message\":", "no MassTransit envelope");
        back.ZoneId.Should().Be("zone-7");
        back.Id.Should().Be(probe.Id);
    }

    [Fact]
    public void DeadLetter_Should_StayUnderOneMegabyte_When_TheBodyAndHeadersAreLarge()
    {
        var body = new byte[900 * 1024];
        var headers = Enumerable.Range(0, 80).Select(i => new KeyValuePair<string, string>($"h{i}", new string('x', 5000)))
            .Append(new KeyValuePair<string, string>("empty", null));

        var letter = DeadLetters.Create(KafkaTopics.FlowNowcast, 3, 42, "zone-9", body, headers, "ariva-main.probe", new InvalidOperationException(new string('e', 5000)), DateTime.UtcNow);
        var size = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(letter, EventCatalog.Json).Length;

        letter.BodyTruncated.Should().BeTrue();
        letter.BodyBytes.Should().Be(900 * 1024);
        Convert.FromBase64String(letter.BodyBase64).Length.Should().Be(DeadLetters.MaxBodyBytes);
        letter.Headers.Should().HaveCount(50);
        letter.Headers.Values.Should().OnlyContain(v => v.Length <= 1000);
        letter.ErrorMessage.Length.Should().Be(2000);
        size.Should().BeLessThan(Ariva.Infra.Messaging.KafkaSettings.MaxMessageBytes, "a dead letter that does not fit would hold its partition forever");
    }

    [Fact]
    public async Task Outbox_Should_RefuseTheCommit_When_AnEventIsTooLargeForKafka()
    {
        var outbox = new NHibernateDomainEventOutbox(new EventCatalog([typeof(CatalogProbe).Assembly]), TimeProvider.System);
        var huge = new CatalogProbe { ZoneId = new string('z', Ariva.Infra.Messaging.KafkaSettings.MaxMessageBytes) };

        var write = () => outbox.WriteAsync(new Ariva.UnitTests.Persistence.FakeStorageProvider(), [huge], TestContext.Current.CancellationToken);

        await write.Should().ThrowAsync<InvalidOperationException>().WithMessage("*larger than a Kafka message*");
    }

    [Fact]
    public void Backoff_Should_DoubleUpToFiveMinutes_When_AttemptsGrow()
    {
        OutboxRelay.Backoff(1).Should().Be(TimeSpan.FromSeconds(2));
        OutboxRelay.Backoff(3).Should().Be(TimeSpan.FromSeconds(8));
        OutboxRelay.Backoff(50).Should().Be(TimeSpan.FromMinutes(5));
    }
}

/// <summary>ARV-020 registration: what the composition root wires with Kafka on and off.</summary>
public sealed class MessagingRegistrationTests
{
    private static Microsoft.Extensions.DependencyInjection.ServiceCollection Services(bool kafka)
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string> { ["Kafka:Enabled"] = kafka ? "true" : "false", ["Kafka:ProvisionTopics"] = "true" })
            .Build();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        Ariva.Di.Extensions.PersistenceExtensions.AddArivaPersistence(services, configuration);
        Ariva.Di.Extensions.MessagingExtensions.AddArivaMessaging(services, configuration);
        return services;
    }

    [Fact]
    public void AddArivaMessaging_Should_RegisterTheOutboxAndRefuseToPublish_When_KafkaIsOff()
    {
        var services = Services(kafka: false);

        services.Should().Contain(d => d.ServiceType == typeof(Ariva.Core.Services.IDomainEventOutbox) && d.ImplementationType == typeof(NHibernateDomainEventOutbox),
            "domain events reach the outbox table even without a broker");
        services.Should().Contain(d => d.ServiceType == typeof(Ariva.Core.Services.ISvcMessageBus) && d.ImplementationType != null && d.ImplementationType.Name == "DisabledMessageBus");
        services.Should().NotContain(d => d.ImplementationType == typeof(OutboxRelay));
    }

    [Fact]
    public async Task AddArivaMessaging_Should_RegisterRelayProvisioningAndAReadinessCheck_When_KafkaIsOn()
    {
        var services = Services(kafka: true);
        await using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);

        services.Should().Contain(d => d.ImplementationType == typeof(OutboxRelay));
        services.Should().Contain(d => d.ImplementationType != null && d.ImplementationType.Name == "TopicProvisioner");
        var checks = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckServiceOptions>>(provider).Value.Registrations;
        checks.Should().Contain(c => c.Tags.Contains(Ariva.Api.Common.HealthChecks.HealthEndpoints.ReadyTag), "MassTransit's bus check gates readiness");
    }

    [Fact]
    public async Task AddArivaMessaging_Should_TurnOffMassTransitUsageTelemetry_When_KafkaIsOn()
    {
        // ARV-097: every host registers the bus here, so this is the one place that must keep MassTransit from
        // reporting versions, the OS, the time zone and the topic names to an endpoint outside the deployment.
        var services = Services(kafka: true);
        await using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);

        var options = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<MassTransit.UsageTelemetryOptions>>(provider).Value;

        options.Enabled.Should().BeFalse("an on-premises border deployment makes no call home");
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    public void AddArivaMessaging_Should_RefuseToStart_When_TheEnvironmentTurnsUsageTelemetryBackOn(string value, bool refused)
    {
        // ARV-097: MassTransit's tracker reads this variable after the options are built and would report anyway.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Kafka:Enabled"] = "true",
            ["MASSTRANSIT_USAGE_TELEMETRY"] = value
        }).Build();

        var add = () => Ariva.Di.Extensions.MessagingExtensions.AddArivaMessaging(new ServiceCollection(), configuration);

        if (refused)
            add.Should().Throw<InvalidOperationException>().WithMessage("*MASSTRANSIT_USAGE_TELEMETRY*");
        else
            add.Should().NotThrow();
    }

    [Theory]
    [InlineData("k8s-prd", "Plaintext", true)]
    [InlineData("k8s-prd", "Ssl", true)]
    [InlineData("k8s-prd", "SaslSsl", false)]
    [InlineData("k8s-dev", "Plaintext", false)]
    public void AddArivaMessaging_Should_RefuseUnauthenticatedKafka_When_InProduction(string environment, string protocol, bool refused)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Application:Environment"] = environment,
            ["Kafka:Enabled"] = "true",
            ["Kafka:SecurityProtocol"] = protocol,
            ["Kafka:SaslMechanism"] = "ScramSha512"
        }).Build();

        var add = () => Ariva.Di.Extensions.MessagingExtensions.AddArivaMessaging(new ServiceCollection(), configuration);

        if (refused)
            add.Should().Throw<InvalidOperationException>().WithMessage("*SaslSsl*");
        else
            add.Should().NotThrow();
    }
}
