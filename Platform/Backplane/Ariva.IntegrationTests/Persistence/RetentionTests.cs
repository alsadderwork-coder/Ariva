using System.Globalization;
using Ariva.Di;
using Ariva.Infra.Retention;
using Ariva.Infra.Settings;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Ariva.IntegrationTests.Persistence;

/// <summary>
/// ARV-111a against TimescaleDB, through the real migration job (<see cref="DatabaseMigration"/>, as <c>api-main --migrate</c>)
/// and the real runtime login it creates:
/// <list type="bullet">
/// <item>the job sets every hypertable's retention policy and every relational period from <c>Database:Retention</c>, changes
/// nothing on a second run (the policies keep their jobs), puts the defaults back when the configuration says nothing, and
/// changes nothing at all when a period is out of range;</item>
/// <item>the runtime login cannot remove, add, alter, pause or delete a retention policy, drop chunks, change a table, write a
/// period or delete a record itself; the purge function refuses a batch out of bounds and a data set without a period;</item>
/// <item>the Cronz retention job, as the runtime login, deletes exactly the rows past their periods (never a Pending email), in
/// batches of its size and at most its batches per run (the rest the next run), keeps every row inside its period, skips the
/// run while another replica holds the lock, and skips only the data set that has no period.</item>
/// </list>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RetentionTests(PostgresFixture fixture)
{
    #region Fields

    private const string RuntimeLogin = "it_retention_app";

    private static readonly string RuntimePassword = "rt-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));

    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static string _database;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    #endregion

    #region Migration job

    [Fact]
    public async Task MigrationJob_Should_SetEveryPeriodOnceAndPutTheDefaultsBack_When_RunWithAndWithoutParameters()
    {
        (await MigrateAsync(("RawSensingDays", "30"), ("DeskZoneReadingDays", "3"), ("LaneStaffingDays", "120"), ("AlertEmailDays", "31"),
            ("IntegrationCallDays", "45"), ("ReportDeliveryDays", "60"))).Should().Be(0);

        var first = await PoliciesAsync();
        first.ToDictionary(p => p.Key, p => p.Value.Days).Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["sensing_event"] = 30, ["sensing_batch"] = 30, ["device_health_event"] = 30, ["desk_zone_reading"] = 3, ["lane_staffing"] = 120
        }, "every hypertable's policy is the configured period, raw events and device health together");
        (await PeriodsAsync()).Should().BeEquivalentTo(new Dictionary<string, int> { ["email_message"] = 31, ["integration_call"] = 45, ["report_delivery"] = 60 });
        var setOn = await ScalarAsync<DateTime>(Admin, "SELECT max(set_on) FROM retention_period");

        // A second release with the same values changes nothing: the policies keep their jobs and the periods their time.
        (await MigrateAsync(("RawSensingDays", "30"), ("DeskZoneReadingDays", "3"), ("LaneStaffingDays", "120"), ("AlertEmailDays", "31"),
            ("IntegrationCallDays", "45"), ("ReportDeliveryDays", "60"))).Should().Be(0);
        (await PoliciesAsync()).Should().BeEquivalentTo(first, "an unchanged policy is left alone");
        (await ScalarAsync<DateTime>(Admin, "SELECT max(set_on) FROM retention_period")).Should().Be(setOn);

        // Nothing configured: the defaults, which are the scripts' values and the Proposed periods.
        (await MigrateAsync()).Should().Be(0);
        var defaults = await PoliciesAsync();
        defaults.ToDictionary(p => p.Key, p => p.Value.Days).Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["sensing_event"] = 90, ["sensing_batch"] = 90, ["device_health_event"] = 90, ["desk_zone_reading"] = 7, ["lane_staffing"] = 90
        });
        defaults["sensing_event"].Job.Should().NotBe(first["sensing_event"].Job, "a changed policy is replaced, in the job's transaction");
        defaults.Values.Select(p => p.Job).Should().OnlyHaveUniqueItems("one retention policy per hypertable");
        (await PeriodsAsync()).Should().BeEquivalentTo(new Dictionary<string, int> { ["email_message"] = 180, ["integration_call"] = 400, ["report_delivery"] = 400 });
    }

    [Fact]
    public async Task MigrationJob_Should_ChangeNothing_When_APeriodIsOutOfRange()
    {
        (await MigrateAsync(("RawSensingDays", "45"), ("AlertEmailDays", "90"))).Should().Be(0);
        var policies = await PoliciesAsync();
        var periods = await PeriodsAsync();

        (await MigrateAsync(("RawSensingDays", "3"), ("AlertEmailDays", "120"))).Should().Be(1, "a raw sensing period of 3 days is refused");
        (await MigrateAsync(("RawSensingDays", "60"), ("AlertEmailDays", "10"))).Should().Be(1, "an email period of 10 days is refused");
        (await MigrateAsync(("LaneStaffingDays", "30"))).Should().Be(1, "the alert backtest reads 90 days of lane_staffing");

        (await PoliciesAsync()).Should().BeEquivalentTo(policies, "the job stops before anything changes");
        (await PeriodsAsync()).Should().BeEquivalentTo(periods);
    }

    [Fact]
    public async Task RuntimeLogin_Should_NeverChangeARetentionPolicyOrPeriodOrDeleteARecord_When_ItTries()
    {
        (await MigrateAsync()).Should().Be(0);
        var policies = await PoliciesAsync();
        var periods = await PeriodsAsync();
        var job = policies["sensing_event"].Job;

        var refusals = new (string Sql, string State)[]
        {
            ("SELECT remove_retention_policy('sensing_event')", PostgresErrorCodes.InsufficientPrivilege),
            ("SELECT remove_retention_policy('lane_staffing', if_exists => true)", PostgresErrorCodes.InsufficientPrivilege),
            ("SELECT add_retention_policy('desk_zone_reading', INTERVAL '1 day', if_not_exists => true)", PostgresErrorCodes.InsufficientPrivilege),
            ("SELECT alter_job(@job, scheduled => false)", PostgresErrorCodes.InsufficientPrivilege),
            ("SELECT delete_job(@job)", PostgresErrorCodes.InsufficientPrivilege),
            ("SELECT drop_chunks('sensing_event', older_than => INTERVAL '1 day')", PostgresErrorCodes.InsufficientPrivilege),
            ("ALTER TABLE lane_staffing SET (timescaledb.compress = false)", PostgresErrorCodes.InsufficientPrivilege),
            ("UPDATE retention_period SET days = 3650", PostgresErrorCodes.InsufficientPrivilege),
            ("INSERT INTO retention_period (data_set, days, set_on, set_by) VALUES ('email_message', 30, now(), 'x') ON CONFLICT DO NOTHING", PostgresErrorCodes.InsufficientPrivilege),
            ("DELETE FROM retention_period", PostgresErrorCodes.InsufficientPrivilege),
            ("TRUNCATE retention_period", PostgresErrorCodes.InsufficientPrivilege),
            ("DELETE FROM email_message", PostgresErrorCodes.InsufficientPrivilege),
            ("DELETE FROM integration_call", PostgresErrorCodes.InsufficientPrivilege),
            ("DELETE FROM report_delivery", PostgresErrorCodes.InsufficientPrivilege),
            ("TRUNCATE integration_call", PostgresErrorCodes.InsufficientPrivilege),
            // The time column each purge branch reads cannot be moved, or a row could be aged into the purge (round 2, N1).
            ("UPDATE report_delivery SET created_utc = now() - INTERVAL '3650 days'", PostgresErrorCodes.InsufficientPrivilege),
            ("UPDATE email_message SET created_utc = now() - INTERVAL '3650 days'", PostgresErrorCodes.InsufficientPrivilege),
            ("UPDATE integration_call SET at_utc = now() - INTERVAL '3650 days'", PostgresErrorCodes.InsufficientPrivilege),
            ("SELECT ariva_retention_purge('integration_call', 10001)", PostgresErrorCodes.InvalidParameterValue),
            ("SELECT ariva_retention_purge('integration_call', 0)", PostgresErrorCodes.InvalidParameterValue),
            ("SELECT ariva_retention_purge('audit_entry', 100)", "P0002"),
            ("SELECT ariva_retention_purge(NULL, 100)", "P0002")
        };

        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await DatabaseAsync(), RuntimeLogin, RuntimePassword));
        await connection.OpenAsync(Ct);
        foreach (var (sql, state) in refusals)
        {
            await using var transaction = await connection.BeginTransactionAsync(Ct);
#pragma warning disable CA2100 // literal statements from the list above
            await using var command = new NpgsqlCommand(sql, connection, transaction);
#pragma warning restore CA2100
            command.Parameters.AddWithValue("job", job);
            var act = () => command.ExecuteNonQueryAsync(Ct);
            (await act.Should().ThrowAsync<PostgresException>(sql)).Which.SqlState.Should().Be(state, sql);
            await transaction.RollbackAsync(Ct);
        }

        (await PoliciesAsync()).Should().BeEquivalentTo(policies);
        (await PeriodsAsync()).Should().BeEquivalentTo(periods);
    }

    [Fact]
    public async Task RuntimeLogin_Should_LockAndMarkADelivery_When_ItHoldsOnlyTheColumnGrant()
    {
        // Script 0054 leaves the runtime role UPDATE of status, attempts, reason and sent_utc of report_delivery only: the
        // delivery round still claims a delivery with SELECT ... FOR UPDATE OF d and marks it with the statements of
        // SvcReportDeliveries.MarkAsync (sent, and failed with a reason).
        (await MigrateAsync()).Should().Be(0);
        await PlantAsync();

        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await DatabaseAsync(), RuntimeLogin, RuntimePassword));
        await connection.OpenAsync(Ct);
        var claimed = new List<Guid>();
        foreach (var (status, attempts, reason, sent) in new (string, int, string, bool)[] { ("Sent", 2, null, true), ("Failed", 3, "relay refused", false) })
        {
            await using var transaction = await connection.BeginTransactionAsync(Ct);
            await using (var claim = new NpgsqlCommand("""
                SELECT d.id FROM report_delivery d JOIN report_schedule rs ON rs.id = d.schedule_id JOIN "user" u ON u.id = d.recipient_id
                 WHERE rs.enabled AND rs.deleted_on IS NULL AND NOT (d.id = ANY (@claimed))
                 ORDER BY d.created_utc DESC LIMIT 1
                FOR UPDATE OF d SKIP LOCKED
                """, connection, transaction))
            {
                claim.Parameters.AddWithValue("claimed", claimed.ToArray());
                claimed.Add((Guid)await claim.ExecuteScalarAsync(Ct));
            }

            await using (var mark = sent
                             ? new NpgsqlCommand("UPDATE report_delivery SET status = @status, attempts = @attempts, reason = NULL, sent_utc = @sent WHERE id = @id RETURNING id",
                                 connection, transaction)
                             : new NpgsqlCommand("UPDATE report_delivery SET status = @status, attempts = @attempts, reason = NULLIF(@reason, '') WHERE id = @id RETURNING id",
                                 connection, transaction))
            {
                mark.Parameters.AddWithValue("id", claimed[^1]);
                mark.Parameters.AddWithValue("status", status);
                mark.Parameters.AddWithValue("attempts", attempts);
                if (sent)
                    mark.Parameters.AddWithValue("sent", DateTime.UtcNow);
                else
                    mark.Parameters.AddWithValue("reason", reason);
                (await mark.ExecuteScalarAsync(Ct)).Should().Be(claimed[^1]);
            }

            await transaction.CommitAsync(Ct);
        }

        (await ScalarAsync<string>(Admin, """
            SELECT string_agg(status || ':' || attempts || ':' || coalesce(reason, '-') || ':' || (sent_utc > now() - INTERVAL '1 hour'), ',' ORDER BY status)
              FROM report_delivery WHERE attempts > 1
            """)).Should().Be("Failed:3:relay refused:false,Sent:2:-:true", "the sent mark set sent_utc now; the failed mark left the planted one");
    }

    #endregion

    #region Retention job

    [Fact]
    public async Task RetentionJob_Should_DeleteOnlyRowsPastTheirPeriodsInBoundedBatches_When_RunAsTheRuntimeLogin()
    {
        (await MigrateAsync(("AlertEmailDays", "31"), ("IntegrationCallDays", "45"), ("ReportDeliveryDays", "60"))).Should().Be(0);
        await PlantAsync();
        var round = Round(batchSize: 1_000, maxBatches: 2);

        // Run 1: integration calls stop at two batches of 1,000; the emails and deliveries past their periods go in one batch each.
        var first = await round.RunAsync(Ct);
        first.HeldElsewhere.Should().BeFalse();
        first.DataSets.Should().BeEquivalentTo(new[]
        {
            new RetentionDataSetRun("email_message", 31, 3, 1, true, false),
            new RetentionDataSetRun("integration_call", 45, 2_000, 2, false, false),
            new RetentionDataSetRun("report_delivery", 60, 2, 1, true, false)
        }, options => options.WithStrictOrdering());
        (await CountsAsync()).Should().Be((Calls: 504L, Emails: 3L, PendingOld: 1L, Deliveries: 2L));

        // Run 2 deletes the rest; run 3 finds nothing older.
        var second = await round.RunAsync(Ct);
        second.DataSets.Single(d => d.DataSet == "integration_call").Should().Be(new RetentionDataSetRun("integration_call", 45, 500, 1, true, false));
        second.Deleted.Should().Be(500);
        (await round.RunAsync(Ct)).Deleted.Should().Be(0);

        // Every row inside its period stays, the boundary hour included, and so does the Pending email past it.
        (await CountsAsync()).Should().Be((Calls: 4L, Emails: 3L, PendingOld: 1L, Deliveries: 2L));
        (await ScalarAsync<long>(Admin, "SELECT count(*) FROM integration_call WHERE route = 'it/retention/boundary'")).Should().Be(1);
        (await ScalarAsync<long>(Admin, "SELECT count(*) FROM email_message WHERE created_utc < now() - INTERVAL '31 days'"))
            .Should().Be(1, "the Pending email is never deleted");
    }

    [Fact]
    public async Task RetentionJob_Should_SkipTheRun_When_AnotherReplicaHoldsIt_AndSkipOnlyADataSetWithoutAPeriod()
    {
        (await MigrateAsync(("IntegrationCallDays", "45"))).Should().Be(0);
        await PlantAsync();
        var round = Round(batchSize: 10_000, maxBatches: 10);

        await using (var holder = new NpgsqlConnection(fixture.ConnectionString(await DatabaseAsync())))
        {
            await holder.OpenAsync(Ct);
            await using (var take = new NpgsqlCommand("SELECT pg_advisory_lock(52, 0)", holder))
                await take.ExecuteNonQueryAsync(Ct);

            var held = await round.RunAsync(Ct);

            held.HeldElsewhere.Should().BeTrue();
            held.DataSets.Should().BeEmpty();
            (await CountsAsync()).Calls.Should().Be(2_504, "nothing is deleted while another replica runs the job");
        }

        await ExecuteAsync(Admin, "WITH gone AS (DELETE FROM retention_period WHERE data_set = 'report_delivery' RETURNING 1) SELECT count(*) FROM gone");
        var run = await round.RunAsync(Ct);

        run.DataSets.Single(d => d.DataSet == "report_delivery").Should().Be(new RetentionDataSetRun("report_delivery", null, 0, 0, false, false));
        run.DataSets.Single(d => d.DataSet == "integration_call").Deleted.Should().Be(2_500, "the other data sets go on");
        (await CountsAsync()).Deliveries.Should().Be(4, "a data set without a period keeps every row");
        (await MigrateAsync()).Should().Be(0, "the next release writes the period again");
        (await PeriodsAsync()).Should().ContainKey("report_delivery");
    }

    #endregion

    #region Helpers

    private string Admin => fixture.ConnectionString(_database);

    private async Task<string> DatabaseAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            _database ??= await fixture.CreateDatabaseAsync(TestDatabase.Retention);
            return _database;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>The migration job (api-main --migrate) with the fixture's superuser as the migration login and this class's runtime login.</summary>
    private async Task<int> MigrateAsync(params (string Key, string Value)[] retention)
    {
        var settings = new Dictionary<string, string>
        {
            ["Database:Host"] = fixture.Hostname,
            ["Database:Port"] = fixture.Port.ToString(CultureInfo.InvariantCulture),
            ["Database:Name"] = await DatabaseAsync(),
            ["Database:Migration:Username"] = fixture.AdminUsername,
            ["Database:Migration:Password"] = fixture.AdminPassword,
            ["Database:Username"] = RuntimeLogin,
            ["Database:Password"] = RuntimePassword
        };
        foreach (var (key, value) in retention)
            settings[$"Database:Retention:{key}"] = value;
        return await DatabaseMigration.RunAsync(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), Ct);
    }

    private RetentionRound Round(int batchSize, int maxBatches) => new(
        new DatabaseSettings { Host = fixture.Hostname, Port = fixture.Port, Name = _database, Username = RuntimeLogin, Password = RuntimePassword },
        new RetentionJobSettings { BatchSize = batchSize, MaxBatchesPerRun = maxBatches },
        NullLogger<RetentionRound>.Instance);

    /// <summary>Each hypertable's retention policy: its period in whole days and its job id.</summary>
    private async Task<Dictionary<string, (int Days, int Job)>> PoliciesAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            SELECT j.hypertable_name::text, (extract(epoch FROM (j.config ->> 'drop_after')::interval) / 86400)::int, j.job_id
              FROM timescaledb_information.jobs j WHERE j.proc_name = 'policy_retention'
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var policies = new Dictionary<string, (int Days, int Job)>(StringComparer.Ordinal);
        while (await reader.ReadAsync(Ct))
            policies.Add(reader.GetString(0), (reader.GetInt32(1), reader.GetInt32(2)));
        return policies;
    }

    private async Task<Dictionary<string, int>> PeriodsAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString(await DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT data_set, days FROM retention_period", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var periods = new Dictionary<string, int>(StringComparer.Ordinal);
        while (await reader.ReadAsync(Ct))
            periods.Add(reader.GetString(0), reader.GetInt32(1));
        return periods;
    }

    private async Task<(long Calls, long Emails, long PendingOld, long Deliveries)> CountsAsync() =>
        (await ScalarAsync<long>(Admin, "SELECT count(*) FROM integration_call"),
         await ScalarAsync<long>(Admin, "SELECT count(*) FROM email_message"),
         await ScalarAsync<long>(Admin, "SELECT count(*) FROM email_message WHERE status = 'Pending'"),
         await ScalarAsync<long>(Admin, "SELECT count(*) FROM report_delivery"));

    /// <summary>
    /// Empties the three tables (a test database of this class only) and plants, relative to the database's clock:
    /// integration calls (2,500 fifty days old, 3 forty-four days old and one an hour inside 45 days); alert emails of one
    /// alert (Sent, Failed and Suppressed forty days old, one still Pending forty days old, two Sent thirty days old); report
    /// deliveries (two 61 days old, two 59 days old).
    /// </summary>
    private async Task PlantAsync()
    {
        await DatabaseAsync();
        await ExecuteAsync(Admin, """
            DELETE FROM email_message;
            DELETE FROM integration_call;
            DELETE FROM report_delivery;
            INSERT INTO site (id, code, name) VALUES (gen_random_uuid(), 'RTN', 'Retention') ON CONFLICT DO NOTHING;
            INSERT INTO "user" (id, user_name, password_hash, password_salt, password_algorithm, password_iterations)
            VALUES ('a1110000-0000-0000-0000-000000000001', 'it.retention.owner', 'x', 'x', 'PBKDF2-SHA256', 600000) ON CONFLICT DO NOTHING;
            INSERT INTO report_schedule (id, site_code, name, template, send_at, enabled, owner_id)
            VALUES ('a1110000-0000-0000-0000-000000000002', 'RTN', 'Daily', 'DailyPeaks', '06:00', true, 'a1110000-0000-0000-0000-000000000001') ON CONFLICT DO NOTHING;
            INSERT INTO alert_rule (id, site_code, code, name, scope_zones, metric, comparator, sustain_minutes, clear_after_minutes, severity, notify_by_email, enabled)
            VALUES ('a1110000-0000-0000-0000-000000000003', 'RTN', 'R-001', 'Offline', 'A-VIS', 'SensorOffline', 'IsTrue', 1, 1, 'Warning', true, true) ON CONFLICT DO NOTHING;
            INSERT INTO alert (id, site_code, rule_id, rule_code, rule_name, zone_name, device_code, metric, severity, raised_utc, raised_value, state)
            VALUES ('a1110000-0000-0000-0000-000000000004', 'RTN', 'a1110000-0000-0000-0000-000000000003', 'R-001', 'Offline', 'A-VIS', 'S-01', 'SensorOffline',
                    'Warning', now() - INTERVAL '41 days', 1, 'Raised') ON CONFLICT DO NOTHING;

            INSERT INTO integration_call (id, client_id, method, route, status, payload_bytes, at_utc)
            SELECT gen_random_uuid(), 'ic_aaaaaaaaaaaaaaaaaaaaaaaaaa', 'POST', 'it/retention/old', 200, 10, now() - INTERVAL '50 days' - n * INTERVAL '1 second'
              FROM generate_series(1, 2500) n;
            INSERT INTO integration_call (id, client_id, method, route, status, payload_bytes, at_utc)
            SELECT gen_random_uuid(), 'ic_aaaaaaaaaaaaaaaaaaaaaaaaaa', 'POST', 'it/retention/recent', 200, 10, now() - INTERVAL '44 days' - n * INTERVAL '1 second'
              FROM generate_series(1, 3) n;
            INSERT INTO integration_call (id, client_id, method, route, status, payload_bytes, at_utc)
            VALUES (gen_random_uuid(), 'ic_aaaaaaaaaaaaaaaaaaaaaaaaaa', 'POST', 'it/retention/boundary', 200, 10, now() - INTERVAL '45 days' + INTERVAL '1 hour');

            INSERT INTO email_message (id, kind, alert_id, site_code, recipient, subject, body, status, attempts, created_utc, next_attempt_utc, sent_utc)
            SELECT gen_random_uuid(), 'AlertRaised', 'a1110000-0000-0000-0000-000000000004', 'RTN', e.recipient, 'Alert', 'Alert raised', e.status, 1,
                   now() - e.age, now() - e.age, CASE WHEN e.status = 'Sent' THEN now() - e.age END
              FROM (VALUES ('old.sent@ariva.test', 'Sent', INTERVAL '40 days'), ('old.failed@ariva.test', 'Failed', INTERVAL '40 days'),
                           ('old.suppressed@ariva.test', 'Suppressed', INTERVAL '40 days'), ('old.pending@ariva.test', 'Pending', INTERVAL '40 days'),
                           ('recent.one@ariva.test', 'Sent', INTERVAL '30 days'), ('recent.two@ariva.test', 'Sent', INTERVAL '30 days')) e(recipient, status, age);

            INSERT INTO report_delivery (id, schedule_id, report_date, recipient_id, status, attempts, created_utc, sent_utc)
            SELECT gen_random_uuid(), 'a1110000-0000-0000-0000-000000000002', CAST(now() - d.age AS date), 'a1110000-0000-0000-0000-000000000001', 'Sent', 1,
                   now() - d.age, now() - d.age
              FROM (VALUES (INTERVAL '61 days'), (INTERVAL '62 days'), (INTERVAL '59 days'), (INTERVAL '58 days')) d(age);
            """);
    }

    private async Task ExecuteAsync(string connectionString, [System.Diagnostics.CodeAnalysis.ConstantExpected] string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // every caller passes a literal (ConstantExpected)
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<T> ScalarAsync<T>(string connectionString, [System.Diagnostics.CodeAnalysis.ConstantExpected] string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
#pragma warning disable CA2100 // every caller passes a literal (ConstantExpected)
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return (T)await command.ExecuteScalarAsync(Ct);
    }

    #endregion
}
