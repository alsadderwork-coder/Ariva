using Ariva.Core.Domain.Enums;
using Ariva.Core.Sensing;
using Ariva.Infra.Sensing;
using Ariva.Infra.Settings;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Npgsql;

namespace Ariva.IntegrationTests.Sensing;

/// <summary>
/// ARV-026 against PostgreSQL with script 0017: sensing batches are archived by binary COPY (in several COPYs when
/// large), each batch once, and read back for one zone and range in time order as the canonical events they were; the
/// runtime role cannot change or remove archived rows; with TimescaleDB the tables are hypertables with compression and
/// retention policies.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class SensingArchiveTests(PostgresFixture fixture) : IAsyncDisposable
{
    private readonly AccountsHost _host = new(fixture, database: TestDatabase.SensingArchive);
    private static readonly DateTime At = new(2026, 10, 2, 14, 5, 0, DateTimeKind.Utc);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The archive with a clock just after the test data (<see cref="At"/>), or the given clock.</summary>
    private async Task<(SensingArchive Archive, string ConnectionString)> ArchiveAsync(TimeProvider clock = null)
    {
        var database = await _host.DatabaseAsync();
        var settings = new DatabaseSettings
        {
            Host = fixture.Hostname,
            Port = fixture.Port,
            Name = database,
            Username = fixture.AdminUsername,
            Password = fixture.AdminPassword
        };
        return (new SensingArchive(settings, clock ?? new ManualClock(new DateTimeOffset(At.AddMinutes(10)))), fixture.ConnectionString(database));
    }

    private static T Batch<T>(T batch, string site, string zone, Guid? id = null) where T : SensingBatch
    {
        batch.Id = id ?? Guid.NewGuid();
        batch.DeviceId = Guid.Parse("0199a000-0000-7000-8000-0000000d0017");
        batch.DeviceCode = "S-17";
        batch.SiteCode = site;
        batch.QueueZoneName = zone;
        batch.Dialect = "Canonical";
        batch.Commissioned = true;
        batch.ReceivedUtc = At.AddSeconds(1);
        batch.Clock = new ClockReading(0, true, ClockState.Ok);
        return batch;
    }

    private static TrackSampleBatch Tracks(string site, string zone, int count, DateTime start, Guid? id = null) => Batch(new TrackSampleBatch
    {
        Samples = [.. Enumerable.Range(0, count).Select(i => new Sensed<TrackPosition>(
            new TrackPosition($"S-17/{i % 50}", 10 + (i % 20), 12 + (i % 7), i % 3 == 0 ? null : 1.7, start.AddMilliseconds(i * 200)),
            start.AddMilliseconds(i * 200), i % 5 == 0 ? SensedFlags.Skewed : SensedFlags.None))]
    }, site, zone, id);

    [Fact]
    public async Task Archive_Should_WriteEveryKindAndReadItBackInTimeOrder()
    {
        var (archive, _) = await ArchiveAsync();
        var crossings = Batch(new VendorLineCrossingBatch
        {
            Crossings = [new Sensed<LineCrossing>(new LineCrossing("Entry A", CrossingDirection.In, "S-17/7", At.AddSeconds(2)), At.AddSeconds(2), SensedFlags.Corrected)]
        }, "SAA", "Snake A");
        var occupancy = Batch(new ZoneOccupancyBatch
        {
            Occupancy = [new Sensed<ZoneOccupancy>(new ZoneOccupancy("Overflow A", 41, At.AddSeconds(3)), At.AddSeconds(3), SensedFlags.None)]
        }, "SAA", "Snake A");
        var intervals = Batch(new IntervalCountBatch
        {
            Intervals = [new Sensed<IntervalCount>(new IntervalCount("Exit A", 2, 30, At.AddMinutes(-5), At), At, SensedFlags.None)]
        }, "SAA", "Snake A");
        var tracks = Tracks("SAA", "Snake A", 10, At.AddSeconds(1));
        var otherZone = Tracks("SAA", "Snake B", 5, At);

        var written = await archive.WriteAsync([tracks, crossings, occupancy, intervals, otherZone], Ct);

        written.Should().Be(new SensingArchiveWrite(5, 0, 18, 0));
        var read = await archive.ReadAsync(new SensingReplayQuery("SAA", "Snake A", At.AddMinutes(-1), At.AddMinutes(1)), Ct).ToListAsync(Ct);
        read.Should().HaveCount(13, "Snake B is another zone");
        read.Select(e => e.TimeUtc).Should().BeInAscendingOrder();
        read.First().Should().Match<ArchivedSensingEvent>(e => e.Kind == SensingKind.Interval && e.Name == "Exit A" && e.In == 2 && e.Out == 30 && e.FromUtc == At.AddMinutes(-5));
        read.Single(e => e.Kind == SensingKind.Crossing).ToCanonical().Should().Be(crossings.Crossings[0].Event with { TrackId = read.Single(e => e.Kind == SensingKind.Crossing).TrackId },
            "the crossing as written, its track id a pseudonym");
        read.Single(e => e.Kind == SensingKind.Crossing).Flags.Should().Be(SensedFlags.Corrected);
        read.Single(e => e.Kind == SensingKind.Occupancy).ToCanonical().Should().Be(occupancy.Occupancy[0].Event);
        read.Where(e => e.Kind == SensingKind.Track).Select(e => ((TrackPosition)e.ToCanonical()) with { TrackId = null })
            .Should().Equal(tracks.Samples.Select(s => s.Event with { TrackId = null }), "positions, heights and times as written");
        read.Where(e => e.Kind == SensingKind.Track).Select(e => e.Ordinal).Should().Equal(Enumerable.Range(0, 10));
        read.Should().OnlyContain(e => e.DeviceCode == "S-17" && e.Commissioned && e.ReceivedUtc == At.AddSeconds(1));

        var onlyTracks = await archive.ReadAsync(new SensingReplayQuery("SAA", "Snake A", At, At.AddSeconds(2), new HashSet<SensingKind> { SensingKind.Track }), Ct).ToListAsync(Ct);
        onlyTracks.Should().HaveCount(5, "tracks at 1.0 to 1.8 s are inside [0 s, 2 s)").And.OnlyContain(e => e.Kind == SensingKind.Track);
    }

    [Fact]
    public async Task Archive_Should_ArchiveEachBatchOnce()
    {
        var (archive, connection) = await ArchiveAsync();
        var id = Guid.NewGuid();
        var batch = Tracks("SAB", "Snake A", 4, At, id);

        (await archive.WriteAsync([batch, batch], Ct)).Should().Be(new SensingArchiveWrite(1, 0, 4, 0), "the same batch twice in one write is one batch");
        (await archive.WriteAsync([batch], Ct)).Should().Be(new SensingArchiveWrite(0, 1, 0, 0), "a redelivered batch is skipped");
        var both = await Task.WhenAll(archive.WriteAsync([Tracks("SAB", "Snake A", 3, At, Guid.Parse("0199a000-0000-7000-8000-00000000beef"))], Ct),
            archive.WriteAsync([Tracks("SAB", "Snake A", 3, At, Guid.Parse("0199a000-0000-7000-8000-00000000beef"))], Ct));
        both.Sum(w => w.Batches).Should().Be(1, "two writers of one batch: one archives it, the other waits and skips it");

        await using var sql = new NpgsqlConnection(connection);
        await sql.OpenAsync(Ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM sensing_event WHERE site_code = 'SAB'", sql);
        ((long)(await count.ExecuteScalarAsync(Ct))!).Should().Be(7);
    }

    [Fact]
    public async Task Archive_Should_SplitLargeWritesIntoSeveralCopies()
    {
        var (archive, _) = await ArchiveAsync();
        var batches = Enumerable.Range(0, 8).Select(i =>
        {
            var batch = Tracks("SAC", "Snake A", 3_000, At.AddMinutes(i));
            batch.ReceivedUtc = batch.Samples[^1].TimeUtc.AddSeconds(1);
            return batch;
        }).ToList();

        var written = await archive.WriteAsync(batches, Ct);

        written.Events.Should().Be(24_000).And.BeGreaterThan(SensingArchive.CopyChunk);
        (await archive.ReadAsync(new SensingReplayQuery("SAC", "Snake A", At, At.AddHours(1)), Ct).CountAsync(Ct)).Should().Be(24_000);
    }

    [Fact]
    public async Task Archive_Should_RefuseRangesOutsideTheRules()
    {
        var (archive, _) = await ArchiveAsync();

        foreach (var query in new[]
                 {
                     new SensingReplayQuery("SAD", "Snake A", At, At),
                     new SensingReplayQuery("SAD", "Snake A", At, At.AddDays(32)),
                     new SensingReplayQuery("SAD", "Snake A", DateTime.SpecifyKind(At, DateTimeKind.Unspecified), At.AddHours(1))
                 })
        {
            var act = async () => await archive.ReadAsync(query, Ct).ToListAsync(Ct);
            await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        }

        var injection = await archive.ReadAsync(new SensingReplayQuery("SAD' OR '1'='1", "Snake A' --", At, At.AddHours(1)), Ct).ToListAsync(Ct);
        injection.Should().BeEmpty("site and zone are parameters, never SQL");
    }

    [Fact]
    public async Task Archive_Should_KeepOnlyDailyPseudonymsOfTrackIds()
    {
        var (archive, connection) = await ArchiveAsync(TimeProvider.System);
        var today = DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour >= 1 ? 0.5 : 0.1);
        var yesterday = today.AddDays(-1);
        TrackSampleBatch Track(DateTime at, string id, double x)
        {
            var batch = Batch(new TrackSampleBatch { Samples = [new Sensed<TrackPosition>(new TrackPosition(id, x, 14, 1.7, at), at, SensedFlags.None)] }, "SAF", "Snake A");
            batch.ReceivedUtc = at.AddSeconds(1);
            return batch;
        }

        await archive.WriteAsync([Track(today, "S-17/7", 1), Track(today.AddMinutes(5), "S-17/7", 2), Track(today, "S-17/8", 3), Track(yesterday, "S-17/7", 4)], Ct);
        var crossing = Batch(new VendorLineCrossingBatch { Crossings = [new Sensed<LineCrossing>(new LineCrossing("Entry A", CrossingDirection.In, "S-17/7", today), today, SensedFlags.None)] },
            "SAF", "Snake A");
        crossing.ReceivedUtc = today.AddSeconds(1);
        await archive.WriteAsync([crossing], Ct);

        var events = await archive.ReadAsync(new SensingReplayQuery("SAF", "Snake A", yesterday.AddMinutes(-1), today.AddMinutes(10)), Ct).ToListAsync(Ct);
        string At(double x) => events.Single(e => e.X == x).TrackId;
        events.Select(e => e.TrackId).Should().OnlyContain(id => id.StartsWith("S-17/~", StringComparison.Ordinal) && id.Length == "S-17/~".Length + 22,
            "no raw track id is kept");
        At(1).Should().Be(At(2), "the same track on the same day keeps its pseudonym");
        events.Single(e => e.Kind == SensingKind.Crossing).TrackId.Should().Be(At(1), "a crossing names the track's pseudonym of the day");
        At(3).Should().NotBe(At(1), "another track");
        At(4).Should().NotBe(At(1), "the same track id on another day cannot be linked");

        await using var sql = new NpgsqlConnection(connection);
        await sql.OpenAsync(Ct);
        await using (var old = new NpgsqlCommand("INSERT INTO sensing_day_key (day, key, created_on) VALUES (current_date - 10, decode(repeat('ab', 32), 'hex'), now())", sql))
            await old.ExecuteNonQueryAsync(Ct);
        await archive.WriteAsync([Track(today.AddMinutes(9), "S-17/9", 5)], Ct);
        await using var keys = new NpgsqlCommand("SELECT count(*) FROM sensing_day_key WHERE day < current_date - 3", sql);
        ((long)(await keys.ExecuteScalarAsync(Ct))!).Should().Be(0, "keys of days no event can still arrive for are destroyed");
    }

    [Fact]
    public async Task Archive_Should_SkipEventsAndBatchesOutOfBounds_And_KeepTheRest()
    {
        var (archive, _) = await ArchiveAsync(TimeProvider.System);
        var now = DateTime.UtcNow.AddSeconds(-5);
        Sensed<TrackPosition> Good(int i) => new(new TrackPosition($"S-17/{i}", 10, 12, 1.7, now), now, SensedFlags.None);
        var mixed = Batch(new TrackSampleBatch
        {
            Samples =
            [
                Good(1),
                new Sensed<TrackPosition>(new TrackPosition("S-17/2", double.NaN, 12, 1.7, now), now, SensedFlags.None),
                new Sensed<TrackPosition>(new TrackPosition("S-17/3", 10, 12, 1.7, now), now, (SensedFlags)65536),
                new Sensed<TrackPosition>(new TrackPosition("X-99/4", 10, 12, 1.7, now), now, SensedFlags.None),
                new Sensed<TrackPosition>(new TrackPosition("S-17/5\0", 10, 12, 1.7, now), now, SensedFlags.None),
                new Sensed<TrackPosition>(new TrackPosition("S-17/6", 10, 12, 1.7, DateTime.MinValue.ToUniversalTime()), DateTime.MinValue.ToUniversalTime(), SensedFlags.None),
                null,
                Good(7)
            ]
        }, "SAG", "Snake A");
        mixed.ReceivedUtc = now.AddSeconds(1);
        var occupancy = Batch(new ZoneOccupancyBatch
        {
            Occupancy = [new Sensed<ZoneOccupancy>(new ZoneOccupancy(new string('z', 201), 3, now), now, SensedFlags.None)]
        }, "SAG", "Snake A");
        occupancy.ReceivedUtc = now.AddSeconds(1);
        var noSite = Tracks(null, "Snake A", 2, now);
        var stale = Tracks("SAG", "Snake A", 2, now.AddDays(-10));
        stale.ReceivedUtc = now.AddDays(-10);
        var lowerCase = Tracks("SAG", "Snake A", 2, now);
        lowerCase.DeviceCode = "s-17\ud800";
        lowerCase.ReceivedUtc = now.AddSeconds(1);
        var tooMany = Tracks("SAG", "Snake A", 3_001, now.AddMinutes(-10));
        tooMany.ReceivedUtc = now.AddSeconds(1);

        var written = await archive.WriteAsync([mixed, occupancy, noSite, stale, lowerCase, tooMany], Ct);

        written.Should().Be(new SensingArchiveWrite(1, 0, 2, 7), "two good tracks are kept; six bad events and the bad occupancy are skipped; batches without a site, received long ago, with a malformed device code or over 3,000 events are dropped whole");
    }

    [Fact]
    public async Task Script_Should_RequireTimescale_Unless_ThePlainPostgresSettingIsOn()
    {
        await using var admin = new NpgsqlConnection(fixture.ConnectionString("postgres"));
        await admin.OpenAsync(Ct);
        await using var available = new NpgsqlCommand("SELECT count(*) FROM pg_available_extensions WHERE name = 'timescaledb'", admin);
        if ((long)(await available.ExecuteScalarAsync(Ct))! > 0)
            return; // TimescaleDB (CI): the script installs it, as the other tests prove.

        await using (var drop = new NpgsqlCommand("DROP DATABASE IF EXISTS it_sensing_plain WITH (FORCE)", admin))
            await drop.ExecuteNonQueryAsync(Ct);
        await using (var create = new NpgsqlCommand("CREATE DATABASE it_sensing_plain", admin))
            await create.ExecuteNonQueryAsync(Ct);
        var runner = new Ariva.Infra.Timescale.SqlScriptRunner(fixture.ConnectionString("it_sensing_plain"), Microsoft.Extensions.Logging.Abstractions.NullLogger<Ariva.Infra.Timescale.SqlScriptRunner>.Instance);

        var act = async () => await runner.ApplyAsync(Ariva.Infra.Timescale.SqlScriptCatalog.Embedded(), Ct);

        (await act.Should().ThrowAsync<Exception>()).Which.ToString().Should().Contain("TimescaleDB is required");
        await using var cleanup = new NpgsqlCommand("DROP DATABASE IF EXISTS it_sensing_plain WITH (FORCE)", admin);
        await cleanup.ExecuteNonQueryAsync(Ct);
    }

    [Fact]
    public async Task Runtime_Should_NeverChangeOrRemoveArchivedRows_And_TimescaleKeepsThemForTheWindow()
    {
        var (archive, connection) = await ArchiveAsync();
        await archive.WriteAsync([Tracks("SAE", "Snake A", 2, At)], Ct);
        await using var sql = new NpgsqlConnection(connection);
        await sql.OpenAsync(Ct);

        foreach (var statement in new[]
                 {
                     "UPDATE sensing_event SET x = 0 WHERE site_code = 'SAE'",
                     "DELETE FROM sensing_event WHERE site_code = 'SAE'",
                     "TRUNCATE sensing_event",
                     "DELETE FROM sensing_batch WHERE site_code = 'SAE'"
                 })
        {
            await using var transaction = await sql.BeginTransactionAsync(Ct);
            await using (var role = new NpgsqlCommand("SET LOCAL ROLE ariva_runtime", sql, transaction))
                await role.ExecuteNonQueryAsync(Ct);
#pragma warning disable CA2100 // fixed statements from this test, not input
            await using var change = new NpgsqlCommand(statement, sql, transaction);
#pragma warning restore CA2100
            var act = async () => await change.ExecuteNonQueryAsync(Ct);
            (await act.Should().ThrowAsync<PostgresException>(statement)).Which.SqlState.Should().Be("42501");
            await transaction.RollbackAsync(Ct);
        }

        await using var timescale = new NpgsqlCommand("SELECT count(*) FROM pg_extension WHERE extname = 'timescaledb'", sql);
        if ((long)(await timescale.ExecuteScalarAsync(Ct))! == 0)
        {
            // Plain PostgreSQL (local runs): ordinary tables, no retention. CI runs the TimescaleDB image and checks the policies.
            Console.WriteLine("TimescaleDB not installed: retention and compression policies not checked here");
            return;
        }

        await using var jobs = new NpgsqlCommand("""
            SELECT string_agg(hypertable_name || ':' || proc_name, ',' ORDER BY hypertable_name, proc_name)
            FROM timescaledb_information.jobs WHERE hypertable_name IN ('sensing_event', 'sensing_batch')
            """, sql);
        var policies = (string)(await jobs.ExecuteScalarAsync(Ct))!;
        policies.Should().Contain("sensing_event:policy_retention").And.Contain("sensing_event:policy_compression").And.Contain("sensing_batch:policy_retention");
        await using var hypertables = new NpgsqlCommand("SELECT count(*) FROM timescaledb_information.hypertables WHERE hypertable_name IN ('sensing_event', 'sensing_batch')", sql);
        ((long)(await hypertables.ExecuteScalarAsync(Ct))!).Should().Be(2);

        // The chunks carry the same grants as the hypertable: the runtime role cannot delete through a chunk either.
        await using (var transaction = await sql.BeginTransactionAsync(Ct))
        {
            await using (var role = new NpgsqlCommand("SET LOCAL ROLE ariva_runtime", sql, transaction))
                await role.ExecuteNonQueryAsync(Ct);
            await using var delete = new NpgsqlCommand("""
                DO $$ BEGIN EXECUTE format('DELETE FROM %s', (SELECT c FROM show_chunks('sensing_event') AS c LIMIT 1)); END $$
                """, sql, transaction);
            var act = async () => await delete.ExecuteNonQueryAsync(Ct);
            (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
        }
    }
}
