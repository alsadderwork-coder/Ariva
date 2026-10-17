using System.Globalization;
using System.Text;
using Ariva.Core.Domain.ViewModels;
using Ariva.Infra.Sensing.Declarative;
using Ariva.IntegrationTests.Messaging;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;

namespace Ariva.IntegrationTests.Streaming;

/// <summary>
/// ARV-174 end to end, on a broker of its own: pushes go through Ingest's sensing path into the queue stream, and a resent
/// push is counted once. Q6rt's device sends crossings without a time of their own (stamped at receipt) under a mapping
/// that names the vendor's sequence number: it resends one message 20 seconds later, which now has the id of the first,
/// and every other message (a new sequence number, the same content) counts. Q7rt's device speaks canonical with a
/// <c>packageId</c> and resends one push with <c>sentUtc</c> stamped again: the same package and events, so the same id,
/// counted once. Q8rt's canonical device sends no package and no track ids, one message per person, and two people cross
/// its entry line in the same second of every minute: their messages differ only in <c>sentUtc</c>, so both count (the
/// security review's R1), while a message sent again with its <c>sentUtc</c> unchanged is still counted once.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class QueueStreamResentPushTests(PostgresFixture postgres, KafkaFixture kafka) : IClassFixture<KafkaFixture>
{
    private const int Minutes = 10;
    private const string Received = "Q6rt";
    private const string Canonical = "Q7rt";
    private const string Anonymous = "Q8rt";

    private static readonly DeclarativeMappingCatalog Mappings = new(
    [
        DeclarativeMapping.Parse(Encoding.UTF8.GetBytes("""
            { "name": "it-sequenced-crossings-v1", "title": "Crossings stamped at receipt, with a sequence number (ARV-174 test)",
              "package": "$.seq",
              "crossings": { "items": "$.events[*]", "line": "@.line", "direction": { "path": "@.dir", "in": ["in"], "out": ["out"] }, "time": { "received": true } } }
            """), "it-sequenced-crossings-v1")
    ]);

    private readonly IngestToStream _pipeline = new(postgres, kafka, "resent-push", Mappings);

    private static string Utc(DateTime t) => t.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    /// <summary>Message <paramref name="seq"/> of the receipt-timed device: two in and one out, no times.</summary>
    private static byte[] Sequenced(long seq) => Encoding.UTF8.GetBytes($$"""
        { "seq": {{seq}}, "events": [{ "line": "{{Received}} entry", "dir": "in" }, { "line": "{{Received}} entry", "dir": "in" }, { "line": "{{Received}} exit", "dir": "out" }] }
        """);

    /// <summary>The canonical push of minute <paramref name="m"/>: two anonymous entries and an exit inside it, sent at <paramref name="sent"/>.</summary>
    private static byte[] CanonicalPush(DateTime start, int m, DateTime sent)
    {
        var from = start.AddMinutes(m);
        return Encoding.UTF8.GetBytes($$"""
            { "sentUtc": "{{Utc(sent)}}", "packageId": {{m + 1}},
              "crossings": [{ "lineName": "{{Canonical}} entry", "direction": "In", "timeUtc": "{{Utc(from.AddSeconds(10))}}" },
                            { "lineName": "{{Canonical}} entry", "direction": "In", "timeUtc": "{{Utc(from.AddSeconds(20))}}" },
                            { "lineName": "{{Canonical}} exit", "direction": "Out", "timeUtc": "{{Utc(from.AddSeconds(40))}}" }] }
            """);
    }

    /// <summary>One anonymous crossing of the Q8rt device at <paramref name="at"/>, sent at <paramref name="sent"/>, without a package.</summary>
    private static byte[] OnePerson(string line, string direction, DateTime at, DateTime sent) => Encoding.UTF8.GetBytes($$"""
        { "sentUtc": "{{Utc(sent)}}", "crossings": [{ "lineName": "{{Anonymous}} {{line}}", "direction": "{{direction}}", "timeUtc": "{{Utc(at)}}" }] }
        """);

    [Fact]
    public async Task Stream_Should_CountAResentPushOnce_When_ItsCrossingsAreReceiptTimedWithAPackageOrItsSendTimeIsStampedAgain()
    {
        // Ten minutes ending half an hour ago, a push a second after each: message k of the receipt-timed device (its crossings
        // stamped at that receipt, so in minute k + 1), and the canonical push of minute k. Message 4 is sent again 20 seconds
        // after it; the canonical push of minute 3 is sent again 20 seconds after it with a new sentUtc. Without ARV-174 both
        // resends had new ids: Q6rt's minute 5 and Q7rt's minute 3 held 4 entries and 2 exits.
        await _pipeline.CreateTopicsAsync();
        var now = DateTime.UtcNow;
        var start = new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc).AddMinutes(-30);
        DateTime Receipt(int m) => start.AddMinutes(m + 1).AddSeconds(1);
        var sequenced = IngestToStream.Device("S-96", Received) with { Dialect = "Declarative", MappingName = "it-sequenced-crossings-v1" };
        var canonical = IngestToStream.Device("S-97", Canonical);
        var anonymous = IngestToStream.Device("S-98", Anonymous);
        var sends = new List<(DeviceCredentialRecord Device, byte[] Body, DateTime ReceivedUtc)>();
        for (var m = 0; m < Minutes; m++)
        {
            sends.Add((sequenced, Sequenced(m + 1), Receipt(m)));
            sends.Add((canonical, CanonicalPush(start, m, Receipt(m).AddSeconds(-0.5)), Receipt(m)));
            // Two people cross Q8rt's entry line in the same second, one message each, 0.1 seconds apart; one leaves.
            var second = start.AddMinutes(m).AddSeconds(10);
            sends.Add((anonymous, OnePerson("entry", "In", second, Receipt(m).AddSeconds(-0.5)), Receipt(m)));
            sends.Add((anonymous, OnePerson("entry", "In", second, Receipt(m).AddSeconds(-0.4)), Receipt(m).AddSeconds(0.1)));
            sends.Add((anonymous, OnePerson("exit", "Out", start.AddMinutes(m).AddSeconds(40), Receipt(m).AddSeconds(-0.3)), Receipt(m).AddSeconds(0.2)));
        }

        sends.Add((sequenced, Sequenced(4), Receipt(3).AddSeconds(20)));
        sends.Add((canonical, CanonicalPush(start, 3, Receipt(3).AddSeconds(19.5)), Receipt(3).AddSeconds(20)));
        // Q8rt's first message of minute 3 sent again as it was (its sentUtc unchanged): recognised.
        sends.Add((anonymous, OnePerson("entry", "In", start.AddMinutes(3).AddSeconds(10), Receipt(3).AddSeconds(-0.5)), Receipt(3).AddSeconds(20)));

        var database = await _pipeline.DatabaseAsync(TestDatabase.StreamResentPush);
        await _pipeline.IngestAsync(database, sends, body => Encoding.UTF8.GetString(body).Split("\"line").Length - 1);
        var records = _pipeline.CrossingRecords();
        foreach (var zone in new[] { Received, Canonical })
        {
            records["DMO/" + zone].Should().HaveCount(Minutes + 1, "{0}: the resend reached the stream", zone);
            records["DMO/" + zone].Distinct().Should().HaveCount(Minutes, "{0}: the resend has the id of the push it repeats, the other pushes ids of their own", zone);
        }

        records["DMO/" + Anonymous].Should().HaveCount((3 * Minutes) + 1);
        records["DMO/" + Anonymous].Distinct().Should().HaveCount(3 * Minutes, "the two people of one second have ids of their own; the unchanged resend repeats one");

        await _pipeline.StreamAsync(database);

        // Every closed minute of the three zones holds two entries and one exit, the resent minute included: Q6rt's and Q7rt's
        // resends counted once, Q8rt's two people of one second both counted and its resend once.
        const string LineMinutes = """
            SELECT line_role, to_char(minute_utc AT TIME ZONE 'UTC', 'HH24:MI'), crossings_in, crossings_out FROM line_minute WHERE zone_key = @zone
            ORDER BY line_role, minute_utc
            """;
        foreach (var (zone, resent) in new[] { (Received, start.AddMinutes(4)), (Canonical, start.AddMinutes(3)), (Anonymous, start.AddMinutes(3)) })
        {
            var rows = await _pipeline.RowsAsync(database, LineMinutes, new Dictionary<string, object> { ["zone"] = "DMO/" + zone });
            var closed = rows.Where(r => r.StartsWith("Entry|", StringComparison.Ordinal)).Select(r => r.Split('|')[1]).ToList();
            closed.Should().HaveCountGreaterThanOrEqualTo(6, zone).And.Contain($"{resent:HH:mm}", "{0}: the resent minute closed", zone);
            rows.Should().Equal([.. closed.Select(m => $"Entry|{m}|2|0"), .. closed.Select(m => $"Exit|{m}|0|1")],
                "{0}: the resent push is counted once and every other push counts", zone);
        }
    }
}
