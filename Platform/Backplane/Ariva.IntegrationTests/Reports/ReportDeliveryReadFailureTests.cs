using Ariva.Core;
using Ariva.Core.Services;
using Ariva.Core.Services.Reports;
using Ariva.Infra.Notifications;
using Ariva.Infra.Services.Reports;
using Ariva.IntegrationTests.Security;
using Ariva.IntegrationTests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Ariva.IntegrationTests.Reports;

/// <summary>
/// ARV-291 (CWE-400) against PostgreSQL 17 with TimescaleDB: a scheduled delivery whose report read fails in the database counts its
/// attempt. The read fails while another session holds the minutes table (ACCESS EXCLUSIVE, so the hours statement waits for it):
/// in one database the server's statement timeout ends the wait (one second, set on that database, so every session of the host
/// has it), in another the waiting session is terminated (its connection lost). The database ends the delivery's transaction with
/// its statement, so the round rolls it back and counts the attempt in a transaction of its own: Failed with the fixed reason, round
/// by round, up to <see cref="ISvcReportDeliveries.MaxAttempts"/>, after which no round tries it. A delivery left as it was by a busy
/// slot (ARV-234) still counts none, and a read that succeeds in a later round sends the report.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReportDeliveryReadFailureTests(PostgresFixture fixture) : IAsyncDisposable
{
    // The host's clock is 2026-10-01 08:00 UTC, so a schedule at 06:00 owes 2026-09-30, the day seeded (the sites have no airport,
    // so their day is UTC's).
    private const string Reason = ISvcReportDeliveries.ReadFailedReason;

    private static readonly SemaphoreSlim TimeoutGate = new(1, 1);
    private static bool _timeoutSet;

    private readonly CountingTransport _transport = new();
    private readonly CapturedLogs _logs = new();
    private AccountsHost _host;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
            await _host.DisposeAsync();
    }

    [Fact]
    public async Task Round_Should_CountTheAttemptRoundByRoundUpToTheLimit_When_TheReadTimesOut()
    {
        var host = await TimeoutHostAsync();
        var admin = await SiteAsync(host, "RFT", "it.readfail.timeout", "readfail.timeout@ariva.test");
        var schedule = await ScheduleAsync(host, admin, "RFT", "Times out");
        var statuses = new List<string>();
        var runs = new List<ReportDeliveryRun>();

        await using (await LockMinutesAsync(host))
        {
            for (var round = 0; round < 2; round++)
            {
                runs.Add(await RunAsync(host));
                statuses.Add(await StatusAsync(host, schedule));
            }
        }

        // ARV-234: with the minutes free and the deliveries' slot held, the delivery is left as it was: no attempt counted.
        ReportDeliveryRun busy;
        await using (await HoldSlotAsync(host, 2))
            busy = await RunAsync(host);
        var afterBusy = await StatusAsync(host, schedule);

        await using (await LockMinutesAsync(host))
        {
            for (var round = 2; round < ISvcReportDeliveries.MaxAttempts; round++)
            {
                runs.Add(await RunAsync(host));
                statuses.Add(await StatusAsync(host, schedule));
            }

            runs.Add(await RunAsync(host));
        }

        var unlocked = await RunAsync(host);

        statuses.Should().Equal([.. Enumerable.Range(1, ISvcReportDeliveries.MaxAttempts).Select(n => $"Failed:{n}:{Reason}")],
            "each failed read is counted once, with the fixed reason");
        runs.Take(ISvcReportDeliveries.MaxAttempts).Should().AllSatisfy(r => r.Should().Be(new ReportDeliveryRun(1, 0, 0, 1)));
        busy.Should().Be(new ReportDeliveryRun(1, 0, 0, 0), "a busy slot leaves the delivery as it was");
        afterBusy.Should().Be($"Failed:2:{Reason}", "no attempt counted while the slot is in use");
        _logs.Entries.Should().Contain(e => e.Contains("the deliveries' report database slot is in use, left for a later round", StringComparison.Ordinal));
        runs[^1].Due.Should().Be(0, "a delivery at its attempt limit is no longer due");
        unlocked.Due.Should().Be(0, "nor once the read would succeed");
        _transport.Calls.Should().Be(0, "no report was sent");
        (await StatusAsync(host, schedule)).Should().Be($"Failed:{ISvcReportDeliveries.MaxAttempts}:{Reason}");
        _logs.Failures.Should().HaveCount(ISvcReportDeliveries.MaxAttempts).And.AllSatisfy(f =>
        {
            f.Message.Should().Contain("its report could not be read from the database (attempt counted: True)");
            f.Exception.GetBaseException().Should().BeOfType<PostgresException>().Which.SqlState.Should().Be(PostgresErrorCodes.QueryCanceled, "the server's statement timeout ended it");
        });
        (await HeldSlotsAsync(host)).Should().BeEmpty("a failed read's slot ends with its rollback");
    }

    [Fact]
    public async Task Round_Should_SendTheReport_When_AReadThatFailedSucceedsInALaterRound()
    {
        var host = await TimeoutHostAsync();
        var admin = await SiteAsync(host, "RFL", "it.readfail.later", "readfail.later@ariva.test");
        var schedule = await ScheduleAsync(host, admin, "RFL", "Read later");

        ReportDeliveryRun failed;
        await using (await LockMinutesAsync(host))
            failed = await RunAsync(host);
        var afterFailure = await StatusAsync(host, schedule);
        var sent = await RunAsync(host);

        failed.Failed.Should().Be(1);
        afterFailure.Should().Be($"Failed:1:{Reason}");
        sent.Sent.Should().Be(1, "the next round reads the report and sends it");
        (await StatusAsync(host, schedule)).Should().Be("Sent:2:", "the sent delivery keeps both attempts and no reason");
        _transport.SentTo("readfail.later@ariva.test").Should().Be(1);
        (await CountAsync(host, await host.ReadAsync<Guid>("SELECT id FROM report_delivery WHERE schedule_id = @id", schedule))).Should().BeFalse("a sent delivery is no longer due");
        (await StatusAsync(host, schedule)).Should().Be("Sent:2:");
    }

    [Fact]
    public async Task Round_Should_CountTheAttempt_When_TheHostsCommandTimeoutEndsTheRead()
    {
        // Review (L2): with the default settings the host's command timeout (Database:CommandTimeoutSeconds, 30 s; 5 here, its least)
        // ends the wait before the runtime login's statement timeout (360 s): Npgsql cancels the statement and the connection stays
        // usable, so the rollback is an ordinary one.
        var settings = Settings();
        settings["Database:CommandTimeoutSeconds"] = "5";
        var host = new AccountsHost(fixture, settings, TestDatabase.ReportReadLost, Configure);
        _host = host;
        var admin = await SiteAsync(host, "RFK", "it.readfail.command", "readfail.command@ariva.test");
        var schedule = await ScheduleAsync(host, admin, "RFK", "Client timeout");

        ReportDeliveryRun run;
        await using (await LockMinutesAsync(host))
            run = await RunAsync(host);

        run.Should().Be(new ReportDeliveryRun(1, 0, 0, 1));
        (await StatusAsync(host, schedule)).Should().Be($"Failed:1:{Reason}");
        _logs.Failures.Should().ContainSingle().Which.Exception.GetBaseException().Should().BeOfType<TimeoutException>("the host stopped waiting");
        (await RunAsync(host)).Sent.Should().Be(1, "the next round sends it");
    }

    [Fact]
    public async Task CountFailedRead_Should_CountNothing_When_AnotherTransactionHoldsTheDeliveryOrItIsAtItsLimit()
    {
        var host = await TimeoutHostAsync();
        var admin = await SiteAsync(host, "RFN", "it.readfail.none", "readfail.none@ariva.test");
        var schedule = await ScheduleAsync(host, admin, "RFN", "Counted once");
        Guid delivery;
        await using (var scope = host.Provider.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            delivery = (await scope.ServiceProvider.GetRequiredService<ISvcReportDeliveries>().PrepareAsync(Ct)).Should().ContainSingle().Subject;
            await unitOfWork.EndAsync(Ct);
        }

        // A round that claimed it meanwhile holds it: passed over at once (with a wait, this database's statement timeout would throw).
        bool held;
        await using (var row = new NpgsqlConnection(Unpooled(await host.DatabaseAsync())))
        {
            await row.OpenAsync(Ct);
            await using var transaction = await row.BeginTransactionAsync(Ct);
            await using (var lockRow = new NpgsqlCommand("WITH held AS (SELECT id FROM report_delivery WHERE id = @id FOR UPDATE) SELECT count(*)::int FROM held", row, transaction))
            {
                lockRow.Parameters.AddWithValue("id", delivery);
                ((int)await lockRow.ExecuteScalarAsync(Ct)).Should().Be(1);
            }

            held = await CountAsync(host, delivery);
            await transaction.RollbackAsync(Ct);
        }

        var afterHeld = await StatusAsync(host, schedule);
        var counted = new List<bool>();
        for (var i = 0; i <= ISvcReportDeliveries.MaxAttempts; i++)
            counted.Add(await CountAsync(host, delivery));

        held.Should().BeFalse("another transaction holds the delivery");
        afterHeld.Should().Be("Pending:0:");
        counted.Should().Equal([.. Enumerable.Repeat(true, ISvcReportDeliveries.MaxAttempts), false], "counted up to the limit, then no more");
        (await StatusAsync(host, schedule)).Should().Be($"Failed:{ISvcReportDeliveries.MaxAttempts}:{Reason}");
    }

    [Fact]
    public async Task Round_Should_CountTheAttempt_When_TheReadLosesItsConnection()
    {
        var host = new AccountsHost(fixture, Settings(), TestDatabase.ReportReadLost, Configure);
        _host = host;
        var admin = await SiteAsync(host, "RFC", "it.readfail.lost", "readfail.lost@ariva.test");
        var schedule = await ScheduleAsync(host, admin, "RFC", "Connection lost");

        ReportDeliveryRun run;
        bool terminated;
        await using (await LockMinutesAsync(host))
        {
            var round = Task.Run(() => RunAsync(host), Ct);
            terminated = await TerminateWaitingReadAsync(host);
            run = await round;
        }

        terminated.Should().BeTrue("the delivery's read was waiting for the minutes and its session was ended");
        run.Should().Be(new ReportDeliveryRun(1, 0, 0, 1));
        (await StatusAsync(host, schedule)).Should().Be($"Failed:1:{Reason}", "counted in a transaction of its own, on another connection");
        _logs.Failures.Should().ContainSingle().Which.Message.Should().Contain("(attempt counted: True)");
        // The broken connection's rollback is logged with the cause's type and SQLSTATE only, never its text (CWE-532).
        _logs.Entries.Where(e => e.StartsWith("A transaction was rolled back", StringComparison.Ordinal)).Should().Equal(
            ["A transaction was rolled back after its database connection broke (PostgresException, SQLSTATE 57P01); nothing of it commits"]);
        var sent = await RunAsync(host);
        sent.Sent.Should().Be(1, "the next round sends it");
        (await StatusAsync(host, schedule)).Should().Be("Sent:2:");
    }

    #region Helpers

    private Dictionary<string, string> Settings() => new()
    {
        ["Email:Enabled"] = "true",
        ["Email:FromAddress"] = "no-reply@ariva.test"
    };

    private void Configure(IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<IEmailTransport>(_transport));
        services.AddSingleton<ILoggerProvider>(_logs);
        services.TryAddScoped<ISvcReportDeliveries, SvcReportDeliveries>();
        services.TryAddSingleton<ReportDeliveryRound>();
    }

    /// <summary>
    /// A host on the database whose sessions have a statement timeout of one second (set once, before any session of a host opens; the
    /// scripts and the test's own seeding run well within it).
    /// </summary>
    private async Task<AccountsHost> TimeoutHostAsync()
    {
        var host = new AccountsHost(fixture, Settings(), TestDatabase.ReportReadTimeout, Configure);
        _host = host;
        var database = await host.DatabaseAsync();
        await TimeoutGate.WaitAsync(Ct);
        try
        {
            if (!_timeoutSet)
            {
                await using var connection = new NpgsqlConnection(Unpooled(database));
                await connection.OpenAsync(Ct);
                // The database's name is the fixture's own constant (it_report_read_timeout); a statement cannot bind it.
                await using var set = new NpgsqlCommand("ALTER DATABASE it_report_read_timeout SET statement_timeout = '1s'", connection);
                await set.ExecuteNonQueryAsync(Ct);
                _timeoutSet = true;
            }
        }
        finally
        {
            TimeoutGate.Release();
        }

        database.Should().Be("it_report_read_timeout");
        return host;
    }

    private static Task<ReportDeliveryRun> RunAsync(AccountsHost host) => host.Provider.GetRequiredService<ReportDeliveryRound>().RunAsync(Ct);

    /// <summary>Counts a failed read of the delivery as the round does: in a scope and transaction of its own.</summary>
    private static async Task<bool> CountAsync(AccountsHost host, Guid delivery)
    {
        await using var scope = host.Provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var counted = await scope.ServiceProvider.GetRequiredService<ISvcReportDeliveries>().CountFailedReadAsync(delivery, Ct);
        await unitOfWork.EndAsync(Ct);
        return counted;
    }

    /// <summary>A site with one queue zone's ten minutes of the day (three entries a minute), and an administrator of all sites.</summary>
    private async Task<Guid> SiteAsync(AccountsHost host, string site, string userName, string email)
    {
        var admin = await host.CreateUserAsync(userName, roles: [RoleCodes.SystemAdministrator], allSites: true);
        (await host.ReadAsync<int>("UPDATE \"user\" SET email = @secret WHERE id = @id RETURNING 1", admin, email)).Should().Be(1);
        await using var connection = new NpgsqlConnection(Unpooled(await host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var seed = new NpgsqlCommand("""
            WITH site AS (INSERT INTO site (id, code, name) VALUES (gen_random_uuid(), @site, 'Read failures') RETURNING code)
            INSERT INTO queue_minute (zone_key, minute_utc, profile_version, status, entries, exits, waits, queue_length, wait_buckets, wait_counts, updated_on)
            SELECT @site || '/Q1', timestamptz '2026-09-30T10:00:00Z' + m * interval '1 minute', 1, 'Final', 3, 3, 3, 4, ARRAY[2, 6], ARRAY[2, 1], now()
            FROM generate_series(0, 9) m
            """, connection);
        seed.Parameters.AddWithValue("site", site);
        (await seed.ExecuteNonQueryAsync(Ct)).Should().Be(10);
        return admin;
    }

    private static async Task<Guid> ScheduleAsync(AccountsHost host, Guid admin, string site, string name) =>
        (await host.AsCallerAsync(admin, s => s.GetRequiredService<ISvcReportSchedules>()
            .CreateAsync(new ReportScheduleRequest(site, name, null, "06:00", [admin]), Ct))).Data.Id;

    private static Task<string> StatusAsync(AccountsHost host, Guid schedule) =>
        host.ReadAsync<string>("SELECT string_agg(status || ':' || attempts || ':' || COALESCE(reason, ''), ',' ORDER BY status) FROM report_delivery WHERE schedule_id = @id", schedule);

    private string Unpooled(string database) => new NpgsqlConnectionStringBuilder(fixture.ConnectionString(database)) { Pooling = false }.ConnectionString;

    /// <summary>
    /// The minutes table held by a transaction of the test's own (ACCESS EXCLUSIVE, until the connection is disposed), so the hours
    /// statement waits for it; the deliveries' claim, the recipient's roles and the round's preparation read other tables.
    /// </summary>
    private async Task<NpgsqlConnection> LockMinutesAsync(AccountsHost host)
    {
        var connection = new NpgsqlConnection(Unpooled(await host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using (var begin = new NpgsqlCommand("BEGIN", connection))
            await begin.ExecuteNonQueryAsync(Ct);
        await using (var hold = new NpgsqlCommand("LOCK TABLE queue_minute IN ACCESS EXCLUSIVE MODE", connection))
            await hold.ExecuteNonQueryAsync(Ct);
        return connection;
    }

    /// <summary>A report slot held by another session (a session advisory lock, freed when the connection is disposed).</summary>
    private async Task<NpgsqlConnection> HoldSlotAsync(AccountsHost host, int slot)
    {
        var connection = new NpgsqlConnection(Unpooled(await host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        await using var hold = new NpgsqlCommand("SELECT pg_advisory_lock(@class, @slot)", connection);
        hold.Parameters.AddWithValue("class", ReportReader.SlotLockClass);
        hold.Parameters.AddWithValue("slot", slot);
        await hold.ExecuteNonQueryAsync(Ct);
        return connection;
    }

    private static async Task<List<int>> HeldSlotsAsync(AccountsHost host)
    {
        var held = await host.ReadAsync<string>("""
            SELECT COALESCE(string_agg(objid::text, ',' ORDER BY objid), '') FROM pg_locks
            WHERE locktype = 'advisory' AND classid = 60 AND objsubid = 2 AND granted
              AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
            """);
        return [.. held.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse)];
    }

    /// <summary>Ends the session whose statement waits for the minutes table (the delivery's hours read), once it waits; false after 30 s.</summary>
    private async Task<bool> TerminateWaitingReadAsync(AccountsHost host)
    {
        await using var connection = new NpgsqlConnection(Unpooled(await host.DatabaseAsync()));
        await connection.OpenAsync(Ct);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await using var terminate = new NpgsqlCommand("""
                SELECT COALESCE(bool_and(pg_terminate_backend(pid)), false) FROM pg_stat_activity
                WHERE datname = current_database() AND pid <> pg_backend_pid() AND wait_event_type = 'Lock' AND query LIKE '%queue_minute%'
                """, connection);
            if ((bool)await terminate.ExecuteScalarAsync(Ct))
                return true;
            await Task.Delay(50, Ct);
        }

        return false;
    }

    /// <summary>Counts the sends; every email is taken.</summary>
    private sealed class CountingTransport : IEmailTransport
    {
        private readonly Lock _gate = new();
        private readonly List<string> _sent = [];
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public int SentTo(string recipient)
        {
            lock (_gate)
                return _sent.Count(r => string.Equals(r, recipient, StringComparison.OrdinalIgnoreCase));
        }

        public Task<EmailBatch> SendAsync(IReadOnlyList<OutgoingEmail> emails, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            lock (_gate)
                _sent.AddRange(emails.Select(e => e.Recipient));
            return Task.FromResult(EmailBatch.Tried(new Dictionary<Guid, string>()));
        }
    }

    private sealed class CapturedLogs : ILoggerProvider, ILogger
    {
        private readonly List<string> _entries = [];
        private readonly List<(string Message, Exception Exception)> _failures = [];

        public IReadOnlyList<string> Entries
        {
            get
            {
                lock (_entries)
                    return [.. _entries];
            }
        }

        /// <summary>The round's lines for a failed read, with their exception.</summary>
        public IReadOnlyList<(string Message, Exception Exception)> Failures
        {
            get
            {
                lock (_entries)
                    return [.. _failures];
            }
        }

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            var message = formatter(state, exception);
            lock (_entries)
            {
                _entries.Add(message);
                if (message.Contains("its report could not be read from the database", StringComparison.Ordinal))
                    _failures.Add((message, exception));
            }
        }

        public void Dispose()
        {
        }
    }

    #endregion
}
