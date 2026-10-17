using Ariva.Core.Domain.ViewModels;
using Ariva.IntegrationTests.Messaging;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;

namespace Ariva.IntegrationTests.Streaming;

/// <summary>
/// ARV-173 end to end, on a broker of its own: T1 counters push each minute's entry and exit line counts (two readings that
/// end together) through Ingest's sensing path into the queue stream, in three shapes: one push with the entry line first,
/// one with the exit line first, and the two lines in two pushes of the same minute. The stream counts each minute's
/// exits in that minute (before, they landed in the next one), and the three zones' line and queue minutes are the same.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class QueueStreamIntervalTests(PostgresFixture postgres, KafkaFixture kafka) : IClassFixture<KafkaFixture>
{
    private const int Minutes = 12;
    private static readonly string[] Zones = ["Q3ia", "Q4ia", "Q5ia"];
    private readonly IngestToStream _pipeline = new(postgres, kafka, "intervals");

    [Fact]
    public async Task Stream_Should_CountEachMinutesExitsInThatMinute_When_TheEntryAndExitReadingsOfAPushEndTogether()
    {
        // Twelve minutes ending half an hour ago, 5 in and 3 out each, pushed a second after the minute: S-93 for Q3ia in one
        // push (entry, exit), S-94 for Q4ia in one push (exit, entry), S-95 for Q5ia in two pushes (exit, then entry).
        await _pipeline.CreateTopicsAsync();
        var now = DateTime.UtcNow;
        var start = new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc).AddMinutes(-30);
        var sends = new List<(DeviceCredentialRecord Device, byte[] Body, DateTime ReceivedUtc)>();
        for (var m = 0; m < Minutes; m++)
        {
            var received = start.AddMinutes(m + 1).AddSeconds(1);
            sends.Add((IngestToStream.Device("S-93", "Q3ia"), IngestToStream.Push(start, m, m + 1, ("Q3ia entry", 5, 0), ("Q3ia exit", 0, 3)), received));
            sends.Add((IngestToStream.Device("S-94", "Q4ia"), IngestToStream.Push(start, m, m + 1, ("Q4ia exit", 0, 3), ("Q4ia entry", 5, 0)), received));
            sends.Add((IngestToStream.Device("S-95", "Q5ia"), IngestToStream.Push(start, m, (2 * m) + 1, ("Q5ia exit", 0, 3)), received));
            sends.Add((IngestToStream.Device("S-95", "Q5ia"), IngestToStream.Push(start, m, (2 * m) + 2, ("Q5ia entry", 5, 0)), received));
        }

        var database = await _pipeline.DatabaseAsync(TestDatabase.StreamIntervals);
        await _pipeline.IngestAsync(database, sends, body => System.Text.Encoding.UTF8.GetString(body).Split("lineName").Length - 1);
        var records = _pipeline.IntervalRecords();
        records["DMO/Q5ia"].Should().HaveCount(2 * Minutes, "the two lines of each minute are two batches").And.OnlyHaveUniqueItems();
        await _pipeline.StreamAsync(database);

        // The minutes the stream closed (the last pushes' watermark is two minutes and a half before their minute's end): each
        // holds its own 5 entries and 3 exits, on the lines and in the zone, from the first minute on.
        const string LineMinutes = """
            SELECT line_role, to_char(minute_utc AT TIME ZONE 'UTC', 'HH24:MI'), crossings_in, crossings_out FROM line_minute WHERE zone_key = @zone
            ORDER BY line_role, minute_utc
            """;
        const string QueueMinutes = """
            SELECT to_char(minute_utc AT TIME ZONE 'UTC', 'HH24:MI'), status, entries, exits, waits FROM queue_minute WHERE zone_key = @zone ORDER BY minute_utc
            """;
        var first = await _pipeline.RowsAsync(database, LineMinutes, new Dictionary<string, object> { ["zone"] = "DMO/Q3ia" });
        var closed = first.Where(r => r.StartsWith("Entry|", StringComparison.Ordinal)).Select(r => r.Split('|')[1]).ToList();
        closed.Should().HaveCountGreaterThanOrEqualTo(8).And.StartWith($"{start:HH:mm}");
        first.Should().Equal([.. closed.Select(m => $"Entry|{m}|5|0"), .. closed.Select(m => $"Exit|{m}|0|3")],
            "each minute's exits are counted in that minute, from the first minute on, and nowhere else");
        var minutes = await _pipeline.RowsAsync(database, QueueMinutes, new Dictionary<string, object> { ["zone"] = "DMO/Q3ia" });
        // Minutes are final with their 15-minute bin, which twelve minutes need not close: the closed ones are checked whatever
        // their status.
        minutes.Where(r => closed.Contains(r.Split('|')[0])).Select(r => string.Join('|', r.Split('|').Skip(2).Take(2))).Should()
            .HaveCount(closed.Count).And.OnlyContain(r => r == "5|3", "the zone's minutes hold 5 entries and 3 exits each");
        foreach (var zone in Zones.Skip(1))
        {
            var at = new Dictionary<string, object> { ["zone"] = "DMO/" + zone };
            (await _pipeline.RowsAsync(database, LineMinutes, at)).Should().Equal(first, "{0}: the order of the lines or pushes changes no line minute", zone);
            (await _pipeline.RowsAsync(database, QueueMinutes, at)).Should().Equal(minutes, "{0}: nor any queue minute's entries, exits and waits", zone);
        }
    }
}
