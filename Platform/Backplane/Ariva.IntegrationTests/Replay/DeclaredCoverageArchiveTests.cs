using Ariva.Core.Sensing;
using Ariva.Core.Services;
using Ariva.Infra.Sensing;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Seed;
using Ariva.Infra.Settings;
using Ariva.Infra.Streaming;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using Ariva.UnitTests.Replay;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ariva.IntegrationTests.Replay;

/// <summary>
/// ARV-259e5 against PostgreSQL and TimescaleDB with script 0067: the device health archive keeps the lines each report says its
/// device counts (NULL for All, an empty array for None, the names for Listed), written and read with parameters; a row archived
/// before the script reads as All; a compressed chunk takes the column and new rows; a declaration out of the registry's shape is
/// not archived, and the database refuses one out of its own bounds. The reference evening with S-17 alone counting the exit,
/// archived as the stream host archives it, replays to the in-memory replay's hash.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DeclaredCoverageArchiveTests(PostgresFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The archive keeps three days: its clock is the evening after the replayed one.
    private static readonly ManualClock Clock = new(new DateTimeOffset(ReferenceReplay.To.AddHours(1)));

    private async Task<DatabaseSettings> DatabaseAsync(AccountsHost host) => new()
    {
        Host = fixture.Hostname, Port = fixture.Port, Name = await host.DatabaseAsync(), Username = fixture.AdminUsername, Password = fixture.AdminPassword
    };

    private static DeviceHealthReported Report(string zone, string device, DateTime received, IReadOnlyList<string> lines) => new()
    {
        DeviceId = Guid.CreateVersion7(),
        DeviceCode = device,
        SiteCode = ReferenceReplay.Site,
        QueueZoneName = zone,
        Status = new DeviceStatus(true, null, null, 0, received),
        ReceivedUtc = received,
        Commissioned = true,
        CoveredLines = lines
    };

    private static async Task<List<ArchivedDeviceHealth>> ReadAsync(DeviceHealthArchive archive, string zone)
    {
        var rows = new List<ArchivedDeviceHealth>();
        await foreach (var row in archive.ReadAsync(ReferenceReplay.Site, zone, ReferenceReplay.From, ReferenceReplay.To, Ct))
            rows.Add(row);
        return rows;
    }

    [Fact]
    public async Task Archive_Should_KeepEachReportsDeclaredLines_When_WrittenReadAndCompressed()
    {
        await using var host = new AccountsHost(fixture, database: TestDatabase.HealthArchiveCoverage);
        var settings = await DatabaseAsync(host);
        var archive = new DeviceHealthArchive(settings, Clock);
        var at = ReferenceReplay.WallOf(1100);
        // A name is data, never SQL (CWE-89): quotes, a comment and a statement separator round-trip as written.
        const string Quoted = "Exit 'B'; -- \"north\"";

        var reports = new[]
        {
            Report("Z-NEW", "S-1", at, null),
            Report("Z-NEW", "S-2", at.AddSeconds(1), []),
            Report("Z-NEW", "S-3", at.AddSeconds(2), ["Entry A", Quoted]),
            Report("Z-NEW", "S-4", at.AddSeconds(3), [.. Enumerable.Range(0, 17).Select(i => $"line {i}")]),
            Report("Z-NEW", "S-5", at.AddSeconds(4), ["Exit A", "Exit A"]),
            // A line feed in a name would split it in the archive's join (and an empty name fails the column's CHECK): refused first.
            Report("Z-NEW", "S-7", at.AddSeconds(5), ["Exit A\nEntry A"]),
            Report("Z-NEW", "S-8", at.AddSeconds(6), ["\n"])
        };
        (await archive.WriteAsync(reports, Ct)).Should().Be(3, "17 names, a repeated name and names with a line feed are not the registry's shape: skipped");

        await using (var connection = new NpgsqlConnection(fixture.ConnectionString(settings.Name)))
        {
            await connection.OpenAsync(Ct);
            // A row as a build before script 0067 wrote it: no covered_lines.
            await using var legacy = new NpgsqlCommand("""
                INSERT INTO device_health_event (received_utc, site_code, queue_zone_name, device_id, device_code, event_id, online, commissioned, status_utc, clock_state)
                VALUES (@at, 'DMO', 'Z-OLD', gen_random_uuid(), 'S-9', gen_random_uuid(), true, true, NULL, NULL)
                """, connection);
            legacy.Parameters.AddWithValue("at", at);
            await legacy.ExecuteNonQueryAsync(Ct);
        }

        static void Expect(List<ArchivedDeviceHealth> rows)
        {
            rows.Select(r => r.DeviceCode).Should().Equal("S-1", "S-2", "S-3");
            rows[0].CoveredLines.Should().BeNull("All");
            rows[1].CoveredLines.Should().NotBeNull().And.BeEmpty("None");
            rows[2].CoveredLines.Should().Equal("Entry A", Quoted);
            rows[2].ToBatch().CoveredLines.Should().Equal("Entry A", Quoted);
        }

        Expect(await ReadAsync(archive, "Z-NEW"));
        (await ReadAsync(archive, "Z-OLD")).Should().ContainSingle().Which.CoveredLines.Should().BeNull("a row from before the script reads as All");

        await using (var connection = new NpgsqlConnection(fixture.ConnectionString(settings.Name)))
        {
            await connection.OpenAsync(Ct);
            await using var compress = new NpgsqlCommand(
                "SELECT count(*) FROM (SELECT compress_chunk(c, if_not_compressed => true) FROM show_chunks('device_health_event') c) AS compressed", connection);
            ((long)(await compress.ExecuteScalarAsync(Ct))!).Should().BePositive();
            await using var compressed = new NpgsqlCommand(
                "SELECT bool_and(is_compressed) FROM timescaledb_information.chunks WHERE hypertable_name = 'device_health_event'", connection);
            ((bool)(await compressed.ExecuteScalarAsync(Ct))!).Should().BeTrue();
        }

        Expect(await ReadAsync(archive, "Z-NEW"));
        (await ReadAsync(archive, "Z-OLD")).Single().CoveredLines.Should().BeNull();
        (await archive.WriteAsync([Report("Z-NEW", "S-6", at.AddSeconds(5), ["Exit A"])], Ct)).Should().Be(1, "a compressed chunk takes new rows");
        (await ReadAsync(archive, "Z-NEW")).Last().CoveredLines.Should().Equal("Exit A");
    }

    private const string Insert =
        "INSERT INTO device_health_event (received_utc, site_code, queue_zone_name, device_id, device_code, event_id, online, commissioned, covered_lines) " +
        "VALUES (now(), 'DMO', 'Z-BAD', gen_random_uuid(), 'S-9', gen_random_uuid(), true, true, ";

    [Theory]
    [InlineData(Insert + "ARRAY['']::text[])")]
    [InlineData(Insert + "ARRAY['Exit A', NULL]::text[])")]
    [InlineData(Insert + "ARRAY[['a', 'b'], ['c', 'd']]::text[])")]
    [InlineData(Insert + "(SELECT array_agg('l' || i) FROM generate_series(1, 65) AS i))")]
    public async Task Database_Should_RefuseADeclaration_When_ItIsOutOfTheColumnsBounds(string statement)
    {
        await using var host = new AccountsHost(fixture, database: TestDatabase.HealthArchiveCoverage);
        var settings = await DatabaseAsync(host);
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(settings.Name));
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // the statements are the theory's literals
        await using var insert = new NpgsqlCommand(statement, connection);
