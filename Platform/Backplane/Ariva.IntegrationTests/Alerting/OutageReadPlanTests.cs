using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ariva.Core;
using Ariva.Core.Alerting;
using Ariva.Core.Domain.Entities;
using Ariva.Core.Domain.Enums;
using Ariva.Core.Queueing;
using Ariva.Core.Sensing;
using Ariva.Infra.Alerting;
using Ariva.Infra.Services.Reports;
using Ariva.Infra.Services.Validation;
using Ariva.Infra.Settings;
using Ariva.Infra.Streaming;
using Ariva.Infra.Timescale;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Ariva.IntegrationTests.Alerting;

/// <summary>
/// ARV-228a (CWE-400, docs/security/costly-operations.md section 15 item 14): the reads of <c>zone_outage</c> and of interval 0 of
/// <c>lane_staffing</c> follow the range they read, not the table's history, and a P90 rule reads only the bins its look-back
/// can use (script 0068). Each read is explained (<c>EXPLAIN (ANALYZE, BUFFERS)</c>) as the reader writes it (its SQL constant,
/// with <c>IN (:keys)</c> written as <c>= ANY(@keys)</c>, which PostgreSQL plans alike, and each <c>:name</c> as <c>@name</c>), and
/// the rows its scans touch are counted: the rows they return and those they read and removed, over every loop.
/// <para>
/// The seed, in a database of this class alone: a year of <c>zone_outage</c> (64 zones, 4 devices each, 4 outages of 12 minutes
/// a day per device: 373,760 rows) with two outages still open (one started on the last day, one 200 days before it); one day
/// of <c>lane_staffing</c> for 8 lanes as the staffing job writes it (13 intervals a minute). The retention policies of both tables
/// are turned off in this database first, so a year of history stays while the test runs; the policies themselves are
/// asserted (90 days, the raw sensing period, for zone_outage). Each read is shown against a control: the same read with the
/// index script 0068 adds dropped, in a transaction rolled back, touches many times the rows.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed partial class OutageReadPlanTests(PostgresFixture fixture) : IAsyncDisposable
{
    #region Seed shape

    private const string Database = "it_outage_read_plan";
    private const string Site = "DMO";
    private const int Zones = 64;
    private const int DevicesPerZone = 4;
    private const int OutagesPerDay = 4;
    private const int HistoryDays = 365;
    private const int OutagesADay = Zones * DevicesPerZone * OutagesPerDay;
    private const int StaffingLanes = 8;
    private const int StaffingIntervals = 13;

    /// <summary>The last day of the seeded history (its outages and the staffing day).</summary>
    private static readonly DateTime LastDay = new(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime FirstDay = LastDay.AddDays(1 - HistoryDays);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _seeded;

    private readonly AccountsHost _host = new(fixture, new Dictionary<string, string> { ["Database:Name"] = Database }, TestDatabase.AlertRules);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ZoneKey(int zone) => string.Create(CultureInfo.InvariantCulture, $"{Site}/RP-{zone:00}");

    private static string[] AllZoneKeys => [.. Enumerable.Range(0, Zones).Select(ZoneKey)];

    #endregion

    #region zone_outage

    public static TheoryData<string> Days => new() { "last", "middle" };

    /// <summary>The day read: the last of the history, or one half a year before it (chunks after it are skipped, not read).</summary>
    private static DateTime DayOf(string which) => which == "last" ? LastDay : LastDay.AddDays(-182);

    [Theory]
    [MemberData(nameof(Days))]
    public async Task SensorRuleRead_Should_TouchTheRangeNotTheHistory_When_AYearOfOutagesIsStored(string which)
    {
        await SeededAsync();
        var day = DayOf(which);
        var parameters = new Dictionary<string, object> { ["keys"] = AllZoneKeys, ["from"] = day, ["to"] = day.AddDays(1), ["limit"] = AlertInputs.DefaultMaxOutageRows + 1 };

        var plan = await ExplainAsync(AlertInputs.OutagesSql, parameters, "zone_outage");
        var control = await ExplainAsync(AlertInputs.OutagesSql, parameters, "zone_outage", dropIndex: "ix_zone_outage_to");

        Report("sensor rule", which, plan, control);
        plan.SeqScanned.Where(c => !Overlaps(c, day, day.AddDays(1))).Should().BeEmpty("no chunk before the range is scanned; one holding the range may be, if it is small");
        plan.Indexes.Should().Contain(i => i.EndsWith("ix_zone_outage_to", StringComparison.Ordinal));
        plan.Touched.Should().BeLessThanOrEqualTo(9L * OutagesADay, "a day's read touches about the day's outages and one chunk's later starts, not the year");
        control.Touched.Should().BeGreaterThan(10 * plan.Touched, "without the end index the same read takes the zone's whole history before the range");
    }

    [Theory]
    [MemberData(nameof(Days))]
    public async Task ReportReads_Should_TouchTheRangeNotTheHistory_When_AYearOfOutagesIsStored(string which)
    {
        await SeededAsync();
        var day = DayOf(which);
        var daily = new Dictionary<string, object> { ["prefix"] = Site + "/%", ["from"] = day, ["to"] = day.AddDays(1), ["cap"] = 100_001 };
        var weekly = new Dictionary<string, object> { ["prefix"] = Site + "/%", ["from"] = day.AddDays(-6), ["to"] = day.AddDays(1), ["cap"] = 100_001 };
        var validation = new Dictionary<string, object> { ["keys"] = AllZoneKeys, ["from"] = day, ["to"] = day.AddDays(1), ["limit"] = 100_001 };

        var dailyPlan = await ExplainAsync(ReportReader.OutagesSql, daily, "zone_outage");
        var weeklyPlan = await ExplainAsync(WeeklySummaryReader.OutagesSql, weekly, "zone_outage");
        var validationPlan = await ExplainAsync(SvcValidationResults.OutagesSql, validation, "zone_outage");
        var control = await ExplainAsync(ReportReader.OutagesSql, daily, "zone_outage", dropIndex: "ix_zone_outage_to");

        Report("daily report", which, dailyPlan, control);
        Report("weekly summary", which, weeklyPlan, null);
        Report("validation results", which, validationPlan, null);
        foreach (var (plan, from) in new[] { (dailyPlan, day), (weeklyPlan, day.AddDays(-6)), (validationPlan, day) })
        {
            plan.SeqScanned.Where(c => !Overlaps(c, from, day.AddDays(1))).Should().BeEmpty(
                "an outage overlapping the range is found by its end and one still open by the open index; no chunk before the range is scanned");
            plan.Indexes.Should().Contain(i => i.EndsWith("ix_zone_outage_to", StringComparison.Ordinal));
            plan.Indexes.Should().Contain(i => i.EndsWith("ix_zone_outage_open", StringComparison.Ordinal), "an outage still open is read whatever its end");
        }

        dailyPlan.Touched.Should().BeLessThanOrEqualTo(9L * OutagesADay);
        validationPlan.Touched.Should().BeLessThanOrEqualTo(9L * OutagesADay);
        weeklyPlan.Touched.Should().BeLessThanOrEqualTo(15L * OutagesADay, "a week's read touches about the week's outages and one chunk more");
        control.Touched.Should().BeGreaterThan(10 * dailyPlan.Touched);
    }

    [Fact]
    public async Task OutageReads_Should_FindAnOutageStillOpen_When_ItStartedLongBeforeTheRange()
    {
        await SeededAsync();
        await using var connection = await OpenAsync();
        await using var command = Command(ReportReader.OutagesSql, explain: false, connection, null);
        Bind(command, new Dictionary<string, object> { ["prefix"] = Site + "/%", ["from"] = LastDay, ["to"] = LastDay.AddDays(1), ["cap"] = 100_001 });
        var open = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
                if (!reader.GetBoolean(3))
                    open.Add(reader.GetString(0));
        }

        open.Should().BeEquivalentTo(["RP-OPEN-OLD", "RP-OPEN-NEW"], "the reports keep reading an outage still open, the one from 200 days before too");
    }

    public static TheoryData<string> Refused => new()
    {
        "DELETE FROM zone_outage",
        "TRUNCATE zone_outage",
        "DO $$ BEGIN EXECUTE format('DELETE FROM %s', (SELECT c FROM show_chunks('zone_outage') AS c LIMIT 1)); END $$",
        "DO $$ BEGIN EXECUTE format('TRUNCATE %s', (SELECT c FROM show_chunks('zone_outage') AS c LIMIT 1)); END $$",
        "SELECT drop_chunks('zone_outage', older_than => now())",
        "SELECT remove_retention_policy('zone_outage')",
        "SELECT alter_job(j.job_id, scheduled => true) FROM timescaledb_information.jobs j WHERE j.proc_name = 'policy_retention' AND j.hypertable_name = 'zone_outage'",
        "UPDATE zone_outage SET from_utc = from_utc - interval '200 days' WHERE device_code = 'RP-S0000'",
        "DO $$ BEGIN EXECUTE format('UPDATE %s SET from_utc = from_utc - interval ''1 day''', (SELECT c FROM show_chunks('zone_outage') AS c ORDER BY c DESC LIMIT 1)); END $$"
    };

    /// <summary>
    /// The runtime role cannot remove an outage by any route the hypertable opens (CWE-269, ARV-228a): not by deleting or
    /// truncating the table or one of its chunks (the chunks carry the table's grants), not by dropping chunks or changing the
    /// retention policy (the migration login owns both), and not by moving an outage's start, the column the retention reads,
    /// into a chunk the policy drops (its UPDATE covers the three columns the stream's upsert sets). Each runs in its own
    /// transaction, rolled back.
    /// </summary>
    [Theory]
    [MemberData(nameof(Refused))]
    public async Task RuntimeRole_Should_BeRefused_When_ItTriesToRemoveOrAgeAnOutage(string statement)
    {
        await SeededAsync();
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE ariva_runtime", connection, transaction))
            await role.ExecuteNonQueryAsync(Ct);
#pragma warning disable CA2100 // every row is a constant of this test (Refused)
        await using var command = new NpgsqlCommand(statement, connection, transaction);
#pragma warning restore CA2100

        var act = async () => await command.ExecuteNonQueryAsync(Ct);

        (await act.Should().ThrowAsync<PostgresException>(statement)).Which.SqlState.Should().Be("42501", statement);
        await transaction.RollbackAsync(Ct);
    }

    /// <summary>
    /// The stream's upsert (StreamStore's statement, the same columns) as the runtime role on the hypertable: an outage written
    /// open and then closed under its key is one row, closed, in a chunk the runtime role's insert created.
    /// </summary>
    [Fact]
    public async Task RuntimeRole_Should_WriteAndCloseAnOutage_When_TheStreamUpsertsIt()
    {
        await SeededAsync();
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE ariva_runtime", connection, transaction))
            await role.ExecuteNonQueryAsync(Ct);
        var from = LastDay.AddDays(40);
        foreach (var closed in new[] { false, true })
        {
            await using var upsert = new NpgsqlCommand("""
                INSERT INTO zone_outage (zone_key, device_code, from_utc, to_utc, closed, recorded_on)
                SELECT * FROM unnest(@zones, @devices, @froms, @tos, @closed, @now)
                ON CONFLICT (zone_key, device_code, from_utc) DO UPDATE SET to_utc = EXCLUDED.to_utc, closed = EXCLUDED.closed, recorded_on = EXCLUDED.recorded_on
                """, connection, transaction);
            upsert.Parameters.AddWithValue("zones", new[] { "DMO/RP-00" });
            upsert.Parameters.AddWithValue("devices", new[] { "RP-UPSERT" });
            upsert.Parameters.AddWithValue("froms", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz, new[] { from });
            upsert.Parameters.AddWithValue("tos", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz, new[] { from.AddMinutes(closed ? 30 : 5) });
            upsert.Parameters.AddWithValue("closed", new[] { closed });
            upsert.Parameters.AddWithValue("now", NpgsqlDbType.Array | NpgsqlDbType.TimestampTz, new[] { LastDay });
            await upsert.ExecuteNonQueryAsync(Ct);
        }

        await using var read = new NpgsqlCommand("SELECT count(*), bool_and(closed), max(to_utc) FROM zone_outage WHERE device_code = 'RP-UPSERT'", connection, transaction);
        await using (var reader = await read.ExecuteReaderAsync(Ct))
        {
            (await reader.ReadAsync(Ct)).Should().BeTrue();
            reader.GetInt64(0).Should().Be(1, "the second write under the same key updates the first");
            reader.GetBoolean(1).Should().BeTrue();
            reader.GetFieldValue<DateTime>(2).Should().Be(from.AddMinutes(30));
        }

        // The chunk the runtime role's own insert created carries the narrowed grants too: its start cannot be moved there.
        await using var age = new NpgsqlCommand("""
            DO $$ BEGIN EXECUTE format('UPDATE %s SET from_utc = from_utc - interval ''1 minute'' WHERE device_code = ''RP-UPSERT''',
                (SELECT tableoid::regclass FROM zone_outage WHERE device_code = 'RP-UPSERT')); END $$
            """, connection, transaction);
        var aging = async () => await age.ExecuteNonQueryAsync(Ct);
        (await aging.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
        await transaction.RollbackAsync(Ct);
    }

    /// <summary>
    /// The upgrade every deployed database takes (review of ARV-228a, round 2): zone_outage already holds outages over more than
    /// a week when script 0068 runs, so <c>create_hypertable(..., migrate_data => true)</c> moves them into chunks before the
    /// script's REVOKE and column GRANT. In a database of its own, migrated to 0067, seeded, then to the latest script: every
    /// row is still there, in more than one chunk, and every migrated chunk carries the narrowed grants (no table-wide UPDATE,
    /// no UPDATE of <c>from_utc</c>, UPDATE of the three columns the upsert sets, no DELETE or TRUNCATE), which the runtime
    /// role finds when it tries.
    /// </summary>
    [Fact]
    public async Task Upgrade_Should_KeepTheOutagesAndNarrowEveryMigratedChunk_When_Script0068RunsOnAPopulatedTable()
    {
        try
        {
            await UpgradeAsync();
        }
        finally
        {
            // The database holds a scheduled retention job and a month of outages: it is not left in the shared server.
            await using var admin = new NpgsqlConnection(fixture.ConnectionString("postgres"));
            await admin.OpenAsync(Ct);
            await using var drop = new NpgsqlCommand("DROP DATABASE IF EXISTS it_outage_upgrade WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(Ct);
        }
    }

    private async Task UpgradeAsync()
    {
        await using (var admin = new NpgsqlConnection(fixture.ConnectionString("postgres")))
        {
            await admin.OpenAsync(Ct);
            await using (var drop = new NpgsqlCommand("DROP DATABASE IF EXISTS it_outage_upgrade WITH (FORCE)", admin))
                await drop.ExecuteNonQueryAsync(Ct);
            await using var create = new NpgsqlCommand("CREATE DATABASE it_outage_upgrade", admin);
            await create.ExecuteNonQueryAsync(Ct);
        }

        var runner = new SqlScriptRunner(fixture.ConnectionString(UpgradeDatabase), NullLogger<SqlScriptRunner>.Instance);
        await runner.ApplyAsync([.. SqlScriptCatalog.Embedded().Where(s => s.Number < 68)], Ct);
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(UpgradeDatabase));
        await connection.OpenAsync(Ct);
        await using (var seed = new NpgsqlCommand("""
            INSERT INTO zone_outage (zone_key, device_code, from_utc, to_utc, closed, recorded_on)
            SELECT 'DMO/RP-UP', 'RP-UP-' || d, @first + k * interval '1 day' + d * interval '1 hour', @first + k * interval '1 day' + d * interval '1 hour' + interval '12 minutes',
                   k < 29, now()
              FROM generate_series(0, 2) d, generate_series(0, 29) k
            """, connection))
        {
            // The month before today: within the retention period, so the job this database keeps scheduled never drops a row of
            // it while the test runs, whatever the day it runs on.
            seed.Parameters.AddWithValue("first", NpgsqlDbType.TimestampTz, DateTime.UtcNow.Date.AddDays(-29));
            await seed.ExecuteNonQueryAsync(Ct);
        }

        var applied = await runner.ApplyAsync(SqlScriptCatalog.Embedded(), Ct);

        applied.Should().Contain("0068_outage_and_staffing_reads.sql");
        await using (var check = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM zone_outage),
                   (SELECT count(*) FROM show_chunks('zone_outage')),
                   (SELECT count(*) FROM show_chunks('zone_outage') c
                     WHERE has_table_privilege('ariva_runtime', c, 'UPDATE') OR has_table_privilege('ariva_runtime', c, 'DELETE')
                        OR has_table_privilege('ariva_runtime', c, 'TRUNCATE') OR has_column_privilege('ariva_runtime', c, 'from_utc', 'UPDATE')
                        OR NOT has_column_privilege('ariva_runtime', c, 'to_utc', 'UPDATE') OR NOT has_column_privilege('ariva_runtime', c, 'closed', 'UPDATE')
                        OR NOT has_column_privilege('ariva_runtime', c, 'recorded_on', 'UPDATE') OR NOT has_table_privilege('ariva_runtime', c, 'INSERT'))
            """, connection))
        await using (var reader = await check.ExecuteReaderAsync(Ct))
        {
            (await reader.ReadAsync(Ct)).Should().BeTrue();
            reader.GetInt64(0).Should().Be(90, "every outage stored before 0068 is moved into the hypertable, none lost");
            reader.GetInt64(1).Should().BeGreaterThan(1, "a month of outages spans more than one 7-day chunk");
            reader.GetInt64(2).Should().Be(0, "every migrated chunk carries the narrowed grants of script 0068");
        }

        foreach (var statement in UpgradeRefused)
        {
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            await using (var role = new NpgsqlCommand("SET LOCAL ROLE ariva_runtime", connection, transaction))
                await role.ExecuteNonQueryAsync(Ct);
#pragma warning disable CA2100 // every statement is a constant of this test (UpgradeRefused)
            await using var command = new NpgsqlCommand(statement, connection, transaction);
#pragma warning restore CA2100
            var act = async () => await command.ExecuteNonQueryAsync(Ct);
            (await act.Should().ThrowAsync<PostgresException>(statement)).Which.SqlState.Should().Be("42501", statement);
            await transaction.RollbackAsync(Ct);
        }
    }

    private const string UpgradeDatabase = "it_outage_upgrade";

    private static readonly string[] UpgradeRefused =
    [
        "DO $$ BEGIN EXECUTE format('UPDATE %s SET from_utc = from_utc - interval ''1 minute''', (SELECT c FROM show_chunks('zone_outage') AS c ORDER BY c LIMIT 1)); END $$",
        "DO $$ BEGIN EXECUTE format('DELETE FROM %s', (SELECT c FROM show_chunks('zone_outage') AS c ORDER BY c LIMIT 1)); END $$",
        "DO $$ BEGIN EXECUTE format('TRUNCATE %s', (SELECT c FROM show_chunks('zone_outage') AS c ORDER BY c LIMIT 1)); END $$"
    ];

    [Fact]
    public async Task Retention_Should_BeTheRawSensingPeriod_When_TheScriptsAreApplied()
    {
        await SeededAsync();
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT h.hypertable_name, (j.config ->> 'drop_after')::interval = make_interval(days => @days)
              FROM timescaledb_information.hypertables h
              LEFT JOIN timescaledb_information.jobs j ON j.hypertable_name = h.hypertable_name AND j.proc_name = 'policy_retention'
             WHERE h.hypertable_name = 'zone_outage'
            """, connection);
        command.Parameters.AddWithValue("days", new RetentionSettings().RawSensingDays);
        await using var reader = await command.ExecuteReaderAsync(Ct);

        (await reader.ReadAsync(Ct)).Should().BeTrue("zone_outage is a hypertable since script 0068");
        reader.GetBoolean(1).Should().BeTrue("its retention is the raw sensing period, 90 days by default, as device_health_event's");
    }

    #endregion

    #region lane_staffing

    [Fact]
    public async Task StaffingRead_Should_TouchOnlyIntervalZero_When_ABacktestReadsARange()
    {
        await SeededAsync();
        var keys = Enumerable.Range(0, StaffingLanes).Select(z => string.Create(CultureInfo.InvariantCulture, $"{Site}/RS-{z:00}")).ToArray();
        var from = LastDay.AddHours(6);
        var parameters = new Dictionary<string, object> { ["keys"] = keys, ["from"] = from, ["to"] = from.AddHours(6), ["limit"] = 100_001 };
        const long intervalZeroRows = StaffingLanes * 360L;

        var plan = await ExplainAsync(AlertInputs.StaffingSql, parameters, "lane_staffing");
        var control = await ExplainAsync(AlertInputs.StaffingSql, parameters, "lane_staffing", dropIndex: "ix_lane_staffing_interval0");

        Report("staffing interval 0", "last", plan, control);
        plan.SeqScanned.Should().BeEmpty();
        plan.IndexOnly.Should().BeTrue("the partial index carries every column the read takes, so no heap page is read");
        plan.Indexes.Should().OnlyContain(i => i.EndsWith("ix_lane_staffing_interval0", StringComparison.Ordinal));
        plan.Touched.Should().Be(intervalZeroRows, "the read touches interval 0 of each lane minute of the range and nothing else");
        plan.HeapFetches.Should().Be(0);
        control.Blocks.Should().BeGreaterThan(4 * plan.Blocks, "the primary key and the heap hold the 13 intervals of a minute together");
    }

    #endregion

    #region P90 look-back

    [Fact]
    public void BinReadFrom_Should_BeTheLookBackAndOneBin_When_ARangeStarts()
    {
        var from = new DateTime(2026, 10, 31, 6, 0, 0, DateTimeKind.Utc);

        AlertInputs.BinReadFrom(from).Should().Be(from.AddMinutes(-165), "150 minutes of look-back and one 15-minute bin, not a day more");
        AlertInputs.LongestBin.Should().Be(TimeSpan.FromMinutes(15), "the stream writes 15-minute bins only");
    }

    /// <summary>
    /// The first minute after the range's start takes the bin that ended 148 minutes before it (inside the 150-minute look-back,
    /// started 163 minutes before the range), not the one that ended exactly 150 minutes before it; a stored bin of a day that
    /// ends inside the range is not read, since the read reaches back one 15-minute bin beyond the look-back (before ARV-228a it
    /// reached back a day more and that bin's P90 decided every minute after it ended).
    /// </summary>
    [Fact]
    public async Task BinP90_Should_ReadOnlyTheBinsItsLookBackUses_When_ARangeIsRead()
    {
        await SeededAsync();
        var from = LastDay.AddHours(12);
        var key = ZoneKeys.For(Site, P90Zone);
        var admin = await _host.CreateUserAsync("it.readplan." + Guid.NewGuid().ToString("N")[..8]);
        await new StreamStore(new DatabaseSettings
        {
            Host = fixture.Hostname, Port = fixture.Port, Name = Database, Username = fixture.AdminUsername, Password = fixture.AdminPassword
        }, TimeProvider.System).SaveAsync(new StreamCheckpoint("it-read-plan", [new ZoneOutputs(key,
            [.. Enumerable.Range(1, 60).Select(m => new MinuteResult(P90Zone, from.AddMinutes(m), BinStatus.Final, 5, 5, Waits))],
            [Bin(from.AddMinutes(-163), 15, 30), Bin(from.AddMinutes(-164), 15, 50), Bin(from.AddMinutes(-1430), 1440, 90), Bin(from.AddMinutes(-30), 15, 12)],
            [.. Enumerable.Range(1, 60).Select(m => new QueueLiveMinute(key, from.AddMinutes(m), 20, true, false, 4, 5, null, false))], [])], [], [], []), Ct);
        var rule = new AlertRuleValues("P90 bound", [P90Zone], AlertMetric.BinP90, AlertComparator.GreaterThan, 15, null, null, 1, 1, AlertSeverity.Warning, null,
            null, null, null, false, true, null);

        var series = await _host.AsCallerAsync(admin, s => s.GetRequiredService<AlertInputs>().ReadAsync(Site, rule, [new AlertTarget(P90Zone, null)], from, from.AddMinutes(60), Ct));

        var minutes = series.Should().ContainSingle().Which.Minutes;
        minutes.Should().HaveCount(60);
        minutes[0].Value.Should().Be(30, "the bin that ended 148 minutes before the first minute is in its look-back, the one that ended 150 minutes before is not");
        minutes[0].BinStartUtc.Should().Be(from.AddMinutes(-163));
        minutes.Should().OnlyContain(m => m.Value != 90, "a day-long bin starts before the read reaches back, so it is not read");
    }

    private const string P90Zone = "RP-P90";

    private static readonly WaitSummary Waits = new(6, 4.5, 4.0, 7.5, 8.0, 0.9, [(8, 1)]);

    private static BinResult Bin(DateTime start, int minutes, double p90) =>
        new(P90Zone, start, TimeSpan.FromMinutes(minutes), 1, BinStatus.Final, BinQuality.Good, 60, 58,
            new WaitSummary(55, p90 * 0.6, p90 * 0.5, p90, p90 * 1.1, 0.8, []), 2, 0, 0, 0, 0, 0, 0, 12, null);

    #endregion

    #region Explain

    /// <summary>What a plan's scans of the table did: the rows touched, the buffers, the heap fetches, the time range of each chunk scanned sequentially, the indexes and whether every scan was index-only.</summary>
    private sealed record Plan(long Touched, long Blocks, long HeapFetches, IReadOnlyList<ChunkRange> SeqScanned, IReadOnlyList<string> Indexes, bool IndexOnly);

    /// <summary>A chunk's time range (none for the table itself, without TimescaleDB).</summary>
    private sealed record ChunkRange(DateTime? Start, DateTime? End);

    /// <summary>Whether a chunk may hold an outage of the range: it does not end a day or more before the range, nor start after it.</summary>
    private static bool Overlaps(ChunkRange chunk, DateTime from, DateTime to) =>
        chunk.Start is not { } start || chunk.End is not { } end || (end > from.AddDays(-1) && start < to);

    /// <summary>
    /// The reader's SQL explained with its values; <paramref name="dropIndex"/> runs it once more without that index, in a transaction
    /// rolled back, as the control.
    /// </summary>
    private async Task<Plan> ExplainAsync([ConstantExpected] string sql, IReadOnlyDictionary<string, object> parameters, string table, string dropIndex = null)
    {
        await using var connection = await OpenAsync();
        var relations = await RelationsAsync(connection, table);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        if (dropIndex is not null)
        {
            // One constant statement per index a control drops, in this transaction, which is rolled back.
            await using var drop = dropIndex switch
            {
                "ix_zone_outage_to" => new NpgsqlCommand("DROP INDEX ix_zone_outage_to", connection, transaction),
                "ix_lane_staffing_interval0" => new NpgsqlCommand("DROP INDEX ix_lane_staffing_interval0", connection, transaction),
                _ => throw new ArgumentException("Not an index a control of this test drops.", nameof(dropIndex))
            };
            await drop.ExecuteNonQueryAsync(Ct);
        }

        await using var command = Command(sql, explain: true, connection, transaction);
        Bind(command, parameters);
        var json = (string)await command.ExecuteScalarAsync(Ct);
        await transaction.RollbackAsync(Ct);

        using var document = JsonDocument.Parse(json);
        long touched = 0, blocks = 0, heap = 0;
        var seq = new List<ChunkRange>();
        var indexes = new List<string>();
        var indexOnly = true;
        var scans = 0;
        void Walk(JsonElement node)
        {
            var type = node.GetProperty("Node Type").GetString();
            var relation = node.TryGetProperty("Relation Name", out var r) ? r.GetString() : null;
            if (relation is not null && relations.TryGetValue(relation, out var range))
            {
                var loops = Number(node, "Actual Loops");
                var rows = (Number(node, "Actual Rows") + Number(node, "Rows Removed by Filter") + Number(node, "Rows Removed by Index Recheck")) * loops;
                scans++;
                if (type == "Seq Scan")
                    seq.Add(range);
                if (type is "Seq Scan" or "Index Scan" or "Index Only Scan" or "Bitmap Heap Scan")
                    touched += rows;
                if (type != "Index Only Scan")
                    indexOnly = false;
                heap += Number(node, "Heap Fetches");
            }

            if (type is "Index Scan" or "Index Only Scan" or "Bitmap Index Scan" && node.TryGetProperty("Index Name", out var index))
                indexes.Add(index.GetString());
            if (node.TryGetProperty("Plans", out var children))
                foreach (var child in children.EnumerateArray())
                    Walk(child);
        }

        var top = document.RootElement[0].GetProperty("Plan");
        Walk(top);
        blocks = Number(top, "Shared Hit Blocks") + Number(top, "Shared Read Blocks");
        return new Plan(touched, blocks, heap, seq, indexes, indexOnly && scans > 0);
    }

    private const string ExplainPrefix = "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) ";

    /// <summary>
    /// A reader's SQL constant as Npgsql runs it (<see cref="Npgsql"/>), explained or as it is: every caller passes a reader's
    /// constant, so the text is never built from input, which is what CA2100 guards against.
    /// </summary>
    private static NpgsqlCommand Command([ConstantExpected] string sql, bool explain, NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        var text = explain ? ExplainPrefix + Npgsql(sql) : Npgsql(sql);
#pragma warning disable CA2100 // every caller passes a reader's constant (ConstantExpected)
        return new NpgsqlCommand(text, connection, transaction);
#pragma warning restore CA2100
    }

    private static long Number(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) ? (long)Math.Round(value.GetDouble()) : 0;

    /// <summary>The table and its chunks, by the names a plan gives them, with each chunk's time range.</summary>
    private static async Task<Dictionary<string, ChunkRange>> RelationsAsync(NpgsqlConnection connection, string table)
    {
        await using var command = new NpgsqlCommand("""
            SELECT chunk_name, range_start, range_end FROM timescaledb_information.chunks WHERE hypertable_name = @table
            """, connection);
        command.Parameters.AddWithValue("table", table);
        var names = new Dictionary<string, ChunkRange>(StringComparer.Ordinal) { [table] = new ChunkRange(null, null) };
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            names[reader.GetString(0)] = new ChunkRange(reader.GetFieldValue<DateTime>(1), reader.GetFieldValue<DateTime>(2));
        return names;
    }

    /// <summary>
    /// A reader's SQL as Npgsql binds it: an NHibernate parameter list (<c>IN (:keys)</c>) as an array, each named parameter with an at
    /// sign. The alert inputs' constants already bind the array (<c>= ANY(:keys)</c>, the typed reader, ARV-228b).
    /// </summary>
    private static string Npgsql(string sql) => NamedParameter().Replace(sql.Replace("IN (:keys)", "= ANY(:keys)", StringComparison.Ordinal), "@$1");

    [GeneratedRegex(@"(?<![:\w]):([A-Za-z_]\w*)", RegexOptions.CultureInvariant)]
    private static partial Regex NamedParameter();

    private static void Bind(NpgsqlCommand command, IReadOnlyDictionary<string, object> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            if (value is DateTime at)
                command.Parameters.AddWithValue(name, NpgsqlDbType.TimestampTz, at);
            else
                command.Parameters.AddWithValue(name, value);
        }
    }

    private static void Report(string read, string which, Plan plan, Plan control)
    {
        var indexes = string.Join(", ", plan.Indexes.Select(i => i[Math.Max(0, i.LastIndexOf("ix_", StringComparison.Ordinal))..]).Distinct());
        var without = control is null
            ? string.Empty
            : string.Create(CultureInfo.InvariantCulture, $"; without the index {control.Touched:N0} rows, {control.Blocks:N0} blocks");
        Send(string.Create(CultureInfo.InvariantCulture, $"Read plan: {read} ({which} day): {plan.Touched:N0} rows touched, {plan.Blocks:N0} blocks, indexes {indexes}{without}"));
    }

    private static void Send(string line)
    {
        TestContext.Current.SendDiagnosticMessage(line);
        TestContext.Current.TestOutputHelper?.WriteLine(line);
    }

    #endregion

    #region Seed

    private async Task SeededAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            if (_seeded)
                return;
            await using (var admin = new NpgsqlConnection(fixture.ConnectionString("postgres")))
            {
                await admin.OpenAsync(Ct);
                await using (var drop = new NpgsqlCommand("DROP DATABASE IF EXISTS it_outage_read_plan WITH (FORCE)", admin))
                    await drop.ExecuteNonQueryAsync(Ct);
                await using var create = new NpgsqlCommand("CREATE DATABASE it_outage_read_plan", admin);
                await create.ExecuteNonQueryAsync(Ct);
            }

            await new SqlScriptRunner(fixture.ConnectionString(Database), NullLogger<SqlScriptRunner>.Instance).ApplyAsync(SqlScriptCatalog.Embedded(), Ct);
            await using var connection = await OpenAsync();
            // A year of history stays while the test runs: the retention jobs of this database alone are turned off.
            await using (var off = new NpgsqlCommand("""
                SELECT alter_job(j.job_id, scheduled => false) FROM timescaledb_information.jobs j
                 WHERE j.proc_name = 'policy_retention' AND j.hypertable_name IN ('zone_outage', 'lane_staffing')
                """, connection))
                await off.ExecuteNonQueryAsync(Ct);
            await using (var outages = new NpgsqlCommand("""
                INSERT INTO zone_outage (zone_key, device_code, from_utc, to_utc, closed, recorded_on)
                SELECT 'DMO/RP-' || lpad(CAST(z AS text), 2, '0'), 'RP-S' || lpad(CAST(z * @devices + d AS text), 4, '0'), o.from_utc,
                       o.from_utc + interval '12 minutes', true, now()
                  FROM generate_series(0, @zones - 1) z, generate_series(0, @devices - 1) d,
                       LATERAL (SELECT @first + k * interval '6 hours' + ((z * @devices + d) % 240) * interval '1 minute' AS from_utc
                                  FROM generate_series(0, @count - 1) k) o;
                INSERT INTO zone_outage (zone_key, device_code, from_utc, to_utc, closed, recorded_on) VALUES
                    ('DMO/RP-00', 'RP-OPEN-OLD', @last - interval '200 days', @last - interval '200 days' + interval '1 minute', false, now()),
                    ('DMO/RP-01', 'RP-OPEN-NEW', @last + interval '3 hours', @last + interval '3 hours 1 minute', false, now());
                """, connection))
            {
                outages.Parameters.AddWithValue("zones", Zones);
                outages.Parameters.AddWithValue("devices", DevicesPerZone);
                outages.Parameters.AddWithValue("count", HistoryDays * OutagesPerDay);
                outages.Parameters.AddWithValue("first", NpgsqlDbType.TimestampTz, FirstDay);
                outages.Parameters.AddWithValue("last", NpgsqlDbType.TimestampTz, LastDay);
                outages.CommandTimeout = 600;
                await outages.ExecuteNonQueryAsync(Ct);
            }

            await StaffingAsync(connection);
            await using (var vacuum = new NpgsqlCommand("VACUUM (ANALYZE) zone_outage, lane_staffing", connection))
            {
                vacuum.CommandTimeout = 600;
                await vacuum.ExecuteNonQueryAsync(Ct);
            }

            await using (var count = new NpgsqlCommand("SELECT count(*) FROM zone_outage", connection))
                ((long)await count.ExecuteScalarAsync(Ct)).Should().Be((long)Zones * DevicesPerZone * HistoryDays * OutagesPerDay + 2, "a year of outages");
            _seeded = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString(Database));
        await connection.OpenAsync(Ct);
        return connection;
    }

    /// <summary>The last day of <c>lane_staffing</c> for 8 lanes, as the staffing job writes it: per minute its 13 intervals together.</summary>
    private static async Task StaffingAsync(NpgsqlConnection connection)
    {
        await using var copy = await connection.BeginBinaryImportAsync("""
            COPY lane_staffing (zone_key, minute_utc, interval_index, lane, start_utc, minutes, arrivals, recommended, bound, at_limit, shortfall,
                wait_recommended, wait_open, no_service_open, backlog_end, gap, lane_gap, queue_minute_utc, queue_now, open_now, open_now_degraded, desks,
                target_minutes, busy_cap, throughput_per_desk, throughput_source, computed_utc) FROM STDIN (FORMAT BINARY)
            """, Ct);
        for (var z = 0; z < StaffingLanes; z++)
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"{Site}/RS-{z:00}");
            var lane = LaneCategory.Reference[z % LaneCategory.Reference.Count];
            for (var t = 0; t < 1440; t++)
            {
                var minute = LastDay.AddMinutes(t);
                var quarter = minute.AddMinutes(-(t % 15));
                for (short k = 0; k < StaffingIntervals; k++)
                {
                    await copy.StartRowAsync(Ct);
                    await copy.WriteAsync(key, NpgsqlDbType.Varchar, Ct);
                    await copy.WriteAsync(minute, NpgsqlDbType.TimestampTz, Ct);
                    await copy.WriteAsync(k, NpgsqlDbType.Smallint, Ct);
                    await copy.WriteAsync(lane, NpgsqlDbType.Varchar, Ct);
                    await copy.WriteAsync(k == 0 ? minute : quarter.AddMinutes(k * 15), NpgsqlDbType.TimestampTz, Ct);
                    await copy.WriteAsync((short)(k == 0 ? 15 - t % 15 : 15), NpgsqlDbType.Smallint, Ct);
                    await copy.WriteAsync(30.0, NpgsqlDbType.Double, Ct);
                    await copy.WriteAsync((short)(4 + (t / 5 + z) % 4), NpgsqlDbType.Smallint, Ct);
                    await copy.WriteAsync("Wait", NpgsqlDbType.Varchar, Ct);
                    await copy.WriteAsync(false, NpgsqlDbType.Boolean, Ct);
                    await copy.WriteNullAsync(Ct);
                    await copy.WriteAsync(8.0, NpgsqlDbType.Double, Ct);
                    await copy.WriteAsync(12.0, NpgsqlDbType.Double, Ct);
                    await copy.WriteAsync(false, NpgsqlDbType.Boolean, Ct);
                    await copy.WriteNullAsync(Ct);
                    await copy.WriteAsync("None", NpgsqlDbType.Varchar, Ct);
                    await copy.WriteAsync("None", NpgsqlDbType.Varchar, Ct);
                    await copy.WriteAsync(minute.AddMinutes(-1), NpgsqlDbType.TimestampTz, Ct);
                    await copy.WriteAsync(40.0, NpgsqlDbType.Double, Ct);
                    await copy.WriteAsync((short)4, NpgsqlDbType.Smallint, Ct);
                    await copy.WriteAsync(false, NpgsqlDbType.Boolean, Ct);
                    await copy.WriteAsync((short)8, NpgsqlDbType.Smallint, Ct);
                    await copy.WriteAsync(10.0, NpgsqlDbType.Double, Ct);
                    await copy.WriteAsync(0.85, NpgsqlDbType.Double, Ct);
                    await copy.WriteAsync(0.5, NpgsqlDbType.Double, Ct);
                    await copy.WriteAsync("Measured", NpgsqlDbType.Varchar, Ct);
                    await copy.WriteAsync(minute.AddSeconds(45), NpgsqlDbType.TimestampTz, Ct);
                }
            }
        }

        await copy.CompleteAsync(Ct);
    }

    #endregion
}
