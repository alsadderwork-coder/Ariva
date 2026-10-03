using System.Text;
using Ariva.Api.Integration.Batches;
using Ariva.Core.Flights;
using Ariva.Core.Integration;
using Ariva.Api.Common.Security;
using Ariva.Api.Common.Settings;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Ariva.UnitTests.Flights;

/// <summary>
/// ARV-043: the Integration API's batch bodies are read strictly (CWE-20, CWE-501): exact member names, no unknown or
/// repeated member, no comments or trailing commas, numbers as numbers, bounded depth and 1 to 500 items; a refusal
/// never echoes the client's text; idempotency keys and feed names have one shape.
/// </summary>
public sealed class IntegrationBatchTests
{
    private const string Leg = """{"flightKey":"DMO-RJ111-20261003-A","carrier":"RJ","number":"111","direction":"Arrival","scheduledUtc":"2026-10-03T10:00:00Z"}""";

    private static (IntegrationBatch<T> Batch, string Error) Parse<T>(string json) => BatchBody.Parse<T>(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Parse_Should_ReadABatch_When_ItIsWellFormed()
    {
        var (batch, error) = Parse<FlightLegData>($$"""{"messageTimeUtc":"2026-10-03T09:59:00Z","items":[{{Leg}},{{Leg}}]}""");

        error.Should().BeNull();
        batch.Items.Should().HaveCount(2);
        batch.MessageTimeUtc.Should().Be(new DateTime(2026, 10, 3, 9, 59, 0, DateTimeKind.Utc));
        batch.MessageTimeUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
        batch.Items[0].Should().Match<FlightLegData>(l => l.FlightKey == "DMO-RJ111-20261003-A" && l.ScheduledUtc!.Value.Kind == DateTimeKind.Utc);
    }

    [Theory]
    [InlineData("""{"items":[{"flightKey":"K1","eventType":"Landed","timeUtc":"2026-10-03T10:00:00Z","extra":1}]}""", "an unknown member")]
    [InlineData("""{"items":[],"items":[{"flightKey":"K1","eventType":"Landed","timeUtc":"2026-10-03T10:00:00Z"}]}""", "a repeated member")]
    [InlineData("""{"items":[{"flightKey":"K1","flightKey":"K2","eventType":"Landed","timeUtc":"2026-10-03T10:00:00Z"}]}""", "a repeated member in an item")]
    [InlineData("""{"Items":[{"flightKey":"K1","eventType":"Landed","timeUtc":"2026-10-03T10:00:00Z"}]}""", "a member in another case")]
    [InlineData("""{"items":[{"flightKey":"K1","eventType":"Landed","timeUtc":"2026-10-03T10:00:00Z"},]}""", "a trailing comma")]
    [InlineData("""{/* note */"items":[{"flightKey":"K1","eventType":"Landed","timeUtc":"2026-10-03T10:00:00Z"}]}""", "a comment")]
    [InlineData("""{"items":[{"flightKey":"K1","eventType":"Landed","timeUtc":"not a time"}]}""", "a time that is not one")]
    [InlineData("""{"items":[{"flightKey":12,"eventType":"Landed","timeUtc":"2026-10-03T10:00:00Z"}]}""", "a number for a string")]
    [InlineData("""{"items":{"flightKey":"K1"}}""", "an object for the items")]
    [InlineData("""[{"flightKey":"K1","eventType":"Landed","timeUtc":"2026-10-03T10:00:00Z"}]""", "a bare array")]
    [InlineData("""{"items":[{"flightKey":"K1","eventType":"Landed","timeUtc":"2026-10-03T10:00:00Z"}]""", "truncated JSON")]
    [InlineData("""{"items":[]}""", "no items")]
    [InlineData("""{"messageTimeUtc":"2026-10-03T10:00:00Z"}""", "no items member")]
    [InlineData("""null""", "null")]
    [InlineData("""{"items":[{"flightKey":{"a":{"b":{"c":{"d":{"e":{"f":{"g":1}}}}}}}}]}""", "nesting beyond 8 levels")]
    public void Parse_Should_RefuseTheBatch_When_TheBodyIsNotStrictlyABatch(string json, string why)
    {
        var (batch, error) = Parse<FlightEventData>(json);

        batch.Should().BeNull(why);
        error.Should().NotBeNullOrEmpty(why);
    }

    [Fact]
    public void Parse_Should_RefuseMoreThan500Items_When_TheBatchIsTooLong()
    {
        var item = """{"flightKey":"K1","eventType":"Landed","timeUtc":"2026-10-03T10:00:00Z"}""";

        Parse<FlightEventData>("{\"items\":[" + string.Join(',', Enumerable.Repeat(item, 500)) + "]}").Error.Should().BeNull();
        Parse<FlightEventData>("{\"items\":[" + string.Join(',', Enumerable.Repeat(item, 501)) + "]}").Error.Should().Contain("1 to 500 items");
    }

    [Theory]
    [InlineData("""{"items":[{"flightKey":"K1","eventType":"Landed","timeUtc":"2026-10-03T10:00:00Z","<script>alert(1)</script>":1}]}""", "<script>")]
    [InlineData("""{"items":[{"flightKey":"K1","eventType":"Landed","timeUtc":"2026-10-03T10:00:00Z","x' OR 1=1 --":1}]}""", "OR 1=1")]
    [InlineData("""{"items":[{"flightKey":"K1","eventType":"Landed","timeUtc":"2026-10-03T10:00:00Z","secretField":"ics_aaaa"}]}""", "secretField")]
    public void Parse_Should_NameOnlyKnownMembers_When_Refusing(string json, string echoed)
    {
        var (_, error) = Parse<FlightEventData>(json);

        error.Should().NotContain(echoed).And.Contain("$.items[0]");
    }

    [Fact]
    public void Parse_Should_KeepAnOffsetTimeOutOfUtc_When_TheClientSendsOne()
    {
        // Not UTC: FlightRules refuses the item, rather than Ariva guessing the zone.
        var (batch, _) = Parse<FlightEventData>("""{"items":[{"flightKey":"K1","eventType":"Landed","timeUtc":"2026-10-03T13:00:00+03:00"}]}""");

        batch.Items[0].TimeUtc!.Value.Kind.Should().NotBe(DateTimeKind.Utc);
        FlightRules.Check(batch.Items[0]).Errors.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("0f8fad5b-d9cb-469f-a165-70867728950e", true)]
    [InlineData("batch-2026-10-03-0001", true)]
    [InlineData("aodb:42.7", true)]
    [InlineData("short", false)]
    [InlineData("-leading-hyphen", false)]
    [InlineData("has space here", false)]
    [InlineData("key'; DROP TABLE x;--", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsKey_Should_AcceptOnlyTheDocumentedShape_When_Checked(string key, bool valid)
    {
        IntegrationBatches.IsKey(key).Should().Be(valid);
        IntegrationBatches.IsKey(new string('a', 64)).Should().BeTrue();
        IntegrationBatches.IsKey(new string('a', 65)).Should().BeFalse();
    }

    [Fact]
    public void FeedOf_Should_NameTheClientsOwnFeed_When_Called()
    {
        var feed = IntegrationBatches.FeedOf("ic_abcdefghijklmnopqrstuvwxyz");

        feed.Should().Be("api-abcdefghijklmnopqrstuvwxyz");
        FlightRules.NormalizeFeed(feed).Should().Be(feed, "a feed name the intake takes");
        var bad = () => IntegrationBatches.FeedOf("ic_' OR 1=1");
        bad.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("application/json", true)]
    [InlineData("application/json; charset=utf-8", true)]
    [InlineData("application/vnd.ariva+json", true)]
    [InlineData("text/plain", false)]
    [InlineData("application/xml", false)]
    [InlineData(null, false)]
    public void IsJson_Should_AcceptOnlyJson_When_Checked(string contentType, bool json)
    {
        BatchBody.IsJson(contentType).Should().Be(json);
    }

    [Fact]
    public void Of_Should_CountAppliedUnchangedAndRefused_When_Summarising()
    {
        var answer = IntegrationBatchViewModel.Of([
            new FlightItemResult(0, "K1", true, [], []),
            new FlightItemResult(1, "K1", false, [], ["Nothing newer than what is known."]),
            new FlightItemResult(2, null, false, ["A flight key is required."], [])
        ]);

        answer.Should().Match<IntegrationBatchViewModel>(a => a.Received == 3 && a.Applied == 1 && a.Unchanged == 1 && a.Refused == 1);
        System.Text.Json.JsonSerializer.Serialize(answer, System.Text.Json.JsonSerializerOptions.Web).Should().NotContain("hasErrors");
    }

    // A request whose body is a stream of the given length, with or without a Content-Length (chunked).
    private static HttpRequest Request(int length, bool withLength)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(new byte[length]);
        context.Request.ContentLength = withLength ? length : null;
        return context.Request;
    }

    [Fact]
    public async Task ReadAsync_Should_StopAtOneMegabyte_When_TheBodyHasNoLength()
    {
        var ct = TestContext.Current.CancellationToken;

        (await BatchBody.ReadAsync(Request(IntegrationBatches.MaxBodyBytes + 1, withLength: false), ct)).Should().BeNull("chunked, one byte beyond the limit");
        (await BatchBody.ReadAsync(Request(IntegrationBatches.MaxBodyBytes, withLength: false), ct)).Should().HaveCount(IntegrationBatches.MaxBodyBytes);
        (await BatchBody.ReadAsync(Request(IntegrationBatches.MaxBodyBytes, withLength: true), ct)).Should().HaveCount(IntegrationBatches.MaxBodyBytes);
    }

    [Fact]
    public async Task ReadAsync_Should_RefuseWithoutReading_When_TheLengthIsBeyondTheLimit()
    {
        var request = Request(16, withLength: true);
        request.ContentLength = IntegrationBatches.MaxBodyBytes + 1;

        (await BatchBody.ReadAsync(request, TestContext.Current.CancellationToken)).Should().BeNull();
        request.Body.Position.Should().Be(0, "nothing was read");
    }

    [Fact]
    public void ClientLimiter_Should_CountEachClientOnItsOwn_When_Batches()
    {
        using var limiter = new IntegrationClientRateLimiter(Options.Create(new RateLimitingSettings { IntegrationClient = new FixedWindowSettings { PermitLimit = 3, WindowSeconds = 60 } }));

        Enumerable.Range(0, 3).Select(_ => limiter.TryAcquire("ic_aaaaaaaaaaaaaaaaaaaaaaaaaa").Allowed).ToList().Should().AllSatisfy(a => a.Should().BeTrue());
        var refused = limiter.TryAcquire("ic_aaaaaaaaaaaaaaaaaaaaaaaaaa");
        refused.Allowed.Should().BeFalse();
        refused.RetryAfter.Should().BePositive();
        limiter.TryAcquire("ic_bbbbbbbbbbbbbbbbbbbbbbbbbb").Allowed.Should().BeTrue("another client has its own allowance");
    }
}
