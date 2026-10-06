using System.Text.Json;
using Ariva.Core.Queueing.Replay;
using Ariva.Core.Services;
using Ariva.Infra.Sensing;
using Ariva.Infra.Services.Administration;
using Ariva.Infra.Services.Seed;
using Ariva.Infra.Settings;
using Ariva.Infra.Streaming;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using Ariva.UnitTests.Replay;
using RepositoryPaths = Ariva.UnitTests.Setup.RepositoryPaths;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ariva.IntegrationTests.Replay;

/// <summary>
/// ARV-036 against PostgreSQL and TimescaleDB: the reference evening (seed 9303) archived as the stream host archives it
/// (sensing batches with track pseudonyms, device health reports) and replayed by the replay command's runner under the
/// demo seed's profile version 12 gives the golden output hash of the in-memory replay, every time; the run is on record
/// in replay_run, the export checks out against it, and the runtime role cannot change or remove that record.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class GoldenReplayArchiveTests(PostgresFixture fixture) : IAsyncDisposable
{
    private readonly AccountsHost _host = new(fixture, database: TestDatabase.Replay);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DatabaseSettings _archived;

    // One database for the class: the demo seed and the archived evening, written once (the fixture creates each test
    // database once).
    private async Task<(ReplayRunner Runner, ReplayStore Store, string Database)> ArchivedAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (_archived is null)
            {
                await _host.CreateUserAsync("it.replay");
                await _host.AsCallerAsync(null, s =>
                    new DemoTopologySeed(s.GetRequiredService<IUnitOfWork>(), s.GetRequiredService<ICurrentUser>(), _host.Clock, s.GetRequiredService<AuditTrail>())
                        .RunAsync(Ct));
                var settings = new DatabaseSettings
                {
                    Host = fixture.Hostname,
                    Port = fixture.Port,
                    Name = await _host.DatabaseAsync(),
                    Username = fixture.AdminUsername,
                    Password = fixture.AdminPassword
                };
                var sensing = new SensingArchive(settings, Clock);
                var health = new DeviceHealthArchive(settings, Clock);
                var (batches, reports) = ReferenceReplay.Ingested;
                foreach (var chunk in batches.Chunk(200))
                    (await sensing.WriteAsync(chunk, Ct)).Duplicates.Should().Be(0);
                (await health.WriteAsync(reports, Ct)).Should().Be(reports.Count);
                (await health.WriteAsync(reports, Ct)).Should().Be(0, "a report is archived once");
                _archived = settings;
            }
        }
        finally
        {
            Gate.Release();
        }

        var store = new ReplayStore(_archived);
        return (new ReplayRunner(new SensingArchive(_archived, Clock), new DeviceHealthArchive(_archived, Clock), new ZoneGeometrySource(_archived), store),
            store, _archived.Name);
    }

    // The archive keeps three days: its clock is the evening after the replayed one.
    private static readonly ManualClock Clock = new(new DateTimeOffset(ReferenceReplay.To.AddHours(1)));

    private async Task<long> RunsAsync(string database)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(database));
        await connection.OpenAsync(Ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM replay_run", connection);
        return (long)(await count.ExecuteScalarAsync(Ct))!;
    }

    private static ReplayRequest Request(IReadOnlyList<string> zones = null) =>
        new(ReferenceReplay.Site, zones ?? ["CI-C", "A-VIS"], ReferenceReplay.From, ReferenceReplay.To, 12, "integration-test");

    [Fact]
    public async Task Replay_Should_GiveTheGoldenOutputHash_When_TheArchivedEveningIsReplayed()
    {
        var (runner, store, database) = await ArchivedAsync();
        var golden = JsonDocument.Parse(await File.ReadAllTextAsync(RepositoryPaths.Resolve("Platform/Backplane/Ariva.UnitTests/Replay/replay-golden.json"), Ct)).RootElement;

        using var export = new StringWriter();
        var first = await runner.RunAsync(Request(), export, Ct);
        var second = await runner.RunAsync(Request(["A-VIS", "CI-C"]), null, Ct);

        first.Hashes.OutputHead.Should().Be(golden.GetProperty("outputHead").GetString(),
            "the archive's form of the evening (pseudonymous tracks, health reports) replays to the same outputs as the in-memory replay");
        first.Hashes.Outputs.Should().Be(golden.GetProperty("outputs").GetInt64());
        second.Hashes.Should().Be(first.Hashes, "the same archive, version and settings give the same hashes, in any zone order");
        first.Manifest.Zones.Should().Equal("A-VIS", "CI-C");
        first.Manifest.ProfileVersion.Should().Be(12);

        var verified = ReplayLedger.Verify(new StringReader(export.ToString()));
        verified.Valid.Should().BeTrue(string.Join("; ", verified.Problems));
        verified.Hashes.Should().Be(first.Hashes);
        var records = await store.FindAsync(first.Hashes.ReplayHash, Ct);
        records.Select(r => r.Id).Should().Contain([first.Id, second.Id]);
        var (one, two) = (records.Single(r => r.Id == first.Id), records.Single(r => r.Id == second.Id));
        one.RowHash.Should().Be(first.RowHash);
        two.PreviousHash.Should().Be(one.RowHash, "each run is chained to the one before");
        one.RequestedBy.Should().Be("integration-test");
        one.RecordedBy.Should().Be(fixture.AdminUsername, "the database records the session's login");
        (await RunsAsync(database)).Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Geometry_Should_CarryThePublishedCapacities_When_LoadedForTheStream()
    {
        // ARV-114a: the demo seed's v12 gives each queue zone the scenario's snake capacity and its bands none.
        var _ = await ArchivedAsync();
        var source = new ZoneGeometrySource(_archived);

        var visitors = await source.LoadAsync(ReferenceReplay.Site, "A-VIS", 12, Ct);
        var islandC = await source.LoadAsync(ReferenceReplay.Site, "CI-C", null, Ct);

        visitors!.Geometry.Capacities.Should().Equal(new Dictionary<string, int> { ["A-VIS"] = 190 });
        visitors.Geometry.CapacityOf("A-OV").Should().BeNull();
        islandC!.Geometry.Capacities.Should().Equal(new Dictionary<string, int> { ["CI-C"] = 70 });
        visitors.Geometry.Should().BeEquivalentTo(ReferenceReplay.GeometryOf("A-VIS"), "the replay's reference geometry is the seed's");
    }

    [Fact]
    public async Task Replay_Should_RefuseARequest_When_ItNamesAnUnknownVersionOrZone()
    {
        var (runner, _, database) = await ArchivedAsync();
        var before = await RunsAsync(database);

        Func<Task> version = () => runner.RunAsync(Request() with { ProfileVersion = 99 }, null, Ct);
        Func<Task> zone = () => runner.RunAsync(Request(["A-VIS", "NOPE"]), null, Ct);
        Func<Task> range = () => runner.RunAsync(Request() with { ToUtc = ReferenceReplay.From.AddDays(40) }, null, Ct);

        await version.Should().ThrowAsync<InvalidOperationException>().WithMessage("*version 99*");
        await zone.Should().ThrowAsync<InvalidOperationException>().WithMessage("*NOPE*");
        await range.Should().ThrowAsync<ArgumentException>();
        (await RunsAsync(database)).Should().Be(before, "a refused replay leaves no record");
    }

    [Fact]
    public async Task RuntimeRole_Should_NotBackdateOrUnchainARun_When_ItInsertsOne()
    {
        var (runner, _, database) = await ArchivedAsync();
        var run = await runner.RunAsync(Request(["A-VIS"]), null, Ct);
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(database));
        await connection.OpenAsync(Ct);
        await using (var role = new NpgsqlCommand("SET ROLE ariva_runtime", connection))
            await role.ExecuteNonQueryAsync(Ct);
        await using var forge = new NpgsqlCommand("""
            INSERT INTO replay_run (id, format, site_code, zones, from_utc, to_utc, profile_version, settings_hash, input_records, output_records,
                                    input_head, output_head, replay_hash, requested_by, created_on, recorded_by, previous_hash, row_hash)
            VALUES (gen_random_uuid(), 'ariva-replay/1', 'DMO', 'A-VIS', '2026-09-28T17:00Z', '2026-09-28T20:30Z', 12, repeat('a', 64), 1, 1,
                    repeat('b', 64), repeat('c', 64), repeat('d', 64), 'ops', '2026-09-29T08:00Z', 'ops@stream-job', repeat('0', 64), repeat('e', 64))
            RETURNING created_on, recorded_by, previous_hash, row_hash
            """, connection);
        await using var reader = await forge.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).Should().BeTrue();

        reader.GetDateTime(0).Should().BeAfter(new DateTime(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc).AddMinutes(1), "the database sets the time, not the client");
        reader.GetString(1).Should().NotBe("ops@stream-job");
        reader.GetString(2).Should().Be(run.RowHash, "the row is chained to the latest run");
        reader.GetString(3).Should().NotBe(new string('e', 64));
    }

    [Fact]
    public async Task RuntimeLogin_Should_NotRedirectTheChain_When_ItShadowsTheTableWithATemporaryOne()
    {
        var (runner, _, database) = await ArchivedAsync();
        var run = await runner.RunAsync(Request(["A-VIS"]), null, Ct);
        var password = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString(database)))
        {
            await admin.OpenAsync(Ct);
            await using var login = new NpgsqlCommand("SELECT ariva_ensure_runtime_login('it_replay_runtime', @password)", admin);
            login.Parameters.AddWithValue("password", password);
            await login.ExecuteNonQueryAsync(Ct);
        }

        // A real runtime login, not SET ROLE from the superuser: its own temporary table named replay_run.
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(database, "it_replay_runtime", password));
        await connection.OpenAsync(Ct);
        await using (var shadow = new NpgsqlCommand(
                         "CREATE TEMP TABLE replay_run (row_hash char(64), created_on timestamptz, id uuid); INSERT INTO replay_run VALUES (repeat('e', 64), now() + interval '1 day', gen_random_uuid())",
                         connection))
            await shadow.ExecuteNonQueryAsync(Ct);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO public.replay_run (id, format, site_code, zones, from_utc, to_utc, profile_version, settings_hash, input_records, output_records,
                                           input_head, output_head, replay_hash, requested_by)
            VALUES (gen_random_uuid(), 'ariva-replay/1', 'DMO', 'A-VIS', '2026-09-28T17:00Z', '2026-09-28T20:30Z', 12, repeat('a', 64), 1, 1,
                    repeat('b', 64), repeat('c', 64), repeat('d', 64), 'ops')
            RETURNING previous_hash, recorded_by
            """, connection);
        await using var reader = await insert.ExecuteReaderAsync(Ct);
        (await reader.ReadAsync(Ct)).Should().BeTrue();

        reader.GetString(0).Should().Be(run.RowHash, "the trigger reads the real table, whatever the session's temporary tables are called");
        reader.GetString(1).Should().Be("it_replay_runtime");
    }

    [Theory]
    [InlineData("UPDATE replay_run SET output_head = repeat('0', 64)")]
    [InlineData("DELETE FROM replay_run")]
    [InlineData("TRUNCATE replay_run")]
    [InlineData("UPDATE device_health_event SET online = false")]
    [InlineData("DELETE FROM device_health_event")]
    [InlineData("DELETE FROM zone_outage")]
    public async Task RuntimeRole_Should_NotChangeTheReplayRecord_When_ItTries(string statement)
    {
        var (runner, _, database) = await ArchivedAsync();
        await runner.RunAsync(Request(["A-VIS"]), null, Ct);
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(database));
        await connection.OpenAsync(Ct);
        await using (var role = new NpgsqlCommand("SET ROLE ariva_runtime", connection))
            await role.ExecuteNonQueryAsync(Ct);
#pragma warning disable CA2100 // the statements are the theory's literals
        await using var command = new NpgsqlCommand(statement, connection);
#pragma warning restore CA2100

        Func<Task> run = () => command.ExecuteNonQueryAsync(Ct);

        (await run.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
    }
}
