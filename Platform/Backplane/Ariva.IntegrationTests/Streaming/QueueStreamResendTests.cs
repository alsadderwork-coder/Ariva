using Ariva.Core.Domain.ViewModels;
using Ariva.IntegrationTests.Messaging;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;

namespace Ariva.IntegrationTests.Streaming;

/// <summary>
/// ARV-168 end to end, on a broker of its own: a T1 counter pushes interval counts through Ingest's sensing path (the
/// canonical mapper, the clock estimate, the batch ids and the Kafka sink of Ariva.Api.Ingest) and sends one push again
/// twice, as a device does when it dropped the request; the same body gets the same batch id, the three records reach
/// the queue stream, and the stream's minute counts apply the interval once: the zone's line and queue minutes are those
/// of a zone whose device never resent.
/// </summary>

[Collection(PostgresCollection.Name)]
public sealed class QueueStreamResendTests(PostgresFixture postgres, KafkaFixture kafka) : IClassFixture<KafkaFixture>
{
    private const string Resent = "Q8rs";
    private const string Control = "Q9rs";
    private const int Minutes = 12;
    private readonly IngestToStream _pipeline = new(postgres, kafka, "resend");

    /// <summary>A canonical push of minute <paramref name="m"/>: 5 in on the entry line and 3 out on the exit line.</summary>
    private static byte[] Push(string zone, DateTime start, int m) => IngestToStream.Push(start, m, m + 1, ($"{zone} entry", 5, 0), ($"{zone} exit", 0, 3));

    [Fact]
    public async Task Stream_Should_ApplyTheIntervalOnce_When_ADeviceResendsAPushThroughIngestWithTheSameBatchId()
    {
        // Twelve minutes ending half an hour ago (Ingest and the stream check event times against their clocks). S-81 pushes
        // its counts a second after each minute and sends minute 3's push again 20 seconds later and after its next push:
        // both times before the push of minute 5, which would have made the repeat late, so without ARV-168 the engine
        // applied the interval three times (10 more entries in minute 3). S-92 pushes the same counts for the control zone
        // and never resends.
        await _pipeline.CreateTopicsAsync();
        var now = DateTime.UtcNow;
        var start = new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc).AddMinutes(-30);
        DateTime Received(int m) => start.AddMinutes(m + 1).AddSeconds(1);
        var sends = new List<(DeviceCredentialRecord Device, byte[] Body, DateTime ReceivedUtc)>();
        for (var m = 0; m < Minutes; m++)
        {
            sends.Add((IngestToStream.Device("S-81", Resent), Push(Resent, start, m), Received(m)));
            sends.Add((IngestToStream.Device("S-92", Control), Push(Control, start, m), Received(m)));
        }

        sends.Add((IngestToStream.Device("S-81", Resent), Push(Resent, start, 3), Received(3).AddSeconds(19)));
        sends.Add((IngestToStream.Device("S-81", Resent), Push(Resent, start, 3), Received(4).AddSeconds(4)));

        var database = await _pipeline.DatabaseAsync(TestDatabase.StreamResend);
        await _pipeline.IngestAsync(database, sends, _ => 2);

        // Ingest gave the resent body its first id: the stream got minute 3's batch three times under one id.
        var records = _pipeline.IntervalRecords();
        records["DMO/" + Resent].Should().HaveCount(Minutes + 2);
        var minute3 = records["DMO/" + Resent][3];
        records["DMO/" + Resent].Count(id => id == minute3).Should().Be(3, "the two resends carry minute 3's batch id");
        records["DMO/" + Resent].Where(id => id != minute3).Should().HaveCount(Minutes - 1).And.OnlyHaveUniqueItems();
        records["DMO/" + Control].Should().HaveCount(Minutes).And.OnlyHaveUniqueItems();

        await _pipeline.StreamAsync(database);

        // The stream's minute counts: every closed minute of the entry line holds one push's 5 entries (minute 3 and the
        // next among them); the zone's lines and minutes are the control zone's, minute by minute.
        var resent = new Dictionary<string, object> { ["zone"] = "DMO/" + Resent };
        var control = new Dictionary<string, object> { ["zone"] = "DMO/" + Control };
        const string EntryMinutes = """
            SELECT to_char(minute_utc AT TIME ZONE 'UTC', 'HH24:MI'), crossings_in FROM line_minute WHERE zone_key = @zone AND line_role = 'Entry' ORDER BY minute_utc
            """;
        var entries = await _pipeline.RowsAsync(database, EntryMinutes, resent);
        entries.Should().HaveCountGreaterThanOrEqualTo(8, "at least the first eight minutes closed").And.OnlyContain(r => r.EndsWith("|5", StringComparison.Ordinal),
            "each minute's 5 entries count once, the resent minute 3 and minute 4 included");
        entries.Should().Contain($"{start.AddMinutes(3):HH:mm}|5").And.Contain($"{start.AddMinutes(4):HH:mm}|5");
        const string Lines = "SELECT line_role, minute_utc, crossings_in, crossings_out FROM line_minute WHERE zone_key = @zone ORDER BY line_role, minute_utc";
        (await _pipeline.RowsAsync(database, Lines, resent)).Should().Equal(await _pipeline.RowsAsync(database, Lines, control), "the resends change no line minute");
        const string QueueMinutes = "SELECT minute_utc, status, entries, exits, waits FROM queue_minute WHERE zone_key = @zone ORDER BY minute_utc";
        var minutes = await _pipeline.RowsAsync(database, QueueMinutes, resent);
        minutes.Should().NotBeEmpty().And.Equal(await _pipeline.RowsAsync(database, QueueMinutes, control), "nor any queue minute's entries, exits and waits");
        (await _pipeline.RowsAsync(database, "SELECT coalesce(sum(entries), 0) FROM queue_minute WHERE zone_key = @zone AND minute_utc = @minute",
            new Dictionary<string, object> { ["zone"] = "DMO/" + Resent, ["minute"] = start.AddMinutes(3) })).Should().Equal(["5"], "minute 3's interval is applied once");
    }
}
