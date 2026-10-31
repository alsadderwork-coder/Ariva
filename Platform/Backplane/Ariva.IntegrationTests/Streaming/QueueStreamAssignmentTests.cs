using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Messaging;
using Ariva.Core.Sensing;
using Ariva.Infra.Messaging;
using Ariva.Infra.Streaming;
using Ariva.IntegrationTests.Messaging;
using Ariva.IntegrationTests.Setup;
using Confluent.Kafka;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Ariva.IntegrationTests.Streaming;

/// <summary>
/// ARV-274 on the live path, on a broker of its own: a zone whose only device reported itself offline is restored from its saved state
/// when the stream starts again and takes the zone's partition, with no record of the zone arriving, so it is idle-ticked, keeps its
/// spell without coverage, counts in ariva.stream.zones_unknown at once, has its bins stored Unknown, and leaves the gauge once its device
/// is forgotten (Stream:DeviceForgetHours). Saved zones no longer in the published profile, or saved under another profile version, are
/// not restored. The restores' bounds, their summary and their database failures: <see cref="QueueStreamRestoreTests"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class QueueStreamAssignmentTests(PostgresFixture postgres, KafkaFixture kafka) : IClassFixture<KafkaFixture>
{
    private static readonly DateTime Start = new(2026, 9, 28, 17, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly StreamAssignmentHarness _h = new(postgres, kafka);

    private Task<List<string>> RowsAsync(string database, [ConstantExpected] string sql, IReadOnlyDictionary<string, object> parameters) =>
        _h.RowsAsync(database, sql, parameters, Ct);

    private static async Task<double?> UnknownAtDmo(InstrumentReader reader, double expected, int seconds = 30)
    {
        double? seen;
        var until = DateTime.UtcNow.AddSeconds(seconds);
        do
        {
            seen = reader.Observe(StreamMetrics.ZonesUnknownName).Where(m => m.Tags["site"] == "DMO").Select(m => (double?)m.Value).LastOrDefault();
            if (seen == expected)
                break;
            await Task.Delay(200, Ct);
        }
        while (DateTime.UtcNow < until);
        return seen;
    }

    [Fact]
    public async Task Worker_Should_RestoreADarkZoneOnAssignment_When_NoRecordOfItArrivesAfterARestart()
    {
        // S-76, the only device of Q8as, reads the queue each minute to 17:10 and reports itself offline at 17:12; the stream applies it
        // and stops with the spell open. Two more saved zones: X8as, no longer in the published profile (the fixed geometry knows only
        // zones whose name starts with Q), and Q8mv, saved under profile version 6 where 7 is published; both copies of Q8as's state, so
        // either would count as Unknown if it were restored.
        await _h.CreateTopicsAsync();
        const string zone = "Q8as";
        var device = Guid.Parse("0199a000-0000-7000-8000-000000000076");
        DateTime End(int m) => Start.AddMinutes(m + 1);
        var offlineAt = Start.AddMinutes(12);
        var records = Enumerable.Range(0, 11).Select(m => (KafkaTopics.DeviceZoneOccupancy, (Ariva.Core.Domain.Contracts.IEvent)new ZoneOccupancyBatch
        {
            Id = Guid.NewGuid(), DeviceId = device, DeviceCode = "S-76", SiteCode = "DMO", QueueZoneName = zone, Dialect = "canonical", Commissioned = true,
            ReceivedUtc = End(m).AddSeconds(0.5), Clock = new ClockReading(0, true, ClockState.Ok),
            Occupancy = [new Sensed<ZoneOccupancy>(new ZoneOccupancy(zone, 2, End(m)), End(m), SensedFlags.None)]
        })).Append((KafkaTopics.DeviceHealth, new DeviceHealthReported
        {
            Id = Guid.NewGuid(), DeviceId = device, DeviceCode = "S-76", SiteCode = "DMO", QueueZoneName = zone, Commissioned = true,
            Status = new DeviceStatus(false, null, null, null, offlineAt), ReceivedUtc = offlineAt, OccurredOn = offlineAt
        }));
        using (var producer = new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = _h.BootstrapServers, EnableIdempotence = true }).Build())
        {
            foreach (var (topic, e) in records)
            {
                var key = e is SensingBatch b ? b.ZoneKey : ((DeviceHealthReported)e).ZoneKey;
                await producer.ProduceAsync(topic, new Message<string, byte[]> { Key = key, Value = JsonSerializer.SerializeToUtf8Bytes(e, e.GetType(), EventCatalog.Json) }, Ct);
            }
        }

        var name = await _h.DatabaseAsync(TestDatabase.StreamAssigned, Ct);
        var at = new Dictionary<string, object> { ["zone"] = "DMO/" + zone };
        const string Saved = "SELECT state->>'version', jsonb_array_length(state->'coverage'), jsonb_array_length(state->'devices') FROM stream_zone_state WHERE zone_key = @zone";
        var clock = new MovableClock();

        using (var host = _h.Host(name, new Dictionary<string, string>(), clock))
        {
            using var reader = new InstrumentReader(host.Services.GetRequiredService<StreamMetrics>().Meter);
            await host.StartAsync(Ct);
            (await UnknownAtDmo(reader, 1, 120)).Should().Be(1, "S-76, the zone's only device, reported itself offline");
            var until = DateTime.UtcNow.AddSeconds(60);
            while (!(await RowsAsync(name, Saved, at)).SequenceEqual(new[] { "15|2|1" }) && DateTime.UtcNow < until)
                await Task.Delay(250, Ct);
            await host.StopAsync(Ct);
        }

        (await RowsAsync(name, Saved, at)).Should().Equal(["15|2|1"], "both lines without a live covering device, one device tracked, when the stream stops");
        const string Copy = """
            INSERT INTO stream_zone_state (zone_key, profile_version, state, state_bytes, reference_utc, updated_on)
            SELECT @key, @version, jsonb_set(jsonb_set(state, '{zoneKey}', to_jsonb(@key::text)), '{profileVersion}', to_jsonb(@version)), state_bytes, reference_utc,
                   '2026-10-01T00:00:00Z'
            FROM stream_zone_state WHERE zone_key = @zone
            """;
        await RowsAsync(name, Copy, new Dictionary<string, object> { ["key"] = "DMO/X8as", ["version"] = 7, ["zone"] = "DMO/" + zone });
        await RowsAsync(name, Copy, new Dictionary<string, object> { ["key"] = "DMO/Q8mv", ["version"] = 6, ["zone"] = "DMO/" + zone });
        const string Untouched = "SELECT zone_key, updated_on = '2026-10-01T00:00:00Z' FROM stream_zone_state WHERE zone_key IN ('DMO/X8as', 'DMO/Q8mv') ORDER BY zone_key";

        using (var host = _h.Host(name, new Dictionary<string, string> { ["Stream:IdleTickSeconds"] = "1" }, clock))
        {
            using var reader = new InstrumentReader(host.Services.GetRequiredService<StreamMetrics>().Meter);
            await host.StartAsync(Ct);

            // Restored on assignment: no record of the zone is produced after the restart.
            (await UnknownAtDmo(reader, 1, 120)).Should().Be(1, "the restored zone is still without a measurement, and only it: the two copies are not restored");
            await Task.Delay(TimeSpan.FromSeconds(3), Ct);
            (await UnknownAtDmo(reader, 1)).Should().Be(1, "idle ticks move its clock on, and the spell stays open");

            // A day and an hour on the host's clock: the next idle tick moves the zone's clock past 24 hours of S-76's silence, so the device
            // is forgotten, the zone tracks none and leaves the gauge; its bins over the spell become final, Unknown.
            clock.MoveOn(TimeSpan.FromHours(25));
            (await UnknownAtDmo(reader, 0, 60)).Should().Be(0, "a zone whose devices are all forgotten is taken as out of use (ARV-259e3)");
            var finals = await Finals();
            var until = DateTime.UtcNow.AddSeconds(60);
            while (finals.Count < 2 && DateTime.UtcNow < until)
            {
                await Task.Delay(500, Ct);
                finals = await Finals();
            }

            await host.StopAsync(Ct);
            finals.Should().StartWith(["17:00|Unknown", "17:15|Unknown"], "no measurement at all since 17:12 (F11), marked while the zone was held without a record");
        }

        (await RowsAsync(name, Untouched, new Dictionary<string, object>())).Should().Equal(["DMO/Q8mv|True", "DMO/X8as|True"],
            "neither copy was held, so neither was saved again");
        (await RowsAsync(name, Saved, at)).Should().Equal(["15|2|0"], "saved with the spell still open and the device forgotten");

        async Task<List<string>> Finals() => await RowsAsync(name, """
            SELECT to_char(start_utc AT TIME ZONE 'UTC', 'HH24:MI'), quality FROM queue_bin
            WHERE zone_key = @zone AND status = 'Final' AND start_utc >= '2026-09-28 17:00:00+00' AND start_utc < '2026-09-28 17:30:00+00'
            ORDER BY start_utc, revision
            """, at);
    }
}
