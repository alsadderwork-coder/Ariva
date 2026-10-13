using System.Diagnostics;
using Ariva.Core.Messaging;
using Ariva.Infra.Messaging;
using Ariva.Infra.Messaging.Kafka;
using Ariva.Infra.Messaging.Outbox;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Ariva.UnitTests.Messaging;

/// <summary>
/// ARV-171: a dead letter Kafka did not take before its endpoint stopped is kept as an outbox row, and only then does the
/// record complete; the record is left unacknowledged (ARV-166) only when the letter cannot be kept either. The letter
/// always fits a Kafka message and an outbox row, its row id is the same for a record delivered again, its row key fits
/// <c>message_key</c>, and the relay sends a kept letter only to the dead-letter topic of a known topic.
/// </summary>
public sealed class DeadLetterOutboxTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DeadLetter Letter(string key = "zone-1", long offset = 7) => DeadLetters.Create(KafkaTopics.FlowNowcast, 0, offset, key, [1, 2, 3], [],
        "ariva-it.probe", new InvalidOperationException("probe"), DateTime.UtcNow);

    [Fact]
    public void Create_Should_FitKafkasLimit_When_TheHeadersAreLongAndNotAscii()
    {
        // The JSON encoder writes each of these characters as six bytes: 50 headers of 1,000 + 1,000 take 600 KB, which
        // with a full 512 KB body (683 KB of base64) passed 1 MB before ARV-171.
        var headers = Enumerable.Range(0, 50).Select(i => new KeyValuePair<string, string>(i + new string('é', 999), new string('€', 1000)));

        var letter = DeadLetters.Create(KafkaTopics.FlowNowcast, 3, 42, "zone-9", new byte[900 * 1024], headers, "ariva-main.probe",
            new InvalidOperationException("probe"), DateTime.UtcNow);

        DeadLetters.SizeOf(letter).Should().BeLessThanOrEqualTo(DeadLetters.MaxLetterBytes);
        letter.BodyTruncated.Should().BeTrue();
        letter.BodyBytes.Should().Be(900 * 1024, "the full length is kept");
        Convert.FromBase64String(letter.BodyBase64).Length.Should().BeLessThan(DeadLetters.MaxBodyBytes, "the body gave way first");
        letter.Headers.Should().HaveCount(50, "the headers are kept while the body can give way");
        letter.Key.Should().Be("zone-9");
    }

    [Fact]
    public void Create_Should_LeaveTheHeadersOutAndCutTheKey_When_NothingElseMakesItFit()
    {
        var key = new string('ü', 200_000); // 1.2 MB of JSON on its own
        var headers = new[] { new KeyValuePair<string, string>("h", "v") };

        var letter = DeadLetters.Create(KafkaTopics.FlowNowcast, 3, 42, key, new byte[1024], headers, "ariva-main.probe",
            new InvalidOperationException(new string('x', 5000)), DateTime.UtcNow);

        DeadLetters.SizeOf(letter).Should().BeLessThanOrEqualTo(DeadLetters.MaxLetterBytes);
        letter.BodyBase64.Should().BeEmpty();
        letter.BodyTruncated.Should().BeTrue();
        letter.Headers.Should().Equal(new Dictionary<string, string> { [DeadLetters.HeadersDroppedHeader] = "1" });
        letter.Key.Should().HaveLength(200);
    }

    [Fact]
    public void Create_Should_KeepAnOrdinaryLetterWhole_When_ItFits()
    {
        var letter = DeadLetters.Create(KafkaTopics.FlowNowcast, 3, 42, "zone-9", [1, 2, 3], [new("a", "b")], "ariva-main.probe",
            new InvalidOperationException("probe"), DateTime.UtcNow);

        letter.BodyTruncated.Should().BeFalse();
        Convert.FromBase64String(letter.BodyBase64).Should().Equal(1, 2, 3);
        letter.Headers.Should().Equal(new Dictionary<string, string> { ["a"] = "b" });
    }

    [Fact]
    public void IdOf_Should_BeTheSameForTheSameRecord_When_ItIsKeptAgain()
    {
        DeadLetters.IdOf(Letter()).Should().Be(DeadLetters.IdOf(Letter()), "a record delivered again and kept again adds no second row");
        DeadLetters.IdOf(Letter(offset: 8)).Should().NotBe(DeadLetters.IdOf(Letter()));
        var placeless = Letter() with { Offset = null };
        DeadLetters.IdOf(placeless).Should().NotBe(DeadLetters.IdOf(placeless), "without a place there is nothing to derive it from");
    }

    [Theory]
    [InlineData("zone-1", "zone-1")]
    [InlineData("", "none")]
    public void OutboxKeyOf_Should_UseTheRecordsKey_When_ItFits(string key, string expected) =>
        DeadLetters.OutboxKeyOf(Letter() with { Key = key }).Should().Be(expected);

    [Fact]
    public void OutboxKeyOf_Should_UseADigest_When_TheKeyIsLongerThanTheColumn()
    {
        var key = new string('k', 201);

        var outboxKey = DeadLetters.OutboxKeyOf(Letter() with { Key = key });

        outboxKey.Should().StartWith("sha256-").And.HaveLength(71);
        OutboxLimits.FitsMessageKey(outboxKey).Should().BeTrue();
        DeadLetters.OutboxKeyOf(Letter() with { Key = key }).Should().Be(outboxKey);
    }

    [Fact]
    public async Task Retry_Should_KeepTheLetterAndReturn_When_TheEndpointStopsBeforeKafkaTakesIt()
    {
        var sink = new RefusingSink();
        var store = new Store();
        var stopping = new EndpointStopping();

        var loop = DeadLetterRetry.SendUntilAcceptedAsync(sink, Letter(), TimeProvider.System, NullLogger.Instance, CancellationToken.None, stopping.Token, store);
        await sink.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        stopping.Signal();

        await loop.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        store.Kept.Should().ContainSingle().Which.Offset.Should().Be(7, "the letter is kept, so the record may complete");
    }

    [Fact]
    public async Task Retry_Should_LeaveTheMessageUnacknowledged_When_TheLetterCannotBeKeptEither()
    {
        var sink = new RefusingSink();
        var store = new Store { Fail = true };
        var stopping = new EndpointStopping();
        using var message = new CancellationTokenSource();

        var loop = DeadLetterRetry.SendUntilAcceptedAsync(sink, Letter(), TimeProvider.System, NullLogger.Instance, message.Token, stopping.Token, store);
        await sink.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        stopping.Signal();

        (await loop.Invoking(l => l.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Should().ThrowAsync<OperationCanceledException>(
            "with the database away as well nothing durable was written, so the record must not complete (ARV-166's path)"))
            .Which.CancellationToken.Should().Be(message.Token);
        store.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task Retry_Should_GiveUpTheKeepWithinItsBound_When_TheStoreNeverAnswers()
    {
        var sink = new RefusingSink();
        var store = new Store { Hang = true };
        var stopping = new EndpointStopping();
        var clock = Stopwatch.StartNew();

        var loop = DeadLetterRetry.SendUntilAcceptedAsync(sink, Letter(), TimeProvider.System, NullLogger.Instance, CancellationToken.None, stopping.Token, store);
        await sink.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        clock.Restart();
        stopping.Signal();

        await loop.Invoking(l => l.WaitAsync(DeadLetterRetry.KeepTimeout + TimeSpan.FromSeconds(20), Ct)).Should().ThrowAsync<OperationCanceledException>();
        clock.Elapsed.Should().BeGreaterThanOrEqualTo(DeadLetterRetry.KeepTimeout - TimeSpan.FromMilliseconds(100), "the store had its whole bound")
            .And.BeLessThan(DeadLetterRetry.KeepTimeout + TimeSpan.FromSeconds(20), "the stop is held no longer than the bound");
    }

    [Fact]
    public async Task Relay_Should_RefuseAKeptLetter_When_TheRowsTopicIsNotItsDeadLetterTopic()
    {
        var transport = new KafkaOutboxTransport(new KafkaProduce(new Mock<Confluent.Kafka.IProducer<string, byte[]>>(MockBehavior.Strict).Object, TimeProvider.System, null), new EventCatalog([]));
        var payload = System.Text.Json.JsonSerializer.Serialize(Letter(), EventCatalog.Json);

        foreach (var topic in new[] { KafkaTopics.FlowNowcast, KafkaTopics.DeadLetter(KafkaTopics.AlertStateChanged), "ariva.flow.other.v1.dlq.v1" })
        {
            var row = new OutboxEnvelope(1, Guid.NewGuid(), topic, "zone-1", DeadLetters.OutboxMessageType, payload, new Dictionary<string, string>());

            await transport.Invoking(t => t.SendAsync(row, Ct)).Should().ThrowAsync<InvalidOperationException>(
                $"a kept letter goes only to its source topic's dead-letter topic, not {topic}");
        }

        var unknownSource = new OutboxEnvelope(2, Guid.NewGuid(), "ariva.flow.other.v1.dlq.v1", "zone-1", DeadLetters.OutboxMessageType,
            payload.Replace(KafkaTopics.FlowNowcast, "ariva.flow.other.v1", StringComparison.Ordinal), new Dictionary<string, string>());
        await transport.Invoking(t => t.SendAsync(unknownSource, Ct)).Should().ThrowAsync<InvalidOperationException>("the source topic is not a constant");
    }

    private sealed class RefusingSink : IDeadLetterSink
    {
        public TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SendAsync(DeadLetter letter, CancellationToken ct)
        {
            Attempted.TrySetResult();
            throw new KafkaProduceTimeoutException(DeadLetters.TopicFor(letter), KafkaProduce.SendTimeout);
        }
    }

    private sealed class Store : IDeadLetterStore
    {
        private int _attempts;

        public bool Fail { get; init; }
        public bool Hang { get; init; }
        public List<DeadLetter> Kept { get; } = [];
        public int Attempts => Volatile.Read(ref _attempts);

        public async Task KeepAsync(DeadLetter letter, CancellationToken ct)
        {
            Interlocked.Increment(ref _attempts);
            if (Hang)
                await new TaskCompletionSource().Task; // ignores its token: the caller's bound still ends the wait
            if (Fail)
                throw new TimeoutException("The database is away.");
            lock (Kept)
                Kept.Add(letter);
        }
    }
}
