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
    public void Create_Should_FlagTheHalfPairAndCountOnlyTheRecordsHeaders_When_TheLastCutSplitsTheKeysPair()
    {
        // ARV-175: the last cut (200 characters) falls between the halves of an emoji; a NUL in a header flags the headers first.
        var key = new string('a', 199) + string.Concat(Enumerable.Repeat("\U0001F600", 100_000)); // 1.2 MB of JSON on its own
        var headers = new[] { new KeyValuePair<string, string>("h", "v\0") };

        var letter = DeadLetters.Create(KafkaTopics.FlowNowcast, 3, 42, key, [1], headers, "ariva-main.probe",
            new InvalidOperationException("probe"), DateTime.UtcNow);

        DeadLetters.SizeOf(letter).Should().BeLessThanOrEqualTo(DeadLetters.MaxLetterBytes);
        letter.Key.Should().HaveLength(200).And.EndWith("a\uFFFD");
        letter.Headers.Should().Equal(new Dictionary<string, string>
        {
            [DeadLetters.HeadersDroppedHeader] = "1",
            [DeadLetters.TextReplacedHeader] = "headers,key"
        }, "one record header was left out (the letter's own flag is not counted), and the cut key's half pair replaced");
        DeadLetters.OutboxKeyOf(letter).Should().StartWith("sha256-");
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
    public void Create_Should_ReplaceNulAndFlagTheLetter_When_TheKeyAndAHeaderHoldOne()
    {
        // ARV-175: PostgreSQL text refuses NUL, so a letter with one could not be kept.
        var letter = DeadLetters.Create(KafkaTopics.FlowNowcast, 3, 42, "zone\0-9", [1], [new("h\0", "v\0w"), new("a", "b")], "ariva-main.probe",
            new InvalidOperationException("probe"), DateTime.UtcNow);

        letter.Key.Should().Be("zone\uFFFD-9");
        letter.Headers.Should().Contain("h\uFFFD", "v\uFFFDw").And.Contain("a", "b")
            .And.Contain(DeadLetters.TextReplacedHeader, "headers,key", "the parts named, never the characters");
        (letter.Topic, letter.Partition, letter.Offset, letter.Consumer).Should().Be((KafkaTopics.FlowNowcast, 3, 42L, "ariva-main.probe"),
            "a replay fetches the original record by its exact place");
        DeadLetters.OutboxKeyOf(letter).Should().StartWith("sha256-", "the key is no longer the record's");
    }

    [Fact]
    public void Create_Should_ReplaceALoneSurrogate_When_TheErrorMessageHoldsOne()
    {
        // ARV-175: jsonb refuses an unpaired surrogate; a pair is kept.
        var letter = DeadLetters.Create(KafkaTopics.FlowNowcast, 3, 42, "zone-9", [1], [new("smile", "\U0001F600")], "ariva-main.probe",
            new InvalidOperationException("bad \uD800 and \uDC00 text"), DateTime.UtcNow);

        letter.ErrorMessage.Should().Be("bad \uFFFD and \uFFFD text");
        letter.Headers.Should().Contain("smile", "\U0001F600").And.Contain(DeadLetters.TextReplacedHeader, "error");
        DeadLetters.OutboxKeyOf(letter).Should().Be("zone-9", "the key held nothing to replace");
    }

    [Fact]
    public void Create_Should_ReplaceTheHalfPair_When_ACutSplitsASurrogatePair()
    {
        // A header value is cut at 1,000 characters: here between the two halves of an emoji.
        var letter = DeadLetters.Create(KafkaTopics.FlowNowcast, 3, 42, "zone-9", [1], [new("long", new string('a', 999) + "\U0001F600")], "ariva-main.probe",
            new InvalidOperationException("probe"), DateTime.UtcNow);

        letter.Headers["long"].Should().HaveLength(1000).And.EndWith("a\uFFFD");
        letter.Headers[DeadLetters.TextReplacedHeader].Should().Be("headers");
    }

    [Fact]
    public void Create_Should_AddNoFlag_When_NothingWasReplaced()
    {
        var letter = Letter();

        letter.Headers.Should().NotContainKey(DeadLetters.TextReplacedHeader);
        DeadLetters.OutboxKeyOf(letter).Should().Be("zone-1");
    }

    [Fact]
    public void Create_Should_KeepTheRecordsHeaderApartFromTheFlags_When_ItIsNamedLikeOne()
    {
        // ARV-175: a record cannot set the letter's flags; a spoofed "key" flag would have keyed a clean key by its digest.
        var letter = DeadLetters.Create(KafkaTopics.FlowNowcast, 3, 42, "zone-9", [1],
            [new(DeadLetters.TextReplacedHeader, "key"), new("Ariva-Dead-Letter-Headers-Dropped", "3")], "ariva-main.probe",
            new InvalidOperationException("probe"), DateTime.UtcNow);

        letter.Headers.Should().NotContainKey(DeadLetters.TextReplacedHeader).And.NotContainKey("Ariva-Dead-Letter-Headers-Dropped");
        letter.Headers.Should().Contain(DeadLetters.RecordHeaderPrefix + DeadLetters.TextReplacedHeader, "key")
            .And.Contain(DeadLetters.RecordHeaderPrefix + "Ariva-Dead-Letter-Headers-Dropped", "3");
        DeadLetters.OutboxKeyOf(letter).Should().Be("zone-9", "nothing was replaced in the key");

        var flagged = DeadLetters.Create(KafkaTopics.FlowNowcast, 3, 42, "zone\0-9", [1], [new(DeadLetters.TextReplacedHeader, "error")], "ariva-main.probe",
            new InvalidOperationException("probe"), DateTime.UtcNow);
        flagged.Headers.Should().Contain(DeadLetters.TextReplacedHeader, "key", "the letter's own flag, not the record's")
            .And.Contain(DeadLetters.RecordHeaderPrefix + DeadLetters.TextReplacedHeader, "error");
    }

    [Theory]
    [InlineData(@"", @"", false)]
    [InlineData(@"plain", @"plain", false)]
    [InlineData(@"a\u0000b", @"a\uFFFDb", true)]
    [InlineData(@"\uD83D\uDE00", @"\uD83D\uDE00", false)]
    [InlineData(@"\uDE00x", @"\uFFFDx", true)]
    [InlineData(@"x\uD83D", @"x\uFFFD", true)]
    [InlineData(@"\uD83D\uD83D\uDE00", @"\uFFFD\uD83D\uDE00", true)]
    [InlineData(@"\uDE00\uD83D", @"\uFFFD\uFFFD", true)]
    public void Printable_Should_ReplaceOnlyNulAndUnpairedSurrogates_When_GivenText(string escapedText, string escapedExpected, bool replaced)
    {
        // Escaped, since xUnit serializes theory data and a lone surrogate would not survive it.
        var text = System.Text.RegularExpressions.Regex.Unescape(escapedText);

        DeadLetters.Printable(text, out var wasReplaced).Should().Be(System.Text.RegularExpressions.Regex.Unescape(escapedExpected));
        wasReplaced.Should().Be(replaced);
    }

    /// <summary>ARV-125a: the dead letters counted by outcome, for the letters of <see cref="Letter"/>.</summary>
    private static double Counted(Setup.InstrumentReader reader, string outcome) => reader.Sum(KafkaProducerMetrics.DeadLettersName,
        ("topic", KafkaTopics.FlowNowcast), ("group", "ariva-it.probe"), ("outcome", outcome));

    [Fact]
    public async Task Retry_Should_KeepTheLetterAndReturn_When_TheEndpointStopsBeforeKafkaTakesIt()
    {
        var sink = new RefusingSink();
        var store = new Store();
        var stopping = new EndpointStopping();
        using var metrics = new KafkaProducerMetrics();
        using var reader = new Setup.InstrumentReader(metrics.Meter);

        var loop = DeadLetterRetry.SendUntilAcceptedAsync(sink, Letter(), TimeProvider.System, NullLogger.Instance, CancellationToken.None, stopping.Token, store, metrics);
        await sink.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        stopping.Signal();

        await loop.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        store.Kept.Should().ContainSingle().Which.Offset.Should().Be(7, "the letter is kept, so the record may complete");
        Counted(reader, KafkaProducerMetrics.KeptOutcome).Should().Be(1, "ARV-125a: counted once, as kept");
        reader.Sum(KafkaProducerMetrics.DeadLettersName).Should().Be(1, "a refused attempt is not counted");
    }

    [Fact]
    public async Task Retry_Should_LeaveTheMessageUnacknowledged_When_TheLetterCannotBeKeptEither()
    {
        var sink = new RefusingSink();
        var store = new Store { Fail = true };
        var stopping = new EndpointStopping();
        using var message = new CancellationTokenSource();
        using var metrics = new KafkaProducerMetrics();
        using var reader = new Setup.InstrumentReader(metrics.Meter);

        var loop = DeadLetterRetry.SendUntilAcceptedAsync(sink, Letter(), TimeProvider.System, NullLogger.Instance, message.Token, stopping.Token, store, metrics);
        await sink.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        stopping.Signal();

        (await loop.Invoking(l => l.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Should().ThrowAsync<OperationCanceledException>(
            "with the database away as well nothing durable was written, so the record must not complete (ARV-166's path)"))
            .Which.CancellationToken.Should().Be(message.Token);
        store.Attempts.Should().Be(1);
        Counted(reader, KafkaProducerMetrics.FailedOutcome).Should().Be(1, "ARV-125a: neither sent nor kept, the record is delivered again");
        reader.Sum(KafkaProducerMetrics.DeadLettersName).Should().Be(1);
    }

    [Fact]
    public async Task Retry_Should_CountTheLetterSentOnce_When_TheBrokerTakesItAfterARefusal()
    {
        var sink = new RefusingSink { Refusals = 1 };
        using var metrics = new KafkaProducerMetrics();
        using var reader = new Setup.InstrumentReader(metrics.Meter);

        await DeadLetterRetry.SendUntilAcceptedAsync(sink, Letter(), TimeProvider.System, NullLogger.Instance, CancellationToken.None, CancellationToken.None,
            new Store(), metrics).WaitAsync(TimeSpan.FromSeconds(20), Ct);

        Counted(reader, KafkaProducerMetrics.SentOutcome).Should().Be(1, "ARV-125a: the broker took it on its second attempt");
        reader.Sum(KafkaProducerMetrics.DeadLettersName).Should().Be(1, "the refusal before it is not a dead letter of its own");
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
        private int _attempts;

        public TaskCompletionSource Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>How many attempts are refused before one is taken; all of them by default.</summary>
        public int Refusals { get; init; } = int.MaxValue;

        public Task SendAsync(DeadLetter letter, CancellationToken ct)
        {
            Attempted.TrySetResult();
            if (Interlocked.Increment(ref _attempts) > Refusals)
                return Task.CompletedTask;
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