#pragma warning restore CA2100

        Func<Task> run = () => insert.ExecuteNonQueryAsync(Ct);

        (await run.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514", "ck_device_health_event_covered_lines");
    }

    [Fact]
    public async Task Replay_Should_GiveTheInMemoryHash_When_TheDeclaredEveningIsReplayedFromTheArchive()
    {
        await using var host = new AccountsHost(fixture, database: TestDatabase.ReplayDeclared);
        await host.CreateUserAsync("it.replay.declared");
        await host.AsCallerAsync(null, s =>
            new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), host.Clock, s.GetRequiredService<AuditTrail>()).RunAsync(Ct));
        var settings = await DatabaseAsync(host);
        var declared = ReferenceReplay.Declared(ReferenceReplay.ExitAlone);
        var sensing = new SensingArchive(settings, Clock);
        foreach (var chunk in declared.Batches.Chunk(200))
            (await sensing.WriteAsync(chunk, Ct)).Duplicates.Should().Be(0);
        (await new DeviceHealthArchive(settings, Clock).WriteAsync(declared.Health, Ct)).Should().Be(declared.Health.Count);
        var runner = new ReplayRunner(sensing, new DeviceHealthArchive(settings, Clock), new ZoneGeometrySource(settings), new ReplayStore(settings));
        var expected = ReferenceReplay.Run(source: declared).Hashes;
        var undeclared = ReferenceReplay.Run().Hashes;

        var run = await runner.RunAsync(new ReplayRequest(ReferenceReplay.Site, ["A-VIS", "CI-C"], ReferenceReplay.From, ReferenceReplay.To, 12, "integration-test"), null, Ct);

        run.Hashes.OutputHead.Should().Be(expected.OutputHead, "the archive keeps each report's declaration, so the replay sees what the in-memory replay saw");
        run.Hashes.Outputs.Should().Be(expected.Outputs);
        run.Hashes.OutputHead.Should().NotBe(undeclared.OutputHead, "S-17 alone counts the exit, so its outage makes the zone Unknown");
    }
}
