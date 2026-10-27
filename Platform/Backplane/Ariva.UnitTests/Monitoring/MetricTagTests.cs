using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using Ariva.Core.Flights;
using Ariva.Core.Messaging;
using Ariva.Infra.Border;
using Ariva.Infra.Flights;
using Ariva.Infra.Messaging.Kafka;
using Ariva.Infra.Messaging.Outbox;
using Ariva.Infra.Monitoring;
using Ariva.Infra.Sensing;
using Ariva.Infra.Services.Integration;
using Ariva.Infra.Storage;
using Ariva.Infra.Streaming;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ariva.UnitTests.Monitoring;

/// <summary>
/// ARV-125a (CWE-532, data boundary): no metric tag carries a secret, a credential, a person, officer, traveller or document
/// identifier, or a value taken from a request. Every Ariva metrics type (<see cref="MeterExportTests.MeterTypes"/>) is
/// built on a meter factory of the test's own and driven through every method; every instrument it publishes must have
/// been measured, with tag keys from <see cref="AllowedKeys"/> only. Tag values are constants, validated names or codes
/// (a site that exists, a feed named by the client's credential or by an administrator, never by a request (ARV-179:
/// <see cref="FlightItems_Should_BeTaggedWithTheClientsOwnFeed_When_ARequestNamesAnother"/>), a topic constant, an Ariva
/// group, a check's registration name), and those that come from a record or a registration are reduced to <c>unknown</c> or <c>other</c> otherwise.
/// </summary>
public sealed partial class MetricTagTests
{
    /// <summary>
    /// The tag keys Ariva's instruments may use. Adding one needs a reason a reviewer can check: it must name a bounded set
    /// of configuration values or constants, never a person, a credential or a request's value.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "check", "certificate", "contract", "feed", "feed_kind", "group", "kind", "outcome", "site", "topic"
    };

    private const string Site = "DMO";
    private const string Group = "ariva-integration.aman-egate-intervals";

    private static DeadLetter Letter(string topic = KafkaTopics.AmanEgateIntervalStats, string group = Group) =>
        DeadLetters.Create(topic, 0, 1, "key", [1], [], group, new InvalidOperationException("probe"), DateTime.UtcNow);

    /// <summary>Every method of every metrics type, called with plausible values (MonitoringRulesTests builds the instruments with it too).</summary>
    internal static readonly Dictionary<Type, Action<object>> Drives = new()
    {
        [typeof(KafkaProducerMetrics)] = m =>
        {
            var kafka = (KafkaProducerMetrics)m;
            kafka.FatalError();
            kafka.Recreated();
            kafka.ObserveConsumedTopics(() => [(KafkaTopics.AmanEgateIntervalStats, Group, true)]);
            kafka.ObserveUnconnectedConsumers(() => [(KafkaTopics.AmanEgateIntervalStats, Group, false)]);
            kafka.ObserveConsumerHolds(() => [(KafkaTopics.AmanEgateIntervalStats, Group, 12.5)]);
            kafka.DeadLetter(Letter(), KafkaProducerMetrics.SentOutcome);
            kafka.DeadLetter(Letter(), KafkaProducerMetrics.KeptOutcome);
            kafka.DeadLetter(Letter(), KafkaProducerMetrics.FailedOutcome);
        },
        [typeof(DeviceHealthMetrics)] = m =>
        {
            var devices = (DeviceHealthMetrics)m;
            devices.HeartbeatLost(Site);
            devices.Recovered(Site);
            devices.ZoneDegraded(Site);
            devices.ZoneNotKeyable(Site, "DMO/zone");
            devices.Snapshot(4, 1, 1, 1, TimeSpan.FromMinutes(1));
        },
        [typeof(SensingArchiveMetrics)] = m => ((SensingArchiveMetrics)m).Skipped(SensingArchiveMetrics.BatchKind, 1),
        [typeof(BorderMetrics)] = m =>
        {
            var border = (BorderMetrics)m;
            border.Record("desk-interval-stats", "kafka", "applied");
            border.Unmapped(Site);
            border.ObserveFeedAges(() => [(Site, 30.0)]);
        },
        [typeof(FlightMetrics)] = m =>
        {
            var flights = (FlightMetrics)m;
            flights.Items("legs", Site, "api-aodb", [new FlightItemResult(0, "EK0001", true, [], [])]);
            flights.WentStale(Site, "api-aodb");
            flights.Recovered(Site, "api-aodb");
            flights.Snapshot(1, TimeSpan.FromMinutes(1));
        },
        [typeof(IntegrationMetrics)] = m =>
        {
            var integration = (IntegrationMetrics)m;
            integration.Exchange("issued");
            integration.LockedOut();
        },
        [typeof(HealthStatusMetrics)] = m => ((HealthStatusMetrics)m).PublishAsync(new HealthReport(new Dictionary<string, HealthReportEntry>
        {
            ["database"] = new(HealthStatus.Healthy, null, TimeSpan.Zero, null, null),
            ["kafka"] = new(HealthStatus.Degraded, "a description that is never a tag", TimeSpan.Zero, null, null)
        }, TimeSpan.Zero), CancellationToken.None).GetAwaiter().GetResult(),
        [typeof(CertificateMetrics)] = m =>
        {
            var certificates = (CertificateMetrics)m;
            certificates.Track(CertificateMetrics.DataProtection, DateTime.UtcNow.AddDays(90));
            certificates.Track(CertificateMetrics.MqttServer, DateTime.UtcNow.AddDays(30));
        },
        [typeof(OutboxMetrics)] = m =>
        {
            var outbox = (OutboxMetrics)m;
            outbox.Report(new OutboxBacklog(3, DateTime.UtcNow.AddMinutes(-1)), TimeSpan.FromMinutes(1));
            outbox.BrokerStreak(KafkaTopics.DeviceZoneOccupancy);
            outbox.BrokerStreak(KafkaTopics.DeadLetter(KafkaTopics.AmanEgateIntervalStats));
            outbox.TopicHeld(KafkaTopics.FlowNowcast, OutboxMetrics.PartitionUnavailableHold);
            outbox.TopicHeld(KafkaTopics.FlowNowcast, OutboxMetrics.TopicRefusedHold);
            outbox.TopicHeld(KafkaTopics.DeadLetter(KafkaTopics.AmanEgateIntervalStats), OutboxMetrics.DeadLetterHold);
        },
        [typeof(StreamMetrics)] = m =>
        {
            ((StreamMetrics)m).Report("ariva-stream.queue-engine", new Dictionary<string, long> { [KafkaTopics.DeviceZoneOccupancy] = 5 }, TimeSpan.FromMinutes(1));
            ((StreamMetrics)m).Refused("AUH-TA", nameof(Ariva.Core.Queueing.QueueRejections.BufferFull), 3);
            ((StreamMetrics)m).Checkpointed("ariva-stream.queue-engine");
        },
        [typeof(UploadScanMetrics)] = m => ((UploadScanMetrics)m).Report(
            new Ariva.Core.Services.Storage.MalwareSignatures("1.4.3", 27927, DateTime.UtcNow.AddHours(-3)), TimeSpan.FromMinutes(30)),
        [typeof(Ariva.Api.Common.Logging.SecurityEventMetrics)] = m =>
        {
            var security = (Ariva.Api.Common.Logging.SecurityEventMetrics)m;
            foreach (var kind in Enum.GetValues<Ariva.Api.Common.Logging.SecurityEventKind>())
            {
                security.Count(kind, Ariva.Api.Common.Logging.SecurityEventMetrics.Written);
                security.Count(kind, Ariva.Api.Common.Logging.SecurityEventMetrics.Suppressed);
                security.Count(kind, Ariva.Api.Common.Logging.SecurityEventMetrics.Failed);
            }

            security.ProtectionFailed();
        }
    };

    [Fact]
    public void EveryInstrument_Should_CarryOnlyAllowedTagKeys_When_EveryMetricsTypeIsDriven()
    {
        using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = services.GetRequiredService<IMeterFactory>();
        var published = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var tagKeys = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>(StringComparer.Ordinal);
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (!ReferenceEquals(instrument.Meter.Scope, factory))
                    return;
                published[$"{instrument.Meter.Name}/{instrument.Name}"] = 0;
                l.EnableMeasurementEvents(instrument);
            }
        };
        void Measured(Instrument instrument, ReadOnlySpan<KeyValuePair<string, object>> tags)
        {
            var keys = tagKeys.GetOrAdd($"{instrument.Meter.Name}/{instrument.Name}", _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
            foreach (var tag in tags)
                keys[tag.Key] = 0;
        }

        listener.SetMeasurementEventCallback<int>((instrument, _, tags, _) => Measured(instrument, tags));
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => Measured(instrument, tags));
        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => Measured(instrument, tags));
        listener.Start();

        var built = new List<IDisposable>();
        try
        {
            foreach (var (type, _) in MeterExportTests.MeterTypes())
            {
                Drives.Should().ContainKey(type, $"{type.Name} creates a meter: drive every method of it here so its tags are checked");
                var metrics = ActivatorUtilities.CreateInstance(services, type);
                built.Add((IDisposable)metrics);
                Drives[type](metrics);
            }

            listener.RecordObservableInstruments();
        }
        finally
        {
            foreach (var metrics in built)
                metrics.Dispose();
        }

        published.Keys.Should().NotBeEmpty();
        published.Keys.Except(tagKeys.Keys).Should().BeEmpty("every instrument was measured, so none goes unchecked");
        foreach (var (instrument, keys) in tagKeys)
        {
            keys.Keys.Where(key => !AllowedKeys.Contains(key)).Should().BeEmpty($"{instrument} tags only with allowed keys");
            keys.Keys.Where(key => Forbidden().IsMatch(key)).Should().BeEmpty($"{instrument} carries no person, credential or document key");
        }
    }

    [Fact]
    public void AllowedKeys_Should_NameNoPersonCredentialOrDocument_When_Checked() =>
        AllowedKeys.Should().NotContain(key => Forbidden().IsMatch(key));

    [Theory]
    [InlineData(KafkaTopics.AmanEgateIntervalStats, KafkaTopics.AmanEgateIntervalStats)]
    [InlineData(KafkaTopics.DeviceZoneOccupancy, KafkaTopics.DeviceZoneOccupancy)]
    [InlineData("ariva.flow.other.v1", KafkaProducerMetrics.Unknown)]
    [InlineData("passport 123", KafkaProducerMetrics.Unknown)]
    [InlineData(null, KafkaProducerMetrics.Unknown)]
    public void TopicTag_Should_BeATopicConstantOrUnknown_When_ALetterNamesItsTopic(string topic, string tag) =>
        KafkaProducerMetrics.TopicTag(topic).Should().Be(tag);

    [Theory]
    [InlineData(KafkaTopics.DeviceZoneOccupancy, KafkaTopics.DeviceZoneOccupancy)]
    [InlineData(KafkaTopics.DeviceZoneOccupancy + KafkaTopics.DeadLetterSuffix, KafkaTopics.DeviceZoneOccupancy + KafkaTopics.DeadLetterSuffix)]
    [InlineData("ariva." + KafkaTopics.AmanEgateIntervalStats + KafkaTopics.DeadLetterSuffix, "ariva." + KafkaTopics.AmanEgateIntervalStats + KafkaTopics.DeadLetterSuffix)]
    [InlineData(KafkaTopics.AmanEgateIntervalStats + KafkaTopics.DeadLetterSuffix, OutboxMetrics.UnknownTopic)]
    [InlineData("ariva.flow.other.v1" + KafkaTopics.DeadLetterSuffix, OutboxMetrics.UnknownTopic)]
    [InlineData("passport 123", OutboxMetrics.UnknownTopic)]
    [InlineData(null, OutboxMetrics.UnknownTopic)]
    public void OutboxTopicTag_Should_BeATopicConstantItsDeadLetterTopicOrUnknown_When_ARowNamesItsTopic(string topic, string tag) =>
        OutboxMetrics.TopicTag(topic).Should().Be(tag);

    [Fact]
    public void TopicHolds_Should_BeTaggedWithTheTopicAndAKnownKind_When_TheRelayHoldsATopic()
    {
        // ARV-264: the hold's kind is one of the relay's constants, anything else "other"; the topic as every outbox tag.
        using var metrics = new OutboxMetrics();
        using var reader = new Setup.InstrumentReader(metrics.Meter);

        metrics.TopicHeld(KafkaTopics.FlowNowcast, OutboxMetrics.PartitionUnavailableHold);
        metrics.TopicHeld("passport 123", "a value from a row");

        reader.Sum(OutboxMetrics.TopicHoldsName, ("topic", KafkaTopics.FlowNowcast), ("kind", OutboxMetrics.PartitionUnavailableHold)).Should().Be(1);
        reader.Sum(OutboxMetrics.TopicHoldsName, ("topic", OutboxMetrics.UnknownTopic), ("kind", OutboxMetrics.OtherHold)).Should().Be(1);
    }

    [Theory]
    [InlineData("ariva-stream.queue-engine", "ariva-stream.queue-engine")]
    [InlineData("ariva-integration.aman-egate-intervals", "ariva-integration.aman-egate-intervals")]
    [InlineData("probe-queue", KafkaProducerMetrics.Unknown)]
    [InlineData("ariva-x.Officer Name", KafkaProducerMetrics.Unknown)]
    [InlineData(null, KafkaProducerMetrics.Unknown)]
    public void GroupTag_Should_BeAnArivaGroupOrUnknown_When_ALetterNamesItsConsumer(string group, string tag) =>
        KafkaProducerMetrics.GroupTag(group).Should().Be(tag);

    [Theory]
    [InlineData("database", "database")]
    [InlineData("masstransit-bus", "masstransit-bus")]
    [InlineData("Some Check", HealthStatusMetrics.OtherCheck)]
    [InlineData("", HealthStatusMetrics.OtherCheck)]
    public void CheckTag_Should_BeTheRegistrationsNameOrOther_When_ItIsNotAPlainName(string name, string tag) =>
        HealthStatusMetrics.CheckTag(name).Should().Be(tag);

    [Fact]
    public void DeadLetter_Should_TagAnUnknownTopicAndGroup_When_TheLetterNamesNoneOfAriva()
    {
        using var metrics = new KafkaProducerMetrics();
        using var reader = new Setup.InstrumentReader(metrics.Meter);

        metrics.DeadLetter(Letter("not.a.topic", "anything at all"), KafkaProducerMetrics.FailedOutcome);

        reader.Sum(KafkaProducerMetrics.DeadLettersName, ("topic", KafkaProducerMetrics.Unknown), ("group", KafkaProducerMetrics.Unknown),
            ("outcome", KafkaProducerMetrics.FailedOutcome)).Should().Be(1);
        FluentActions.Invoking(() => metrics.DeadLetter(Letter(), "lost")).Should().Throw<ArgumentOutOfRangeException>("an outcome is sent, kept or failed");
    }

    private const string Client = "ic_aodbclientaodbclientaodbcl";

    /// <summary>An integration batch call to a flight endpoint as the client <see cref="Client"/>, through the real controller.</summary>
    private static async Task<int> PostLegsAsync(Ariva.Api.Integration.Controllers.FlightFeedController controller, string body, string query)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        http.Items[Ariva.Api.Common.Security.IntegrationAuthentication.CallerItem] =
            new Ariva.Core.Domain.ViewModels.IntegrationCallerViewModel(Client, "AODB", ["flights:write"], [Site], false);
        http.Request.Method = "POST";
        http.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString(query);
        http.Request.ContentType = "application/json";
        http.Request.Headers[Ariva.Core.Integration.IntegrationBatches.IdempotencyKeyHeader] = Guid.NewGuid().ToString();
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        http.Request.Body = new MemoryStream(bytes);
        http.Request.ContentLength = bytes.Length;
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = http };
        var result = await controller.Legs(Site, TestContext.Current.CancellationToken);
        return result switch
        {
            Microsoft.AspNetCore.Mvc.ContentResult content => content.StatusCode ?? 200,
            Microsoft.AspNetCore.Mvc.ObjectResult problem => problem.StatusCode ?? 0,
            _ => 0
        };
    }

    /// <summary>
    /// ARV-179 (the ARV-125a review's finding 1): the feed a flight metric is tagged with is the client's own
    /// (<see cref="Ariva.Core.Integration.IntegrationBatches.FeedOf"/>), never a name the request chooses. A body that names a
    /// feed is refused (the batch is read strictly), a query that names one is not read, and every series an accepted batch
    /// adds carries the client's feed, so a client adds at most one feed value per site, kind and outcome however many names
    /// it tries. The other feeds are named by administrators (ACRIS and AMAN pull endpoints) or are constants (<c>ssim</c>).
    /// </summary>
    [Fact]
    public async Task FlightItems_Should_BeTaggedWithTheClientsOwnFeed_When_ARequestNamesAnother()
    {
        using var metrics = new FlightMetrics();
        using var reader = new Setup.InstrumentReader(metrics.Meter);
        var intake = new Moq.Mock<Ariva.Core.Services.Flights.ISvcFlightIntake>();
        intake.Setup(i => i.ApplyLegsAsync(Moq.It.IsAny<string>(), Moq.It.IsAny<string>(), Moq.It.IsAny<IReadOnlyList<FlightLegData>>(),
                Moq.It.IsAny<DateTime?>(), Moq.It.IsAny<CancellationToken>()))
            .Returns((string site, string feed, IReadOnlyList<FlightLegData> legs, DateTime? _, CancellationToken _) =>
            {
                // As SvcFlightIntake ends every call: the items counted under the call's feed.
                IReadOnlyList<FlightItemResult> results = [.. legs.Select((l, i) => new FlightItemResult(i, l.FlightKey, true, [], []))];
                metrics.Items("legs", site, feed, results);
                return Task.FromResult(results);
            });
        var idempotency = new Moq.Mock<Ariva.Core.Services.Integration.ISvcIntegrationIdempotency>();
        idempotency.Setup(i => i.ClaimAsync(Moq.It.IsAny<Ariva.Core.Services.Integration.IdempotencyRequest>(), Moq.It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(Ariva.Core.Services.Integration.IdempotencyClaim.Claimed));
        var controller = new Ariva.Api.Integration.Controllers.FlightFeedController(intake.Object, idempotency.Object, TimeProvider.System);
        const string leg = """{"flightKey":"DMO-RJ111-20261003-A","carrier":"RJ","number":"111","direction":"Arrival","scheduledUtc":"2026-10-03T10:00:00Z"}""";

        (await PostLegsAsync(controller, $$"""{"feed":"chosen-by-the-client","items":[{{leg}}]}""", "")).Should().Be(400, "a batch names no feed");
        (await PostLegsAsync(controller, $$"""{"items":[{{leg}}]}""", "?feed=chosen-by-the-client")).Should().Be(200);
        (await PostLegsAsync(controller, $$"""{"items":[{{leg}}]}""", "?feed=another-name")).Should().Be(200);

        reader.Sum("ariva.flights.items").Should().Be(2);
        reader.Sum("ariva.flights.items", ("feed", Ariva.Core.Integration.IntegrationBatches.FeedOf(Client))).Should().Be(2,
            "every series is the client's own feed, whatever name the request tried");
    }

    [Fact]
    public async Task AidxItems_Should_BeTaggedWithTheClientsOwnFeed_When_TheMessageNamesAnotherOriginator()
    {
        // ARV-258 (Low 6 of the ARV-179 review): AidxController passes the caller's own feed on its own path, not through
        // BatchCall, so it is pinned here as the batch endpoints are above: the message's originator names no feed.
        using var metrics = new FlightMetrics();
        using var reader = new Setup.InstrumentReader(metrics.Meter);
        var feeds = new List<string>();
        var aidx = new Moq.Mock<Ariva.Core.Services.Flights.ISvcAidxIntake>();
        aidx.Setup(i => i.ApplyAsync(Moq.It.IsAny<string>(), Moq.It.IsAny<string>(), Moq.It.IsAny<AidxMessage>(), Moq.It.IsAny<CancellationToken>()))
            .Returns((string site, string feed, AidxMessage message, CancellationToken _) =>
            {
                // As SvcAidxIntake ends every call (through SvcFlightIntake): the legs counted under the call's feed.
                feeds.Add(feed);
                IReadOnlyList<FlightItemResult> results = [.. message.Legs.Select((l, i) => new FlightItemResult(i, $"{l.Airline}{l.FlightNumber}", true, [], []))];
                metrics.Items("legs", site, feed, results);
                return Task.FromResult(results);
            });
        var controller = new Ariva.Api.Integration.Controllers.AidxController(aidx.Object,
            Moq.Mock.Of<Ariva.Core.Services.Integration.ISvcIntegrationIdempotency>(), new Setup.ManualClock(new DateTimeOffset(2026, 10, 3, 9, 1, 0, TimeSpan.Zero)));

        foreach (var originator in new[] { "AODB", "another-feed", "api-ic_otherclientotherclientoth" })
            (await PostAidxAsync(controller, Flights.AidxTests.Message(Flights.AidxTests.Leg())
                .Replace("CompanyShortName=\"AODB\"", $"CompanyShortName=\"{originator}\"", StringComparison.Ordinal))).Should().Be(200);

        feeds.Should().Equal(Enumerable.Repeat(Ariva.Core.Integration.IntegrationBatches.FeedOf(Client), 3), "the intake is called with the client's own feed");
        reader.Sum("ariva.flights.items").Should().Be(3);
        reader.Sum("ariva.flights.items", ("feed", Ariva.Core.Integration.IntegrationBatches.FeedOf(Client))).Should().Be(3,
            "every series is the client's own feed, whatever originator the message names");
    }

    /// <summary>An AIDX message pushed as the client <see cref="Client"/>, through the real controller.</summary>
    private static async Task<int> PostAidxAsync(Ariva.Api.Integration.Controllers.AidxController controller, string xml)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControllers();
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        http.Items[Ariva.Api.Common.Security.IntegrationAuthentication.CallerItem] =
            new Ariva.Core.Domain.ViewModels.IntegrationCallerViewModel(Client, "AODB", ["flights:write"], [Site], false);
        http.Request.Method = "POST";
        http.Request.ContentType = "application/xml";
        var bytes = System.Text.Encoding.UTF8.GetBytes(xml);
        http.Request.Body = new MemoryStream(bytes);
        http.Request.ContentLength = bytes.Length;
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = http };
        var result = await controller.Push(Site, TestContext.Current.CancellationToken);
        return result switch
        {
            Microsoft.AspNetCore.Mvc.ContentResult content => content.StatusCode ?? 200,
            Microsoft.AspNetCore.Mvc.ObjectResult problem => problem.StatusCode ?? 0,
            _ => 0
        };
    }

    // Keys that would name a person, a credential, a document or a network address.
    [GeneratedRegex("user|name|mail|person|officer|travel|passenger|document|passport|nationality|phone|address|^ip$|token|secret|password|credential|key|auth|cookie|session|client|subject|serial",
        RegexOptions.IgnoreCase)]
    private static partial Regex Forbidden();
}
