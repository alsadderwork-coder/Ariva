using Ariva.Infra.Settings;
using Ariva.Infra.Streaming;
using Ariva.Infra.Timescale;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ariva.IntegrationTests.Streaming;

/// <summary>
/// ARV-259b against TimescaleDB with the scripts applied, as the runtime login: the bin maturity sweep counts, by site, the queue
/// bins whose latest revision is still Provisional though they ended before their maturity (bin end, the censoring timeout and
/// the longest lateness), among the bins that started within the look-back, of the zones in their site's published profile
/// that have not moved to a newer profile version, at most <see cref="StreamStore.MaxOverdueSites"/> sites; one replica a round takes its lock (class <see cref="StreamStore.BinMaturityLockClass"/>), and only that replica
/// reports the gauge, for two sweep intervals.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class BinMaturityTests(PostgresFixture postgres)
{
    // A sweep at 12:01:30 under the defaults (censoring 120 minutes, lateness 90 seconds): bins that ended before 10:00 are mature.
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 1, 30, TimeSpan.Zero);
    private static readonly DateTime MatureBefore = new(2026, 10, 28, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Since = MatureBefore.AddHours(-24);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateTime At(int hour, int minute) => new(2026, 10, 28, hour, minute, 0, DateTimeKind.Utc);

    private async Task<(string Database, StreamStore Store)> DatabaseAsync()
    {
        var database = await postgres.CreateDatabaseAsync(TestDatabase.StreamBinMaturity);
        await new SqlScriptRunner(postgres.ConnectionString(database), NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded());
        const string login = "it_runtime_bin_maturity";
        var password = "rt-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
        await using (var admin = new NpgsqlConnection(postgres.ConnectionString(database)))
        {
            await admin.OpenAsync(Ct);
            await using var ensure = new NpgsqlCommand("SELECT ariva_ensure_runtime_login(@login, @password)", admin);
            ensure.Parameters.AddWithValue("login", login);
            ensure.Parameters.AddWithValue("password", password);
            await ensure.ExecuteNonQueryAsync(Ct);
        }

        var store = new StreamStore(new DatabaseSettings { Host = postgres.Hostname, Port = postgres.Port, Name = database, Username = login, Password = password },
            TimeProvider.System, NullLogger<StreamStore>.Instance);
        return (database, store);
    }

    private async Task SeedAsync(string database, int profileVersion, params (string Zone, DateTime Start, int Revision, int Length, string Status)[] bins)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString(database));
        await connection.OpenAsync(Ct);
        foreach (var bin in bins)
        {
            await using var insert = new NpgsqlCommand("""
                INSERT INTO queue_bin (zone_key, start_utc, revision, length_minutes, status, quality, entries, exits, waits, abandoned,
                    fragmented, censored, reanchored, rejected, open_people, late_events, profile_version, updated_on)
                VALUES (@zone, @start, @revision, @length, @status, 'Good', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, @version, now())
                """, connection);
            insert.Parameters.AddWithValue("version", profileVersion);
            insert.Parameters.AddWithValue("zone", bin.Zone);
            insert.Parameters.AddWithValue("start", bin.Start);
            insert.Parameters.AddWithValue("revision", bin.Revision);
            insert.Parameters.AddWithValue("length", bin.Length);
            insert.Parameters.AddWithValue("status", bin.Status);
            await insert.ExecuteNonQueryAsync(Ct);
        }
    }

    /// <summary>
    /// A version of a site's zone profile with these queue zones (and overflow zones, by name), published in place of the one
    /// before (retired), or left a draft when <paramref name="version"/> is null: the sweep counts only the bins of a queue zone
    /// of its site's published profile.
    /// </summary>
    private async Task ProfileAsync(string database, string site, int? version, string[] queueZones, params string[] overflowZones)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString(database));
        await connection.OpenAsync(Ct);
        await using var profile = new NpgsqlCommand("""
            INSERT INTO airport (id, iata_code, name, time_zone_id)
                SELECT gen_random_uuid(), 'XBM', 'Bin maturity', 'UTC' WHERE NOT EXISTS (SELECT 1 FROM airport WHERE iata_code = 'XBM');
            INSERT INTO site (id, code, name) SELECT gen_random_uuid(), @site, @site WHERE NOT EXISTS (SELECT 1 FROM site WHERE code = @site);
            INSERT INTO terminal (id, airport_id, code, name, site_code)
                SELECT gen_random_uuid(), a.id, @site, @site, @site FROM airport a
                WHERE a.iata_code = 'XBM' AND NOT EXISTS (SELECT 1 FROM terminal WHERE site_code = @site);
            INSERT INTO level (id, terminal_id, site_code, code, name, floor_number, width_metres, depth_metres)
                SELECT gen_random_uuid(), t.id, @site, 'L0', 'Arrivals', 0, 100, 100 FROM terminal t
                WHERE t.site_code = @site AND NOT EXISTS (SELECT 1 FROM level WHERE site_code = @site);
            INSERT INTO zone_profile (id, site_code, name, status) VALUES (@profile, @site, 'Bins', 'Draft');
            INSERT INTO zone (id, profile_id, name, kind, level_id, polygon)
                SELECT gen_random_uuid(), @profile, z.name, z.kind, (SELECT id FROM level WHERE site_code = @site),
                    '1.000 1.000,3.000 1.000,3.000 2.000,1.000 2.000'
                FROM unnest(@names, @kinds) AS z (name, kind);
            UPDATE zone_profile SET status = 'Retired', retired_on = now() WHERE site_code = @site AND status = 'Published' AND @version IS NOT NULL;
            UPDATE zone_profile SET status = 'Published', version = @version, geometry_hash = repeat('b', 64), published_on = now()
                WHERE id = @profile AND @version IS NOT NULL;
            """, connection);
        profile.Parameters.AddWithValue("site", site);
        profile.Parameters.AddWithValue("profile", Guid.NewGuid());
        profile.Parameters.AddWithValue("names", (string[])[.. queueZones, .. overflowZones]);
        profile.Parameters.AddWithValue("kinds", (string[])[.. queueZones.Select(_ => "Queue"), .. overflowZones.Select(_ => "Overflow")]);
        profile.Parameters.Add(new NpgsqlParameter<int?>("version", NpgsqlTypes.NpgsqlDbType.Integer) { TypedValue = version });
        await profile.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>Another replica sweeping: the sweep's lock held in a transaction of its own, rolled back when disposed.</summary>
    private sealed class LockHolder(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private async Task<LockHolder> HoldTheLockAsync(string database)
    {
        var connection = new NpgsqlConnection(postgres.ConnectionString(database));
        await connection.OpenAsync(Ct);
        var transaction = await connection.BeginTransactionAsync(Ct);
        await using var hold = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@class, 0)", connection, transaction);
        hold.Parameters.AddWithValue("class", StreamStore.BinMaturityLockClass);
        await hold.ExecuteNonQueryAsync(Ct);
        return new LockHolder(connection, transaction);
    }

    private async Task<bool> LockIsFreeAsync(string database)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString(database));
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using var probe = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(@class, 0)", connection, transaction);
        probe.Parameters.AddWithValue("class", StreamStore.BinMaturityLockClass);
        return await probe.ExecuteScalarAsync(Ct) is true;
    }

    [Fact]
    public async Task CountOverdueBins_Should_CountEachSitesLatestRevisionsStillProvisionalPastMaturity_When_ASweepRuns()
    {
        var (database, store) = await DatabaseAsync();
        // DMO took Q9 out of its profile: version 1 had it as a queue zone (retired), version 2 has an overflow zone of that
        // name, and the draft has it back as a queue zone; none of them makes Q9 a queue zone of the published profile.
        await ProfileAsync(database, "DMO", 1, ["Q1", "Q2", "Q3", "Q9"]);
        await ProfileAsync(database, "DMO", 2, ["Q1", "Q2", "Q3"], "Q9");
        await ProfileAsync(database, "DMO", null, ["Q1", "Q2", "Q3", "Q9"]);
        await ProfileAsync(database, "AUH-TA", 1, ["Q1", "Q2"]);
        await ProfileAsync(database, "DXB", 1, ["Q1", "Q2", "Q4", "Q5"]);
        await ProfileAsync(database, "RUH", 1, ["Q1", "Q2"]);
        await SeedAsync(database, 1,
            ("DMO/Q1", At(9, 30), 0, 15, "Provisional"),     // ended 09:45: counted
            ("DMO/Q2", At(9, 30), 0, 15, "Provisional"),     // another zone of the site: counted
            ("DMO/Q3", Since, 0, 15, "Provisional"),         // started at the look-back's start: counted
            ("DMO/Q3", Since.AddMinutes(-15), 0, 15, "Provisional"), // started before it: not read
            ("AUH-TA/Q1", At(9, 30), 0, 15, "Provisional"),  // counted
            ("AUH-TA/Q1", At(9, 45), 0, 15, "Provisional"),  // ends at 10:00, mature only after: not counted
            ("AUH-TA/Q2", At(9, 40), 0, 30, "Provisional"),  // a 30-minute bin ends at 10:10: not counted
            ("DXB/Q1", At(9, 0), 0, 15, "Final"),            // final: not counted
            ("DXB/Q2", At(9, 0), 0, 15, "Provisional"),      // provisional, then final in a later revision: not counted
            ("DXB/Q2", At(9, 0), 1, 15, "Final"),
            ("RUH/Q1", At(9, 0), 0, 15, "Final"),            // a later revision provisional again, at a site of its own: counted
            ("RUH/Q1", At(9, 0), 1, 15, "Provisional"),
            ("DXB/Q4", At(11, 0), 0, 15, "Provisional"),     // not mature: not counted
            ("DXB/Q5", At(9, 30), 0, 15, "Provisional"),     // the zone moved to profile version 2 after it: not counted
            ("RUH/Q2", At(9, 30), 0, 15, "Provisional"),     // version 2 only recomputed an earlier bin: counted
            ("DMO/Q9", At(9, 30), 0, 15, "Provisional"),     // a zone taken out of the published profile: not counted
            ("OMN/Q1", At(9, 30), 0, 15, "Provisional"));    // a site without a published profile: not counted
        await SeedAsync(database, 2,
            ("DXB/Q5", At(11, 30), 0, 15, "Provisional"),    // started afresh under the new version
            ("RUH/Q2", At(9, 0), 0, 15, "Final"));

        var sites = await store.CountOverdueBinsAsync(Since, MatureBefore, Ct);

        sites.Should().Equal([("DMO", 3), ("RUH", 2), ("AUH-TA", 1)], "the most overdue site first, then by site code; none at DXB");
        (await LockIsFreeAsync(database)).Should().BeTrue("the sweep's lock ends with its transaction");

        // A provisional bin past maturity at each of 101 more sites: the report keeps the 100 most overdue.
        for (var i = 1; i <= 101; i++)
            await ProfileAsync(database, $"S{i:D3}", 1, ["Q1"]);
        await using (var connection = new NpgsqlConnection(postgres.ConnectionString(database)))
        {
            await connection.OpenAsync(Ct);
            await using var many = new NpgsqlCommand("""
                INSERT INTO queue_bin (zone_key, start_utc, revision, length_minutes, status, quality, entries, exits, waits, abandoned,
                    fragmented, censored, reanchored, rejected, open_people, late_events, profile_version, updated_on)
                SELECT 'S' || lpad(i::text, 3, '0') || '/Q1', @start, 0, 15, 'Provisional', 'Good', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, now()
                FROM generate_series(1, 101) AS i
                """, connection);
            many.Parameters.AddWithValue("start", At(9, 0));
            await many.ExecuteNonQueryAsync(Ct);
        }

        var bounded = await store.CountOverdueBinsAsync(Since, MatureBefore, Ct);

        bounded.Should().HaveCount(StreamStore.MaxOverdueSites);
        bounded[0].Should().Be(("DMO", 3));
        bounded[^1].Should().Be(("S097", 1), "after DMO and RUH, AUH-TA and S001 to S097 are the first 98 sites with one bin");
    }

    [Fact]
    public async Task Sweep_Should_ReportOnlyFromTheReplicaThatTookTheLock_When_AnotherReplicaHoldsIt()
    {
        var (database, store) = await DatabaseAsync();
        await ProfileAsync(database, "DMO", 1, ["Q1"]);
        await SeedAsync(database, 1, ("DMO/Q1", At(9, 30), 0, 15, "Provisional"), ("DMO/Q1", At(9, 45), 0, 15, "Provisional"));
        var clock = new ManualClock(Now);
        using var metrics = new StreamMetrics(timeProvider: clock);
        using var reader = new InstrumentReader(metrics.Meter);
        var monitor = new BinMaturityMonitor(store, new StreamSettings { Enabled = true }, metrics, clock, NullLogger<BinMaturityMonitor>.Instance);
        monitor.MaturityDelay.Should().Be(Now.UtcDateTime - MatureBefore, "the engine's default censoring timeout and lateness");

        await using (await HoldTheLockAsync(database))
        {
            (await store.CountOverdueBinsAsync(Since, MatureBefore, Ct)).Should().BeNull("another replica sweeps this round");
            await monitor.RunOnceAsync(Ct);
            monitor.Last.Should().BeNull();
            reader.Observe(StreamMetrics.BinsOverdueName).Should().BeEmpty("only the replica that swept reports");
        }

        await monitor.RunOnceAsync(Ct);

        monitor.Last.Should().Equal([("DMO", 1)], "the bin that ends at 10:00 is not mature yet");
        var gauge = reader.Observe(StreamMetrics.BinsOverdueName).Should().ContainSingle().Subject;
        (gauge.Value, gauge.Tags["site"]).Should().Be((1d, "DMO"));

        await using (await HoldTheLockAsync(database))
        {
            clock.Advance(TimeSpan.FromSeconds(60));
            await monitor.RunOnceAsync(Ct);
            reader.Observe(StreamMetrics.BinsOverdueName).Should().ContainSingle("this replica's last sweep stays fresh for two intervals");
            clock.Advance(TimeSpan.FromSeconds(61));
            reader.Observe(StreamMetrics.BinsOverdueName).Should().BeEmpty("and then lapses, so a dashboard reads the replica that sweeps now");
        }

        await monitor.RunOnceAsync(Ct);
        monitor.Last.Should().Equal([("DMO", 2)], "two minutes on, the bin that ended at 10:00 is mature too");
    }
}
